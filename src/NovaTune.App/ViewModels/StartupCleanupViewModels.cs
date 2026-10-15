using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaTune.App.Services;
using NovaTune.Core.Cleanup;
using NovaTune.Core.Hardware;
using NovaTune.Core.Localization;
using NovaTune.Core.Startup;

namespace NovaTune.App.ViewModels;

public sealed partial class StartupRow : ObservableObject
{
    private readonly StartupViewModel _owner;
    private bool _suppress;

    public StartupRow(StartupEntry entry, StartupViewModel owner)
    {
        Entry = entry;
        _owner = owner;
        _isEnabled = entry.Enabled ?? true;
    }

    public StartupEntry Entry { get; }

    public string Name => Entry.Name;

    public string Command => Entry.Command;

    public string Source => Entry.SourceDisplay;

    public string? Publisher => Entry.Publisher;

    public bool CanToggle => Entry.CanToggle;

    public bool IsSystem => Entry.LooksLikeSystemComponent;

    public bool RequiresAdmin => Entry.RequiresAdmin;

    public string StateText => Entry.Enabled switch
    {
        true => Loc.T("включено", "enabled"),
        false => Loc.T("отключено", "disabled"),
        _ => Loc.T("только просмотр", "view only"),
    };

    [ObservableProperty] private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (_suppress)
        {
            return;
        }

        _ = _owner.ToggleAsync(this, value);
    }

    /// <summary>Вернуть переключатель без повторного запуска операции (после отмены или ошибки).</summary>
    public void Revert(bool value)
    {
        _suppress = true;
        IsEnabled = value;
        _suppress = false;
    }
}

/// <summary>Автозагрузка: включение/отключение тем же механизмом, что и диспетчер задач (StartupApproved).</summary>
public sealed partial class StartupViewModel : PageViewModel
{
    public override string Title => Loc.T("Автозагрузка", "Startup");

    public override string Subtitle => Loc.T(
        "Отключение — через флаг StartupApproved, как в диспетчере задач: запись не удаляется и её можно включить обратно. Задачи планировщика и RunOnce — только просмотр.",
        "Disabling uses the StartupApproved flag, like Task Manager: entries are not deleted and can be re-enabled. Scheduled tasks and RunOnce are view-only.");

    public ObservableCollection<StartupRow> Entries { get; } = [];

    [ObservableProperty] private string _summary = "";

    protected override Task OnNavigatedToAsync(bool firstTime) => LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var entries = await S.Startup.GetEntriesAsync(PageCts.Token);
            Entries.Clear();
            foreach (var e in entries.OrderBy(e => !e.CanToggle).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Entries.Add(new StartupRow(e, this));
            }

