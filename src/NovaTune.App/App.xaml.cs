using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using NovaTune.App.Services;
using NovaTune.App.ViewModels;
using NovaTune.Core.Backup;
using NovaTune.Core.Common;
using NovaTune.Core.Localization;
using NovaTune.Core.Logging;
using NovaTune.Core.Settings;
using NovaTune.Platform;

namespace NovaTune.App;

public partial class App : Application
{
    public const string RelaunchArgument = "--relaunch";
    private const string MutexName = @"Local\NovaTune.SingleInstance";
    private const string ActivateEventName = @"Local\NovaTune.Activate";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private FileLog? _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        var paths = AppPaths.Default();
        paths.EnsureCreated();
        _log = new FileLog(paths.Logs);

        // ── Повышенный процесс: одна операция из белого списка, без окна.
        if (args.Length >= 2 && args[0] == ElevationService.Argument)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(ElevatedTaskHost.RunFromFile(args[1], _log));
            return;
        }

        // ── Самопроверка: чтение реальных данных системы в JSON/TXT, без окна (для CI и диагностики).
        if (args.Length >= 2 && args[0] == "--self-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(SelfTest.Run(args[1], _log));
            return;
        }

        // ── Проверка записи экрана: 3 секунды основного монитора в MP4 (для CI и диагностики).
        if (args.Length >= 2 && args[0] == "--record-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(RecordTest.Run(args[1], _log));
            return;
        }

        var screenshotDir = ArgValue(args, "--screenshot");

        // ── Один экземпляр: при повторном запуске активируем уже открытое окно.
        _mutex = new Mutex(initiallyOwned: false, MutexName);
        var waitMs = args.Contains(RelaunchArgument) ? 15000 : 0;
        bool owned;
        try
        {
            owned = _mutex.WaitOne(waitMs);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // предыдущий экземпляр аварийно завершился
        }

        if (!owned)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ActivateEventName);
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                _log.Warn("Второй экземпляр: не найдено событие активации первого окна");
            }

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(0);
            return;
        }

        RegisterExceptionHandlers();

        var services = new AppServices(paths, _log);
        AppServices.Current = services;
        var settings = services.Settings.Current;
        ApplyLanguage(ArgValue(args, "--lang") == "en" ? AppLanguage.En : ArgValue(args, "--lang") == "ru" ? AppLanguage.Ru : settings.Language);
        ThemeService.Apply(ArgValue(args, "--theme") == "light" ? AppTheme.Light : ArgValue(args, "--theme") == "dark" ? AppTheme.Dark : settings.Theme);

        _log.Info($"Запуск NOVA TUNE {typeof(App).Assembly.GetName().Version}, Windows {OsInfo.Current.FullBuild}, администратор: {OsInfo.IsElevated()}");

        MainViewModel vm;
        MainWindow window;
        try
        {
            vm = new MainViewModel();
            window = new MainWindow(vm);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FailStartup(ex);
            return;
        }

        if (ArgValue(args, "--size") is { } size && size.Split('x') is [var w, var h]
            && double.TryParse(w, CultureInfo.InvariantCulture, out var width) && double.TryParse(h, CultureInfo.InvariantCulture, out var height))
        {
            window.Width = width;
            window.Height = height;
        }

        MainWindow = window;
        try
        {
            window.Show();
            vm.Navigate("dashboard");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FailStartup(ex);
            return;
        }

        StartActivationListener(window);

        if (screenshotDir is not null)
        {
            _ = ScreenshotRunner.RunAsync(window, vm, screenshotDir, _log).ContinueWith(t => Dispatcher.Invoke(() => Shutdown(t.IsFaulted ? 1 : 0)));
            return;
        }

        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _ = CheckInterruptedOperationsAsync(vm));
    }

    /// <summary>
    /// Ошибка при создании окна: показываем сообщение и полностью завершаем процесс.
    /// Иначе процесс остался бы висеть без окна, удерживая Mutex, и повторный запуск «ничего бы не делал».
    /// </summary>
    private void FailStartup(Exception ex)
    {
        _log?.Error("Не удалось открыть главное окно", ex);
        MessageBox.Show(
            Loc.T("NOVA TUNE не смог открыть окно и будет закрыт.\n\n", "NOVA TUNE could not open its window and will close.\n\n") + ex.Message
            + (ex.InnerException is null ? "" : "\n" + ex.InnerException.Message)
            + Loc.T("\n\nЖурнал: ", "\n\nLog: ") + AppPaths.Default().Logs,
            "NOVA TUNE",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        ReleaseSingleInstance();
        Shutdown(1);
    }

    public static void ApplyLanguage(AppLanguage language)
    {
        var culture = CultureInfo.GetCultureInfo(language == AppLanguage.En ? "en-US" : "ru-RU");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        Loc.Instance.Language = language;
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Операция, прерванная сбоем или выключением ПК, — предлагаем откат.</summary>
    private static async Task CheckInterruptedOperationsAsync(MainViewModel vm)
    {
        var s = AppServices.Current;
        IReadOnlyList<BackupRecord> incomplete;
        try
        {
            incomplete = s.Backup.FindIncomplete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            s.Log.Error("Не удалось проверить незавершённые операции", ex);
            return;
        }

        foreach (var record in incomplete)
        {
            var applied = record.Items.Count(i => i.State is BackupItemState.Applied or BackupItemState.Applying);
            var rollback = await s.Dialogs.ConfirmAsync(
                Loc.T("Найдена незавершённая операция", "Interrupted operation found"),
                Loc.T(
                    $"Операция «{record.Title}» от {record.CreatedAt.LocalDateTime:g} не была завершена (возможно, из-за сбоя или выключения ПК). Применено шагов: {applied} из {record.Items.Count}. Откатить изменения к состоянию до операции?",
                    $"Operation \"{record.Title}\" from {record.CreatedAt.LocalDateTime:g} did not finish (possibly due to a crash or power loss). Steps applied: {applied} of {record.Items.Count}. Roll back to the state before the operation?"),
                Loc.T("Откатить", "Roll back"));
            try
            {
                if (rollback)
                {
                    var result = await s.Backup.RestoreAsync(record);
                    s.Toasts.Show(result.Success ? Loc.T("Изменения откачены", "Changes rolled back") : Loc.T("Откат выполнен частично — подробности в разделе «Бэкапы»", "Partially rolled back — see Backup & Restore"),
                        result.Success ? DialogKind.Success : DialogKind.Warning);
                }
                else
                {
                    s.Backup.MarkIncompleteAsAccepted(record);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                s.Log.Error("Ошибка обработки незавершённой операции", ex);
                await s.Dialogs.AlertAsync(Loc.T("Ошибка отката", "Rollback error"), ex.Message, DialogKind.Error);
            }
        }
    }

    private void StartActivationListener(MainWindow window)
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var thread = new Thread(() =>
        {
            try
            {
                while (_activateEvent.WaitOne())
                {
                    Dispatcher.BeginInvoke(window.BringToFront);
                }
            }
            catch (ObjectDisposedException)
            {
                // Приложение закрывается.
            }
        })
        {
            IsBackground = true,
            Name = "NovaTune activation listener",
        };
        thread.Start();
    }

    /// <summary>Глобальная обработка исключений: ошибка логируется и показывается понятным сообщением.</summary>
    private void RegisterExceptionHandlers()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            _log?.Error("Необработанное исключение в UI", e.Exception);
            e.Handled = true;
            if (MainWindow is not { IsLoaded: true })
            {
                FailStartup(e.Exception);
                return;
            }

            ShowCrashMessage(e.Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _log?.Error("Необработанное исключение в фоновой задаче", e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            _log?.Error("Критическая ошибка, приложение будет закрыто", e.ExceptionObject as Exception);
            MessageBox.Show(
                Loc.T("Произошла критическая ошибка. Подробности записаны в журнал: ", "A critical error occurred. Details were written to the log: ") + AppPaths.Default().Logs,
                "NOVA TUNE",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        };
    }

    private static void ShowCrashMessage(Exception ex)
    {
        var text = Loc.T("Операция завершилась ошибкой. Приложение продолжает работу. Подробности — в журнале.", "The operation failed. The app keeps running. Details are in the log.") + "\n\n" + ex.Message;
        if (AppServices.Current?.Dialogs is { } dialogs && Current?.MainWindow?.IsLoaded == true)
        {
            _ = dialogs.AlertAsync(Loc.T("Ошибка", "Error"), text, DialogKind.Error);
        }
        else
        {
            MessageBox.Show(text, "NOVA TUNE", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Перезапуск с правами администратора (для расширенных датчиков). Отказ UAC — не ошибка.</summary>
    public static bool RelaunchElevated()
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            psi.ArgumentList.Add(RelaunchArgument);
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;
        }

        // Освобождаем Mutex до выхода, чтобы новый экземпляр сразу его получил.
        if (Current is App app)
        {
            app.ReleaseSingleInstance();
        }

        Current?.Shutdown();
        return true;
    }

    private void ReleaseSingleInstance()
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Mutex уже освобождён.
        }

        _mutex?.Dispose();
        _mutex = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppServices.Current?.Dispose();
        _activateEvent?.Dispose();
        ReleaseSingleInstance();
        _log?.Info("Выход");
        base.OnExit(e);
    }
}
