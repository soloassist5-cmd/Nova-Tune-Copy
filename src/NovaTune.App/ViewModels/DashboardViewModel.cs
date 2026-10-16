using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaTune.App.Services;
using NovaTune.Core.Analysis;
using NovaTune.Core.Hardware;
using NovaTune.Core.Localization;
using NovaTune.Core.Optimization;
using NovaTune.Core.Steam;
using NovaTune.Platform;

namespace NovaTune.App.ViewModels;

/// <summary>Главная: оценка состояния и ключевые показатели, которые считываются за несколько секунд.</summary>
public sealed partial class DashboardViewModel(MainViewModel main) : PageViewModel, IUsesMonitoring
{
    public override string Title => Loc.T("Главная", "Dashboard");

    public override string Subtitle => Loc.T("Состояние системы и ключевые показатели", "System status and key metrics");

    public MainViewModel Main => main;

    [ObservableProperty] private string _osLine = "";
    [ObservableProperty] private string _lastScanText = "";
    [ObservableProperty] private string _profileText = "";

    // Оценка
    [ObservableProperty] private int _score;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private CriterionLevel _statusLevel = CriterionLevel.Good;
    [ObservableProperty] private bool _hasHealth;
    [ObservableProperty] private bool _isScanning;
    public ObservableCollection<HealthCriterion> Criteria { get; } = [];
    public ObservableCollection<string> NotEvaluated { get; } = [];

    // CPU
    [ObservableProperty] private string _cpuName = "";
    [ObservableProperty] private string _cpuLoad = Fmt.Dash;
    [ObservableProperty] private string _cpuClock = Fmt.Dash;
    [ObservableProperty] private string _cpuTemp = Fmt.Dash;
    [ObservableProperty] private string? _cpuTempReason;
    [ObservableProperty] private double[]? _cpuHistory;

    // GPU
    [ObservableProperty] private string _gpuName = "";
    [ObservableProperty] private string _gpuLoad = Fmt.Dash;
    [ObservableProperty] private string? _gpuLoadReason;
    [ObservableProperty] private string _gpuTemp = Fmt.Dash;
    [ObservableProperty] private string? _gpuTempReason;
    [ObservableProperty] private double[]? _gpuHistory;

    // RAM
    [ObservableProperty] private string _ramUsed = Fmt.Dash;
    [ObservableProperty] private string _ramTotal = "";
    [ObservableProperty] private string _ramPercent = Fmt.Dash;
    [ObservableProperty] private double[]? _ramHistory;

    // Диск
    [ObservableProperty] private string _diskTitle = "";
    [ObservableProperty] private string _diskFree = Fmt.Dash;
    [ObservableProperty] private string _diskDetail = "";
    [ObservableProperty] private double _diskUsedPercent;
    [ObservableProperty] private string _diskType = "";

    // Автозагрузка
    [ObservableProperty] private string _startupEnabled = Fmt.Dash;
    [ObservableProperty] private string _startupDetail = "";

    protected override async Task OnNavigatedToAsync(bool firstTime)
    {
        var os = OsInfo.Current;
        OsLine = $"{Environment.MachineName} · {os.Name} {os.DisplayVersion} · {Loc.T("сборка", "build")} {os.FullBuild}";
        ProfileText = Loc.T("Профиль CS2: ", "CS2 profile: ") + S.Settings.Current.Cs2Profile;
        LastScanText = Fmt.Ago(S.Settings.Current.LastScan);

        S.Monitoring.Sampled += OnSampled;
        S.Monitoring.Start(S.Settings.Current.AdvancedSensors);
        if (S.Monitoring.Last is { } last)
        {
            Apply(last);
        }

        if (firstTime)
        {
            await ScanAsync(refreshHardware: false);
        }
    }

    protected override void OnNavigatedFrom()
    {
        S.Monitoring.Sampled -= OnSampled;
    }

    [RelayCommand]
    private Task Diagnose() => ScanAsync(refreshHardware: true);

