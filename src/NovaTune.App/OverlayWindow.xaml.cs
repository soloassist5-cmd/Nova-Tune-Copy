using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using NovaTune.App.Services;

namespace NovaTune.App;

/// <summary>
/// Прозрачная плашка поверх игр. Отдельное окно, а не внедрение в процесс игры: так безопасно для античитов (VAC и др.).
/// — клики проходят сквозь окно (WS_EX_TRANSPARENT), окно не забирает фокус у игры (WS_EX_NOACTIVATE);
/// — исключено из захвата экрана (WDA_EXCLUDEFROMCAPTURE), поэтому не попадает в запись и стримы;
/// — периодически поднимается наверх: видно в оконном режиме, «без рамки» (F11 в большинстве игр)
///   и в полноэкранном режиме с оптимизацией Windows. В «настоящем» эксклюзивном полноэкранном режиме
///   Windows не показывает поверх игры никакие окна — там поможет переключение игры в режим без рамки.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExLayered = 0x80000;
    private const int WsExNoActivate = 0x08000000;
    private const uint WdaExcludeFromCapture = 0x11;
    private const uint SwpNoSize = 0x1;
    private const uint SwpNoActivate = 0x10;
    private const uint SwpNoOwnerZOrder = 0x200;
    private const uint SwpShowWindow = 0x40;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly CaptureController _controller;
    private IntPtr _hwnd;
    private string? _device;
    private int _corner;

    public OverlayWindow(CaptureController controller)
    {
        InitializeComponent();
        _controller = controller;
        DataContext = controller;
        controller.PropertyChanged += OnControllerChanged;
        SizeChanged += (_, _) => KeepOnTop();
        SourceInitialized += OnSourceInitialized;
        Refresh();
    }

    /// <summary>Исключение из захвата поддерживается с Windows 10 2004; на старых сборках плашка попадёт в запись.</summary>
    public bool ExcludedFromCapture { get; private set; }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(_hwnd, GwlExStyle);
        SetWindowLong(_hwnd, GwlExStyle, style | WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate);
        ExcludedFromCapture = SetWindowDisplayAffinity(_hwnd, WdaExcludeFromCapture);
        if (!ExcludedFromCapture)
        {
            AppServices.Current.Log.Warn("Оверлей: исключение из захвата недоступно (Windows старше 10 2004)");
        }
    }

    public void Place(string? deviceName, int corner)
    {
        _device = deviceName;
        _corner = corner;
        KeepOnTop();
    }

    /// <summary>Обновляет набор блоков по текущим настройкам и состоянию записи.</summary>
    public void Refresh()
    {
        var o = _controller.Options;
        var rec = _controller.IsRecording;
        RecGroup.Visibility = rec ? Visibility.Visible : Visibility.Collapsed;
        StatsGroup.Visibility = o.OverlayShowStats ? Visibility.Visible : Visibility.Collapsed;
        ClockText.Visibility = o.OverlayShowClock ? Visibility.Visible : Visibility.Collapsed;
        BrandText.Visibility = !rec && !o.OverlayShowStats && !o.OverlayShowClock ? Visibility.Visible : Visibility.Collapsed;
        Sep1.Visibility = rec && (o.OverlayShowStats || o.OverlayShowClock) ? Visibility.Visible : Visibility.Collapsed;
        Sep2.Visibility = o.OverlayShowStats && o.OverlayShowClock ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Ставит плашку в выбранный угол монитора и поверх всех окон (игра могла перекрыть её или сменить разрешение).</summary>
    public void KeepOnTop()
    {
        if (_hwnd == IntPtr.Zero || !IsVisible)
        {
            return;
        }

        var monitor = FindMonitor(_device);
        if (!GetWindowRect(_hwnd, out var self))
        {
            return;
        }

        var w = self.Right - self.Left;
        var h = self.Bottom - self.Top;
        var r = monitor.Monitor;
        var x = _corner is 1 or 3 ? r.Left + 8 : r.Right - w - 8;
        var y = _corner is 2 or 3 ? r.Bottom - h - 8 : r.Top + 8;
        SetWindowPos(_hwnd, HwndTopmost, x, y, 0, 0, SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder | SwpShowWindow);
    }

    private void OnControllerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CaptureController.IsRecording))
        {
            Refresh();
        }
    }

    /// <summary>Монитор по имени устройства (\\.\DISPLAY1); не нашли — основной.</summary>
    private static MonitorInfoEx FindMonitor(string? device)
    {
        MonitorInfoEx? found = null;
        MonitorInfoEx? primary = null;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(handle, ref info))
            {
                if ((info.Flags & 1) != 0)
                {
                    primary = info;
                }

                if (device is not null && string.Equals(info.Device, device, StringComparison.OrdinalIgnoreCase))
                {
                    found = info;
                }
            }

            return true;
        }, IntPtr.Zero);
        return found ?? primary ?? new MonitorInfoEx { Monitor = new Rect32 { Right = 1920, Bottom = 1080 } };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public int Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
