using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NovaTune.App.Services;
using NovaTune.Core.Hardware;
using NovaTune.Core.Localization;
using NovaTune.Platform.Recording;

namespace NovaTune.App.ViewModels;

public sealed record ChoiceItem(int Value, string Name);

/// <summary>Файл записи в списке «Недавние записи».</summary>
public sealed class RecordingFile(FileInfo file)
{
    public string Path { get; } = file.FullName;

    public string Name { get; } = System.IO.Path.GetFileNameWithoutExtension(file.Name);

    public string Details { get; } = file.LastWriteTime.ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture) + "  ·  " + Units.Bytes(file.Length);
}

/// <summary>Страница «Запись»: запись экрана со звуком, оверлей поверх игр, горячие клавиши, список записей.</summary>
public sealed partial class RecordingViewModel : PageViewModel
{
    private bool _loading;

    [ObservableProperty]
    private CaptureMonitor? _monitor;

    [ObservableProperty]
    private ChoiceItem? _fps;

    [ObservableProperty]
    private ChoiceItem? _quality;

    [ObservableProperty]
    private ChoiceItem? _corner;

    [ObservableProperty]
    private bool _captureAudio;

    [ObservableProperty]
    private bool _overlayWhileRecording;

    [ObservableProperty]
    private bool _overlayShowStats;

    [ObservableProperty]
    private bool _overlayShowClock;

    [ObservableProperty]
    private string _folder = "";

    [ObservableProperty]
    private string _estimate = "";

    public RecordingViewModel()
    {
        Capture = S.Capture;
        Capture.RecordingsChanged += (_, _) => LoadRecordings();
        Capture.PropertyChanged += OnCaptureChanged;

        FpsChoices = [new(30, "30 FPS"), new(60, "60 FPS")];
        QualityChoices =
        [
            new(0, Loc.T("Экономно — меньше файл", "Eco — smaller files")),
            new(1, Loc.T("Стандарт", "Standard")),
            new(2, Loc.T("Высокое — для монтажа", "High — for editing")),
        ];
        CornerChoices =
        [
            new(0, Loc.T("Справа сверху", "Top right")),
            new(1, Loc.T("Слева сверху", "Top left")),
            new(2, Loc.T("Справа снизу", "Bottom right")),
            new(3, Loc.T("Слева снизу", "Bottom left")),
        ];
    }

    public override string Title => Loc.T("Запись", "Capture");

    public override string Subtitle => Loc.T(
        "Запись экрана со звуком в MP4 (H.264, аппаратный кодер видеокарты) и прозрачная плашка поверх игр",
        "Screen recording with sound to MP4 (H.264, GPU hardware encoder) and a transparent overlay above games");

    public CaptureController Capture { get; }

    public ObservableCollection<CaptureMonitor> Monitors { get; } = [];

    public IReadOnlyList<ChoiceItem> FpsChoices { get; }

    public IReadOnlyList<ChoiceItem> QualityChoices { get; }

    public IReadOnlyList<ChoiceItem> CornerChoices { get; }

    public ObservableCollection<RecordingFile> Recordings { get; } = [];

    public bool HasRecordings => Recordings.Count > 0;

    public string RecordButtonText => Capture.IsRecording ? Loc.T("Остановить запись", "Stop recording") : Loc.T("Начать запись", "Start recording");

    public string OverlayButtonText => Capture.IsOverlayVisible ? Loc.T("Скрыть оверлей", "Hide overlay") : Loc.T("Показать оверлей", "Show overlay");

    protected override Task OnNavigatedToAsync(bool firstTime)
    {
        _loading = true;
        var o = Capture.Options;
        Monitors.Clear();
        foreach (var m in Capture.GetMonitors())
        {
            Monitors.Add(m);
        }

        Monitor = Capture.SelectedMonitor() is { } sel ? Monitors.FirstOrDefault(m => m.DeviceName == sel.DeviceName) : null;
        Fps = FpsChoices.FirstOrDefault(c => c.Value == o.Fps) ?? FpsChoices[1];
        Quality = QualityChoices.FirstOrDefault(c => c.Value == o.Quality) ?? QualityChoices[1];
        Corner = CornerChoices.FirstOrDefault(c => c.Value == o.OverlayCorner) ?? CornerChoices[0];
        CaptureAudio = o.CaptureAudio;
        OverlayWhileRecording = o.OverlayWhileRecording;
        OverlayShowStats = o.OverlayShowStats;
        OverlayShowClock = o.OverlayShowClock;
        Folder = Capture.Folder;
        _loading = false;
        UpdateEstimate();
        LoadRecordings();
        return Task.CompletedTask;
    }