    private async Task ScanAsync(bool refreshHardware)
    {
        if (IsScanning)
        {
            return;
        }

        IsScanning = true;
        IsLoading = !HasHealth;
        ErrorText = null;
        try
        {
            var result = await SystemScan.RunAsync(refreshHardware, PageCts.Token);
            ApplyScan(result);
            if (refreshHardware)
            {
                S.Settings.Current.LastScan = result.Time;
                S.Settings.Save();
                S.Journal.Add("scan", Loc.T("Диагностика системы", "System diagnostics"), true, Loc.T($"оценка {result.Health.Score}/100", $"score {result.Health.Score}/100"));
                S.Toasts.Show(Loc.T("Диагностика завершена", "Diagnostics complete"), DialogKind.Success);
            }

            LastScanText = Fmt.Ago(S.Settings.Current.LastScan ?? result.Time);
        }
        catch (OperationCanceledException)
        {
            // Ушли со страницы — сканирование продолжится при следующем открытии.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            S.Log.Error("Ошибка диагностики", ex);
            ErrorText = Loc.T("Диагностика не выполнена: ", "Diagnostics failed: ") + ex.Message;
        }
        finally
        {
            IsScanning = false;
            IsLoading = false;
        }
    }

    private void ApplyScan(ScanResult r)
    {
        var hw = r.Hardware.Summary;
        CpuName = hw.CpuName ?? Loc.T("Процессор", "Processor");
        GpuName = hw.PrimaryGpu?.Name ?? Loc.T("Видеокарта", "Graphics");
        RamTotal = hw.RamTotalBytes is { } total ? Loc.T("из ", "of ") + Units.Bytes(total) : "";

        if (hw.SystemVolume is { } vol)
        {
            DiskTitle = Loc.T($"Системный диск {vol.Letter}:", $"System drive {vol.Letter}:");
            DiskFree = Units.Bytes(vol.FreeBytes);
            DiskDetail = Loc.T($"свободно из {Units.Bytes(vol.TotalBytes)}", $"free of {Units.Bytes(vol.TotalBytes)}");
            DiskUsedPercent = vol.UsedPercent;
            DiskType = vol.IsSsd switch { true => "SSD", false => "HDD", _ => "" };
        }
        else
        {
            DiskTitle = Loc.T("Системный диск", "System drive");
            DiskDetail = Loc.T("Недоступно — том не найден", "Unavailable — volume not found");
        }

        var enabled = r.Facts.EnabledStartupCount ?? 0;
        var toggleable = r.Startup.Count(e => e.CanToggle);
        StartupEnabled = enabled.ToString(System.Globalization.CultureInfo.CurrentCulture);
        StartupDetail = Loc.T($"включено из {toggleable} записей", $"enabled of {toggleable} entries");

        Score = r.Health.Score;
        StatusText = r.Health.StatusText;
        StatusLevel = r.Health.Level;
        Criteria.Clear();
        foreach (var c in r.Health.Criteria)
        {
            Criteria.Add(c);
        }

        NotEvaluated.Clear();
        foreach (var n in r.Health.NotEvaluated)
        {
            NotEvaluated.Add(n);
        }

        HasHealth = true;
    }

    private void OnSampled(object? sender, MonitoringSnapshot snap) =>
        Application.Current?.Dispatcher.InvokeAsync(() => Apply(snap));

    private void Apply(MonitoringSnapshot snap)
    {
        CpuLoad = Fmt.Percent(snap.CpuLoadPercent);
        CpuClock = Fmt.Mhz(snap.CpuClockMhz);
        CpuTemp = Fmt.Temp(snap.CpuTemperatureC);
        CpuTempReason = Fmt.Reason(snap.CpuTemperatureC);
        CpuHistory = S.Monitoring.CpuLoad.ToArray();

        var gpu = snap.Gpus.FirstOrDefault();
        GpuLoad = gpu is null ? Fmt.Dash : Fmt.Percent(gpu.LoadPercent);
        GpuLoadReason = gpu is null ? null : Fmt.Reason(gpu.LoadPercent);
        GpuTemp = gpu is null ? Fmt.Dash : Fmt.Temp(gpu.TemperatureC);
        GpuTempReason = gpu is null ? null : Fmt.Reason(gpu.TemperatureC);
        GpuHistory = S.Monitoring.GpuLoad.ToArray();

        RamUsed = Fmt.Bytes(snap.RamUsedBytes);
        RamPercent = Fmt.Percent(snap.RamUsedPercent);
        RamHistory = S.Monitoring.RamLoad.ToArray();
    }

