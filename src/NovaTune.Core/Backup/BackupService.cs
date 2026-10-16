using System.Globalization;
using System.Security.Cryptography;
using NovaTune.Core.Common;
using NovaTune.Core.Localization;
using NovaTune.Core.Logging;

namespace NovaTune.Core.Backup;

/// <summary>
/// Фундамент всех изменений. Порядок работы:
/// 1) снимок исходного состояния всех целей и запись бэкапа со статусом InProgress (до любых изменений);
/// 2) по одному шагу: отметка Applying → изменение → повторное чтение (проверка) → Applied/Failed, после каждого шага бэкап сохраняется;
/// 3) итоговый статус. Если процесс прервался, при следующем запуске бэкап со статусом InProgress
///    находится через <see cref="FindIncomplete"/> и его можно откатить.
/// </summary>
public sealed class BackupService
{
    private const string RecordFileName = "backup.json";
    private readonly ISystemSettingsStore _store;
    private readonly OperationJournal _journal;
    private readonly ILog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BackupService(string backupRoot, ISystemSettingsStore store, OperationJournal journal, ILog log)
    {
        BackupRoot = backupRoot;
        _store = store;
        _journal = journal;
        _log = log;
    }

    /// <summary>Папка с бэкапами (меняется в настройках).</summary>
    public string BackupRoot { get; set; }

    /// <summary>Идёт ли сейчас операция (защита от повторного запуска).</summary>
    public bool IsBusy => _gate.CurrentCount == 0;

    public event EventHandler? BackupsChanged;

    // ───────────────────────────── Чтение ─────────────────────────────

    public IReadOnlyList<BackupRecord> List()
    {
        if (!Directory.Exists(BackupRoot))
        {
            return [];
        }

        var result = new List<BackupRecord>();
        foreach (var dir in Directory.EnumerateDirectories(BackupRoot))
        {
            var file = Path.Combine(dir, RecordFileName);
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                if (Json.Read<BackupRecord>(file) is { } record)
                {
                    record.Directory = dir;
                    result.Add(record);
                }
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                _log.Warn("Не удалось прочитать бэкап " + dir, ex);
            }
        }

