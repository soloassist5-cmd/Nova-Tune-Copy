using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovaTune.Core.Localization;

namespace NovaTune.App.Services;

public enum DialogKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>Модальный диалог внутри окна (единый стиль подтверждений и ошибок).</summary>
public sealed partial class DialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _tcs = new();

    public required string Title { get; init; }

    public required string Message { get; init; }

    public string ConfirmText { get; init; } = "OK";

    /// <summary>null — кнопки отмены нет (информационное сообщение).</summary>
    public string? CancelText { get; init; }

    public bool IsDanger { get; init; }

    public DialogKind Kind { get; init; } = DialogKind.Info;

    /// <summary>Дополнительное содержимое (например, предпросмотр изменений) — отрисовывается DataTemplate.</summary>
    public object? Details { get; init; }

    public Task<bool> Result => _tcs.Task;

    [RelayCommand]
    private void Confirm() => _tcs.TrySetResult(true);

    [RelayCommand]
    private void Cancel() => _tcs.TrySetResult(false);
}

public sealed partial class DialogService : ObservableObject
{
    private readonly SemaphoreSlim _queue = new(1, 1);

    [ObservableProperty]
    private DialogViewModel? _current;

    public async Task<bool> ShowAsync(DialogViewModel dialog)
    {
        await _queue.WaitAsync();
        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() => Current = dialog);
            return await dialog.Result;
        }
        finally
        {
            await Application.Current.Dispatcher.InvokeAsync(() => Current = null);
            _queue.Release();
        }
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger = false, object? details = null, DialogKind kind = DialogKind.Warning) =>
        ShowAsync(new DialogViewModel
        {
            Title = title,
            Message = message,
            ConfirmText = confirmText,
            CancelText = Loc.T("Отмена", "Cancel"),
            IsDanger = danger,
            Details = details,
            Kind = kind,
        });

    public Task AlertAsync(string title, string message, DialogKind kind = DialogKind.Info, object? details = null) =>
        ShowAsync(new DialogViewModel { Title = title, Message = message, Kind = kind, Details = details });
}

public sealed class ToastItem
{
    public required string Text { get; init; }

    public DialogKind Kind { get; init; }
}

/// <summary>Уведомления внутри окна (вместо системных toast, которым в Windows 10 нужен ярлык и AUMID).</summary>
public sealed class ToastService
{
    public ObservableCollection<ToastItem> Items { get; } = [];

    public void Show(string text, DialogKind kind = DialogKind.Info)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.InvokeAsync(async () =>
        {
            var item = new ToastItem { Text = text, Kind = kind };
            Items.Add(item);
            while (Items.Count > 3)
            {
                Items.RemoveAt(0);
            }

            await Task.Delay(kind == DialogKind.Error ? 7000 : 4200);
            Items.Remove(item);
        });
    }
}
