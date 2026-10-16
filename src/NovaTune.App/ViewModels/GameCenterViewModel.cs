using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NovaTune.App.Services;
using NovaTune.Core.Backup;
using NovaTune.Core.Games;
using NovaTune.Core.Hardware;
using NovaTune.Core.Localization;
using NovaTune.Core.Settings;
using NovaTune.Core.Steam;
using NovaTune.Core.Tools;
using NovaTune.Platform;

namespace NovaTune.App.ViewModels;

/// <summary>Карточка игры в библиотеке.</summary>
public sealed partial class GameCard : ObservableObject
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    public GameProfile? Profile { get; init; }

    public SteamApp? App { get; init; }

    public required GameEntry Entry { get; init; }

    public string? InstallDir { get; init; }

    /// <summary>Инициалы для карточки без обложки: только буквы и цифры (без скобок, «™» и т.п.).</summary>
    public string Initials => string.Concat(Name
        .Split([' ', '-', ':', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
        .Select(w => w.FirstOrDefault(char.IsLetterOrDigit))
        .Where(ch => ch != default)
        .Take(2)
        .Select(char.ToUpperInvariant));

    public bool IsSupported => Profile is not null;

    public bool IsManual => App is null && Profile is null;

    public bool IsCs2 => Profile?.Id == "cs2";

    /// <summary>Игра из каталога, которую не удалось найти автоматически (папку можно указать вручную).</summary>
    public bool NotFound { get; init; }

    public string Source => NotFound
        ? Loc.T("не найдена — укажите папку", "not found — select folder")
        : App is not null
        ? "Steam"
        : Profile is not null ? Loc.T("найдена по конфигам", "found by configs") : Loc.T("добавлена вручную", "added manually");

    public string Detail => App is null
        ? (InstallDir ?? "")
        : string.Join(" · ", new[]
        {
            App.SizeOnDisk is { } s && s > 0 ? Units.Bytes(s) : null,
            App.LastUpdated is { } u ? Loc.T("обновлена ", "updated ") + u.LocalDateTime.ToString("d", CultureInfo.CurrentCulture) : null,
            App.IsFullyInstalled ? null : Loc.T("установка не завершена", "installation incomplete"),
        }.Where(x => x is not null));

    [ObservableProperty] private ImageSource? _cover;
    [ObservableProperty] private ImageSource? _hero;
}

public sealed record ConfigDirRow(string Path, string Kind, int Files);

public sealed record NovaConfigRow(NovaConfig Config)
{
    public string Name => Config.Name;

    public string Description => Config.Description;

    public string Exec => "+exec " + System.IO.Path.GetFileNameWithoutExtension(Config.FileName);
}

/// <summary>
/// Библиотека игр (Steam по манифестам + игры, найденные по папкам конфигов, + добавленные вручную)
/// и страница игры: запуск, папки, бэкап и откат конфигов, для CS2 — оптимизация и конфиги NOVA.
/// </summary>
public sealed partial class GameCenterViewModel(MainViewModel main) : PageViewModel
{
    private string? _steamPath;
    private Cs2ViewModel? _cs2;
    private string? _pendingOpen;

    public override string Title => Loc.T("Игры", "Games");

    public override string Subtitle => Loc.T("Ваша библиотека: запуск, конфиги, бэкапы и оптимизация под каждую игру", "Your library: launch, configs, backups and per-game optimization");

    public ObservableCollection<GameCard> Games { get; } = [];

    public ObservableCollection<GameCard> Filtered { get; } = [];

    public ObservableCollection<ConfigDirRow> ConfigDirs { get; } = [];

    public ObservableCollection<BackupRow> GameBackups { get; } = [];

    public IReadOnlyList<NovaConfigRow> NovaConfigs { get; } = Cs2NovaConfigs.All.Select(c => new NovaConfigRow(c)).ToList();

    public IReadOnlyList<string> Profiles { get; } = ["Competitive", "Balanced", "Quality", "Laptop"];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private GameCard? _selected;
    [ObservableProperty] private bool _isDetail;
    [ObservableProperty] private string _tab = "configs";
    [ObservableProperty] private string _libraryStatus = "";
    [ObservableProperty] private string _configSummary = "";
    [ObservableProperty] private string _gameProfile = "Balanced";
    [ObservableProperty] private string _gameNotes = "";
    [ObservableProperty] private bool _isBusy;

    public Cs2ViewModel? Cs2 => _cs2;

    public IReadOnlyList<string> Tips => Selected?.Profile?.Tips.Select(t => t.Value).ToList() ?? [];

    protected override async Task OnNavigatedToAsync(bool firstTime)
    {
        if (firstTime || Games.Count == 0)
        {
            await LoadAsync();
        }

        if (_pendingOpen is { } key)
        {
            _pendingOpen = null;
            OpenByProfile(key);
        }
    }

    /// <summary>Открыть страницу игры по id профиля (например, «cs2» с главной).</summary>
    public void OpenByProfile(string profileId)
    {
        if (Games.Count == 0)
        {
            _pendingOpen = profileId;
            return;
        }

        var card = Games.FirstOrDefault(g => g.Profile?.Id == profileId);
        if (card is not null)
        {
            Open(card);
        }
        else if (profileId == "cs2")
        {
            // CS2 не найдена: всё равно показываем страницу CS2 — там есть ручной выбор папки.
            Open(new GameCard { Key = "cs2", Name = "Counter-Strike 2", Profile = GameCatalog.Profiles[0], Entry = new GameEntry { Name = "Counter-Strike 2", SteamAppId = 730 } });
        }
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            _steamPath = SteamLocator.FindSteamPath();
            var steam = _steamPath;
            var apps = steam is null ? [] : await Task.Run(() => new SteamScanner(steam, S.Log).GetApps(), PageCts.Token);
            var folders = KnownFolders.FromSystem();
            var saved = S.Settings.Current.Games;
            var cards = new List<GameCard>();

            // Steam: всё, кроме служебных пакетов (Steamworks Common Redistributables).
            foreach (var app in apps.Where(a => a.AppId != 228980))
            {
                var entry = saved.FirstOrDefault(g => g.SteamAppId == app.AppId) ?? new GameEntry { SteamAppId = app.AppId, Name = app.Name };
                cards.Add(new GameCard
                {
                    Key = "steam:" + app.AppId,
                    Name = app.Name,
                    App = app,
                    Entry = entry,
                    InstallDir = app.InstallDir,
                    Profile = GameCatalog.ForSteamApp(app.AppId, app.InstallDir),
                });
            }

            // Игры вне Steam (и Steam-игры из других лаунчеров): найдены по существующим папкам конфигов.
            foreach (var profile in GameCatalog.Profiles.Where(p => cards.All(c => c.Profile?.Id != p.Id)))
            {
                if (profile.SteamAppId is not null && profile.Configs.All(c => c.Root is ConfigRoot.InstallDir or ConfigRoot.SteamUserdata))
                {
                    continue;
                }

                if (ConfigLocator.ResolveDirectories(profile, null, null, [], folders).Count > 0)
                {
                    cards.Add(new GameCard
                    {
                        Key = "profile:" + profile.Id,
                        Name = profile.Name,
                        Profile = profile,
                        Entry = saved.FirstOrDefault(g => g.Name == profile.Name) ?? new GameEntry { Name = profile.Name },
                    });
                }
            }

            // Инструменты CS2 (прицел, чувствительность, конфиги) доступны всегда — даже если игра не найдена автоматически.
            if (cards.All(c => c.Profile?.Id != "cs2"))
            {
                cards.Add(new GameCard
                {
                    Key = "profile:cs2",
                    Name = "Counter-Strike 2",
                    Profile = GameCatalog.Profiles.First(p => p.Id == "cs2"),
                    Entry = saved.FirstOrDefault(g => g.SteamAppId == 730) ?? new GameEntry { Name = "Counter-Strike 2", SteamAppId = 730 },
                    NotFound = true,
                });
            }

            foreach (var manual in saved.Where(g => g.SteamAppId is null && cards.All(c => c.Name != g.Name)))
            {
                cards.Add(new GameCard { Key = "manual:" + manual.Id, Name = manual.Name, Entry = manual, InstallDir = manual.InstallDir });
            }

            Games.Clear();
            foreach (var c in cards.OrderByDescending(c => c.IsSupported).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Games.Add(c);
            }

            ApplyFilter();
            LibraryStatus = Loc.T(
                $"Игр: {Games.Count} · с поддержкой конфигов: {Games.Count(g => g.IsSupported)}" + (steam is null ? " · Steam не найден" : ""),
                $"Games: {Games.Count} · with config support: {Games.Count(g => g.IsSupported)}" + (steam is null ? " · Steam not found" : ""));

            // Обложки — в фоне, чтобы список появился сразу.
            _ = Task.Run(() =>
            {
                foreach (var card in cards.Where(c => c.App is not null))
                {
                    var cover = SteamArt.Load(SteamArt.FindCover(steam, card.App!.AppId), 300);
                    Application.Current?.Dispatcher.InvokeAsync(() => card.Cover = cover);
                }
            });
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSearchChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Filtered.Clear();
        foreach (var g in Games.Where(g => Search.Length == 0 || g.Name.Contains(Search, StringComparison.CurrentCultureIgnoreCase)))
        {
            Filtered.Add(g);
        }
    }

    [RelayCommand]
    private void Open(GameCard card)
    {
        Selected = card;
        GameProfile = card.Entry.Profile;
        GameNotes = card.Entry.Notes;
        Tab = card.IsCs2 ? "cs2" : card.IsSupported ? "configs" : "notes";
        IsDetail = true;
        OnPropertyChanged(nameof(Tips));
        if (card.App is not null && card.Hero is null)
        {
            var steam = _steamPath;
            _ = Task.Run(() =>
            {
                var hero = SteamArt.Load(SteamArt.FindHero(steam, card.App.AppId), 1400);
                Application.Current?.Dispatcher.InvokeAsync(() => card.Hero = hero);
            });
        }

        RefreshConfigs();
        if (card.IsCs2)
        {
            _cs2 ??= new Cs2ViewModel(main);
            OnPropertyChanged(nameof(Cs2));
            _ = _cs2.NavigatedToAsync();
        }
    }

    [RelayCommand]
    private void Back()
    {
        IsDetail = false;
        Selected = null;
    }

    // ───────── Конфиги и бэкапы ─────────

    private IReadOnlyList<(ConfigLocation Location, string Directory)> ResolveDirs()
    {
        if (Selected?.Profile is not { } profile)
        {
            return [];
        }

        var accounts = _steamPath is null ? [] : new SteamScanner(_steamPath).GetUsers().Select(u => u.AccountId.ToString(CultureInfo.InvariantCulture)).ToList();
        return ConfigLocator.ResolveDirectories(profile, Selected.InstallDir, _steamPath, accounts, KnownFolders.FromSystem());
    }

    private void RefreshConfigs()
    {
        ConfigDirs.Clear();
        GameBackups.Clear();
        if (Selected?.Profile is not { } profile)
        {
            ConfigSummary = Loc.T("Для этой игры расположение конфигов неизвестно — доступны заметки и профиль.", "Config locations for this game are unknown — notes and profile are available.");
            return;
        }

        var dirs = ResolveDirs();
        foreach (var (location, dir) in dirs)
        {
            var count = ConfigLocator.CollectFiles([(location, dir)]).Count;
            ConfigDirs.Add(new ConfigDirRow(dir, location.Root switch
            {
                ConfigRoot.InstallDir => Loc.T("папка игры", "game folder"),
                ConfigRoot.SteamUserdata => Loc.T("аккаунт Steam", "Steam account"),
                _ => Loc.T("папка пользователя", "user folder"),
            }, count));
        }

        ConfigSummary = dirs.Count == 0
            ? Loc.T("Папки конфигов не найдены. Возможно, игра ещё не запускалась на этом ПК.", "No config folders found. The game may not have been launched on this PC yet.")
            : Loc.T($"Папок с конфигами: {dirs.Count}, файлов для бэкапа: {ConfigLocator.CollectFiles(dirs).Count}", $"Config folders: {dirs.Count}, files to back up: {ConfigLocator.CollectFiles(dirs).Count}");

        var tag = ConfigLocator.BackupTag(profile);
        foreach (var r in S.Backup.List().Where(b => b.Tag == tag || (b.Tag is null && profile.Id == "cs2" && b.Kind == BackupKind.GameConfig)))
        {
            GameBackups.Add(new BackupRow(r));
        }
    }

    [RelayCommand]
    private Task BackupConfigs() => RunSafeAsync(async () =>
    {
        if (Selected?.Profile is not { } profile)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var files = ConfigLocator.CollectFiles(ResolveDirs());
            if (files.Count == 0)
            {
                S.Toasts.Show(Loc.T("Файлов конфигурации не найдено", "No config files found"), DialogKind.Warning);
                return;
            }

            var record = await Task.Run(() => S.Backup.CreateFileSnapshot(Loc.T("Конфиги: ", "Configs: ") + profile.Name, BackupKind.GameConfig, files, ConfigLocator.BackupTag(profile)));
            S.Toasts.Show(Loc.T($"Бэкап создан: {record.Items.Count} файлов", $"Backup created: {record.Items.Count} files"), DialogKind.Success);
            RefreshConfigs();
        }
        finally
        {
            IsBusy = false;
        }
    }, Loc.T("Не удалось создать бэкап", "Failed to create backup"));

    [RelayCommand]
    private Task RestoreBackup(BackupRow row) => RunSafeAsync(async () =>
    {
        var running = Selected?.Profile?.ProcessNames.Where(n => Process.GetProcessesByName(n).Length > 0).ToList() ?? [];
        var warning = running.Count > 0
            ? Loc.T("\n\nИгра сейчас запущена — закройте её, иначе она может перезаписать восстановленные файлы при выходе.", "\n\nThe game is running — close it, otherwise it may overwrite the restored files on exit.")
            : "";
        if (!await S.Dialogs.ConfirmAsync(
                Loc.T("Откатить конфиги?", "Roll back configs?"),
                Loc.T($"Файлы конфигов вернутся к состоянию на {row.Date} ({row.Count}). Хэш каждого файла проверяется.", $"Config files will return to their state at {row.Date} ({row.Count}). Each file's hash is verified.") + warning,
                Loc.T("Откатить", "Roll back"),
                kind: running.Count > 0 ? DialogKind.Warning : DialogKind.Info))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await S.Backup.RestoreAsync(row.Record);
            await S.Dialogs.AlertAsync(
                result.Success ? Loc.T("Конфиги восстановлены", "Configs restored") : Loc.T("Восстановлено частично", "Partially restored"),
                Loc.T($"Восстановлено файлов: {result.Restored}", $"Files restored: {result.Restored}"),
                result.Success ? DialogKind.Success : DialogKind.Warning,
                result.Failures.Count > 0 ? new TextLines(result.Failures.Select(f => f.Title + ": " + f.Error).ToList()) : null);
            RefreshConfigs();
        }
        finally
        {
            IsBusy = false;
        }
    }, Loc.T("Ошибка отката", "Rollback error"));

    [RelayCommand]
    private Task DeleteBackup(BackupRow row) => RunSafeAsync(async () =>
    {
        if (await S.Dialogs.ConfirmAsync(Loc.T("Удалить бэкап?", "Delete backup?"), row.Title + " · " + row.Date, Loc.T("Удалить", "Delete"), danger: true))
        {
            S.Backup.Delete(row.Record);
            RefreshConfigs();
        }
    }, Loc.T("Не удалось удалить бэкап", "Failed to delete backup"));

    [RelayCommand]
    private void OpenConfigDir(ConfigDirRow row) => ChangeRunner.OpenFolder(row.Path);

    // ───────── Конфиги NOVA для CS2 ─────────

    [RelayCommand]
    private Task ApplyNova(NovaConfigRow row) => RunSafeAsync(async () =>
    {
        var cfgDir = ResolveDirs().FirstOrDefault(d => d.Location.Root == ConfigRoot.InstallDir).Directory;
        if (cfgDir is null)
        {
            await S.Dialogs.AlertAsync("CS2", Loc.T("Папка game\\csgo\\cfg не найдена. Укажите папку CS2 на вкладке «CS2».", "The game\\csgo\\cfg folder was not found. Select the CS2 folder on the CS2 tab."), DialogKind.Warning);
            return;
        }

        var result = await ChangeRunner.ApplyAsync(
            Loc.T("Конфиг NOVA: ", "NOVA config: ") + row.Name,
            [new FileWriteChange(Path.Combine(cfgDir, row.Config.FileName), Encoding.UTF8.GetBytes(row.Config.Content)) { Title = row.Config.FileName }],
            Loc.T($"Затем добавьте в Steam → CS2 → Свойства → Параметры запуска: {row.Exec}. Старый файл с таким именем (если был) сохраняется в бэкап.", $"Then add to Steam → CS2 → Properties → Launch options: {row.Exec}. Any existing file with this name is saved to a backup."));
        if (result?.Success == true)
        {
            CopyExec(row);
        }
    }, Loc.T("Не удалось записать конфиг", "Failed to write config"));

    [RelayCommand]
    private void CopyExec(NovaConfigRow row)
    {
        try
        {
            Clipboard.SetText(row.Exec);
            S.Toasts.Show(Loc.T("Скопировано: ", "Copied: ") + row.Exec, DialogKind.Success);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            S.Log.Warn("Буфер обмена занят", ex);
        }
    }

    // ───────── Запуск, папки, заметки ─────────

    [RelayCommand]
    private void Launch()
    {
        if (Selected?.App is { } app)
        {
            ChangeRunner.OpenUrl("steam://rungameid/" + app.AppId.ToString(CultureInfo.InvariantCulture));
        }
        else if (Selected?.Entry.ExecutablePath is { } exe && File.Exists(exe))
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
        }
        else
        {
            S.Toasts.Show(Loc.T("Не знаю, как запустить эту игру: укажите EXE при добавлении вручную.", "Don't know how to launch this game: pick the EXE when adding it manually."), DialogKind.Warning);
        }
    }

    public bool CanLaunch => Selected?.App is not null || Selected?.Entry.ExecutablePath is not null;

    public string SelectedExe => Selected?.Entry.ExecutablePath ?? "—";

    partial void OnSelectedChanged(GameCard? value)
    {
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(SelectedExe));
    }

    [RelayCommand]
    private void OpenGameFolder() => ChangeRunner.OpenFolder(Selected?.InstallDir ?? "");

    [RelayCommand]
    private void SaveNotes()
    {
        if (Selected is not { } card)
        {
            return;
        }

        card.Entry.Profile = GameProfile;
        card.Entry.Notes = GameNotes ?? "";
        if (!S.Settings.Current.Games.Contains(card.Entry))
        {
            S.Settings.Current.Games.RemoveAll(g => (g.SteamAppId is not null && g.SteamAppId == card.Entry.SteamAppId) || (g.SteamAppId is null && g.Name == card.Entry.Name));
            S.Settings.Current.Games.Add(card.Entry);
        }

        S.Settings.Save();
        S.Toasts.Show(Loc.T("Сохранено", "Saved"), DialogKind.Success);
    }

    /// <summary>
    /// Добавление вручную: выбирается EXE игры (это и есть кнопка «Играть»), папка определяется по нему.
    /// Если EXE выбрать не получилось — можно указать папку, и приложение поищет EXE в ней само.
    /// </summary>
    [RelayCommand]
    private void Add()
    {
        var exe = new OpenFileDialog
        {
            Title = Loc.T("Выберите EXE игры (не установщик и не папку с сохранениями)", "Select the game EXE (not an installer or a saves folder)"),
            Filter = Loc.T("Программы", "Programs") + "|*.exe",
        };
        string? exePath = null;
        string folder;
        if (exe.ShowDialog() == true)
        {
            exePath = exe.FileName;
            folder = Path.GetDirectoryName(exePath)!;
        }
        else
        {
            var dialog = new OpenFolderDialog { Title = Loc.T("Или выберите папку установки игры", "Or select the game install folder") };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            folder = dialog.FolderName;
            exePath = GuessExe(folder);
        }

        var entry = new GameEntry
        {
            Name = GuessName(exePath, folder),
            InstallDir = folder,
            ExecutablePath = exePath,
        };
        S.Settings.Current.Games.Add(entry);
        S.Settings.Save();
        S.Toasts.Show(exePath is null
            ? Loc.T("Игра добавлена без EXE — укажите его на странице игры, чтобы появилась кнопка «Играть».", "Game added without an EXE — set it on the game page to get the Play button.")
            : Loc.T("Игра добавлена: ", "Game added: ") + entry.Name, exePath is null ? DialogKind.Warning : DialogKind.Success);
        _ = LoadAsync();
    }

    /// <summary>Указать или сменить EXE у игры, добавленной вручную.</summary>
    [RelayCommand]
    private void SetExe()
    {
        if (Selected is not { IsManual: true } card)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = Loc.T("Выберите EXE игры", "Select the game EXE"),
            Filter = Loc.T("Программы", "Programs") + "|*.exe",
            InitialDirectory = Directory.Exists(card.InstallDir) ? card.InstallDir : null,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        card.Entry.ExecutablePath = dialog.FileName;
        card.Entry.InstallDir = Path.GetDirectoryName(dialog.FileName);
        S.Settings.Save();
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(SelectedExe));
        S.Toasts.Show(Loc.T("EXE сохранён — кнопка «Играть» доступна", "EXE saved — the Play button is available"), DialogKind.Success);
    }

    /// <summary>
    /// Поиск EXE игры в папке (верхний уровень и один уровень вглубь, например Binaries или bin).
    /// Исключаются установщики, деинсталляторы и служебные программы; из оставшихся — самый большой файл.
    /// </summary>
    internal static string? GuessExe(string folder)
    {
        string[] skip = ["unins", "setup", "install", "crash", "report", "redist", "vcredist", "dxsetup", "dotnet", "update", "helper", "launcherhelper", "easyanticheat_setup", "cleanup"];
        try
        {
            var candidates = Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateDirectories(folder).SelectMany(d =>
                {
                    try
                    {
                        return Directory.EnumerateFiles(d, "*.exe", SearchOption.TopDirectoryOnly);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return [];
                    }
                }))
                .Where(f => !skip.Any(k => Path.GetFileName(f).Contains(k, StringComparison.OrdinalIgnoreCase)))
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.Length)
                .FirstOrDefault();
            return candidates?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string GuessName(string? exePath, string folder)
    {
        if (exePath is not null)
        {
            try
            {
                var product = FileVersionInfo.GetVersionInfo(exePath).ProductName?.Trim();
                if (!string.IsNullOrWhiteSpace(product) && product.Length <= 60)
                {
                    return product;
                }
            }
            catch (FileNotFoundException)
            {
                // Имя возьмём из папки.
            }
        }

        return Path.GetFileName(folder.TrimEnd('\\'));
    }

    [RelayCommand]
    private void Remove()
    {
        if (Selected is not { IsManual: true } card)
        {
            return;
        }

        S.Settings.Current.Games.Remove(card.Entry);
        S.Settings.Save();
        Back();
        _ = LoadAsync();
    }
}
