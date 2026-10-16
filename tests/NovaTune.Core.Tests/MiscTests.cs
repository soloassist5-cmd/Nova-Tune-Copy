using NovaTune.Core.Analysis;
using NovaTune.Core.Backup;
using NovaTune.Core.Cleanup;
using NovaTune.Core.Common;
using NovaTune.Core.Elevation;
using NovaTune.Core.Frametime;
using NovaTune.Core.Hardware;
using NovaTune.Core.Localization;
using NovaTune.Core.Logging;
using NovaTune.Core.Network;
using NovaTune.Core.Optimization;
using NovaTune.Core.Settings;
using NovaTune.Core.Startup;

namespace NovaTune.Core.Tests;

public sealed class StartupApprovedTests
{
    [Fact]
    public void MissingValue_MeansEnabled() => Assert.True(StartupApproved.IsEnabled(null));

    [Theory]
    [InlineData((byte)0x02, true)]
    [InlineData((byte)0x06, true)]
    [InlineData((byte)0x03, false)]
    [InlineData((byte)0x07, false)]
    public void FirstByteParity_DefinesState(byte first, bool enabled)
    {
        var bytes = new byte[12];
        bytes[0] = first;
        Assert.Equal(enabled, StartupApproved.IsEnabled(RegValue.Binary(bytes)));
    }

    [Fact]
    public void Disabled_StoresFiletime()
    {
        var when = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var value = StartupApproved.Disabled(when);

        Assert.False(StartupApproved.IsEnabled(value));
        Assert.Equal(when, StartupApproved.DisabledAtUtc(value));
        Assert.True(StartupApproved.IsEnabled(StartupApproved.Enabled()));
    }

    [Fact]
    public void RunOnceAndTasks_CannotBeToggled()
    {
        Assert.Null(StartupApproved.LocationFor(StartupSource.HkcuRunOnce, "x"));
        Assert.Null(StartupApproved.LocationFor(StartupSource.ScheduledTaskLogon, "x"));
        Assert.Equal(RegHive.LocalMachine, StartupApproved.LocationFor(StartupSource.HklmRun32, "x")!.Hive);
    }

    [Fact]
    public void PlannedToggle_IsAppliedThroughSandbox()
    {
        var store = new InMemorySettingsStore();
        var entry = new StartupEntry
        {
            Name = "App",
            Command = "app.exe",
            Source = StartupSource.HkcuRun,
            Approved = StartupApproved.LocationFor(StartupSource.HkcuRun, "App"),
        };

        var change = StartupApproved.PlanToggle(entry, enable: false, DateTime.UtcNow);
        store.WriteValue(change.Hive, change.Key, change.Name, change.NewValue!);

        Assert.False(StartupApproved.IsEnabled(store.ReadValue(RegHive.CurrentUser, StartupApproved.RunKey, "App")));
        Assert.False(change.RequiresAdmin);
    }
}

public sealed class CleanupEngineTests : IDisposable
{
    private readonly TestDir _dir = new();
    private readonly CleanupEngine _engine = new(NullLog.Instance);

    public void Dispose() => _dir.Dispose();

    private CleanupCategory Category(string root, TimeSpan minAge) =>
        new("t", LocText.Same("t"), LocText.Same("t"), [new CleanupRoot(root)], minAge, true);

