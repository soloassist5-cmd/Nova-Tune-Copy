using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NovaTune.App.ViewModels;
using NovaTune.Core.Hardware;
using NovaTune.Core.Localization;
using NovaTune.Core.Settings;
using NovaTune.Platform.Recording;

namespace NovaTune.App.Services;

/// <summary>
/// Центр записи экрана: запуск/остановка записи, прозрачный оверлей поверх игр и горячие клавиши.
/// Живёт всё время работы приложения, поэтому запись и оверлей не зависят от открытой страницы.
/// </summary>
public sealed partial class CaptureController : ObservableObject, IDisposable
{
    private readonly ScreenRecorder _recorder;
    private readonly DispatcherTimer _tick;
    private OverlayWindow? _overlay;
    private bool _statsSubscribed;
    private bool _busy;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private bool _isOverlayVisible;

    [ObservableProperty]
    private string _elapsedText = "00:00";

    [ObservableProperty]
    private string _sizeText = "";

    [ObservableProperty]
    private string _cpuText = "CPU —";

    [ObservableProperty]
    private string _gpuText = "GPU —";

    [ObservableProperty]
    private string _clockText = "";

    [ObservableProperty]
    private string? _currentGame;

    [ObservableProperty]
    private string? _lastFile;

    [ObservableProperty]
    private string _hotkeyStatus = "";

    public CaptureController(AppServices services)
    {
        S = services;
        _recorder = new ScreenRecorder(services.Log);
        _recorder.Failed += (_, message) => Application.Current?.Dispatcher.InvokeAsync(() => OnFailed(message));
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _tick.Tick += (_, _) => OnTick();
    }

    private AppServices S { get; }

    public RecordingSettings Options => S.Settings.Current.Recording;