            Summary = Loc.T(
                $"Всего записей: {entries.Count}, включено: {entries.Count(e => e.Enabled == true)}, можно переключить: {entries.Count(e => e.CanToggle)}",
                $"Entries: {entries.Count}, enabled: {entries.Count(e => e.Enabled == true)}, toggleable: {entries.Count(e => e.CanToggle)}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task ToggleAsync(StartupRow row, bool enable)
    {
        var warning = row.IsSystem
            ? Loc.T("Похоже на компонент драйвера или системы (звук, видео, тачпад, защита). Отключайте, только если уверены.", "This looks like a driver or system component (audio, graphics, touchpad, security). Disable only if you are sure.")
            : null;
        try
        {
            var change = StartupApproved.PlanToggle(row.Entry, enable, DateTime.UtcNow);
            var result = await ChangeRunner.ApplyAsync(change.Title, [change], warning);
            if (result is null || !result.Success)
            {
                row.Revert(!enable);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            S.Log.Error("Автозагрузка: переключение", ex);
            row.Revert(!enable);
            await S.Dialogs.AlertAsync(Loc.T("Ошибка", "Error"), ex.Message, DialogKind.Error);
        }

        await LoadAsync();
    }
}

public sealed partial class CleanupCategoryRow(CleanupCategory category) : ObservableObject
{
    public CleanupCategory Category { get; } = category;

    public string Title => Category.Title;

    public string Description => Category.Description;

    [ObservableProperty] private bool _isSelected = category.SelectedByDefault;
    [ObservableProperty] private string _sizeText = Loc.T("не сканировалось", "not scanned");
    [ObservableProperty] private CleanupScanResult? _scan;
}

/// <summary>Очистка: только заведомо безопасные категории, предпросмотр размера, занятые файлы пропускаются.</summary>
public sealed partial class CleanupViewModel : PageViewModel
{
    public override string Title => Loc.T("Очистка", "Cleanup");

    public override string Subtitle => Loc.T(
        "Документы, сохранения игр, профили браузеров, пароли и неизвестные файлы не затрагиваются. Удаление необратимо, поэтому в Safe Optimize не входит.",
        "Documents, game saves, browser profiles, passwords and unknown files are never touched. Deletion is irreversible, so it is not part of Safe Optimize.");

    public ObservableCollection<CleanupCategoryRow> Categories { get; } = [];

    [ObservableProperty] private string? _total;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _lastResult;

    protected override Task OnNavigatedToAsync(bool firstTime)
    {
        if (firstTime)
        {
            foreach (var c in CleanupCatalog.Create(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetTempPath()))
            {
                Categories.Add(new CleanupCategoryRow(c));
            }
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task Scan() => RunSafeAsync(async () =>
    {
        IsBusy = true;
        try
        {
            long total = 0;
            foreach (var row in Categories)
            {
                row.SizeText = Loc.T("сканирование…", "scanning…");
                var scan = await S.Cleanup.ScanAsync(row.Category, PageCts.Token);
                row.Scan = scan;
                row.SizeText = Loc.T($"{Units.Bytes(scan.TotalBytes)} · файлов: {scan.FileCount}", $"{Units.Bytes(scan.TotalBytes)} · files: {scan.FileCount}")
                    + (scan.InaccessibleCount > 0 ? Loc.T($" · недоступных папок: {scan.InaccessibleCount}", $" · inaccessible folders: {scan.InaccessibleCount}") : "");
                if (row.IsSelected)
                {
                    total += scan.TotalBytes;
                }
            }

            Total = Loc.T("Выбрано к удалению: ", "Selected for deletion: ") + Units.Bytes(total);
        }
        finally
        {
            IsBusy = false;
        }
    }, Loc.T("Ошибка сканирования", "Scan error"));

    [RelayCommand]
    private Task Clean() => RunSafeAsync(async () =>
    {
        var selected = Categories.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        if (selected.Any(c => c.Scan is null))
        {
            await Scan();
        }

        var bytes = selected.Sum(c => c.Scan?.TotalBytes ?? 0);
        var files = selected.Sum(c => c.Scan?.FileCount ?? 0);
        if (!await S.Dialogs.ConfirmAsync(
                Loc.T("Удалить временные файлы?", "Delete temporary files?"),
                Loc.T($"Будет удалено файлов: {files} ({Units.Bytes(bytes)}) из категорий: {string.Join(", ", selected.Select(c => c.Title))}. Удаление необратимо. Занятые файлы будут пропущены.", $"Files to delete: {files} ({Units.Bytes(bytes)}) from: {string.Join(", ", selected.Select(c => c.Title))}. This cannot be undone. Files in use will be skipped."),
                Loc.T("Удалить", "Delete"),
                danger: true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var lines = new List<string>();
            foreach (var row in selected)
            {
                var r = await S.Cleanup.CleanAsync(row.Scan!, PageCts.Token);
                lines.Add(Loc.T($"{row.Title}: удалено {r.DeletedFiles} ({Units.Bytes(r.FreedBytes)}), пропущено занятых {r.SkippedInUse}", $"{row.Title}: deleted {r.DeletedFiles} ({Units.Bytes(r.FreedBytes)}), skipped in use {r.SkippedInUse}"));
                S.Journal.Add("cleanup", row.Title, true, lines[^1]);
                row.Scan = null;
                row.SizeText = Loc.T("очищено", "cleaned");
            }

            LastResult = string.Join("\n", lines);
            Total = null;
        }
        finally
        {
            IsBusy = false;
        }
    }, Loc.T("Ошибка очистки", "Cleanup error"));

    /// <summary>Системная очистка требует прав администратора — используем штатную «Очистку диска» Windows.</summary>
    [RelayCommand]
    private static void OpenCleanMgr() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cleanmgr.exe") { UseShellExecute = true });
}