        return result.OrderByDescending(r => r.CreatedAt).ToList();
    }

    /// <summary>Операции, прерванные посередине (сбой, выключение ПК).</summary>
    public IReadOnlyList<BackupRecord> FindIncomplete() =>
        List().Where(r => r.Status == BackupStatus.InProgress).ToList();

    // ───────────────────────────── Предпросмотр ─────────────────────────────

    public ChangePreview Preview(PlannedChange change)
    {
        switch (change)
        {
            case RegistryChange rc:
                {
                    var current = _store.ReadValue(rc.Hive, rc.Key, rc.Name);
                    var same = rc.NewValue is null ? current is null : rc.NewValue.ContentEquals(current);
                    return new ChangePreview(change, DisplayOrAbsent(current), DisplayOrAbsent(rc.NewValue), same);
                }

            case FileWriteChange fc:
                {
                    var exists = File.Exists(fc.Path);
                    var same = exists && Sha256File(fc.Path) == Sha256Bytes(fc.Content);
                    var current = exists
                        ? Loc.T($"файл существует, {new FileInfo(fc.Path).Length} байт", $"file exists, {new FileInfo(fc.Path).Length} bytes")
                        : Loc.T("файла нет", "file does not exist");
                    return new ChangePreview(change, current, Loc.T($"{fc.Content.Length} байт", $"{fc.Content.Length} bytes"), same);
                }

            case PowerSchemeChange pc:
                {
                    var active = _store.GetActivePowerScheme();
                    var schemes = _store.GetPowerSchemes();
                    var currentName = SchemeName(schemes, active);
                    var newName = pc.CreateUltimate
                        ? Loc.T("новая схема «Максимальная производительность»", "new \"Ultimate Performance\" scheme")
                        : SchemeName(schemes, pc.Target);
                    return new ChangePreview(change, currentName, newName, !pc.CreateUltimate && active == pc.Target);
                }

            default:
                throw new NotSupportedException(change.GetType().Name);
        }
    }

    // ───────────────────────────── Применение ─────────────────────────────

    public async Task<TransactionResult> ApplyAsync(
        string title,
        IReadOnlyList<PlannedChange> changes,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (changes.Count == 0)
        {
            throw new ArgumentException("Нет изменений для применения.", nameof(changes));
        }

        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(Loc.T("Другая операция ещё выполняется.", "Another operation is still running."));
        }

        try
        {
            return await Task.Run(() => ApplyCore(title, changes, progress, ct), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            BackupsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private TransactionResult ApplyCore(string title, IReadOnlyList<PlannedChange> changes, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report(Loc.T("Создание бэкапа…", "Creating backup…"));
        var record = NewRecord(title, BackupKind.Automatic, BackupStatus.InProgress);
        for (var i = 0; i < changes.Count; i++)
        {
            record.Items.Add(Capture(changes[i], record, i));
        }

        WriteRegFile(record);
        Save(record);
        _journal.Add("backup", Loc.T("Бэкап перед операцией: ", "Backup before operation: ") + title, true, record.Id);

        var applied = 0;
        var skipped = 0;
        var failures = new List<(string, string)>();
        var restart = false;

        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            var item = record.Items[i];

            if (ct.IsCancellationRequested)
            {
                item.State = BackupItemState.Skipped;
                item.Error = Loc.T("Отменено пользователем", "Cancelled by user");
                continue;
            }

            progress?.Report(change.Title);

            if (Preview(change).AlreadyInTargetState)
            {
                item.State = BackupItemState.Skipped;
                skipped++;
                Save(record);
                continue;
            }

            item.State = BackupItemState.Applying;
            Save(record);

            try
            {
                Apply(change, item, record);
                if (!Verify(change, item))
                {
                    throw new InvalidOperationException(Loc.T(
                        "Проверка не пройдена: после записи прочитано другое значение.",
                        "Verification failed: a different value was read back after writing."));
                }

                item.State = BackupItemState.Applied;
                applied++;
                restart |= change.RequiresRestart;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                item.State = BackupItemState.Failed;
                item.Error = FriendlyError(ex);
                failures.Add((change.Title, item.Error));
                _log.Warn("Не удалось применить: " + change.Title, ex);
            }

            Save(record);
        }

        record.Status = failures.Count == 0 && !ct.IsCancellationRequested
            ? BackupStatus.Completed
            : applied > 0 ? BackupStatus.PartiallyCompleted : BackupStatus.Failed;
        record.RequiresRestart = restart;
        Save(record);

        _journal.Add(
            "optimize",
            title,
            failures.Count == 0,
            Loc.T($"применено: {applied}, без изменений: {skipped}, ошибок: {failures.Count}", $"applied: {applied}, unchanged: {skipped}, errors: {failures.Count}")
            + (failures.Count > 0 ? "; " + string.Join("; ", failures.Select(f => f.Item1 + ": " + f.Item2)) : ""));

        return new TransactionResult(record, applied, skipped, failures, restart);
    }

    // ───────────────────────────── Снимки (ручной бэкап) ─────────────────────────────

    /// <summary>Сохраняет текущее состояние целей без изменений (ручной бэкап).</summary>
    public BackupRecord CreateSnapshot(string title, BackupKind kind, IReadOnlyList<PlannedChange> targets, string? tag = null)
    {
        var record = NewRecord(title, kind, BackupStatus.Snapshot);
        record.Tag = tag;
        for (var i = 0; i < targets.Count; i++)
        {
            var item = Capture(targets[i], record, i);
            item.NewDisplay = null;
            record.Items.Add(item);
        }

        WriteRegFile(record);
        Save(record);
        _journal.Add("backup", Loc.T("Создан бэкап: ", "Backup created: ") + title, true, Loc.T($"элементов: {record.Items.Count}", $"items: {record.Items.Count}"));
        BackupsChanged?.Invoke(this, EventArgs.Empty);
        return record;
    }

    /// <summary>Бэкап файлов (например, конфигов CS2) с копиями и хэшами.</summary>
    public BackupRecord CreateFileSnapshot(string title, BackupKind kind, IEnumerable<string> files, string? tag = null)
    {
        var targets = files
            .Where(File.Exists)
            .Select(f => (PlannedChange)new FileWriteChange(f, []) { Title = Path.GetFileName(f) })
            .ToList();
        if (targets.Count == 0)
        {
            throw new InvalidOperationException(Loc.T("Файлов для бэкапа не найдено.", "No files to back up."));
        }

        return CreateSnapshot(title, kind, targets, tag);
    }

    // ───────────────────────────── Восстановление ─────────────────────────────

    public async Task<RestoreResult> RestoreAsync(BackupRecord record, IReadOnlyCollection<string>? itemIds = null, CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(Loc.T("Другая операция ещё выполняется.", "Another operation is still running."));
        }

        try
        {
            return await Task.Run(() => RestoreCore(record, itemIds), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            BackupsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private RestoreResult RestoreCore(BackupRecord record, IReadOnlyCollection<string>? itemIds)
    {
        var restored = 0;
        var failures = new List<(string, string)>();
        var restart = false;

        // Откатываем в обратном порядке — как стек изменений.
        foreach (var item in Enumerable.Reverse(record.Items))
        {
            if (itemIds is not null && !itemIds.Contains(item.Id))
            {
                continue;
            }

            // Пропущенный шаг ничего не менял — откатывать нечего.
            if (item.State == BackupItemState.Skipped)
            {
                continue;
            }

            try
            {
                RestoreItem(item, record);
                item.State = BackupItemState.Restored;
                item.Error = null;
                restored++;
                restart |= item.RequiresRestart;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                item.State = BackupItemState.RestoreFailed;
                item.Error = FriendlyError(ex);
                failures.Add((item.Title, item.Error));
                _log.Warn("Не удалось восстановить: " + item.Title, ex);
            }
        }

        var restorable = record.Items.Where(i => i.State != BackupItemState.Skipped).ToList();
        if (restorable.All(i => i.State == BackupItemState.Restored))
        {
            record.Status = BackupStatus.RolledBack;
        }
        else if (restored > 0 || failures.Count > 0)
        {
            record.Status = BackupStatus.PartiallyRolledBack;
        }

        Save(record);
        _journal.Add(
            "restore",
            Loc.T("Восстановление: ", "Restore: ") + record.Title,
            failures.Count == 0,
            Loc.T($"восстановлено: {restored}, ошибок: {failures.Count}", $"restored: {restored}, errors: {failures.Count}")
            + (failures.Count > 0 ? "; " + string.Join("; ", failures.Select(f => f.Item1 + ": " + f.Item2)) : ""));
        return new RestoreResult(restored, failures, restart);
    }

    /// <summary>Отмечает прерванную операцию как разобранную, не откатывая её (пользователь решил оставить изменения).</summary>
    public void MarkIncompleteAsAccepted(BackupRecord record)
    {
        record.Status = record.Items.Any(i => i.State == BackupItemState.Applied)
            ? BackupStatus.PartiallyCompleted
            : BackupStatus.Failed;
        Save(record);
        _journal.Add("backup", Loc.T("Прерванная операция оставлена без отката: ", "Interrupted operation kept without rollback: ") + record.Title, true);
        BackupsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Delete(BackupRecord record)
    {
        var root = Path.GetFullPath(BackupRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var dir = Path.GetFullPath(record.Directory);

        // Защита: удаляем только папку внутри каталога бэкапов.
        if (!dir.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(dir, RecordFileName)))
        {
            throw new InvalidOperationException("Папка бэкапа вне каталога бэкапов: " + dir);
        }

        Directory.Delete(dir, recursive: true);
        _journal.Add("backup", Loc.T("Удалён бэкап: ", "Backup deleted: ") + record.Title, true, record.Id);
        BackupsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ───────────────────────────── Внутреннее ─────────────────────────────

    private BackupRecord NewRecord(string title, BackupKind kind, BackupStatus status)
    {
        var now = DateTimeOffset.Now;
        var id = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        var dir = Path.Combine(BackupRoot, id);
        Directory.CreateDirectory(dir);
        return new BackupRecord { Id = id, CreatedAt = now, Kind = kind, Title = title, Status = status, Directory = dir };
    }

    private void Save(BackupRecord record)
    {
        record.UpdatedAt = DateTimeOffset.Now;
        Json.WriteAtomic(Path.Combine(record.Directory, RecordFileName), record);
    }

    private void WriteRegFile(BackupRecord record)
    {
        var regItems = record.Items.OfType<RegistryBackupItem>().ToList();
        if (regItems.Count == 0)
        {
            return;
        }

        try
        {
            RegFileWriter.Save(Path.Combine(record.Directory, "restore.reg"), regItems);
        }
        catch (IOException ex)
        {
            // .reg — только дополнительная копия; основной бэкап хранится в backup.json.
            _log.Warn("Не удалось записать restore.reg", ex);
        }
    }

    private BackupItem Capture(PlannedChange change, BackupRecord record, int index)
    {
        BackupItem item;
        switch (change)
        {
            case RegistryChange rc:
                {
                    var original = _store.ReadValue(rc.Hive, rc.Key, rc.Name);
                    item = new RegistryBackupItem
                    {
                        Hive = rc.Hive,
                        Key = RegPath.Normalize(rc.Key),
                        Name = rc.Name,
                        KeyExisted = _store.KeyExists(rc.Hive, rc.Key),
                        Original = original,
                        OriginalDisplay = DisplayOrAbsent(original),
                        NewDisplay = DisplayOrAbsent(rc.NewValue),
                    };
                    break;
                }

            case FileWriteChange fc:
                {
                    var fi = new FileInfo(fc.Path);
                    var fileItem = new FileBackupItem { OriginalPath = fi.FullName, Existed = fi.Exists };
                    if (fi.Exists)
                    {
                        var filesDir = Path.Combine(record.Directory, "files");
                        Directory.CreateDirectory(filesDir);
                        var stored = index.ToString("D3", CultureInfo.InvariantCulture) + "_" + SafeFileName(fi.Name);
                        File.Copy(fi.FullName, Path.Combine(filesDir, stored), overwrite: true);
                        fileItem.StoredFile = stored;
                        fileItem.Size = fi.Length;
                        fileItem.LastWriteUtc = fi.LastWriteTimeUtc;
                        fileItem.Sha256 = Sha256File(fi.FullName);

                        // Проверяем, что копия совпадает с оригиналом, — иначе такой бэкап бесполезен.
                        if (Sha256File(Path.Combine(filesDir, stored)) != fileItem.Sha256)
                        {
                            throw new IOException("Копия файла не совпадает с оригиналом: " + fi.FullName);
                        }
                    }

                    fileItem.OriginalDisplay = fi.Exists ? Loc.T($"{fi.Length} байт", $"{fi.Length} bytes") : Loc.T("файла не было", "file did not exist");
                    fileItem.NewDisplay = fc.Content.Length > 0 ? Loc.T($"{fc.Content.Length} байт", $"{fc.Content.Length} bytes") : null;
                    item = fileItem;
                    break;
                }

            case PowerSchemeChange pc:
                {
                    var active = _store.GetActivePowerScheme();
                    var schemes = _store.GetPowerSchemes();
                    item = new PowerSchemeBackupItem
                    {
                        PreviousActive = active,
                        PreviousActiveName = SchemeName(schemes, active),
                        OriginalDisplay = SchemeName(schemes, active),
                        NewDisplay = pc.CreateUltimate ? "Ultimate Performance" : SchemeName(schemes, pc.Target),
                    };
                    break;
                }

            default:
                throw new NotSupportedException(change.GetType().Name);
        }

        item.Title = change.Title;
        item.Target = change.TargetDisplay;
        item.RequiresAdmin = change.RequiresAdmin;
        item.RequiresRestart = change.RequiresRestart;
        return item;
    }

    private void Apply(PlannedChange change, BackupItem item, BackupRecord record)
    {
        switch (change)
        {
            case RegistryChange rc when rc.NewValue is null:
                _store.DeleteValue(rc.Hive, rc.Key, rc.Name);
                break;
            case RegistryChange rc:
                _store.WriteValue(rc.Hive, rc.Key, rc.Name, rc.NewValue!);
                break;
            case FileWriteChange fc:
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fc.Path))!);
                var tmp = fc.Path + ".novatune.tmp";
                File.WriteAllBytes(tmp, fc.Content);
                File.Move(tmp, fc.Path, overwrite: true);
                break;
            case PowerSchemeChange pc:
                {
                    var target = pc.Target;
                    if (pc.CreateUltimate)
                    {
                        var created = _store.DuplicatePowerScheme(PowerSchemeInfo.UltimatePerformanceTemplate);
                        ((PowerSchemeBackupItem)item).CreatedScheme = created;

                        // Сохраняем GUID созданной схемы до активации, чтобы откат её нашёл даже после сбоя.
                        Save(record);
                        target = created;
                    }

                    _store.SetActivePowerScheme(target ?? throw new InvalidOperationException("Не задана схема питания."));
                    break;
                }

            default:
                throw new NotSupportedException(change.GetType().Name);
        }
    }

    private bool Verify(PlannedChange change, BackupItem item) => change switch
    {
        RegistryChange rc => rc.NewValue is null
            ? _store.ReadValue(rc.Hive, rc.Key, rc.Name) is null
            : rc.NewValue.ContentEquals(_store.ReadValue(rc.Hive, rc.Key, rc.Name)),
        FileWriteChange fc => File.Exists(fc.Path) && Sha256File(fc.Path) == Sha256Bytes(fc.Content),
        PowerSchemeChange pc => _store.GetActivePowerScheme() == (pc.CreateUltimate ? ((PowerSchemeBackupItem)item).CreatedScheme : pc.Target),
        _ => false,
    };

    private void RestoreItem(BackupItem item, BackupRecord record)
    {
        switch (item)
        {
            case RegistryBackupItem r:
                if (r.Original is null)
                {
                    _store.DeleteValue(r.Hive, r.Key, r.Name);
                    if (!r.KeyExisted)
                    {
                        _store.DeleteKeyIfEmpty(r.Hive, r.Key);
                    }

                    if (_store.ReadValue(r.Hive, r.Key, r.Name) is not null)
                    {
                        throw new InvalidOperationException(Loc.T("Значение не удалилось.", "The value was not deleted."));
                    }
                }
                else
                {
                    _store.WriteValue(r.Hive, r.Key, r.Name, r.Original);
                    if (!r.Original.ContentEquals(_store.ReadValue(r.Hive, r.Key, r.Name)))
                    {
                        throw new InvalidOperationException(Loc.T("После восстановления прочитано другое значение.", "A different value was read back after restore."));
                    }
                }

                break;

            case FileBackupItem f:
                if (f.Existed)
                {
                    var stored = Path.Combine(record.Directory, "files", f.StoredFile ?? throw new InvalidOperationException("Нет копии файла."));
                    if (!File.Exists(stored))
                    {
                        throw new FileNotFoundException(Loc.T("Копия файла в бэкапе не найдена.", "The backup copy is missing."), stored);
                    }

                    if (Sha256File(stored) != f.Sha256)
                    {
                        throw new InvalidOperationException(Loc.T("Копия в бэкапе повреждена (хэш не совпадает).", "The backup copy is corrupted (hash mismatch)."));
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(f.OriginalPath)!);
                    File.Copy(stored, f.OriginalPath, overwrite: true);
                    if (Sha256File(f.OriginalPath) != f.Sha256)
                    {
                        throw new InvalidOperationException(Loc.T("Хэш восстановленного файла не совпадает.", "Restored file hash does not match."));
                    }
                }
                else if (File.Exists(f.OriginalPath))
                {
                    // Файла до операции не было — удаляем созданный приложением.
                    File.Delete(f.OriginalPath);
                }

                break;

            case PowerSchemeBackupItem p:
                {
                    var schemes = _store.GetPowerSchemes();
                    if (p.PreviousActive is { } prev && schemes.Any(s => s.Id == prev))
                    {
                        _store.SetActivePowerScheme(prev);
                    }
                    else if (p.PreviousActive is not null)
                    {
                        throw new InvalidOperationException(Loc.T("Прежняя схема питания больше не существует.", "The previous power scheme no longer exists."));
                    }

                    if (p.CreatedScheme is { } created && schemes.Any(s => s.Id == created))
                    {
                        _store.DeletePowerScheme(created);
                    }

                    if (p.PreviousActive is { } expected && _store.GetActivePowerScheme() != expected)
                    {
                        throw new InvalidOperationException(Loc.T("Схема питания не переключилась.", "The power scheme did not switch."));
                    }

                    break;
                }

            default:
                throw new NotSupportedException(item.GetType().Name);
        }
    }

    internal static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string Sha256Bytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return clean.Length > 80 ? clean[..80] : clean;
    }

    private static string DisplayOrAbsent(RegValue? value) =>
        value is null ? Loc.T("(нет значения)", "(not set)") : $"{value.KindName} {value.Display()}";

    private static string SchemeName(IReadOnlyList<PowerSchemeInfo> schemes, Guid? id) =>
        id is null ? Loc.T("неизвестно", "unknown") : schemes.FirstOrDefault(s => s.Id == id)?.Name ?? id.Value.ToString();

    private static string FriendlyError(Exception ex) => ex switch
    {
        ElevationCancelledException => Loc.T("Запрос прав администратора отклонён — шаг не выполнен.", "Administrator request was declined — step not performed."),
        UnauthorizedAccessException => Loc.T("Недостаточно прав: ", "Access denied: ") + ex.Message,
        _ => ex.Message,
    };
}
