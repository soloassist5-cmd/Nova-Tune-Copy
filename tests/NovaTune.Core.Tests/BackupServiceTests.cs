using System.Text;
using NovaTune.Core.Backup;
using NovaTune.Core.Common;
using NovaTune.Core.Logging;
using NovaTune.Core.Optimization;

namespace NovaTune.Core.Tests;

/// <summary>Бэкап и откат проверяются на песочнице (InMemorySettingsStore) и во временных папках.</summary>
public sealed class BackupServiceTests : IDisposable
{
    private const string SandboxKey = @"Software\NovaTune\TestSandbox";
    private readonly TestDir _dir = new();
    private readonly InMemorySettingsStore _store = new();
    private readonly BackupService _service;

    public BackupServiceTests()
    {
        var journal = new OperationJournal(_dir.Combine("journal.jsonl"), NullLog.Instance);
        _service = new BackupService(_dir.Combine("backups"), _store, journal, NullLog.Instance);
    }

    public void Dispose() => _dir.Dispose();

    private static RegistryChange Set(string name, uint value) =>
        new(RegHive.CurrentUser, SandboxKey, name, RegValue.DWord(value)) { Title = name };

    [Fact]
    public async Task Apply_WritesValue_AndRestore_ReturnsOriginal()
    {
        _store.WriteValue(RegHive.CurrentUser, SandboxKey, "A", RegValue.DWord(5));

        var result = await _service.ApplyAsync("test", [Set("A", 1)]);

        Assert.True(result.Success);
        Assert.Equal(1, result.Applied);
        Assert.Equal(1u, _store.ReadValue(RegHive.CurrentUser, SandboxKey, "A")!.AsDWord);
        Assert.Equal(BackupStatus.Completed, result.Backup.Status);

        var restore = await _service.RestoreAsync(result.Backup);

        Assert.True(restore.Success);
        Assert.Equal(5u, _store.ReadValue(RegHive.CurrentUser, SandboxKey, "A")!.AsDWord);
        Assert.Equal(BackupStatus.RolledBack, _service.List().Single().Status);
    }

    [Fact]
    public async Task Restore_DeletesValueAndKey_ThatDidNotExistBefore()
    {
        Assert.False(_store.KeyExists(RegHive.CurrentUser, SandboxKey));

        var result = await _service.ApplyAsync("test", [Set("New", 1)]);
        Assert.True(_store.KeyExists(RegHive.CurrentUser, SandboxKey));

        var item = Assert.IsType<RegistryBackupItem>(result.Backup.Items.Single());
        Assert.Null(item.Original);
        Assert.False(item.KeyExisted);

        await _service.RestoreAsync(result.Backup);

        Assert.Null(_store.ReadValue(RegHive.CurrentUser, SandboxKey, "New"));
        Assert.False(_store.KeyExists(RegHive.CurrentUser, SandboxKey));
    }

    [Fact]
    public async Task Apply_SkipsChangesAlreadyInTargetState()
    {
        _store.WriteValue(RegHive.CurrentUser, SandboxKey, "A", RegValue.DWord(1));
        var writesBefore = _store.WriteCount;

        var result = await _service.ApplyAsync("test", [Set("A", 1)]);

        Assert.Equal(0, result.Applied);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(writesBefore, _store.WriteCount);
    }

    [Fact]
    public async Task FailedStep_IsReported_OtherStepsApplied_AndBackupIsPartial()
    {
        _store.WriteFailure = (hive, key, name) => name == "Bad" ? new ElevationCancelledException() : null;

        var result = await _service.ApplyAsync("test", [Set("Good", 1), Set("Bad", 1)]);

        Assert.False(result.Success);
        Assert.Equal(1, result.Applied);
        Assert.Single(result.Failures);
        Assert.Equal(BackupStatus.PartiallyCompleted, result.Backup.Status);
        Assert.Equal(BackupItemState.Failed, result.Backup.Items[1].State);
        Assert.Contains("администратора", result.Failures[0].Error);
    }

