using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NovaTune.App.Services;
using NovaTune.Core.Backup;
using NovaTune.Core.Hardware;
using NovaTune.Core.Localization;
using NovaTune.Core.Optimization;
using NovaTune.Core.Steam;
using NovaTune.Platform;

namespace NovaTune.App.ViewModels;

public sealed record AccountRow(string Name, string ConfigDir, string Files, string LaunchOptions);

public sealed record NoteRow(string Option, string Explanation);

/// <summary>CS2: поиск игры, отчёт о готовности, профили рекомендаций, бэкап конфигов, novatune.cfg.</summary>
public sealed partial class Cs2ViewModel(MainViewModel main) : PageViewModel
{
    private Cs2Installation? _cs2;
    private HardwareSummary? _hw;

    public override string Title => "CS2 Optimizer";

    public override string Subtitle => Loc.T("Counter-Strike 2: готовность системы, рекомендации и конфиги — без обещаний «+N FPS»", "Counter-Strike 2: system readiness, recommendations and configs — no \"+N FPS\" promises");

    [ObservableProperty] private bool _isFound;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _gameDir = "";
    [ObservableProperty] private string _profile = "Competitive";
    [ObservableProperty] private string _fpsMax = "0";

    public ObservableCollection<ReadinessCheck> Readiness { get; } = [];

    public ObservableCollection<Cs2Recommendation> Recommendations { get; } = [];

    public ObservableCollection<AccountRow> Accounts { get; } = [];

    public IReadOnlyList<NoteRow> LaunchNotes { get; } = Cs2Advisor.LaunchOptionNotes().Select(n => new NoteRow(n.Option, n.Explanation)).ToList();

    protected override Task OnNavigatedToAsync(bool firstTime) => firstTime ? LoadAsync() : Task.CompletedTask;

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            Profile = S.Settings.Current.Cs2Profile;
            CrossColor ??= Colors[0];
            SensSource ??= SensGames[0];
            var steam = SteamLocator.FindSteamPath();
            _cs2 = await Task.Run(() => Cs2Locator.Locate(steam, S.Settings.Current.Cs2ManualPath, S.Log), PageCts.Token);
            _hw = (await S.Hardware.GetAsync(false, PageCts.Token)).Summary;
            IsFound = _cs2 is not null;
            GameDir = _cs2?.GameDir ?? "";
            Status = _cs2 is null
                ? (steam is null
                    ? Loc.T("Steam не найден в реестре. Укажите папку CS2 вручную или установите Steam.", "Steam was not found in the registry. Select the CS2 folder manually or install Steam.")
                    : Loc.T("CS2 не найдена в библиотеках Steam. Укажите папку игры вручную.", "CS2 was not found in Steam libraries. Select the game folder manually."))
                : (_cs2.FoundManually ? Loc.T("Найдена по указанной папке", "Found in the selected folder") : Loc.T("Найдена в библиотеке Steam", "Found in a Steam library"))
                  + (_cs2.ExePath is null ? Loc.T(" · cs2.exe не найден", " · cs2.exe not found") : "");

            Accounts.Clear();
            foreach (var u in _cs2?.Users ?? [])
            {
                Accounts.Add(new AccountRow(
                    u.User.PersonaName ?? u.User.AccountName ?? u.User.AccountId.ToString(CultureInfo.InvariantCulture),
                    u.ConfigDir,
                    Loc.T($"файлов конфигурации: {u.Files.Count}", $"config files: {u.Files.Count}"),
                    string.IsNullOrWhiteSpace(u.LaunchOptions) ? Loc.T("параметры запуска не заданы", "no launch options") : u.LaunchOptions));
            }

            await BuildReadinessAsync();
            BuildRecommendations();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task BuildReadinessAsync()
    {
        if (_hw is null)
        {
            return;
        }

        var context = await S.GetContextAsync();
        VolumeSummary? gameVolume = null;
        if (_cs2 is not null && Path.GetPathRoot(_cs2.GameDir) is { Length: > 0 } root)
        {
            gameVolume = _hw.Volumes.FirstOrDefault(v => char.ToUpperInvariant(v.Letter) == char.ToUpperInvariant(root[0]));
        }

        var samples = Math.Max(1, 60_000 / S.Monitoring.IntervalMs);
        var input = new Cs2ReadinessInput(
            _hw,
            gameVolume,
            new GameModeModule().ReadState(S.Store, context).IsRecommendedState,
            S.Store.GetActivePowerScheme() is { } a ? a == Core.Common.PowerSchemeInfo.PowerSaver : null,
            new GameDvrModule().ReadState(S.Store, context).IsRecommendedState,
            S.Monitoring.CpuLoad.Count >= samples / 2 ? S.Monitoring.CpuLoad.AverageOfLast(samples) : null,
            S.Monitoring.Last?.RamUsedPercent.Value);
        Readiness.Clear();
        foreach (var c in Cs2Advisor.Readiness(input))
        {
            Readiness.Add(c);
        }
    }

