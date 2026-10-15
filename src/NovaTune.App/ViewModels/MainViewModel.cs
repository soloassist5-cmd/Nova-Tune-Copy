using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaTune.App.Services;
using NovaTune.Core.Localization;
using NovaTune.Platform;

namespace NovaTune.App.ViewModels;

/// <summary>Пункт левой навигации.</summary>
public sealed partial class NavItem(string key, LocText title, string iconKey, Func<PageViewModel> factory) : ObservableObject
{
    public string Key { get; } = key;

    public LocText TitleText { get; } = title;

    public string Title => TitleText.Value;

    public Geometry? Icon => Application.Current.TryFindResource(iconKey) as Geometry;

    public Func<PageViewModel> Factory { get; } = factory;

    public void RefreshText() => OnPropertyChanged(nameof(Title));
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly Dictionary<string, PageViewModel> _pages = new();

    [ObservableProperty]
    private NavItem? _selected;

    [ObservableProperty]
    private PageViewModel? _currentPage;

    public MainViewModel()
    {
        Items =
        [
            new("dashboard", new("Главная", "Dashboard"), "Icon.Dashboard", () => new DashboardViewModel(this)),
            new("mypc", new("Мой ПК", "My PC"), "Icon.Pc", () => new MyPcViewModel()),
            new("monitoring", new("Мониторинг", "Monitoring"), "Icon.Pulse", () => new MonitoringViewModel()),
            new("optimize", new("Оптимизация", "Optimize"), "Icon.Sliders", () => new OptimizeViewModel(this)),
            new("cs2", new("CS2", "CS2 Optimizer"), "Icon.Crosshair", () => new Cs2ViewModel(this)),
            new("games", new("Игры", "Game Center"), "Icon.Gamepad", () => new GameCenterViewModel()),
            new("startup", new("Автозагрузка", "Startup"), "Icon.Power", () => new StartupViewModel()),
            new("cleanup", new("Очистка", "Cleanup"), "Icon.Broom", () => new CleanupViewModel()),
            new("backup", new("Бэкапы", "Backup & Restore"), "Icon.Shield", () => new BackupViewModel()),
            new("reports", new("Отчёты", "Reports"), "Icon.Report", () => new ReportsViewModel()),
            new("settings", new("Настройки", "Settings"), "Icon.Settings", () => new SettingsViewModel(this)),
        ];

        // Предварительная сборка: показываем только готовые страницы, чтобы не было пустых разделов.
        foreach (var unfinished in Items.Where(i => i.Key != "dashboard").ToList())
        {
            Items.Remove(unfinished);
        }

        Loc.Instance.LanguageChanged += (_, _) => RebuildPages();
    }

    public ObservableCollection<NavItem> Items { get; }

    public DialogService Dialogs => AppServices.Current.Dialogs;

    public ToastService Toasts => AppServices.Current.Toasts;

    public bool IsElevated { get; } = OsInfo.IsElevated();

    public string Version { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public string DeviceName { get; } = Environment.MachineName;

    partial void OnSelectedChanged(NavItem? oldValue, NavItem? newValue)
    {
        if (newValue is null)
        {
            return;
        }

        CurrentPage?.NavigatedFrom();
        if (!_pages.TryGetValue(newValue.Key, out var page))
        {
            page = newValue.Factory();
            _pages[newValue.Key] = page;
        }

        CurrentPage = page;
        _ = page.NavigatedToAsync();
    }

    public void Navigate(string key)
    {
        Selected = Items.FirstOrDefault(i => i.Key == key) ?? Selected;
    }

    public T? GetPage<T>()
        where T : PageViewModel => _pages.Values.OfType<T>().FirstOrDefault();

    /// <summary>После смены языка пересоздаём страницы: тексты, сформированные в коде, тоже должны обновиться.</summary>
    private void RebuildPages()
    {
        foreach (var item in Items)
        {
            item.RefreshText();
        }

        CurrentPage?.NavigatedFrom();
        _pages.Clear();
        var key = Selected?.Key;
        Selected = null;
        CurrentPage = null;
        Navigate(key ?? "dashboard");
    }

    [RelayCommand]
    private void GoTo(string key) => Navigate(key);
}
