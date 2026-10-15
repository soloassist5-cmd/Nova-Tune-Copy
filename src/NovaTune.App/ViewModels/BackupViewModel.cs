using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaTune.App.Services;
using NovaTune.Core.Backup;
using NovaTune.Core.Elevation;
using NovaTune.Core.Localization;

namespace NovaTune.App.ViewModels;

public sealed partial class BackupItemRow(BackupItem item) : ObservableObject
{
    public BackupItem Item { get; } = item;

    [ObservableProperty] private bool _isSelected = item.State != BackupItemState.Skipped;

    public string Title => Item.Title;

    public string Target => Item.Target;

    public string Change => Item.NewDisplay is null ? Item.OriginalDisplay : $"{Item.OriginalDisplay}  →  {Item.NewDisplay}";

    public string State => Item.State switch
    {
        BackupItemState.Captured => Loc.T("сохранено", "saved"),
        BackupItemState.Applying => Loc.T("прервано при применении", "interrupted while applying"),
        BackupItemState.Applied => Loc.T("применено", "applied"),
        BackupItemState.Skipped => Loc.T("без изменений", "unchanged"),
        BackupItemState.Failed => Loc.T("ошибка: ", "error: ") + Item.Error,
        BackupItemState.Restored => Loc.T("восстановлено", "restored"),
        BackupItemState.RestoreFailed => Loc.T("ошибка восстановления: ", "restore error: ") + Item.Error,
        _ => Item.State.ToString(),
    };

    public bool CanRestore => Item.State != BackupItemState.Skipped;
}

public sealed class BackupRow(BackupRecord record)
{
    public BackupRecord Record { get; } = record;

    public string Title => Record.Title;

    public string Date => Record.CreatedAt.LocalDateTime.ToString("g");

    public string Kind => Record.Kind switch
    {
        BackupKind.Automatic => Loc.T("Автоматический", "Automatic"),
        BackupKind.Manual => Loc.T("Ручной", "Manual"),
        BackupKind.GameConfig => Loc.T("Конфиги игры", "Game configs"),
        _ => Record.Kind.ToString(),
    };

    public string Status => Record.Status switch
    {
        BackupStatus.InProgress => Loc.T("не завершена", "incomplete"),
        BackupStatus.Completed => Loc.T("применено", "applied"),
        BackupStatus.PartiallyCompleted => Loc.T("применено частично", "partially applied"),
        BackupStatus.Failed => Loc.T("не применено", "failed"),
        BackupStatus.Snapshot => Loc.T("снимок", "snapshot"),
        BackupStatus.RolledBack => Loc.T("откачено", "rolled back"),
        BackupStatus.PartiallyRolledBack => Loc.T("откачено частично", "partially rolled back"),
        _ => Record.Status.ToString(),
    };

    public string Count => Loc.T($"{Record.Items.Count} элем.", $"{Record.Items.Count} items");
}

/// <summary>Список бэкапов, восстановление целиком и по пунктам, удаление, точка восстановления системы.</summary>
public sealed partial class BackupViewModel : PageViewModel
{
    public override string Title => Loc.T("Бэкапы и восстановление", "Backup & Restore");

    public override string Subtitle => Loc.T(
        "Бэкап NOVA TUNE хранит только значения, которые меняет приложение. Это не полная копия Windows и не замена точке восстановления системы.",
        "NOVA TUNE backups store only the values the app changes. They are not a full copy of Windows and do not replace a system restore point.");

    public ObservableCollection<BackupRow> Backups { get; } = [];

    public ObservableCollection<BackupItemRow> Items { get; } = [];

    [ObservableProperty] private BackupRow? _selected;
    [ObservableProperty] private bool _isBusy;

    public string Folder => S.Backup.BackupRoot;

    protected override Task OnNavigatedToAsync(bool firstTime)
    {
        Reload();
        return Task.CompletedTask;
    }

    private void Reload()
    {
        var id = Selected?.Record.Id;
        Backups.Clear();
        foreach (var r in S.Backup.List())
        {
            Backups.Add(new BackupRow(r));
        }

        Selected = Backups.FirstOrDefault(b => b.Record.Id == id) ?? Backups.FirstOrDefault();
    }

    partial void OnSelectedChanged(BackupRow? value)
    {
        Items.Clear();
        foreach (var item in value?.Record.Items ?? [])
        {
            Items.Add(new BackupItemRow(item));
        }
    }

    /// <summary>Ручной бэкап всех настроек, которые может изменить приложение.</summary>
    [RelayCommand]
    private Task Create() => RunSafeAsync(async () =>
    {
        IsBusy = true;
        try
        {
            var context = await S.GetContextAsync();
            var targets = S.Modules.SelectMany(m => m.BackupTargets(S.Store, context)).ToList();
            await Task.Run(() => S.Backup.CreateSnapshot(Loc.T("Ручной бэкап настроек", "Manual settings backup"), BackupKind.Manual, targets));
            S.Toasts.Show(Loc.T("Бэкап создан", "Backup created"), DialogKind.Success);
            Reload();
        }
        finally
        {
            IsBusy = false;
        }
    }, Loc.T("Не удалось создать бэкап", "Failed to create backup"));

