using CommunityToolkit.Mvvm.ComponentModel;
using NovaTune.App.Services;
using NovaTune.Core.Localization;

namespace NovaTune.App.ViewModels;

/// <summary>Базовый класс страниц: загрузка при открытии, флаги занятости/ошибки, доступ к сервисам.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorText;

    protected static AppServices S => AppServices.Current;

    public abstract string Title { get; }

    public abstract string Subtitle { get; }

    /// <summary>Отмена фоновых операций страницы при уходе с неё.</summary>
    protected CancellationTokenSource PageCts { get; private set; } = new();

    private bool _loadedOnce;

    public async Task NavigatedToAsync()
    {
        if (PageCts.IsCancellationRequested)
        {
            PageCts.Dispose();
            PageCts = new CancellationTokenSource();
        }

        try
        {
            await OnNavigatedToAsync(!_loadedOnce);
            _loadedOnce = true;
        }
        catch (OperationCanceledException)
        {
            // Пользователь ушёл со страницы — это не ошибка.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            S.Log.Error($"Ошибка загрузки страницы {GetType().Name}", ex);
            ErrorText = Loc.T("Не удалось загрузить данные: ", "Failed to load data: ") + ex.Message;
            IsLoading = false;
        }
    }

    public void NavigatedFrom()
    {
        PageCts.Cancel();
        OnNavigatedFrom();
    }

    protected virtual Task OnNavigatedToAsync(bool firstTime) => Task.CompletedTask;

    protected virtual void OnNavigatedFrom()
    {
    }

    /// <summary>Выполняет действие с перехватом ошибок: ошибка логируется и показывается понятным сообщением.</summary>
    protected async Task RunSafeAsync(Func<Task> action, string errorTitle)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // Отмена — штатный сценарий.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            S.Log.Error(errorTitle, ex);
            await S.Dialogs.AlertAsync(errorTitle, ex.Message, DialogKind.Error);
        }
    }
}