    /// <summary>Записи добавились или удалились — страница обновляет список.</summary>
    public event EventHandler? RecordingsChanged;

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "NOVA TUNE");

    public string Folder => string.IsNullOrWhiteSpace(Options.Folder) ? DefaultFolder : Options.Folder!;

    /// <summary>Нужны ли датчики (оверлей с нагрузкой CPU/GPU) — тогда мониторинг не приостанавливается.</summary>
    public bool NeedsMonitoring => IsOverlayVisible && Options.OverlayShowStats;

    public IReadOnlyList<CaptureMonitor> GetMonitors()
    {
        try
        {
            return ScreenRecorder.GetMonitors();
        }
        catch (Exception ex) when (ex is COMException or SharpGen.Runtime.SharpGenException or DllNotFoundException)
        {
            S.Log.Warn("Не удалось получить список мониторов DXGI", ex);
            return [];
        }
    }

    public CaptureMonitor? SelectedMonitor()
    {
        var monitors = GetMonitors();
        return monitors.FirstOrDefault(m => m.DeviceName == Options.Monitor)
            ?? monitors.FirstOrDefault(m => m.IsPrimary)
            ?? monitors.FirstOrDefault();
    }

    public async Task ToggleRecordingAsync()
    {
        if (IsRecording)
        {
            await StopAsync();
        }
        else
        {
            await StartAsync();
        }
    }

    public async Task StartAsync()
    {
        if (IsRecording || _busy)
        {
            return;
        }

        var monitor = SelectedMonitor();
        if (monitor is null)
        {
            S.Toasts.Show(Loc.T("Не найден монитор для записи.", "No monitor available for capture."), DialogKind.Error);
            return;
        }

        _busy = true;
        try
        {
            CurrentGame = ForegroundApp();
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
            var path = Path.Combine(Folder, $"{CurrentGame ?? "Desktop"}_{stamp}.mp4");

            // Кадров в секунду × пикселей × «бит на пиксель» = битрейт; 1080p60: ~7 / 12 / 19 Мбит/с.
            var bpp = Options.Quality switch { 0 => 0.06, 2 => 0.15, _ => 0.1 };
            var options = new RecordingOptions(monitor, Options.Fps, bpp, Options.CaptureAudio, path);
            await Task.Run(() => _recorder.Start(options));

            IsRecording = true;
            LastFile = path;
            ElapsedText = "00:00";
            SizeText = "";
            _tick.Start();
            if (Options.OverlayWhileRecording)
            {
                ShowOverlay();
            }

            S.Toasts.Show(Loc.T("Запись началась", "Recording started") + (_recorder.HasAudio || !Options.CaptureAudio ? "" : Loc.T(" (без звука: устройство недоступно)", " (no audio: device unavailable)")), DialogKind.Success);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            S.Log.Error("Не удалось начать запись", ex);
            await S.Dialogs.AlertAsync(Loc.T("Запись не началась", "Recording did not start"), ex.Message, DialogKind.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    public async Task StopAsync()
    {
        if (!IsRecording || _busy)
        {
            return;
        }

        _busy = true;
        try
        {
            await Task.Run(_recorder.Stop);
            IsRecording = false;
            if (!IsOverlayVisible || Options.OverlayWhileRecording)
            {
                HideOverlay();
            }

            UpdateTimerState();
            var size = _recorder.FileBytes;
            S.Toasts.Show(Loc.T("Запись сохранена: ", "Recording saved: ") + Path.GetFileName(LastFile) + $" · {Units.Bytes(size)}", DialogKind.Success);
            RecordingsChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _busy = false;
        }
    }

    public void ToggleOverlay()
    {
        if (IsOverlayVisible)
        {
            HideOverlay();
        }
        else
        {
            ShowOverlay();
        }
    }

    public void ShowOverlay()
    {
        _overlay ??= new OverlayWindow(this);
        _overlay.Place(SelectedMonitor()?.DeviceName, Options.OverlayCorner);
        _overlay.Show();
        _overlay.KeepOnTop();
        IsOverlayVisible = true;
        UpdateStatsSubscription();
        UpdateTimerState();
        OnTick();
    }

    public void HideOverlay()
    {
        _overlay?.Hide();
        IsOverlayVisible = false;
        UpdateStatsSubscription();
        UpdateTimerState();
    }

    /// <summary>Настройки оверлея изменились — применяем сразу.</summary>
    public void ApplyOverlaySettings()
    {
        S.Settings.Save();
        if (IsOverlayVisible)
        {
            _overlay?.Place(SelectedMonitor()?.DeviceName, Options.OverlayCorner);
            _overlay?.Refresh();
        }

        UpdateStatsSubscription();
    }

    /// <summary>Горячие клавиши (вызывается из окна приложения).</summary>
    public void OnHotkey(int id)
    {
        if (id == HotkeyService.RecordId)
        {
            _ = ToggleRecordingAsync();
        }
        else if (id == HotkeyService.OverlayId)
        {
            ToggleOverlay();
        }
    }

    private void UpdateTimerState()
    {
        if (IsRecording || IsOverlayVisible)
        {
            _tick.Start();
        }
        else
        {
            _tick.Stop();
        }
    }

    private void UpdateStatsSubscription()
    {
        var need = NeedsMonitoring;
        if (need && !_statsSubscribed)
        {
            S.Monitoring.Sampled += OnSampled;
            S.Monitoring.Start(S.Settings.Current.AdvancedSensors);
            _statsSubscribed = true;
        }
        else if (!need && _statsSubscribed)
        {
            S.Monitoring.Sampled -= OnSampled;
            _statsSubscribed = false;

            // Если текущая страница датчики не показывает — освобождаем их, как при обычной навигации.
            if (Application.Current?.MainWindow?.DataContext is MainViewModel { CurrentPage: not IUsesMonitoring } && !S.Recorder.IsRecording)
            {
                S.Monitoring.Suspend();
            }
        }
    }

    private void OnSampled(object? sender, MonitoringSnapshot s)
    {
        var gpu = s.Gpus.FirstOrDefault();
        var cpu = "CPU " + Pct(s.CpuLoadPercent) + Temp(s.CpuTemperatureC);
        var gpuText = gpu is null ? "GPU —" : "GPU " + Pct(gpu.LoadPercent) + Temp(gpu.TemperatureC);
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            CpuText = cpu;
            GpuText = gpuText;
        });
    }

    private static string Pct(Reading r) => r.Value is { } v ? $"{v:0}%" : "—";

    private static string Temp(Reading r) => r.Value is { } v ? $" · {v:0}°" : "";

    private void OnTick()
    {
        ClockText = DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        if (IsRecording)
        {
            var e = _recorder.Elapsed;
            ElapsedText = e.TotalHours >= 1 ? e.ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture) : e.ToString(@"mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
            SizeText = Units.Bytes(_recorder.FileBytes);

            // Поток записи мог завершиться сам (например, ошибка драйвера) — синхронизируем состояние.
            if (!_recorder.IsRecording && !_busy)
            {
                IsRecording = false;
                UpdateTimerState();
            }
        }

        _overlay?.KeepOnTop();
    }

    private void OnFailed(string message)
    {
        IsRecording = false;
        if (Options.OverlayWhileRecording)
        {
            HideOverlay();
        }

        UpdateTimerState();
        S.Toasts.Show(Loc.T("Запись остановлена: ", "Recording stopped: ") + message, DialogKind.Error);
        RecordingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Имя приложения на переднем плане (обычно игра) — для имени файла. Своё окно не считается.</summary>
    private static string? ForegroundApp()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == Environment.ProcessId)
            {
                return null;
            }

            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            if (name is "explorer" or "ShellExperienceHost" or "SearchHost" or "StartMenuExperienceHost")
            {
                return null;
            }

            var sb = new StringBuilder();
            foreach (var ch in name)
            {
                sb.Append(Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch);
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _tick.Stop();
        if (_recorder.IsRecording)
        {
            _recorder.Stop();
        }

        _overlay?.Close();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
