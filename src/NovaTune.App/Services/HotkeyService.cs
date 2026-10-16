using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace NovaTune.App.Services;

/// <summary>
/// Глобальные горячие клавиши (работают и когда активна игра): Ctrl+Alt+R — запись, Ctrl+Alt+O — оверлей.
/// Регистрируются штатным RegisterHotKey — без перехвата клавиатуры (хуков), поэтому античиты к этому равнодушны.
/// Alt+F9/Alt+F10 не используются: они заняты NVIDIA ShadowPlay.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    public const int RecordId = 0x4E01;
    public const int OverlayId = 0x4E02;

    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1;
    private const uint ModControl = 0x2;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkR = 0x52;
    private const uint VkO = 0x4F;

    private readonly HwndSource _source;
    private readonly Action<int> _handler;

    public HotkeyService(HwndSource source, Action<int> handler)
    {
        _source = source;
        _handler = handler;
        _source.AddHook(Hook);
        RecordRegistered = RegisterHotKey(_source.Handle, RecordId, ModControl | ModAlt | ModNoRepeat, VkR);
        OverlayRegistered = RegisterHotKey(_source.Handle, OverlayId, ModControl | ModAlt | ModNoRepeat, VkO);
    }

    /// <summary>false — сочетание уже занято другой программой.</summary>
    public bool RecordRegistered { get; }

    public bool OverlayRegistered { get; }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && (int)wParam is RecordId or OverlayId)
        {
            handled = true;
            _handler((int)wParam);
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterHotKey(_source.Handle, RecordId);
        UnregisterHotKey(_source.Handle, OverlayId);
        _source.RemoveHook(Hook);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
