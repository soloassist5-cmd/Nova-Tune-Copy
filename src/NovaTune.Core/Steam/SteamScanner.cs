using System.Globalization;
using NovaTune.Core.Logging;

namespace NovaTune.Core.Steam;

public sealed record SteamApp(
    int AppId,
    string Name,
    string InstallDir,
    string LibraryPath,
    string ManifestPath,
    long? SizeOnDisk,
    DateTimeOffset? LastUpdated,
    bool IsFullyInstalled);

public sealed record SteamUser(
    ulong SteamId64,
    uint AccountId,
    string? AccountName,
    string? PersonaName,
    bool MostRecent,
    string UserdataPath);

/// <summary>
/// Читает библиотеки Steam только по манифестам (libraryfolders.vdf, appmanifest_*.acf) — без сканирования диска.
/// Ошибки отдельных файлов не прерывают сканирование, а попадают в <see cref="Warnings"/>.
/// </summary>
public sealed class SteamScanner
{
    /// <summary>Смещение между SteamID64 и 32-битным идентификатором аккаунта (имя папки в userdata).</summary>
    public const ulong SteamId64Base = 76561197960265728UL;

    private readonly ILog _log;
    private readonly List<string> _warnings = [];

    public SteamScanner(string steamPath, ILog? log = null)
    {
        SteamPath = Path.GetFullPath(steamPath);
        _log = log ?? NullLog.Instance;
    }

    public string SteamPath { get; }

    public IReadOnlyList<string> Warnings => _warnings;

    public bool SteamExists => Directory.Exists(Path.Combine(SteamPath, "steamapps")) || File.Exists(Path.Combine(SteamPath, "steam.exe"));

    /// <summary>Папки библиотек: сама папка Steam плюс все из libraryfolders.vdf, которые существуют на диске.</summary>
    public IReadOnlyList<string> GetLibraries()
    {
        var result = new List<string>();
        void AddIfExists(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                Warn($"Некорректный путь библиотеки: {path}", ex);
                return;
            }

            if (Directory.Exists(Path.Combine(full, "steamapps")) && !result.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(full);
            }
        }

        AddIfExists(SteamPath);

        foreach (var vdfPath in new[]
                 {
                     Path.Combine(SteamPath, "steamapps", "libraryfolders.vdf"),
                     Path.Combine(SteamPath, "config", "libraryfolders.vdf"),
                 })
        {
            if (!File.Exists(vdfPath))
            {
                continue;
            }

            try
            {
                var root = VdfParser.ParseFile(vdfPath);
                var folders = root["libraryfolders"];
                if (folders is null)
                {
                    continue;
                }

                foreach (var child in folders.Children)
                {
                    if (!int.TryParse(child.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    {
                        continue;
                    }

                    // Новый формат: "0" { "path" "..." }; старый: "1" "D:\\SteamLibrary".
                    AddIfExists(child.IsSection ? child.GetString("path") : child.Value);
                }
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                Warn($"Не удалось прочитать {vdfPath}", ex);
            }
        }

        return result;
    }

    public IReadOnlyList<SteamApp> GetApps()
    {
        var apps = new Dictionary<int, SteamApp>();
        foreach (var library in GetLibraries())
        {
            var steamapps = Path.Combine(library, "steamapps");
            IEnumerable<string> manifests;
            try
            {
                manifests = Directory.EnumerateFiles(steamapps, "appmanifest_*.acf").ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Warn($"Нет доступа к {steamapps}", ex);
                continue;
            }

            foreach (var manifest in manifests)
            {
                if (ReadManifest(manifest, library) is { } app && !apps.ContainsKey(app.AppId))
                {
                    apps[app.AppId] = app;
                }
            }
        }

        return apps.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public SteamApp? FindApp(int appId) => GetApps().FirstOrDefault(a => a.AppId == appId);

    public SteamApp? ReadManifest(string manifestPath, string libraryPath)
    {
        try
        {
            var state = VdfParser.ParseFile(manifestPath)["AppState"];
            if (state is null || !int.TryParse(state.GetString("appid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId))
            {
                Warn($"В манифесте нет AppState/appid: {manifestPath}");
                return null;
            }

            var installDir = state.GetString("installdir");
            if (string.IsNullOrWhiteSpace(installDir) || installDir.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || installDir.Contains(".."))
            {
                Warn($"Некорректный installdir в {manifestPath}");
                return null;
            }

            var fullDir = Path.Combine(libraryPath, "steamapps", "common", installDir);
            long? size = long.TryParse(state.GetString("SizeOnDisk"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null;
            DateTimeOffset? updated = long.TryParse(state.GetString("LastUpdated"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) && u > 0
                ? DateTimeOffset.FromUnixTimeSeconds(u)
                : null;
            var flags = int.TryParse(state.GetString("StateFlags"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : 0;

            return new SteamApp(
                appId,
                state.GetString("name") ?? installDir,
                fullDir,
                libraryPath,
                manifestPath,
                size,
                updated,
                IsFullyInstalled: (flags & 4) != 0);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            Warn($"Не удалось прочитать манифест {manifestPath}", ex);
            return null;
        }
    }

    /// <summary>Аккаунты Steam: папки userdata, дополненные именами из config\loginusers.vdf.</summary>
    public IReadOnlyList<SteamUser> GetUsers()
    {
        var userdata = Path.Combine(SteamPath, "userdata");
        if (!Directory.Exists(userdata))
        {
            return [];
        }

        var known = new Dictionary<uint, (ulong Id64, string? Account, string? Persona, bool Recent)>();
        var loginUsers = Path.Combine(SteamPath, "config", "loginusers.vdf");
        if (File.Exists(loginUsers))
        {
            try
            {
                var users = VdfParser.ParseFile(loginUsers)["users"];
                foreach (var u in users?.Children ?? [])
                {
                    if (ulong.TryParse(u.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id64) && id64 > SteamId64Base)
                    {
                        known[(uint)(id64 - SteamId64Base)] = (id64, u.GetString("AccountName"), u.GetString("PersonaName"), u.GetString("MostRecent") == "1");
                    }
                }
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                Warn("Не удалось прочитать loginusers.vdf", ex);
            }
        }

        var result = new List<SteamUser>();
        foreach (var dir in Directory.EnumerateDirectories(userdata))
        {
            if (!uint.TryParse(Path.GetFileName(dir), NumberStyles.Integer, CultureInfo.InvariantCulture, out var accountId) || accountId == 0)
            {
                continue;
            }

            known.TryGetValue(accountId, out var info);
            result.Add(new SteamUser(
                info.Id64 != 0 ? info.Id64 : SteamId64Base + accountId,
                accountId,
                info.Account,
                info.Persona,
                info.Recent,
                dir));
        }

        return result.OrderByDescending(u => u.MostRecent).ThenBy(u => u.AccountId).ToList();
    }

    /// <summary>Параметры запуска игры из userdata\&lt;id&gt;\config\localconfig.vdf (только чтение).</summary>
    public string? GetLaunchOptions(SteamUser user, int appId)
    {
        var path = Path.Combine(user.UserdataPath, "config", "localconfig.vdf");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var apps = VdfParser.ParseFile(path).Path("UserLocalConfigStore", "Software", "Valve", "Steam", "apps");
            return apps?[appId.ToString(CultureInfo.InvariantCulture)]?.GetString("LaunchOptions");
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            Warn("Не удалось прочитать localconfig.vdf", ex);
            return null;
        }
    }

    private void Warn(string message, Exception? ex = null)
    {
        _warnings.Add(ex is null ? message : $"{message}: {ex.Message}");
        _log.Warn(message, ex);
    }
}