    private static void Age(string path, TimeSpan age) => File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);

    [Fact]
    public void DeletesOldFiles_KeepsFreshOnes_AndEmptyFoldersRemoved()
    {
        var root = _dir.Combine("temp");
        var old = _dir.WriteFile("temp/sub/old.tmp", "123");
        var fresh = _dir.WriteFile("temp/fresh.tmp", "1");
        Age(old, TimeSpan.FromDays(3));

        var scan = _engine.Scan(Category(root, TimeSpan.FromDays(1)), DateTime.UtcNow);
        Assert.Equal(1, scan.FileCount);
        Assert.Equal(3, scan.TotalBytes);

        var result = _engine.Clean(scan, DateTime.UtcNow);

        Assert.Equal(1, result.DeletedFiles);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.False(Directory.Exists(Path.GetDirectoryName(old)));
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public void DoesNotFollowSymlinkedDirectories()
    {
        var root = _dir.Combine("temp");
        Directory.CreateDirectory(root);
        var precious = _dir.WriteFile("documents/важный.docx", "data");
        Age(precious, TimeSpan.FromDays(10));
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), _dir.Combine("documents"));

        var scan = _engine.Scan(Category(root, TimeSpan.Zero), DateTime.UtcNow);
        _engine.Clean(scan, DateTime.UtcNow);

        Assert.True(File.Exists(precious));
        Assert.DoesNotContain(scan.Files, f => f.Path.Contains("важный", StringComparison.Ordinal));
    }

    [Fact]
    public void FileModifiedAfterScan_IsNotDeleted()
    {
        var root = _dir.Combine("temp");
        var file = _dir.WriteFile("temp/a.tmp", "1");
        Age(file, TimeSpan.FromDays(2));
        var scan = _engine.Scan(Category(root, TimeSpan.FromDays(1)), DateTime.UtcNow);

        File.WriteAllText(file, "now in use");
        var result = _engine.Clean(scan, DateTime.UtcNow);

        Assert.True(File.Exists(file));
        Assert.Equal(1, result.SkippedChanged);
    }

    [Fact]
    public void MissingRoot_IsEmptyScan()
    {
        var scan = _engine.Scan(Category(_dir.Combine("nope"), TimeSpan.Zero), DateTime.UtcNow);
        Assert.Equal(0, scan.FileCount);
    }

    [Fact]
    public void IsInside_RejectsSiblingWithSamePrefix()
    {
        Assert.False(CleanupEngine.IsInside(_dir.Combine("temp2", "a"), _dir.Combine("temp")));
        Assert.True(CleanupEngine.IsInside(_dir.Combine("temp", "a"), _dir.Combine("temp")));
    }
}

public sealed class ModulesTests
{
    private static readonly SystemContext Win10 = new() { OsBuild = 19045, HagsSupported = TriState.Yes };

    [Fact]
    public void GameMode_AbsentValueIsDefaultOn()
    {
        var store = new InMemorySettingsStore();
        Assert.True(new GameModeModule().ReadState(store, Win10).IsRecommendedState);
    }

    [Fact]
    public void SafeOptimize_PlansOnlySafeUserLevelModules()
    {
        var store = new InMemorySettingsStore();
        store.WriteValue(RegHive.CurrentUser, KnownKeys.GameBar, KnownKeys.AutoGameModeEnabled, RegValue.DWord(0));

        var plan = OptimizationCatalog.PlanSafeOptimize(OptimizationCatalog.CreateAll(), store, Win10);

        Assert.Contains(plan, p => p.Module.Id == "game-mode");
        Assert.Contains(plan, p => p.Module.Id == "game-dvr");
        Assert.DoesNotContain(plan, p => p.Module.Id is "hags" or "power-plan");
        Assert.All(plan.SelectMany(p => p.Changes), c => Assert.False(c.RequiresAdmin));
    }

    [Fact]
    public void Hags_UnavailableOnOldBuildOrUnsupportedDriver()
    {
        var hags = new HagsModule();
        Assert.False(hags.CheckAvailability(new SystemContext { OsBuild = 18363 }).IsAvailable);
        Assert.False(hags.CheckAvailability(new SystemContext { OsBuild = 19045, HagsSupported = TriState.No }).IsAvailable);
        Assert.True(hags.CheckAvailability(Win10).IsAvailable);
        Assert.All(hags.Plan(new InMemorySettingsStore(), Win10, "enable"), c => Assert.True(c.RequiresAdmin && c.RequiresRestart));
    }

    [Fact]
    public void PowerPlan_UltimateNotOfferedOnLaptop()
    {
        var store = new InMemorySettingsStore();
        var module = new PowerPlanModule();
        Assert.Contains(module.GetOptions(store, Win10), o => o.Id == "create-ultimate");
        Assert.DoesNotContain(module.GetOptions(store, Win10 with { IsLaptop = true }), o => o.Id == "create-ultimate");
    }
}