    [Fact]
    public async Task BackupIsPersisted_BeforeChangesAreApplied()
    {
        string? statusSeenDuringWrite = null;
        _store.WriteFailure = (_, _, _) =>
        {
            // В момент записи в реестр бэкап уже должен лежать на диске со статусом InProgress.
            statusSeenDuringWrite = _service.List().Single().Status.ToString();
            return null;
        };

        await _service.ApplyAsync("test", [Set("A", 1)]);

        Assert.Equal(nameof(BackupStatus.InProgress), statusSeenDuringWrite);
    }

    [Fact]
    public async Task InterruptedOperation_IsFoundOnNextStart_AndCanBeRolledBack()
    {
        _store.WriteValue(RegHive.CurrentUser, SandboxKey, "A", RegValue.DWord(7));

        // Имитация выключения ПК посередине: шаг A применён и сохранён, до шага B процесс не дошёл.
        await Assert.ThrowsAsync<SimulatedCrashException>(() => ApplyAndCrashBeforeStepB([Set("A", 1), Set("B", 1)]));

        // «Новый запуск»: новый экземпляр сервиса над той же папкой.
        var journal = new OperationJournal(_dir.Combine("journal.jsonl"), NullLog.Instance);
        var restarted = new BackupService(_dir.Combine("backups"), _store, journal, NullLog.Instance);
        var incomplete = Assert.Single(restarted.FindIncomplete());

        var restore = await restarted.RestoreAsync(incomplete);

        Assert.True(restore.Success);
        Assert.Equal(7u, _store.ReadValue(RegHive.CurrentUser, SandboxKey, "A")!.AsDWord);
        Assert.Null(_store.ReadValue(RegHive.CurrentUser, SandboxKey, "B"));
        Assert.Empty(restarted.FindIncomplete());
    }

    /// <summary>
    /// Исключение из обработчика прогресса бросается вне защищённого блока шага,
    /// поэтому операция обрывается так же, как при аварийном завершении процесса.
    /// </summary>
    private Task ApplyAndCrashBeforeStepB(IReadOnlyList<PlannedChange> changes)
    {
        var progress = new SyncProgress(text =>
        {
            if (text == "B")
            {
                throw new SimulatedCrashException();
            }
        });
        return _service.ApplyAsync("crash", changes, progress);
    }

    [Fact]
    public async Task PartialRestore_RestoresOnlySelectedItems()
    {
        _store.WriteValue(RegHive.CurrentUser, SandboxKey, "A", RegValue.DWord(0));
        _store.WriteValue(RegHive.CurrentUser, SandboxKey, "B", RegValue.DWord(0));
        var result = await _service.ApplyAsync("test", [Set("A", 1), Set("B", 1)]);

        await _service.RestoreAsync(result.Backup, [result.Backup.Items[0].Id]);

        Assert.Equal(0u, _store.ReadValue(RegHive.CurrentUser, SandboxKey, "A")!.AsDWord);
        Assert.Equal(1u, _store.ReadValue(RegHive.CurrentUser, SandboxKey, "B")!.AsDWord);
        Assert.Equal(BackupStatus.PartiallyRolledBack, _service.List().Single().Status);
    }

    [Fact]
    public async Task FileChange_IsBackedUpWithHash_AndRestored()
    {
        var path = _dir.WriteFile("game/cfg/autoexec.cfg", "fps_max 300");
        var change = new FileWriteChange(path, Encoding.UTF8.GetBytes("fps_max 0")) { Title = "cfg" };

        var result = await _service.ApplyAsync("cfg", [change]);
        Assert.Equal("fps_max 0", File.ReadAllText(path));

        var item = Assert.IsType<FileBackupItem>(result.Backup.Items.Single());
        Assert.True(item.Existed);
        Assert.NotNull(item.Sha256);

        await _service.RestoreAsync(result.Backup);
        Assert.Equal("fps_max 300", File.ReadAllText(path));
    }