    private void OnCaptureChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CaptureController.IsRecording))
        {
            OnPropertyChanged(nameof(RecordButtonText));
        }
        else if (e.PropertyName == nameof(CaptureController.IsOverlayVisible))
        {
            OnPropertyChanged(nameof(OverlayButtonText));
        }
    }

    partial void OnMonitorChanged(CaptureMonitor? value) => Save(o => o.Monitor = value?.DeviceName, overlay: true);

    partial void OnFpsChanged(ChoiceItem? value) => Save(o => o.Fps = value?.Value ?? 60);

    partial void OnQualityChanged(ChoiceItem? value) => Save(o => o.Quality = value?.Value ?? 1);

    partial void OnCornerChanged(ChoiceItem? value) => Save(o => o.OverlayCorner = value?.Value ?? 0, overlay: true);

    partial void OnCaptureAudioChanged(bool value) => Save(o => o.CaptureAudio = value);

    partial void OnOverlayWhileRecordingChanged(bool value) => Save(o => o.OverlayWhileRecording = value);

    partial void OnOverlayShowStatsChanged(bool value) => Save(o => o.OverlayShowStats = value, overlay: true);

    partial void OnOverlayShowClockChanged(bool value) => Save(o => o.OverlayShowClock = value, overlay: true);

    private void Save(Action<NovaTune.Core.Settings.RecordingSettings> change, bool overlay = false)
    {
        if (_loading)
        {
            return;
        }

        change(Capture.Options);
        if (overlay)
        {
            Capture.ApplyOverlaySettings();
        }
        else
        {
            S.Settings.Save();
        }

        UpdateEstimate();
    }

    /// <summary>Примерный размер файла: битрейт кодера × минута (+ звук 192 кбит/с).</summary>
    private void UpdateEstimate()
    {
        var m = Monitor;
        if (m is null || Fps is null || Quality is null)
        {
            Estimate = "";
            return;
        }

        var bpp = Quality.Value switch { 0 => 0.06, 2 => 0.15, _ => 0.1 };
        var bitrate = Math.Clamp((m.Width & ~1) * (m.Height & ~1) * Fps.Value * bpp, 2_000_000, 80_000_000);
        var perMinute = (bitrate + (CaptureAudio ? 192_000 : 0)) / 8 * 60;
        Estimate = Loc.T("≈ ", "≈ ") + (bitrate / 1_000_000).ToString("0.#", CultureInfo.CurrentCulture) + Loc.T(" Мбит/с · ", " Mbps · ")
            + Units.Bytes(perMinute) + Loc.T(" за минуту", " per minute");
    }

    private void LoadRecordings()
    {
        Recordings.Clear();
        try
        {
            var dir = new DirectoryInfo(Capture.Folder);
            if (dir.Exists)
            {
                foreach (var f in dir.EnumerateFiles("*.mp4").OrderByDescending(f => f.LastWriteTimeUtc).Take(40))
                {
                    Recordings.Add(new RecordingFile(f));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            S.Log.Warn("Не удалось прочитать папку записей", ex);
        }

        OnPropertyChanged(nameof(HasRecordings));
    }

    [RelayCommand]
    private Task ToggleRecord() => Capture.ToggleRecordingAsync();

    [RelayCommand]
    private void ToggleOverlay() => Capture.ToggleOverlay();

    [RelayCommand]
    private void RefreshList() => LoadRecordings();

    [RelayCommand]
    private void ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Папка для записей", "Recordings folder"), InitialDirectory = Capture.Folder };
        if (dialog.ShowDialog() == true)
        {
            Capture.Options.Folder = dialog.FolderName;
            S.Settings.Save();
            Folder = Capture.Folder;
            LoadRecordings();
        }
    }

    [RelayCommand]
    private void ResetFolder()
    {
        Capture.Options.Folder = null;
        S.Settings.Save();
        Folder = Capture.Folder;
        LoadRecordings();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        Directory.CreateDirectory(Capture.Folder);
        Shell(Capture.Folder);
    }

    [RelayCommand]
    private void Play(RecordingFile? file)
    {
        if (file is not null)
        {
            Shell(file.Path);
        }
    }

    [RelayCommand]
    private void Reveal(RecordingFile? file)
    {
        if (file is not null && File.Exists(file.Path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.Path}\"") { UseShellExecute = true })?.Dispose();
        }
    }

    [RelayCommand]
    private async Task Delete(RecordingFile? file)
    {
        if (file is null)
        {
            return;
        }

        if (Capture.IsRecording && string.Equals(Capture.LastFile, file.Path, StringComparison.OrdinalIgnoreCase))
        {
            S.Toasts.Show(Loc.T("Эта запись ещё идёт.", "This recording is still in progress."), DialogKind.Warning);
            return;
        }

        if (!await S.Dialogs.ConfirmAsync(Loc.T("Удалить запись?", "Delete recording?"), file.Name + Loc.T("\nФайл будет перемещён в корзину.", "\nThe file will be moved to the Recycle Bin."), Loc.T("Удалить", "Delete"), danger: true))
        {
            return;
        }

        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            S.Log.Warn("Не удалось удалить запись", ex);
            S.Toasts.Show(ex.Message, DialogKind.Error);
        }

        LoadRecordings();
    }

    private void Shell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            S.Log.Warn("Не удалось открыть " + path, ex);
            S.Toasts.Show(ex.Message, DialogKind.Error);
        }
    }
}