public sealed class ElevatedTaskValidatorTests
{
    [Fact]
    public void AllowsHagsValues_RejectsOthers()
    {
        ElevatedTaskRequest Hags(uint v) => new()
        {
            Operation = ElevatedOperation.RegistrySet,
            Hive = RegHive.LocalMachine,
            Key = KnownKeys.GraphicsDrivers,
            Name = KnownKeys.HwSchMode,
            Value = RegValue.DWord(v),
        };

        Assert.True(ElevatedTaskValidator.Validate(Hags(2)).Ok);
        Assert.True(ElevatedTaskValidator.Validate(Hags(1)).Ok);
        Assert.False(ElevatedTaskValidator.Validate(Hags(0)).Ok);
    }

    [Fact]
    public void RejectsArbitraryRegistryKeys()
    {
        var request = new ElevatedTaskRequest
        {
            Operation = ElevatedOperation.RegistrySet,
            Hive = RegHive.LocalMachine,
            Key = @"SOFTWARE\Policies\Microsoft\Windows Defender",
            Name = "DisableAntiSpyware",
            Value = RegValue.DWord(1),
        };

        Assert.False(ElevatedTaskValidator.Validate(request).Ok);
    }

    [Fact]
    public void StartupApproved_RequiresWellFormedBinary()
    {
        ElevatedTaskRequest R(RegValue v) => new()
        {
            Operation = ElevatedOperation.RegistrySet,
            Hive = RegHive.LocalMachine,
            Key = StartupApproved.RunKey,
            Name = "App",
            Value = v,
        };

        Assert.True(ElevatedTaskValidator.Validate(R(StartupApproved.Disabled(DateTime.UtcNow))).Ok);
        Assert.False(ElevatedTaskValidator.Validate(R(RegValue.DWord(3))).Ok);
        Assert.False(ElevatedTaskValidator.Validate(R(RegValue.Binary([0xFF, 0, 0, 0, 0, 0, 0, 0]))).Ok);
    }

    [Fact]
    public void Volume_RequiresDriveLetter()
    {
        Assert.True(ElevatedTaskValidator.Validate(new ElevatedTaskRequest { Operation = ElevatedOperation.VolumeReTrim, DriveLetter = 'C' }).Ok);
        Assert.False(ElevatedTaskValidator.Validate(new ElevatedTaskRequest { Operation = ElevatedOperation.VolumeReTrim, DriveLetter = '1' }).Ok);
        Assert.False(ElevatedTaskValidator.Validate(new ElevatedTaskRequest { Operation = ElevatedOperation.VolumeReTrim }).Ok);
    }
}

public sealed class HealthScoreTests
{
    [Fact]
    public void UnknownFacts_AreExcluded_NotPenalised()
    {
        var result = HealthScore.Evaluate(new SystemFacts { RamUsedPercent = 40 }, DateTime.Now);

        Assert.Equal(100, result.Score);
        Assert.Single(result.Criteria);
        Assert.NotEmpty(result.NotEvaluated);
    }

    [Fact]
    public void LowDiskSpace_LowersScore_WithRecommendation()
    {
        var volume = new VolumeSummary('C', null, 100UL << 30, 3UL << 30, true, "NTFS");
        var result = HealthScore.Evaluate(new SystemFacts { SystemVolume = volume, RamUsedPercent = 40 }, DateTime.Now);

        var disk = result.Criteria.Single(c => c.Id == "disk");
        Assert.Equal(0, disk.Earned);
        Assert.NotNull(disk.Recommendation);
        Assert.True(result.Score < 50);
    }

    [Fact]
    public void NoFacts_IsNotEnoughData()
    {
        var result = HealthScore.Evaluate(new SystemFacts(), DateTime.Now);
        Assert.Equal(0, result.Score);
        Assert.Empty(result.Criteria);
    }
}

public sealed class FrametimeTests
{
    [Fact]
    public void ParsesPresentMonCsv_UsingMainProcess()
    {
        var lines = new List<string> { "Application,ProcessID,MsBetweenPresents" };
        for (var i = 0; i < 990; i++)
        {
            lines.Add("cs2.exe,1,5.0");
        }

        for (var i = 0; i < 10; i++)
        {
            lines.Add("cs2.exe,1,20.0");
        }

        lines.Add("dwm.exe,2,16.6");

        var stats = FrametimeLogParser.Parse(lines, "log.csv");

        Assert.Equal(1000, stats.Frames);
        Assert.Equal(50.0, stats.OnePercentLowFps, 3);
        Assert.Equal(1000.0 * 1000 / (990 * 5 + 10 * 20), stats.AverageFps, 3);
        Assert.Contains("cs2.exe", stats.Source);
    }

