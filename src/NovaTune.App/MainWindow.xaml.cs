using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NovaTune.App.Services;
using NovaTune.App.ViewModels;
using NovaTune.Core.Localization;
using NovaTune.Platform;

namespace NovaTune.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        FitToWorkArea();

        vm.PropertyChanged += OnViewModelChanged;
        vm.Dialogs.PropertyChanged += OnDialogChanged;
        StateChanged += (_, _) => UpdateMaximizedState();
        SourceInitialized += (_, _) =>
        {
            ApplyWindows11Frame();
            RegisterHotkeys();
        };
        Closed += (_, _) => _hotkeys?.Dispose();
    }

    private HotkeyService? _hotkeys;

    /// <summary>Глобальные горячие клавиши записи и оверлея привязаны к окну приложения.</summary>
    private void RegisterHotkeys()
    {
        if (HwndSource.FromHwnd(new WindowInteropHelper(this).Handle) is not { } source)
        {
            return;
        }

        var capture = AppServices.Current.Capture;
        _hotkeys = new HotkeyService(source, capture.OnHotkey);
        capture.HotkeyStatus = (_hotkeys.RecordRegistered, _hotkeys.OverlayRegistered) switch
        {
            (true, true) => "",
            _ => Loc.T("Часть горячих клавиш занята другой программой: ", "Some hotkeys are taken by another app: ")
                + string.Join(", ", new[] { _hotkeys.RecordRegistered ? null : "Ctrl+Alt+R", _hotkeys.OverlayRegistered ? null : "Ctrl+Alt+O" }.OfType<string>()),
        };
        if (!_hotkeys.RecordRegistered || !_hotkeys.OverlayRegistered)
        {
            AppServices.Current.Log.Warn("Горячие клавиши: " + capture.HotkeyStatus);
        }
    }

    /// <summary>Размер окна по умолчанию не больше 92% рабочей области (ноутбуки 1366×768, масштаб 125–200%).</summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width * 0.92);
        Height = Math.Min(Height, area.Height * 0.92);
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.CurrentPage))
        {
            return;
        }

        // Смена страницы: короткое появление со сдвигом.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        PageShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }

    private void OnDialogChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DialogService.Current) || _vm.Dialogs.Current is not { } dialog)
        {
            return;
        }

        var (iconKey, brushKey) = dialog.Kind switch
        {
            DialogKind.Success => ("Icon.Check", "Brush.Success"),
            DialogKind.Warning => ("Icon.Warning", "Brush.Warning"),
            DialogKind.Error => ("Icon.Error", "Brush.Error"),
            _ => ("Icon.Info", "Brush.AccentText"),
        };
        DialogIcon.Data = (Geometry)FindResource(iconKey);
        DialogIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brushKey);
        DialogConfirm.Style = (Style)FindResource(dialog.IsDanger ? "Button.Danger" : "Button.Primary");

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        DialogLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        var scale = new ScaleTransform(0.96, 0.96);
        DialogPanel.RenderTransformOrigin = new Point(0.5, 0.5);
        DialogPanel.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        Dispatcher.BeginInvoke(() => DialogConfirm.Focus());
    }

    /// <summary>
    /// При развороте окно с WindowChrome выходит за край экрана на ширину рамки — компенсируем отступом,
    /// иначе содержимое обрезается. Панель задач окно не перекрывает (WindowStyle остаётся стандартным).
    /// </summary>
    private void UpdateMaximizedState()
    {
        var maximized = WindowState == WindowState.Maximized;
        var frame = SystemParameters.WindowResizeBorderThickness;
        Root.Margin = maximized ? new Thickness(frame.Left + 1, frame.Top + 1, frame.Right + 1, frame.Bottom + 1) : new Thickness(0);
        MaxButton.SetValue(Controls.Ui.IconProperty, FindResource(maximized ? "Icon.Restore" : "Icon.Maximize"));
        MaxButton.ToolTip = maximized ? Loc.T("Восстановить", "Restore") : Loc.T("Развернуть", "Maximize");
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>Активация окна при повторном запуске приложения.</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    // ───────── Windows 11: скруглённые углы и тёмная рамка (в Windows 10 атрибуты игнорируются) ─────────

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void ApplyWindows11Frame()
    {
        if (!OsInfo.Current.IsWindows11)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        var round = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
        var dark = ThemeService.Theme == Core.Settings.AppTheme.Dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
    }
}
