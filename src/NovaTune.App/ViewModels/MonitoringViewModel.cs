using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaTune.App.Services;
using NovaTune.Core.Elevation;
using NovaTune.Core.Hardware;

using NovaTune.Core.Localization;
using NovaTune.Platform;

namespace NovaTune.App.ViewModels;

public sealed record IntervalOption(int Ms, string Title);

public sealed record SensorRow(string Name, string Value, string? Reason);

/// <summary>Графики в реальном времени. Таймер и датчики освобождаются при уходе со страницы.</summary>
public sealed partial class MonitoringViewModel : PageViewModel, IUsesMonitoring
{
    public override string Title => Loc.T("Мониторинг", "Monitoring");

    public override string Subtitle => Loc.T("Показания датчиков в реальном времени", "Real-time sensor readings");

    public IReadOnlyList<IntervalOption> Intervals { get; } =
    [
        new(500, "0,5 s"),
        new(1000, "1 s"),
        new(2000, "2 s"),
        new(5000, "5 s"),
    ];

    [ObservableProperty] private IntervalOption? _interval;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private int _capacity = 120;

    [ObservableProperty] private string _cpuLoad = Fmt.Dash;
    [ObservableProperty] private string _cpuClock = Fmt.Dash;
    [ObservableProperty] private string _cpuTemp = Fmt.Dash;
    [ObservableProperty] private string? _cpuTempReason;
    [ObservableProperty] private string _ram = Fmt.Dash;
    [ObservableProperty] private string _gpuLoad = Fmt.Dash;
    [ObservableProperty] private string _gpuTemp = Fmt.Dash;
    [ObservableProperty] private string? _gpuTempReason;

    [ObservableProperty] private double[]? _cpuLoadHistory;
    [ObservableProperty] private double[]? _cpuClockHistory;
    [ObservableProperty] private double[]? _cpuTempHistory;
    [ObservableProperty] private double[]? _ramHistory;
    [ObservableProperty] private double[]? _gpuLoadHistory;
    [ObservableProperty] private double[]? _gpuTempHistory;

    [ObservableProperty] private string? _advancedStatus;
    [ObservableProperty] private bool _isReadingSmart;

    public ObservableCollection<SensorRow> GpuRows { get; } = [];

    public ObservableCollection<SensorRow> DiskRows { get; } = [];

    public ObservableCollection<SensorRow> SmartRows { get; } = [];

    protected override Task OnNavigatedToAsync(bool firstTime)
    {
        Interval ??= Intervals.FirstOrDefault(i => i.Ms == S.Settings.Current.MonitoringIntervalMs) ?? Intervals[1];
        S.Monitoring.Sampled += OnSampled;
        if (!IsPaused)
        {
            S.Monitoring.Start(S.Settings.Current.AdvancedSensors);
        }

        UpdateAdvancedStatus();
        if (S.Monitoring.Last is { } last)
        {
            Apply(last);
        }

        return Task.CompletedTask;
    }

    protected override void OnNavigatedFrom() => S.Monitoring.Sampled -= OnSampled;

