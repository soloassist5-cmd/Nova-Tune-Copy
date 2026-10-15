using System.Net.Http;
using Microsoft.Win32;
using NovaTune.Core.Backup;
using NovaTune.Core.Cleanup;
using NovaTune.Core.Common;
using NovaTune.Core.Hardware;
using NovaTune.Core.Logging;
using NovaTune.Core.Network;
using NovaTune.Core.Optimization;
using NovaTune.Core.Settings;
using NovaTune.Platform;

namespace NovaTune.App.Services;

/// <summary>
/// Композиция сервисов приложения (простая ручная DI: зависимостей немного, контейнер не нужен).
/// </summary>
public sealed class AppServices : IDisposable
{
    private MonitoringService? _monitoring;

    public AppServices(AppPaths paths, FileLog log)
    {
        Paths = paths;
        Log = log;
        Settings = new SettingsService(paths.SettingsFile, log);
        Settings.Load();
        Journal = new OperationJournal(paths.JournalFile, log);
        Elevation = new ElevationService(paths, log);
        Store = new RegistrySettingsStore(Elevation);
        Backup = new BackupService(Settings.Current.BackupDirectory ?? paths.DefaultBackups, Store, Journal, log);
        Storage = new StorageService(log);
        Hardware = new HardwareService(Storage, log);
        Startup = new StartupService(Store, log);
        Diagnostics = new DiagnosticsService(log);
        Processes = new ProcessService(log);
        Network = new NetworkDiagnostics();
        Cleanup = new CleanupEngine(log);
        Modules = OptimizationCatalog.CreateAll();
        Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public static AppServices Current { get; set; } = null!;

    public AppPaths Paths { get; }

    public FileLog Log { get; }

    public SettingsService Settings { get; }

    public OperationJournal Journal { get; }

    public ElevationService Elevation { get; }

    public RegistrySettingsStore Store { get; }

    public BackupService Backup { get; }

    public StorageService Storage { get; }

    public HardwareService Hardware { get; }

    public StartupService Startup { get; }

    public DiagnosticsService Diagnostics { get; }

    public ProcessService Processes { get; }

    public NetworkDiagnostics Network { get; }

    public CleanupEngine Cleanup { get; }

    public IReadOnlyList<IOptimizationModule> Modules { get; }

    public HttpClient Http { get; }

    public DialogService Dialogs { get; } = new();

    public ToastService Toasts { get; } = new();

    /// <summary>Мониторинг создаётся при первом обращении (нужна базовая частота CPU).</summary>
    public MonitoringService Monitoring => _monitoring ??= new MonitoringService(Log, ReadCpuBaseMhz())
    {
        IntervalMs = Settings.Current.MonitoringIntervalMs,
    };

    private SystemContext? _context;

    /// <summary>Контекст системы для модулей оптимизации (сборка ОС, ноутбук, HAGS). Кэшируется.</summary>
    public async Task<SystemContext> GetContextAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (_context is not null && !refresh)
        {
            return _context;
        }

        var hw = await Hardware.GetAsync(refresh: false, ct).ConfigureAwait(false);
        var (hagsSupported, hagsEnabled) = await Task.Run(() => HagsProbe.Query(Log), ct).ConfigureAwait(false);
        _context = new SystemContext
        {
            OsBuild = OsInfo.Current.Build,
            IsLaptop = hw.Summary.IsLaptop,
            OnBattery = hw.Summary.OnBattery,
            HagsSupported = hagsSupported,
            HagsEnabled = hagsEnabled,
        };
        return _context;
    }

    /// <summary>Номинальная частота CPU из реестра (~MHz) — быстро и без WMI.</summary>
    private int ReadCpuBaseMhz()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("~MHz") is int mhz ? mhz : 0;
        }
        catch (System.Security.SecurityException ex)
        {
            Log.Warn("Не удалось прочитать частоту CPU из реестра", ex);
            return 0;
        }
    }

    public void Dispose()
    {
        _monitoring?.Dispose();
        Http.Dispose();
    }
}
