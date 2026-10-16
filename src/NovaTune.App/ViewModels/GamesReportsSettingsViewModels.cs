using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NovaTune.App.Services;
using NovaTune.Core.Analysis;
using NovaTune.Core.Backup;
using NovaTune.Core.Frametime;
using NovaTune.Core.Hardware;
using NovaTune.Core.Localization;
using NovaTune.Core.Reports;
using NovaTune.Core.Settings;
using NovaTune.Core.Steam;
using NovaTune.Platform;

namespace NovaTune.App.ViewModels;

// ═══════════════════════════ Reports ═══════════════════════════

/// <summary>Экспорт отчёта (TXT/HTML) и сравнение логов FPS «до/после» (PresentMon, CapFrameX).</summary>
public sealed partial class ReportsViewModel : PageViewModel
{
    private FrametimeStats? _before;
    private FrametimeStats? _after;

    public override string Title => Loc.T("Отчёты", "Reports");

    public override string Subtitle => Loc.T("Отчёт о ПК и выполненных операциях, сравнение FPS по реальным логам", "PC and operations report, FPS comparison from real logs");

    public ObservableCollection<JournalEntry> Journal { get; } = [];

    public ObservableCollection<KeyValuePair<string, string>> Comparison { get; } = [];

    [ObservableProperty] private string _beforeText = Loc.T("не выбран", "not selected");
    [ObservableProperty] private string _afterText = Loc.T("не выбран", "not selected");
    [ObservableProperty] private bool _isBusy;

    protected override Task OnNavigatedToAsync(bool firstTime)
    {
        Journal.Clear();
        foreach (var e in S.Journal.ReadRecent(200))
        {
            Journal.Add(e);
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task ExportTxt() => ExportAsync(false);

    [RelayCommand]
    private Task ExportHtml() => ExportAsync(true);

    private Task ExportAsync(bool html) => RunSafeAsync(async () =>
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"NOVA-TUNE-report-{DateTime.Now:yyyyMMdd-HHmm}",
            DefaultExt = html ? ".html" : ".txt",
            Filter = html ? "HTML|*.html" : "Text|*.txt",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var hw = await S.Hardware.GetAsync(false, PageCts.Token);
            var startup = await S.Startup.GetEntriesAsync(PageCts.Token);
            var context = await S.GetContextAsync();
            var health = HealthScore.Evaluate(SystemScan.BuildFacts(hw, startup, context, S.Store, S.Monitoring), DateTime.Now);
            var diagnostics = await S.Diagnostics.CollectAsync(PageCts.Token);
            var data = new ReportData(
                hw,
                health,
                diagnostics,
                S.Journal.ReadRecent(500),
                _before is not null && _after is not null ? new FrametimeComparison(_before, _after) : null,
                AppInfo.DisplayVersion,
                DateTimeOffset.Now);
            var text = html ? ReportBuilder.BuildHtml(data) : ReportBuilder.BuildText(data);
            await File.WriteAllTextAsync(dialog.FileName, text, new System.Text.UTF8Encoding(true));
            S.Toasts.Show(Loc.T("Отчёт сохранён", "Report saved"), DialogKind.Success);
            ChangeRunner.OpenFolder(System.IO.Path.GetDirectoryName(dialog.FileName)!);
        }
        finally
        {
            IsBusy = false;
        }
    }, Loc.T("Не удалось сохранить отчёт", "Failed to save report"));

    [RelayCommand]
    private void PickBefore() => Pick(true);

    [RelayCommand]
    private void PickAfter() => Pick(false);

    private void Pick(bool before)
    {
        var dialog = new OpenFileDialog { Filter = "CSV (PresentMon, CapFrameX)|*.csv|All|*.*" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var stats = FrametimeLogParser.ParseFile(dialog.FileName);
            var text = $"{stats.Source}: {stats.Frames} {Loc.T("кадров", "frames")}, {stats.AverageFps:0.0} FPS";
            if (before)
            {
                _before = stats;
                BeforeText = text;
            }
            else
            {
                _after = stats;
                AfterText = text;
            }

            UpdateComparison();
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            S.Log.Warn("Не удалось разобрать лог FPS", ex);
            _ = S.Dialogs.AlertAsync(Loc.T("Лог не распознан", "Log not recognized"), ex.Message, DialogKind.Warning);
        }
    }

    private void UpdateComparison()
    {
        Comparison.Clear();
        if (_before is null || _after is null)
        {
            return;
        }

        foreach (var (label, before, after) in ReportBuilder.FrametimeRows(new FrametimeComparison(_before, _after)))
        {
            Comparison.Add(new KeyValuePair<string, string>(label, $"{before}  →  {after}"));
        }
    }
}

// ═══════════════════════════ Settings ═══════════════════════════

public sealed record LicenseRow(string Name, string License);

public sealed partial class SettingsViewModel(MainViewModel main) : PageViewModel
{
    public override string Title => Loc.T("Настройки", "Settings");

    public override string Subtitle => Loc.T("Внешний вид, язык, мониторинг, бэкапы", "Appearance, language, monitoring, backups");