    [Fact]
    public void ParsesCapFrameXWithMetadataLines()
    {
        var lines = new List<string> { "//CapFrameX", "GameName,Counter-Strike 2", "TimeInSeconds,MsBetweenPresents" };
        lines.AddRange(Enumerable.Range(0, 200).Select(i => $"{i * 0.004},4.0"));

        var stats = FrametimeLogParser.Parse(lines, "cx.csv");

        Assert.Equal(250.0, stats.AverageFps, 3);
    }

    [Fact]
    public void RejectsUnknownFormatAndTooShortLogs()
    {
        Assert.Throws<FormatException>(() => FrametimeLogParser.Parse(["a,b", "1,2"], "x"));
        Assert.Throws<FormatException>(() => FrametimeLogParser.Parse(["MsBetweenPresents", "5"], "x"));
    }
}

public sealed class PingStatsTests
{
    [Fact]
    public void ComputesLossAndJitter()
    {
        var stats = PingStats.From("host", "1.1.1.1", [10, 20, null, 30]);

        Assert.Equal(25, stats.LossPercent);
        Assert.Equal(20, stats.AvgMs);
        Assert.Equal(10, stats.JitterMs);
        Assert.Equal(10, stats.MinMs);
        Assert.Equal(30, stats.MaxMs);
    }

    [Fact]
    public void NoReplies_HasNoAverages()
    {
        var stats = PingStats.From("host", null, [null, null]);
        Assert.Equal(100, stats.LossPercent);
        Assert.Null(stats.AvgMs);
    }
}

public sealed class RegFileWriterTests
{
    [Fact]
    public void WritesValuesAndDeletions()
    {
        var text = RegFileWriter.Build(
        [
            new RegistryBackupItem { Hive = RegHive.CurrentUser, Key = @"Software\A", Name = "Dw", Original = RegValue.DWord(255) },
            new RegistryBackupItem { Hive = RegHive.CurrentUser, Key = @"Software\A", Name = "Gone", Original = null },
            new RegistryBackupItem { Hive = RegHive.LocalMachine, Key = @"SYSTEM\B", Name = "S", Original = RegValue.String("C:\\x \"y\"") },
        ]);

        Assert.StartsWith("Windows Registry Editor Version 5.00", text);
        Assert.Contains("[HKEY_CURRENT_USER\\Software\\A]", text);
        Assert.Contains("\"Dw\"=dword:000000ff", text);
        Assert.Contains("\"Gone\"=-", text);
        Assert.Contains("\"S\"=\"C:\\\\x \\\"y\\\"\"", text);
    }
}

public sealed class SettingsTests : IDisposable
{
    private readonly TestDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void RoundTrip_AndClampsInterval()
    {
        var path = _dir.Combine("settings.json");
        var service = new SettingsService(path, NullLog.Instance);
        service.Current.Language = AppLanguage.En;
        service.Current.MonitoringIntervalMs = 50;
        service.Save();

        var loaded = new SettingsService(path, NullLog.Instance).Load();
        Assert.Equal(AppLanguage.En, loaded.Language);
        Assert.Equal(500, loaded.MonitoringIntervalMs);
    }

    [Fact]
    public void BrokenFile_FallsBackToDefaults_AndKeepsCopy()
    {
        var path = _dir.WriteFile("settings.json", "{ broken");
        var loaded = new SettingsService(path, NullLog.Instance).Load();

        Assert.Equal(AppLanguage.Ru, loaded.Language);
        Assert.True(File.Exists(path + ".broken"));
    }
}

public sealed class JournalAndLogTests : IDisposable
{
    private readonly TestDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Journal_ReadsNewestFirst_SkipsCorruptLines()
    {
        var path = _dir.Combine("journal.jsonl");
        var journal = new OperationJournal(path, NullLog.Instance);
        journal.Add("a", "first", true);
        File.AppendAllText(path, "{not json\n");
        journal.Add("a", "second", false, "details");

        var entries = journal.ReadRecent();

        Assert.Equal(["second", "first"], entries.Select(e => e.Title).ToArray());
    }