    [RelayCommand]
    private void Open(string key) => main.Navigate(key);

    /// <summary>
    /// «Готов к матчу?»: одна проверка того, что чаще всего мешает в игре.
    /// Мониторы перечитываются заново (частоту могли сменить), остальное — свежие показания.
    /// </summary>
    [RelayCommand]
    private Task MatchCheck() => RunSafeAsync(async () =>
    {
        IsScanning = true;
        try
        {
            var hw = (await S.Hardware.GetAsync(false, PageCts.Token)).Summary with { Monitors = HardwareService.ReadMonitors() };
            var context = await S.GetContextAsync();
            var samples = Math.Max(1, 60_000 / S.Monitoring.IntervalMs);
            var steam = SteamLocator.FindSteamPath();
            var cs2 = await Task.Run(() => Cs2Locator.Locate(steam, S.Settings.Current.Cs2ManualPath, S.Log));
            VolumeSummary? gameVolume = cs2 is not null && Path.GetPathRoot(cs2.GameDir) is { Length: > 0 } root
                ? hw.Volumes.FirstOrDefault(v => char.ToUpperInvariant(v.Letter) == char.ToUpperInvariant(root[0]))
                : hw.SystemVolume;
            var checks = Cs2Advisor.Readiness(new Cs2ReadinessInput(
                hw,
                gameVolume,
                new GameModeModule().ReadState(S.Store, context).IsRecommendedState,
                S.Store.GetActivePowerScheme() is { } a ? a == Core.Common.PowerSchemeInfo.PowerSaver : null,
                new GameDvrModule().ReadState(S.Store, context).IsRecommendedState,
                S.Monitoring.CpuLoad.Count >= samples / 2 ? S.Monitoring.CpuLoad.AverageOfLast(samples) : null,
                S.Monitoring.Last?.RamUsedPercent.Value)).ToList();
            if (DiagnosticsService.PendingReboot() is true)
            {
                checks.Add(new ReadinessCheck(Loc.T("Ожидающая перезагрузка", "Pending restart"), CriterionLevel.Warning, Loc.T("Windows ждёт перезагрузки — лучше перезагрузиться до матча.", "Windows is waiting for a restart — better restart before the match.")));
            }

            var bad = checks.Count(c => c.Level == CriterionLevel.Bad);
            var warn = checks.Count(c => c.Level == CriterionLevel.Warning);
            var lines = checks
                .OrderByDescending(c => c.Level)
                .Select(c => (c.Level switch { CriterionLevel.Good => "✓ ", CriterionLevel.Warning => "⚠ ", _ => "✗ " }) + c.Title + " — " + c.Detail)
                .ToList();
            if (cs2 is null)
            {
                lines.Add(Loc.T("ⓘ CS2 не найдена — место проверено на системном диске.", "ⓘ CS2 not found — free space checked on the system drive."));
            }

            S.Journal.Add("match-check", Loc.T("Проверка «Готов к матчу?»", "«Ready for a match?» check"), bad == 0, Loc.T($"проблем: {bad}, предупреждений: {warn}", $"problems: {bad}, warnings: {warn}"));
            await S.Dialogs.AlertAsync(
                bad == 0 && warn == 0 ? Loc.T("Готов к матчу", "Ready for a match") : bad == 0 ? Loc.T("Почти готов", "Almost ready") : Loc.T("Есть что исправить", "Some things to fix"),
                bad == 0 && warn == 0
                    ? Loc.T("Всё, что можно проверить, в порядке. Удачной игры!", "Everything that can be checked is fine. Good luck!")
                    : Loc.T($"Проблем: {bad}, предупреждений: {warn}. Подробности ниже — большинство исправляется в разделах «Оптимизация» и «Инструменты».", $"Problems: {bad}, warnings: {warn}. Details below — most can be fixed in Optimize and Tools."),
                bad == 0 && warn == 0 ? DialogKind.Success : bad == 0 ? DialogKind.Warning : DialogKind.Error,
                new TextLines(lines));
        }
        finally
        {
            IsScanning = false;
        }
    }, Loc.T("Проверка не выполнена", "Check failed"));
}

/// <summary>Страница использует мониторинг: пока она открыта, датчики не приостанавливаются.</summary>
public interface IUsesMonitoring;