    [ObservableProperty] private string _theme = S.Settings.Current.Theme.ToString();
    [ObservableProperty] private string _language = S.Settings.Current.Language.ToString();
    [ObservableProperty] private bool _advancedSensors = S.Settings.Current.AdvancedSensors;
    [ObservableProperty] private string _backupFolder = S.Backup.BackupRoot;
    [ObservableProperty] private bool _autostart = AutostartService.IsEnabled;
    [ObservableProperty] private bool _autostartMinimized = S.Settings.Current.AutostartMinimized;

    partial void OnAutostartChanged(bool value)
    {
        try
        {
            if (value)
            {
                AutostartService.Enable();
                S.Toasts.Show(Loc.T("NOVA TUNE будет запускаться вместе с Windows", "NOVA TUNE will start with Windows"), DialogKind.Success);
            }
            else
            {
                AutostartService.Disable();
                S.Toasts.Show(Loc.T("Автозапуск выключен", "Autostart disabled"));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            S.Log.Error("Автозапуск: не удалось изменить реестр", ex);
            S.Toasts.Show(Loc.T("Не удалось изменить автозапуск: ", "Could not change autostart: ") + ex.Message, DialogKind.Error);
        }
    }

    partial void OnAutostartMinimizedChanged(bool value)
    {
        S.Settings.Current.AutostartMinimized = value;
        S.Settings.Save();
    }

    public bool IsElevated => OsInfo.IsElevated();

    public string SensorsStatus => Loc.T(
        $"Права администратора: {(OsInfo.IsElevated() ? "да" : "нет")} · драйвер PawnIO: {(MonitoringService.IsPawnIoInstalled() ? "установлен" : "не установлен")}",
        $"Administrator: {(OsInfo.IsElevated() ? "yes" : "no")} · PawnIO driver: {(MonitoringService.IsPawnIoInstalled() ? "installed" : "not installed")}");

    public string Version => AppInfo.DisplayVersion;

    public string Runtime => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + " · " + OsInfo.Current.Name + " " + OsInfo.Current.FullBuild;

    public string LogsFolder => S.Paths.Logs;

    public IReadOnlyList<LicenseRow> Licenses { get; } =
    [
        new("LibreHardwareMonitorLib 0.9.6", "MPL-2.0"),
        new("DiskInfoToolkit, RAMSPDToolkit-NDD, BlackSharp.Core", "MPL-2.0"),
        new("HidSharp", "Apache-2.0"),
        new("CommunityToolkit.Mvvm 8.4", "MIT"),
        new(".NET 10, WPF, System.Management, PerformanceCounter, EventLog, ServiceController", "MIT"),
    ];

    partial void OnThemeChanged(string value)
    {
        if (!Enum.TryParse<AppTheme>(value, out var theme))
        {
            return;
        }

        S.Settings.Current.Theme = theme;
        S.Settings.Save();
        ThemeService.Apply(theme);
    }

    partial void OnLanguageChanged(string value)
    {
        if (!Enum.TryParse<AppLanguage>(value, out var lang) || lang == Loc.Instance.Language)
        {
            return;
        }

        S.Settings.Current.Language = lang;
        S.Settings.Save();
        App.ApplyLanguage(lang);
    }

    partial void OnAdvancedSensorsChanged(bool value)
    {
        S.Settings.Current.AdvancedSensors = value;
        S.Settings.Save();
        if (S.Monitoring.IsRunning)
        {
            S.Monitoring.Restart(value);
        }

        if (value && !OsInfo.IsElevated())
        {
            S.Toasts.Show(Loc.T("Для температуры CPU перезапустите NOVA TUNE от имени администратора", "Restart NOVA TUNE as administrator for CPU temperature"), DialogKind.Warning);
        }
    }

    [RelayCommand]
    private async Task RestartElevated()
    {
        if (!await S.Dialogs.ConfirmAsync(
                Loc.T("Перезапуск от имени администратора", "Restart as administrator"),
                Loc.T("Нужно для температуры CPU через LibreHardwareMonitor и драйвер PawnIO (драйвер устанавливается отдельно с сайта PawnIO). Антивирус может реагировать на загрузку драйвера.", "Needed for CPU temperature via LibreHardwareMonitor and the PawnIO driver (installed separately from the PawnIO website). Antivirus software may react to the driver being loaded."),
                Loc.T("Перезапустить", "Restart"),
                kind: DialogKind.Info))
        {
            return;
        }

        if (!App.RelaunchElevated())
        {
            S.Toasts.Show(Loc.T("Запрос прав администратора отклонён", "Administrator request declined"));
        }
    }

    [RelayCommand]
    private void ChooseBackupFolder()
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Папка для бэкапов", "Backup folder") };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        S.Settings.Current.BackupDirectory = dialog.FolderName;
        S.Settings.Save();
        S.Backup.BackupRoot = dialog.FolderName;
        BackupFolder = dialog.FolderName;
        S.Toasts.Show(Loc.T("Новые бэкапы будут сохраняться в выбранную папку. Старые остаются на прежнем месте.", "New backups will be saved to the selected folder. Old ones stay where they were."), DialogKind.Info);
    }

    [RelayCommand]
    private void OpenLogs() => ChangeRunner.OpenFolder(S.Paths.Logs);

    [RelayCommand]
    private static void OpenPawnIo() => ChangeRunner.OpenUrl("https://pawnio.eu/");

    public MainViewModel Main => main;
}
