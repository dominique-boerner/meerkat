using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Meerkat.App.Interop;

namespace Meerkat.App.Overlay;

/// <summary>
/// The always-visible status display. Its bar and cards can be clicked to open the panel, but the window
/// never becomes the active one, so the application the user works in keeps the keyboard focus.
/// </summary>
public partial class StatusBarWindow : Window
{
    public StatusBarWindow()
    {
        InitializeComponent();
    }

    /// <summary>Positions the window using physical pixels of the virtual screen.</summary>
    public void MoveToBounds(System.Drawing.Rectangle physicalBounds)
    {
        Apply();

        // Crossing onto a monitor with a different DPI makes WPF rescale the window after the first move,
        // so the bounds are applied once more when that has settled.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, Apply);

        void Apply() => NativeMethods.SetWindowPos(
            new WindowInteropHelper(this).EnsureHandle(),
            NativeMethods.HwndTopmost,
            physicalBounds.X,
            physicalBounds.Y,
            physicalBounds.Width,
            physicalBounds.Height,
            NativeMethods.SwpNoActivate);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // No-activate keeps the focus in the application below even when the bar is clicked, and tool window
        // keeps the bar out of Alt+Tab. The window spans the whole work area, but Windows only delivers clicks
        // where something is drawn; everywhere else they reach the window underneath.
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        style |= NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new IntPtr(style));
    }
}
