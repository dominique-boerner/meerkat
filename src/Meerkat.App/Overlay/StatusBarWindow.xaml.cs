using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Meerkat.App.Interop;

namespace Meerkat.App.Overlay;

/// <summary>The always-visible status display. It never takes input: every click passes through to the window below.</summary>
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

        // Transparent lets clicks through, no-activate keeps focus in the application below,
        // and tool window keeps the bar out of Alt+Tab.
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        style |= NativeMethods.WsExTransparent | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new IntPtr(style));
    }
}