    partial void OnProfileChanged(string value)
    {
        S.Settings.Current.Cs2Profile = value;
        S.Settings.Save();
        BuildRecommendations();
    }

    private void BuildRecommendations()
    {
        if (_hw is null || !Enum.TryParse<Cs2Profile>(Profile, out var profile))
        {
            return;
        }

        Recommendations.Clear();
        foreach (var r in Cs2Advisor.Recommend(profile, _hw))
        {
            Recommendations.Add(r);
        }

        FpsMax = Cs2Advisor.SuggestedFpsMax(profile, _hw).ToString(CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private void ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Папка Counter-Strike Global Offensive", "Counter-Strike Global Offensive folder") };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (Cs2Locator.NormalizeGameDir(dialog.FolderName) is null)
        {
            S.Toasts.Show(Loc.T("В этой папке нет game\\bin\\win64\\cs2.exe", "game\\bin\\win64\\cs2.exe not found in this folder"), DialogKind.Warning);
            return;
        }

        S.Settings.Current.Cs2ManualPath = dialog.FolderName;
        S.Settings.Save();
        _ = LoadAsync();
    }

    [RelayCommand]
    private void ResetManualPath()
    {
        S.Settings.Current.Cs2ManualPath = null;
        S.Settings.Save();
        _ = LoadAsync();
    }

    [RelayCommand]
    private void OpenGameFolder() => ChangeRunner.OpenFolder(GameDir);

    [RelayCommand]
    private void OpenConfigFolder() => ChangeRunner.OpenFolder(Accounts.FirstOrDefault()?.ConfigDir ?? _cs2?.GameCfgDir ?? "");

    [RelayCommand]
    private void OpenGameCfgFolder() => ChangeRunner.OpenFolder(_cs2?.GameCfgDir ?? "");

    [RelayCommand]
    private static void Launch() => ChangeRunner.OpenUrl("steam://rungameid/730");

    [RelayCommand]
    private static void OpenSteam() => ChangeRunner.OpenUrl("steam://open/main");

    [RelayCommand]
    private Task BackupConfigs() => RunSafeAsync(async () =>
    {
        if (_cs2 is null)
        {
            return;
        }

        var files = _cs2.ConfigFilesForBackup();
        if (files.Count == 0)
        {
            S.Toasts.Show(Loc.T("Файлов конфигурации не найдено", "No config files found"), DialogKind.Warning);
            return;
        }

        var record = await Task.Run(() => S.Backup.CreateFileSnapshot(Loc.T("Конфиги CS2", "CS2 configs"), BackupKind.GameConfig, files));
        S.Toasts.Show(Loc.T($"Бэкап конфигов создан: {record.Items.Count} файлов", $"Config backup created: {record.Items.Count} files"), DialogKind.Success);
    }, Loc.T("Не удалось создать бэкап конфигов", "Failed to back up configs"));

    [RelayCommand]
    private void RestoreConfigs() => main.Navigate("backup");

    /// <summary>novatune.cfg с fps_max — через бэкап и предпросмотр. Подключается параметром «+exec novatune».</summary>
    [RelayCommand]
    private Task CreateConfig() => RunSafeAsync(async () =>
    {
        if (_cs2 is null || !Enum.TryParse<Cs2Profile>(Profile, out var profile))
        {
            return;
        }

        if (!int.TryParse(FpsMax, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fps) || fps < 0 || fps > 1000)
        {
            S.Toasts.Show(Loc.T("fps_max: число от 0 до 1000 (0 — без ограничения)", "fps_max: a number from 0 to 1000 (0 = uncapped)"), DialogKind.Warning);
            return;
        }

        var path = Path.Combine(_cs2.GameCfgDir, "novatune.cfg");
        var content = Encoding.UTF8.GetBytes(Cs2Advisor.BuildConfig(profile, fps, DateTimeOffset.Now));
        await ChangeRunner.ApplyAsync(
            Loc.T("Конфиг CS2 novatune.cfg", "CS2 config novatune.cfg"),
            [new FileWriteChange(path, content) { Title = "novatune.cfg (fps_max " + fps + ")" }],
            Loc.T("Чтобы CS2 выполняла этот файл, добавьте в Steam → CS2 → Свойства → Параметры запуска: +exec novatune. Античит и файлы игры не затрагиваются.", "To make CS2 run this file, add to Steam → CS2 → Properties → Launch options: +exec novatune. Anti-cheat and game files are not touched."));
    }, Loc.T("Не удалось создать конфиг", "Failed to create config"));

    [RelayCommand]
    private void CopyExec()
    {
        try
        {
            Clipboard.SetText("+exec novatune");
            S.Toasts.Show(Loc.T("Скопировано: +exec novatune", "Copied: +exec novatune"), DialogKind.Success);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            S.Log.Warn("Буфер обмена занят", ex);
        }
    }
}
