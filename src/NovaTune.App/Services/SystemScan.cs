using NovaTune.Core.Analysis;
using NovaTune.Core.Common;
using NovaTune.Core.Hardware;
using NovaTune.Core.Optimization;
using NovaTune.Core.Startup;
using NovaTune.Platform;

namespace NovaTune.App.Services;

/// <summary>Результат сканирования: факты, оценка и автозагрузка. Используется на Dashboard и в Optimize.</summary>
public sealed record ScanResult(
    HardwareReport Hardware,
    SystemFacts Facts,
    HealthResult Health,
    IReadOnlyList<StartupEntry> Startup,
    SystemContext Context,
    DateTimeOffset Time);

public static class SystemScan
{
    public static async Task<ScanResult> RunAsync(bool refreshHardware, CancellationToken ct)
    {
        var s = AppServices.Current;
        var hw = await s.Hardware.GetAsync(refreshHardware, ct);
        var context = await s.GetContextAsync(refreshHardware, ct);
        var startup = await s.Startup.GetEntriesAsync(ct);
        var facts = await Task.Run(() => BuildFacts(hw, startup, context, s.Store, s.Monitoring), ct);
        var health = HealthScore.Evaluate(facts, DateTime.Now);
        return new ScanResult(hw, facts, health, startup, context, DateTimeOffset.Now);
    }

    public static SystemFacts BuildFacts(HardwareReport hw, IReadOnlyList<StartupEntry> startup, SystemContext context, ISystemSettingsStore store, MonitoringService monitoring)
    {
        var gameMode = new GameModeModule().ReadState(store, context).IsRecommendedState;
        var dvrOff = new GameDvrModule().ReadState(store, context).IsRecommendedState;

        // Средняя загрузка за последнюю минуту — только если мониторинг успел накопить хотя бы 30 секунд.
        var samplesPerMinute = Math.Max(1, 60_000 / monitoring.IntervalMs);
        var cpuAvg = monitoring.CpuLoad.Count >= samplesPerMinute / 2 ? monitoring.CpuLoad.AverageOfLast(samplesPerMinute) : null;

        return new SystemFacts
        {
            SystemVolume = hw.Summary.SystemVolume,
            RamUsedPercent = monitoring.Last?.RamUsedPercent.Value ?? MemoryPercentNow(),
            CpuLoadAverage = cpuAvg,
            EnabledStartupCount = startup.Count(e => e.Enabled == true && e.Source != StartupSource.ScheduledTaskLogon),
            GameModeRecommended = gameMode,
            GameDvrOff = dvrOff,
            PowerSaverActive = store.GetActivePowerScheme() is { } active ? active == PowerSchemeInfo.PowerSaver : null,
            PendingReboot = DiagnosticsService.PendingReboot(),
            GpuDriverDate = hw.Summary.PrimaryGpu?.DriverDate,
            IsLaptop = hw.Summary.IsLaptop,
        };
    }

    private static double? MemoryPercentNow()
    {
        var info = GC.GetGCMemoryInfo();
        return info.TotalAvailableMemoryBytes > 0 && info.MemoryLoadBytes > 0
            ? 100.0 * info.MemoryLoadBytes / info.TotalAvailableMemoryBytes
            : null;
    }
}
