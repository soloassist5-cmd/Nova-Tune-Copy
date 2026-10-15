using NovaTune.Core.Localization;
using NovaTune.Core.Logging;

namespace NovaTune.Core.Cleanup;

/// <summary>Корневая папка категории очистки.</summary>
public sealed record CleanupRoot(string Path, string SearchPattern = "*", bool Recursive = true);

/// <summary>Категория очистки. Содержит только заведомо безопасные временные данные.</summary>
public sealed record CleanupCategory(
    string Id,
    LocText Title,
    LocText Description,
    IReadOnlyList<CleanupRoot> Roots,
    TimeSpan MinAge,
    bool SelectedByDefault);

public sealed record CleanupScanResult(
    CleanupCategory Category,
    IReadOnlyList<CleanupFile> Files,
    long TotalBytes,
    int InaccessibleCount)
{
    public int FileCount => Files.Count;
}

public sealed record CleanupFile(string Path, long Size, string RootPath);

public sealed record CleanupResult(
    string CategoryId,
    int DeletedFiles,
    long FreedBytes,
    int SkippedInUse,
    int SkippedChanged);

/// <summary>
/// Сканирование и удаление файлов. Правила безопасности:
/// — не заходим в точки повторной обработки (junction, symlink) и не удаляем их цели;
/// — удаляем только файлы, полный путь которых лежит внутри корня категории;
/// — файлы моложе MinAge не трогаем (их может использовать работающая программа);
/// — перед удалением повторно проверяем, что файл не изменился после сканирования;
/// — занятые файлы пропускаются без ошибки.
/// </summary>
public sealed class CleanupEngine(ILog log)
{
    public Task<CleanupScanResult> ScanAsync(CleanupCategory category, CancellationToken ct = default) =>
        Task.Run(() => Scan(category, DateTime.UtcNow, ct), ct);

    public CleanupScanResult Scan(CleanupCategory category, DateTime utcNow, CancellationToken ct = default)
    {
        var files = new List<CleanupFile>();
        var inaccessible = 0;
        foreach (var root in category.Roots)
        {
            if (!Directory.Exists(root.Path))
            {
                continue;
            }

            var rootFull = NormalizeRoot(root.Path);
            var pending = new Stack<string>();
            pending.Push(rootFull);
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = pending.Pop();
                try
                {
                    var info = new DirectoryInfo(dir);
                    foreach (var file in info.EnumerateFiles(root.SearchPattern))
                    {
                        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            continue;
                        }

                        if (utcNow - file.LastWriteTimeUtc < category.MinAge)
                        {
                            continue;
                        }

                        if (IsInside(file.FullName, rootFull))
                        {
                            files.Add(new CleanupFile(file.FullName, file.Length, rootFull));
                        }
                    }

                    if (root.Recursive)
                    {
                        foreach (var sub in info.EnumerateDirectories())
                        {
                            // Junction/symlink может вести куда угодно (в том числе в документы) — не заходим.
                            if (!sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            {
                                pending.Push(sub.FullName);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    inaccessible++;
                    log.Warn($"Очистка: нет доступа к {dir}: {ex.Message}");
                }
            }
        }

        return new CleanupScanResult(category, files, files.Sum(f => f.Size), inaccessible);
    }

    public Task<CleanupResult> CleanAsync(CleanupScanResult scan, CancellationToken ct = default) =>
        Task.Run(() => Clean(scan, DateTime.UtcNow, ct), ct);

    public CleanupResult Clean(CleanupScanResult scan, DateTime utcNow, CancellationToken ct = default)
    {
        var deleted = 0;
        long freed = 0;
        var inUse = 0;
        var changed = 0;

        foreach (var file in scan.Files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(file.Path);
                if (!info.Exists)
                {
                    continue;
                }

                // Повторная проверка: файл не подменён ссылкой, лежит в корне и не обновлялся с момента сканирования.
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || !IsInside(info.FullName, file.RootPath) || HasReparseAncestor(info, file.RootPath))
                {
                    changed++;
                    continue;
                }

                if (utcNow - info.LastWriteTimeUtc < scan.Category.MinAge)
                {
                    changed++;
                    continue;
                }

                if (info.Attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    info.Attributes &= ~FileAttributes.ReadOnly;
                }

                var size = info.Length;
                info.Delete();
                deleted++;
                freed += size;
            }
            catch (IOException)
            {
                // Файл занят другой программой — это нормально для временных файлов.
                inUse++;
            }
            catch (UnauthorizedAccessException)
            {
                inUse++;
            }
        }

        foreach (var root in scan.Files.Select(f => f.RootPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            RemoveEmptyDirectories(root);
        }

        if (inUse > 0)
        {
            log.Info($"Очистка {scan.Category.Id}: пропущено занятых файлов: {inUse}");
        }

        return new CleanupResult(scan.Category.Id, deleted, freed, inUse, changed);
    }

    /// <summary>Удаляет пустые подпапки (сам корень остаётся). Ссылки не трогает.</summary>
    private void RemoveEmptyDirectories(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var dirs = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var sub in new DirectoryInfo(dir).EnumerateDirectories())
                {
                    if (!sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        dirs.Add(sub.FullName);
                        pending.Push(sub.FullName);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                log.Warn($"Очистка: пропущена папка {dir}: {ex.Message}");
            }
        }

        // Сначала самые глубокие.
        foreach (var dir in dirs.OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Папка занята или нет прав — оставляем; это не ошибка очистки.
                log.Info($"Очистка: папка оставлена {dir}: {ex.Message}");
            }
        }
    }

    private static bool HasReparseAncestor(FileInfo file, string root)
    {
        var dir = file.Directory;
        while (dir is not null && IsInside(dir.FullName, root) && !PathEquals(dir.FullName, root))
        {
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }

            dir = dir.Parent;
        }

        return false;
    }

    internal static string NormalizeRoot(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static bool IsInside(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var r = NormalizeRoot(root) + Path.DirectorySeparatorChar;
        return full.StartsWith(r, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(NormalizeRoot(a), NormalizeRoot(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
