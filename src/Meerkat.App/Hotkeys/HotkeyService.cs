using System.Windows.Input;
using System.Windows.Interop;
using Meerkat.App.Interop;

namespace Meerkat.App.Hotkeys;

public sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _messageWindow;
    private readonly Dictionary<int, Action> _handlers = [];

    public HotkeyService()
    {
        // A message-only window receives WM_HOTKEY independently of the overlay window's lifetime and visibility.
        var parameters = new HwndSourceParameters("Meerkat.Hotkeys")
        {
            ParentWindow = NativeMethods.HwndMessage,
            WindowStyle = 0,
        };
        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(WndProc);
    }

    /// <summary>Returns false when the gesture is already claimed by another application.</summary>
    public bool Register(int id, HotkeyGesture gesture, Action handler)
    {
        Unregister(id);

        var modifiers = (uint)gesture.Modifiers | NativeMethods.ModNoRepeat;
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(gesture.Key);
        if (!NativeMethods.RegisterHotKey(_messageWindow.Handle, id, modifiers, virtualKey))
        {
            return false;
        }

        _handlers[id] = handler;
        return true;
    }

    public void Unregister(int id)
    {
        if (_handlers.Remove(id))
        {
            NativeMethods.UnregisterHotKey(_messageWindow.Handle, id);
        }
    }

    public void Dispose()
    {
        foreach (var id in _handlers.Keys.ToList())
        {
            Unregister(id);
        }

        _messageWindow.RemoveHook(WndProc);
        _messageWindow.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotkey && _handlers.TryGetValue(wParam.ToInt32(), out var handler))
        {
            handler();
            handled = true;
        }

        return IntPtr.Zero;
    }
}
