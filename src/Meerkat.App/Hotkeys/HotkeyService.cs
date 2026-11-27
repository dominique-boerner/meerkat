using System.Windows.Input;
using System.Windows.Interop;
using Meerkat.App.Interop;

namespace Meerkat.App.Hotkeys;

public sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _messageWindow;
    private readonly Dictionary<int, (HotkeyGesture Gesture, Action Handler)> _registrations = [];

    private int _suspensions;

    public HotkeyService()
    {
        // A message-only window receives WM_HOTKEY independently of the lifetime and visibility of the other windows.
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

        // While suspended the registration is only remembered; it takes effect when the suspension ends.
        if (_suspensions == 0 && !RegisterNative(id, gesture))
        {
            return false;
        }

        _registrations[id] = (gesture, handler);
        return true;
    }

    public void Unregister(int id)
    {
        if (_registrations.Remove(id) && _suspensions == 0)
        {
            NativeMethods.UnregisterHotKey(_messageWindow.Handle, id);
        }
    }

    /// <summary>
    /// Releases all hotkeys until the returned handle is disposed. A registered hotkey is swallowed by Windows,
    /// so it has to be released before the user can press it to assign it again.
    /// </summary>
    public IDisposable Suspend()
    {
        if (_suspensions++ == 0)
        {
            foreach (var id in _registrations.Keys)
            {
                NativeMethods.UnregisterHotKey(_messageWindow.Handle, id);
            }
        }

        return new Suspension(this);
    }

    public void Dispose()
    {
        foreach (var id in _registrations.Keys.ToList())
        {
            Unregister(id);
        }

        _messageWindow.RemoveHook(WndProc);
        _messageWindow.Dispose();
    }

    private void Resume()
    {
        if (--_suspensions == 0)
        {
            foreach (var (id, registration) in _registrations)
            {
                RegisterNative(id, registration.Gesture);
            }
        }
    }

    private bool RegisterNative(int id, HotkeyGesture gesture) => NativeMethods.RegisterHotKey(
        _messageWindow.Handle,
        id,
        (uint)gesture.Modifiers | NativeMethods.ModNoRepeat,
        (uint)KeyInterop.VirtualKeyFromKey(gesture.Key));

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotkey && _registrations.TryGetValue(wParam.ToInt32(), out var registration))
        {
            registration.Handler();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private sealed class Suspension(HotkeyService service) : IDisposable
    {
        private HotkeyService? _service = service;

        public void Dispose()
        {
            _service?.Resume();
            _service = null;
        }
    }
}