    [RelayCommand]
    private Task RestoreAll() => RestoreAsync(null);

    [RelayCommand]
    private Task RestoreSelected() => RestoreAsync(Items.Where(i => i.IsSelected && i.CanRestore).Select(i => i.Item.Id).ToList());

    private Task RestoreAsync(IReadOnlyCollection<string>? ids) => RunSafeAsync(async () =>
    {
        if (Selected is not { } row)
        {
            return;
        }

        var count = ids?.Count ?? row.Record.Items.Count(i => i.State != BackupItemState.Skipped);
        if (count == 0)
        {
            return;
        }

        if (!await S.Dialogs.ConfirmAsync(
                Loc.T("Восстановить значения", "Restore values"),
                Loc.T($"Будет восстановлено элементов: {count} — значения вернутся к состоянию на момент бэкапа «{row.Title}». Изменения в HKLM запросят права администратора.", $"Items to restore: {count} — values return to the state captured in \"{row.Title}\". HKLM changes will ask for administrator rights."),
                Loc.T("Восстановить", "Restore")))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await S.Backup.RestoreAsync(row.Record, ids);
            var lines = new List<string> { Loc.T($"Восстановлено: {result.Restored}", $"Restored: {result.Restored}") };
            lines.AddRange(result.Failures.Select(f => f.Title + ": " + f.Error));
            if (result.RequiresRestart)
            {
                lines.Add(Loc.T("Нужна перезагрузка.", "A restart is required."));
            }

            await S.Dialogs.AlertAsync(
                result.Success ? Loc.T("Восстановлено", "Restored") : Loc.T("Восстановлено частично", "Partially restored"),
                result.Success ? Loc.T("Значения восстановлены и проверены.", "Values restored and verified.") : Loc.T("Часть значений не удалось восстановить.", "Some values could not be restored."),
                result.Success ? DialogKind.Success : DialogKind.Warning,
                new TextLines(lines));
            Reload();
        }
        finally
        {
            IsBusy = false;
        }
    }, Loc.T("Ошибка восстановления", "Restore error"));

    [RelayCommand]
    private Task Delete() => RunSafeAsync(async () =>
    {
        if (Selected is not { } row)
        {
            return;
        }

        if (!await S.Dialogs.ConfirmAsync(Loc.T("Удалить бэкап?", "Delete backup?"), Loc.T($"Бэкап «{row.Title}» от {row.Date} будет удалён без возможности восстановления.", $"Backup \"{row.Title}\" from {row.Date} will be permanently deleted."), Loc.T("Удалить", "Delete"), danger: true))
        {
            return;
        }

        S.Backup.Delete(row.Record);
        Selected = null;
        Reload();
    }, Loc.T("Не удалось удалить бэкап", "Failed to delete backup"));

    [RelayCommand]
    private void OpenFolder() => ChangeRunner.OpenFolder(Selected?.Record.Directory ?? S.Backup.BackupRoot);

    /// <summary>Точка восстановления системы — по явному запросу, с честным пояснением про лимит 24 часа.</summary>
    [RelayCommand]
    private Task CreateRestorePoint() => RunSafeAsync(async () =>
    {
        if (!await S.Dialogs.ConfirmAsync(
                Loc.T("Точка восстановления системы", "System restore point"),
                Loc.T("Требуются права администратора и включённая защита системы для диска C:. Windows по умолчанию создаёт не больше одной точки за 24 часа — если точка уже создавалась сегодня, новая может не появиться.", "Requires administrator rights and System Protection enabled on drive C:. By default Windows creates at most one restore point per 24 hours — if one was created today, a new one may not appear."),
                Loc.T("Создать", "Create"),
                kind: DialogKind.Info))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await S.Elevation.RunAsync(new ElevatedTaskRequest { Operation = ElevatedOperation.CreateRestorePoint, Description = "NOVA TUNE" });
            S.Journal.Add("restore-point", Loc.T("Точка восстановления", "Restore point"), result.Success, result.Output ?? result.Error);
            await S.Dialogs.AlertAsync(Loc.T("Точка восстановления", "Restore point"), result.Output ?? result.Error ?? "", result.Success ? DialogKind.Success : DialogKind.Error);
        }
        catch (Core.Common.ElevationCancelledException)
        {
            S.Toasts.Show(Loc.T("Запрос прав администратора отклонён", "Administrator request declined"));
        }
        finally
        {
            IsBusy = false;
        }
    }, Loc.T("Не удалось создать точку восстановления", "Failed to create restore point"));
}
