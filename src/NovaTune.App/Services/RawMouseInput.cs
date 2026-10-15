using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NovaTune.App.Services;

/// <summary>
/// Подписка на Raw Input мыши для окна приложения (WM_INPUT). Каждое событие с ненулевым смещением —
/// один отчёт мыши; по их отметкам времени считается частота опроса. Регистрация снимается при Dispose.
/// </summary>
public sealed class RawMouseInput : IDisposable
{
    private const int WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeMouse = 0;
    private const uint RidevRemove = 0x00000001;

    private readonly HwndSource _source;
    private readonly Action<long> _onMove;
    private readonly byte[] _buffer = new byte[64];

    public RawMouseInput(Window window, Action<long> onMove)
    {
        _onMove = onMove;
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle) ?? throw new InvalidOperationException("Окно ещё не создано.");
        Register(_source.Handle, 0);
        _source.AddHook(Hook);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmInput)
        {
            return IntPtr.Zero;
        }

        var tick = Stopwatch.GetTimestamp();
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        uint size = (uint)_buffer.Length;
        if (GetRawInputData(lParam, RidInput, _buffer, ref size, headerSize) == unchecked((uint)-1))
        {
            return IntPtr.Zero;
        }

        if (BitConverter.ToUInt32(_buffer, 0) != RimTypeMouse)
        {
            return IntPtr.Zero;
        }

        // RAWMOUSE после заголовка: usFlags(2) + выравнивание(2) + ulButtons(4) + ulRawButtons(4) + lLastX(4) + lLastY(4).
        var offset = (int)headerSize;
        var dx = BitConverter.ToInt32(_buffer, offset + 12);
        var dy = BitConverter.ToInt32(_buffer, offset + 16);
        if (dx != 0 || dy != 0)
        {
            _onMove(tick);
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(Hook);
        Register(IntPtr.Zero, RidevRemove);
    }

    private static void Register(IntPtr hwnd, uint flags)
    {
        var device = new RawInputDevice[] { new() { UsagePage = 0x01, Usage = 0x02, Flags = flags, Target = hwnd } };
        if (!RegisterRawInputDevices(device, 1, (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, [Out] byte[] data, ref uint size, uint headerSize);
}