    partial void OnIntervalChanged(IntervalOption? value)
    {
        if (value is null)
        {
            return;
        }

        S.Monitoring.IntervalMs = value.Ms;

        // По ширине графика — последние ~2 минуты при 1 с.
        Capacity = Math.Clamp(120_000 / value.Ms, 30, MonitoringService.HistoryLength);
        S.Settings.Current.MonitoringIntervalMs = value.Ms;
        S.Settings.Save();
    }

    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        if (IsPaused)
        {
            S.Monitoring.Stop();
        }
        else
        {
            S.Monitoring.Start(S.Settings.Current.AdvancedSensors);
        }
    }

    private void UpdateAdvancedStatus()
    {
        AdvancedStatus = !S.Settings.Current.AdvancedSensors
            ? Loc.T("Температура CPU и SMART дисков требуют расширенных датчиков: включите их в Настройках (нужны права администратора и драйвер PawnIO).", "CPU temperature and disk SMART need advanced sensors: enable them in Settings (administrator rights and the PawnIO driver are required).")
            : S.Monitoring.AdvancedSensorsReason;
    }

    private void OnSampled(object? sender, MonitoringSnapshot snap) =>
        Application.Current?.Dispatcher.InvokeAsync(() => Apply(snap));

    private void Apply(MonitoringSnapshot snap)
    {
        var m = S.Monitoring;
        CpuLoad = Fmt.Percent(snap.CpuLoadPercent);
        CpuClock = Fmt.Mhz(snap.CpuClockMhz);
        CpuTemp = Fmt.Temp(snap.CpuTemperatureC);
        CpuTempReason = Fmt.Reason(snap.CpuTemperatureC);
        Ram = $"{Fmt.Percent(snap.RamUsedPercent)} · {Fmt.Bytes(snap.RamUsedBytes)} / {Fmt.Bytes(snap.RamTotalBytes)}";
        var gpu = snap.Gpus.FirstOrDefault();
        GpuLoad = gpu is null ? Fmt.Dash : Fmt.Percent(gpu.LoadPercent);
        GpuTemp = gpu is null ? Fmt.Dash : Fmt.Temp(gpu.TemperatureC);
        GpuTempReason = gpu is null ? null : Fmt.Reason(gpu.TemperatureC);

        CpuLoadHistory = m.CpuLoad.ToArray();
        CpuClockHistory = m.CpuClock.ToArray();
        CpuTempHistory = m.CpuTemp.ToArray();
        RamHistory = m.RamLoad.ToArray();
        GpuLoadHistory = m.GpuLoad.ToArray();
        GpuTempHistory = m.GpuTemp.ToArray();

        GpuRows.Clear();
        foreach (var g in snap.Gpus)
        {
            GpuRows.Add(new SensorRow(g.Name, $"{Fmt.Percent(g.LoadPercent)} · {Fmt.Temp(g.TemperatureC)} · {Fmt.Mhz(g.CoreClockMhz)} · VRAM {Fmt.Bytes(g.MemoryUsedBytes)}", Fmt.Reason(g.TemperatureC)));
        }

        DiskRows.Clear();
        foreach (var d in snap.Disks)
        {
            DiskRows.Add(new SensorRow(d.Name, $"{Fmt.Temp(d.TemperatureC)} · {Loc.T("активность", "activity")} {Fmt.Percent(d.ActivePercent)}", Fmt.Reason(d.TemperatureC)));
        }
    }

    /// <summary>SMART через отдельный процесс с правами администратора (один запрос UAC).</summary>
    [RelayCommand]
    private async Task ReadSmart()
    {
        IsReadingSmart = true;
        try
        {
            var result = await S.Elevation.RunAsync(new ElevatedTaskRequest { Operation = ElevatedOperation.ReadStorageReliability });
            SmartRows.Clear();
            if (!result.Success)
            {
                S.Toasts.Show(Loc.T("SMART не прочитан: ", "SMART not read: ") + result.Error, DialogKind.Warning);
                return;
            }

            var rows = System.Text.Json.JsonSerializer.Deserialize<List<DiskReliability>>(result.Output ?? "[]", Core.Common.Json.Options) ?? [];
            foreach (var r in rows)
            {
                SmartRows.Add(new SensorRow(
                    r.DiskName,
                    string.Join(" · ", new[]
                    {
                        r.TemperatureC is { } t ? t.ToString("0", CultureInfo.CurrentCulture) + " °C" : Loc.T("темп. нет", "no temp"),
                        r.WearPercent is { } w ? Loc.T($"износ {w:0}%", $"wear {w:0}%") : null,
                        r.PowerOnHours is { } h ? Loc.T($"{h} ч работы", $"{h} power-on hours") : null,
                        r.ReadErrors is { } re ? Loc.T($"ошибок чтения {re}", $"read errors {re}") : null,
                    }.Where(x => x is not null)),
                    null));
            }

            if (rows.Count == 0)
            {
                SmartRows.Add(new SensorRow(Loc.T("Нет данных", "No data"), Loc.T("Диски не отдают счётчики надёжности (типично для виртуальных и части USB-дисков).", "Disks expose no reliability counters (typical for virtual and some USB drives)."), null));
            }
        }
        catch (Core.Common.ElevationCancelledException)
        {
            S.Toasts.Show(Loc.T("Запрос прав администратора отклонён", "Administrator request declined"), DialogKind.Info);
        }
        finally
        {
            IsReadingSmart = false;
        }
    }
}
