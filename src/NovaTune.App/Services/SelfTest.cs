using System.Text;
using NovaTune.Core.Cleanup;
using NovaTune.Core.Common;
using NovaTune.Core.Logging;
using NovaTune.Core.Optimization;
using NovaTune.Core.Steam;
using NovaTune.Platform;

namespace NovaTune.App.Services;

/// <summary>
/// Режим --self-test: только чтение реальных данных системы (характеристики, датчики, автозагрузка, CS2,
/// состояние модулей) и запись отчёта. Ничего не меняет. Нужен для проверки на чистой машине и в CI.
/// </summary>
public static class SelfTest
{
    public static int Run(string outputPath, ILog log)
    {
        var sb = new StringBuilder();
        var failures = 0;

        void Section(string title, Action action)
        {
            sb.AppendLine().AppendLine("=== " + title + " ===");
            try
            {
                action();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures++;
                sb.AppendLine("FAIL: " + ex);
                log.Error("Self-test: " + title, ex);
            }
        }

        var paths = AppPaths.Default();
        var elevation = new ElevationService(paths, log);
        var store = new RegistrySettingsStore(elevation);
        var storage = new StorageService(log);

        sb.AppendLine($"NOVA TUNE self-test {DateTimeOffset.Now:O}");
        Section("OS", () =>
        {
            var os = OsInfo.Current;
            sb.AppendLine($"{os.Name} {os.DisplayVersion} build {os.FullBuild} {os.Architecture}, elevated={OsInfo.IsElevated()}");
        });

        Core.Hardware.HardwareReport? hw = null;
        Section("Hardware", () =>
        {
            hw = new HardwareService(storage, log).GetAsync(true, CancellationToken.None).GetAwaiter().GetResult();
            sb.Append(hw.ToPlainText());
            sb.AppendLine($"Summary: laptop={hw.Summary.IsLaptop} onBattery={hw.Summary.OnBattery} gpus={hw.Summary.Gpus.Count} volumes={hw.Summary.Volumes.Count}");
        });

        Section("Monitoring (5 samples)", () =>
        {
            using var monitoring = new MonitoringService(log, hw?.Summary.CpuBaseMhz ?? 0) { IntervalMs = 1000 };
            var samples = new List<Core.Hardware.MonitoringSnapshot>();
            monitoring.Sampled += (_, s) =>
            {
                lock (samples)
                {
                    samples.Add(s);
                }
            };
            monitoring.Start(advancedSensors: OsInfo.IsElevated());
            Thread.Sleep(5500);
            monitoring.Suspend();
            foreach (var s in samples)
            {
                var gpu = s.Gpus.FirstOrDefault();
                sb.AppendLine($"{s.Time:HH:mm:ss} cpu={s.CpuLoadPercent.Value:0.0}% clock={s.CpuClockMhz.Value:0} temp={s.CpuTemperatureC.Value?.ToString("0") ?? "n/a (" + s.CpuTemperatureC.Reason + ")"} ram={s.RamUsedPercent.Value:0.0}% gpu[{gpu?.Name}]={gpu?.LoadPercent.Value:0.0}% gpuTemp={gpu?.TemperatureC.Value?.ToString("0") ?? "n/a (" + gpu?.TemperatureC.Reason + ")"}");
            }

            sb.AppendLine($"PawnIO installed: {MonitoringService.IsPawnIoInstalled()}; advanced reason: {monitoring.AdvancedSensorsReason}");
        });

        Section("Power", () =>
        {
            var active = store.GetActivePowerScheme();
            foreach (var p in store.GetPowerSchemes())
            {
                sb.AppendLine($"{(p.Id == active ? "*" : " ")} {p.Id} {p.Name}");
            }
        });

        Section("Optimization modules (read-only)", () =>
        {
            var (hagsSupported, hagsEnabled) = HagsProbe.Query(log);
            var context = new SystemContext
            {
                OsBuild = OsInfo.Current.Build,
                IsLaptop = hw?.Summary.IsLaptop ?? false,
                OnBattery = hw?.Summary.OnBattery ?? false,
                HagsSupported = hagsSupported,
                HagsEnabled = hagsEnabled,
            };
            sb.AppendLine($"HAGS supported={hagsSupported} enabled={hagsEnabled}");
            foreach (var module in OptimizationCatalog.CreateAll())
            {
                var availability = module.CheckAvailability(context);
                var state = module.ReadState(store, context);
                sb.AppendLine($"{module.Id}: available={availability.IsAvailable} ({availability.Reason}) state='{state.CurrentDisplay}' recommended={state.IsRecommendedState}");
            }
        });

        Section("Startup", () =>
        {
            foreach (var e in new StartupService(store, log).GetEntries(CancellationToken.None))
            {
                sb.AppendLine($"[{e.Source}] {e.Name} enabled={e.Enabled?.ToString() ?? "n/a"} toggle={e.CanToggle} publisher={e.Publisher}");
            }
        });

        Section("Steam / CS2", () =>
        {
            var steam = SteamLocator.FindSteamPath();
            sb.AppendLine("Steam: " + (steam ?? "not found"));
            if (steam is not null)
            {
                var scanner = new SteamScanner(steam, log);
                sb.AppendLine("Libraries: " + string.Join("; ", scanner.GetLibraries()));
                sb.AppendLine("Apps: " + scanner.GetApps().Count);
            }

            var cs2 = Cs2Locator.Locate(steam, null, log);
            sb.AppendLine(cs2 is null ? "CS2: not found" : $"CS2: {cs2.GameDir} exe={cs2.ExePath} users={cs2.Users.Count}");
        });

        Section("Cleanup scan (no deletion)", () =>
        {
            var engine = new CleanupEngine(log);
            foreach (var category in CleanupCatalog.Create(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetTempPath()))
            {
                var scan = engine.Scan(category, DateTime.UtcNow);
                sb.AppendLine($"{category.Id}: files={scan.FileCount} bytes={scan.TotalBytes} inaccessible={scan.InaccessibleCount}");
            }
        });

        Section("Diagnostics", () =>
        {
            foreach (var section in new DiagnosticsService(log).CollectAsync(CancellationToken.None).GetAwaiter().GetResult())
            {
                foreach (var item in section.Items)
                {
                    sb.AppendLine($"{section.Title} / {item.Label}: {item.DisplayValue}");
                }
            }
        });

        sb.AppendLine().AppendLine($"Failures: {failures}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
        return failures == 0 ? 0 : 1;
    }
}