    [Fact]
    public async Task FileRestore_FailsWhenBackupCopyIsCorrupted()
    {
        var path = _dir.WriteFile("cfg/a.cfg", "original");
        var result = await _service.ApplyAsync("cfg", [new FileWriteChange(path, Encoding.UTF8.GetBytes("new")) { Title = "a" }]);
        var stored = Directory.GetFiles(Path.Combine(result.Backup.Directory, "files")).Single();
        File.WriteAllText(stored, "tampered");

        var restore = await _service.RestoreAsync(result.Backup);

        Assert.False(restore.Success);
        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public async Task CreatedFile_IsDeletedOnRestore()
    {
        var path = _dir.Combine("cfg", "novatune.cfg");
        var result = await _service.ApplyAsync("cfg", [new FileWriteChange(path, Encoding.UTF8.GetBytes("x")) { Title = "n" }]);
        Assert.True(File.Exists(path));

        await _service.RestoreAsync(result.Backup);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task UltimatePowerScheme_IsCreatedAndDeletedOnRestore()
    {
        var before = _store.GetActivePowerScheme();
        var result = await _service.ApplyAsync("power", [new PowerSchemeChange(null, CreateUltimate: true) { Title = "ultimate" }]);

        var item = Assert.IsType<PowerSchemeBackupItem>(result.Backup.Items.Single());
        Assert.NotNull(item.CreatedScheme);
        Assert.Equal(item.CreatedScheme, _store.GetActivePowerScheme());

        await _service.RestoreAsync(result.Backup);

        Assert.Equal(before, _store.GetActivePowerScheme());
        Assert.DoesNotContain(_store.GetPowerSchemes(), s => s.Id == item.CreatedScheme);
    }

    [Fact]
    public void ManualSnapshot_CapturesCurrentValues_AndWritesRegFile()
    {
        _store.WriteValue(RegHive.CurrentUser, KnownKeys.GameBar, KnownKeys.AutoGameModeEnabled, RegValue.DWord(0));
        var context = new SystemContext { OsBuild = 19045 };
        var targets = OptimizationCatalog.CreateAll().SelectMany(m => m.BackupTargets(_store, context)).ToList();

        var record = _service.CreateSnapshot("manual", BackupKind.Manual, targets);

        Assert.Equal(BackupStatus.Snapshot, record.Status);
        var gameMode = record.Items.OfType<RegistryBackupItem>().Single(i => i.Name == KnownKeys.AutoGameModeEnabled);
        Assert.Equal(0u, gameMode.Original!.AsDWord);
        Assert.True(File.Exists(Path.Combine(record.Directory, "restore.reg")));
    }

    [Fact]
    public async Task ConcurrentOperations_AreRejected()
    {
        var gate = new ManualResetEventSlim();
        _store.WriteFailure = (_, _, _) =>
        {
            gate.Wait(TimeSpan.FromSeconds(5));
            return null;
        };

        var first = _service.ApplyAsync("first", [Set("A", 1)]);
        await Task.Delay(100);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ApplyAsync("second", [Set("B", 1)]));
        gate.Set();
        await first;
    }

    [Fact]
    public async Task Delete_RemovesOnlyBackupFolder()
    {
        var result = await _service.ApplyAsync("test", [Set("A", 1)]);
        _service.Delete(result.Backup);
        Assert.Empty(_service.List());

        var outside = new BackupRecord { Directory = _dir.Path, Title = "x" };
        Assert.Throws<InvalidOperationException>(() => _service.Delete(outside));
        Assert.True(Directory.Exists(_dir.Path));
    }

    private sealed class SimulatedCrashException : Exception;

    /// <summary>Синхронный IProgress (стандартный Progress&lt;T&gt; вызывает обработчик асинхронно).</summary>
    private sealed class SyncProgress(Action<string> handler) : IProgress<string>
    {
        public void Report(string value) => handler(value);
    }
}
