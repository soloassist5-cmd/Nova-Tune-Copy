using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaTune.App.Services;
using NovaTune.Core.Hardware;
using NovaTune.Core.Localization;
using NovaTune.Core.Tools;
using NovaTune.Platform;

namespace NovaTune.App.ViewModels;

public sealed record MonitorRow(string Name, string Mode, string Verdict, bool IsProblem);

public sealed record StatRow(string Name, string Average, string Max);

/// <summary>
/// Собственные инструменты NOVA TUNE: частота монитора, частота опроса мыши, запись игровой сессии.
/// Только измерения — системные настройки здесь не меняются.
/// </summary>
public sealed partial class ToolsViewModel : PageViewModel, IUsesMonitoring
{
    private RawMouseInput? _mouse;
    private readonly PollingRateMeter _meter = new(Stopwatch.Frequency);
    private DateTime _lastUi;

    public override string Title => Loc.T("Инструменты", "Tools");

    public override string Subtitle => Loc.T("Проверки, которые часто выявляют реальные проблемы в играх. Только измерения.", "Checks that often reveal real gaming issues. Measurements only.");

    public ObservableCollection<MonitorRow> Monitors { get; } = [];

    public ObservableCollection<StatRow> SessionStats { get; } = [];

    [ObservableProperty] private bool _isMouseTesting;
    [ObservableProperty] private string _mouseHz = Fmt.Dash;
    [ObservableProperty] private string _mousePeak = "";
    [ObservableProperty] private string _mouseHint = Loc.T("Нажмите «Начать тест» и быстро двигайте мышью по кругу 3–5 секунд.", "Press Start test and move the mouse quickly in circles for 3–5 seconds.");

    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string _recordStatus = Loc.T("Запись не идёт", "Not recording");

    private SessionRecorder Recorder => S.Recorder;

    protected override async Task OnNavigatedToAsync(bool firstTime)
    {
        IsRecording = Recorder.IsRecording;
        await LoadMonitorsAsync();
    }

    protected override void OnNavigatedFrom()
    {
        StopMouse();
    }

    [RelayCommand]
    private async Task LoadMonitorsAsync()
    {
        var hw = await S.Hardware.GetAsync(refresh: true, PageCts.Token);
        Monitors.Clear();
        foreach (var m in hw.Summary.Monitors)
        {
            var verdict = m.MaxRefreshHz is null
                ? Loc.T("Не удалось получить список режимов", "Could not read the display mode list")
                : m.BelowMaxRefresh
                    ? Loc.T($"Работает ниже возможного: доступно {m.MaxRefreshHz} Гц. В играх это ограничивает плавность и задержку вывода.", $"Running below capability: {m.MaxRefreshHz} Hz is available. In games this limits smoothness and display latency.")
                    : Loc.T("Работает на максимальной частоте для этого разрешения.", "Running at the maximum refresh rate for this resolution.");
            Monitors.Add(new MonitorRow(m.Name, $"{m.Width}×{m.Height} @ {m.RefreshHz} Hz", verdict, m.BelowMaxRefresh));
        }
    }

    [RelayCommand]
    private static void OpenDisplaySettings() => ChangeRunner.OpenUrl("ms-settings:display-advanced");

    // ───────── Частота опроса мыши ─────────

    [RelayCommand]
    private void ToggleMouse()
    {
        if (IsMouseTesting)
        {
            StopMouse();
            return;
        }

        try
        {
            _meter.Reset();
            _mouse = new RawMouseInput(Application.Current.MainWindow, OnMouseMove);
            IsMouseTesting = true;
            MouseHz = "0";
            MousePeak = "";
            MouseHint = Loc.T("Двигайте мышью… Окно NOVA TUNE должно быть активным.", "Move the mouse… The NOVA TUNE window must be active.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            S.Log.Error("Raw Input не зарегистрирован", ex);
            MouseHint = Loc.T("Не удалось подписаться на Raw Input: ", "Failed to register Raw Input: ") + ex.Message;
        }
    }

    private void OnMouseMove(long tick)
    {
        _meter.Add(tick);
        if ((DateTime.UtcNow - _lastUi).TotalMilliseconds < 150)
        {
            return;
        }

        _lastUi = DateTime.UtcNow;
        MouseHz = _meter.CurrentHz.ToString(CultureInfo.CurrentCulture) + " Hz";
        if (_meter.PeakHz > 0)
        {
            MousePeak = Loc.T(
                $"Пик: {_meter.PeakHz} Гц → вероятная настройка мыши: {PollingRateMeter.NearestStandard(_meter.PeakHz)} Гц",
                $"Peak: {_meter.PeakHz} Hz → likely mouse setting: {PollingRateMeter.NearestStandard(_meter.PeakHz)} Hz");
        }
    }

    private void StopMouse()
    {
        _mouse?.Dispose();
        _mouse = null;
        if (IsMouseTesting)
        {
            IsMouseTesting = false;
            MouseHint = _meter.Samples > 50
                ? Loc.T("Тест остановлен. Значения выше 2000 Гц Windows может доставлять неравномерно — ориентируйтесь на пик.", "Test stopped. Above 2000 Hz Windows may deliver reports unevenly — rely on the peak value.")
                : Loc.T("Слишком мало данных — двигайте мышью активнее.", "Not enough data — move the mouse more.");
        }
    }

    // ───────── Запись сессии ─────────

    [RelayCommand]
    private void ToggleRecording()
    {
        if (Recorder.IsRecording)
        {
            Recorder.Stop();
            IsRecording = false;
            var duration = DateTimeOffset.Now - Recorder.StartedAt;
            RecordStatus = Loc.T($"Записано {Recorder.Rows} строк за {Units.Duration(duration)}: {Recorder.FilePath}", $"Recorded {Recorder.Rows} rows in {Units.Duration(duration)}: {Recorder.FilePath}");
            SessionStats.Clear();
            foreach (var (name, stat) in Recorder.Stats)
            {
                SessionStats.Add(new StatRow(
                    name,
                    stat.Count == 0 ? Loc.T("нет данных", "no data") : stat.Average.ToString("0.0", CultureInfo.CurrentCulture),
                    stat.Count == 0 ? Fmt.Dash : stat.Max.ToString("0.0", CultureInfo.CurrentCulture)));
            }

            S.Journal.Add("session", Loc.T("Запись сессии", "Session recording"), true, Recorder.FilePath);
            return;
        }

        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NOVA TUNE");
        Recorder.Start(folder);
        IsRecording = true;
        SessionStats.Clear();
        RecordStatus = Loc.T("Идёт запись. Можно свернуть NOVA TUNE и играть — запись продолжится.", "Recording. You can minimise NOVA TUNE and play — recording continues.");
    }

    [RelayCommand]
    private void OpenSessions() => ChangeRunner.OpenFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NOVA TUNE"));
}
