using NovaTune.App.ViewModels;
using NovaTune.Core.Backup;
using NovaTune.Core.Localization;

namespace NovaTune.App.Services;

/// <summary>
/// Единый сценарий изменения системы: предпросмотр → подтверждение → бэкап → применение →
/// повторное чтение (проверка) → итоговый отчёт. Используется всеми разделами, которые что-то меняют.
/// </summary>
public static class ChangeRunner
{
    /// <returns>Результат или null, если пользователь отменил.</returns>
    public static async Task<TransactionResult?> ApplyAsync(string title, IReadOnlyList<PlannedChange> changes, string? extraWarning = null)
    {
        var s = AppServices.Current;
        if (changes.Count == 0)
        {
            await s.Dialogs.AlertAsync(title, Loc.T("Изменять нечего — всё уже в нужном состоянии.", "Nothing to change — everything is already in the target state."), DialogKind.Success);
            return null;
        }

        if (s.Backup.IsBusy)
        {
            await s.Dialogs.AlertAsync(title, Loc.T("Другая операция ещё выполняется. Дождитесь её завершения.", "Another operation is still running. Please wait."), DialogKind.Warning);
            return null;
        }

        var previews = await Task.Run(() => changes.Select(s.Backup.Preview).ToList());
        var message = Loc.T(
            "Будет создан бэкап текущих значений, затем применены изменения ниже. Каждое изменение проверяется повторным чтением. Откатить можно в разделе «Бэкапы».",
            "A backup of the current values will be created, then the changes below will be applied. Each change is verified by reading it back. You can roll back in Backup & Restore.");
        if (extraWarning is not null)
        {
            message += "\n\n" + extraWarning;
        }

        if (!await s.Dialogs.ConfirmAsync(title, message, Loc.T("Создать бэкап и применить", "Back up and apply"), details: PreviewList.From(previews), kind: DialogKind.Info))
        {
            return null;
        }

        TransactionResult result;
        try
        {
            result = await s.Backup.ApplyAsync(title, changes);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            s.Log.Error("Ошибка применения: " + title, ex);
            await s.Dialogs.AlertAsync(Loc.T("Не удалось применить", "Failed to apply"), ex.Message, DialogKind.Error);
            return null;
        }

        var lines = new List<string>
        {
            Loc.T($"Применено: {result.Applied}", $"Applied: {result.Applied}"),
            Loc.T($"Уже было в нужном состоянии: {result.Skipped}", $"Already in target state: {result.Skipped}"),
        };
        lines.AddRange(result.Failures.Select(f => Loc.T("Не выполнено — ", "Not applied — ") + f.Title + ": " + f.Error));
        if (result.RequiresRestart)
        {
            lines.Add(Loc.T("Нужна перезагрузка, чтобы изменения вступили в силу.", "A restart is required for the changes to take effect."));
        }

        lines.Add(Loc.T("Бэкап: ", "Backup: ") + result.Backup.Id);

        await s.Dialogs.AlertAsync(
            result.Success ? Loc.T("Готово", "Done") : Loc.T("Выполнено частично", "Partially completed"),
            result.Success
                ? Loc.T("Изменения применены и проверены.", "Changes applied and verified.")
                : Loc.T("Часть изменений не применена. Остальные применены и проверены; всё можно откатить в разделе «Бэкапы».", "Some changes were not applied. The rest were applied and verified; everything can be rolled back in Backup & Restore."),
            result.Success ? DialogKind.Success : DialogKind.Warning,
            new TextLines(lines));
        return result;
    }

    public static void OpenFolder(string path)
    {
        if (Directory.Exists(path) || File.Exists(path))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        else
        {
            AppServices.Current.Toasts.Show(Loc.T("Папка не найдена: ", "Folder not found: ") + path, DialogKind.Warning);
        }
    }

    public static void OpenUrl(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
}