    [Fact]
    public void FileLog_Rotates()
    {
        var log = new FileLog(_dir.Path, maxBytes: 200, maxFiles: 2);
        for (var i = 0; i < 50; i++)
        {
            log.Info(new string('x', 50));
        }

        Assert.True(File.Exists(Path.Combine(_dir.Path, "novatune.1.log")));
        Assert.False(File.Exists(Path.Combine(_dir.Path, "novatune.3.log")));
        Assert.True(new FileInfo(log.CurrentFile).Length < 400);
    }
}

public sealed class MeasurementTests
{
    [Fact]
    public void PollingRate_1000Hz()
    {
        var meter = new NovaTune.Core.Tools.PollingRateMeter(1_000_000);
        for (long t = 0; t <= 2_000_000; t += 1000)
        {
            meter.Add(t);
        }

        Assert.InRange(meter.CurrentHz, 995, 1005);
        Assert.Equal(1000, NovaTune.Core.Tools.PollingRateMeter.NearestStandard(meter.PeakHz));
    }

    [Fact]
    public void PollingRate_PauseRestartsWindow()
    {
        var meter = new NovaTune.Core.Tools.PollingRateMeter(1_000_000);
        for (long t = 0; t <= 500_000; t += 8000)
        {
            meter.Add(t);
        }

        // Пауза 2 секунды, затем движение с той же частотой — частота не должна «проседать» из-за паузы.
        for (long t = 2_500_000; t <= 3_000_000; t += 8000)
        {
            meter.Add(t);
        }

        Assert.InRange(meter.CurrentHz, 120, 130);
    }

    [Fact]
    public void ColumnStats_IgnoresNaN()
    {
        var s = new NovaTune.Core.Tools.ColumnStats();
        s.Add(10);
        s.Add(double.NaN);
        s.Add(30);
        Assert.Equal(20, s.Average);
        Assert.Equal(30, s.Max);
        Assert.Equal(2, s.Count);
    }
}

public sealed class Cs2ToolsTests
{
    [Fact]
    public void CmPer360_KnownValue()
    {
        // 800 DPI, sens 1.0 в CS2: 360 / (0.022 * 800) * 2.54 ≈ 51.95 см.
        Assert.Equal(51.95, NovaTune.Core.Tools.SensitivityCalculator.CmPer360(800, 1.0), 2);
        Assert.Equal(800, NovaTune.Core.Tools.SensitivityCalculator.Edpi(800, 1.0));
    }

    [Fact]
    public void ValorantToCs2_Ratio()
    {
        // Valorant 0.35 → CS2 ≈ 1.1136 (коэффициент 3.1818).
        Assert.Equal(1.1136, NovaTune.Core.Tools.SensitivityCalculator.ToCs2(0.35, 0.07), 3);
    }

    [Fact]
    public void Crosshair_CommandsAreClampedAndInvariant()
    {
        var text = new NovaTune.Core.Tools.CrosshairSettings { Size = 500, Gap = -3.5 }.ToCommands(singleLine: true);
        Assert.Contains("cl_crosshairsize 100", text);
        Assert.Contains("cl_crosshairgap -3.5", text);
        Assert.DoesNotContain(",", text);
    }

    [Fact]
    public void Practice_LaunchUrlRejectsUnknownMap()
    {
        Assert.StartsWith("steam://run/730//", NovaTune.Core.Tools.PracticeConfig.LaunchUrl("de_mirage"));
        Assert.Throws<ArgumentException>(() => NovaTune.Core.Tools.PracticeConfig.LaunchUrl("de_mirage; quit"));
        Assert.Contains("sv_infinite_ammo 1", NovaTune.Core.Tools.PracticeConfig.Build(true, true, true, true));
    }

    [Fact]
    public void Reaction_IgnoresInvalidAndAveragesLast5()
    {
        var r = new NovaTune.Core.Tools.ReactionStats();
        foreach (var v in new double[] { -1, 300, 200, 250, 210, 190, 180, 99999 })
        {
            r.Add(v);
        }

        Assert.Equal(180, r.Best);
        Assert.Equal((200 + 250 + 210 + 190 + 180) / 5.0, r.AverageLast5);
    }
}
