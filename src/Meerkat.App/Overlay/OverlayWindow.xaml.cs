using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Meerkat.App.Interop;

namespace Meerkat.App.Overlay;

public partial class OverlayWindow : Window
{
    private WidgetHostViewModel? _draggedHost;
    private Point _dragStartPointer;
    private Point _dragStartPosition;
    private IntPtr _windowToRestoreFocusTo;

    public OverlayWindow()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user finished moving or resizing a widget.</summary>
    public event EventHandler? LayoutCommitted;

    private IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

    public void SetClickThrough(bool enabled)
    {
        if (enabled)
        {
            ReleaseKeyboardFocus();
        }

        SetExtendedStyle(NativeMethods.WsExTransparent, enabled);
    }

    /// <summary>Positions the window using physical pixels of the virtual screen.</summary>
    public void MoveToBounds(System.Drawing.Rectangle physicalBounds)
    {
        Apply();

        // Crossing onto a monitor with a different DPI makes WPF rescale the window after the first move,
        // so the bounds are applied once more when that has settled.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, Apply);

        void Apply() => NativeMethods.SetWindowPos(
            Handle,
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

        // Tool window keeps the overlay out of Alt+Tab; no-activate keeps focus in the application below.
        SetExtendedStyle(NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow, enabled: true);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);

        // Typing needs keyboard focus, which a no-activate window never gets. Clicking into a text box
        // is an explicit request for it, so the overlay activates until focus moves elsewhere.
        if (!IsActive && e.OriginalSource is Visual source && HasAncestor<TextBoxBase>(source))
        {
            _windowToRestoreFocusTo = NativeMethods.GetForegroundWindow();
            SetExtendedStyle(NativeMethods.WsExNoActivate, enabled: false);
            Activate();
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        Keyboard.ClearFocus();
        SetExtendedStyle(NativeMethods.WsExNoActivate, enabled: true);
    }

    private void ReleaseKeyboardFocus()
    {
        if (IsActive && _windowToRestoreFocusTo != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(_windowToRestoreFocusTo);
        }

        _windowToRestoreFocusTo = IntPtr.Zero;
    }

    private void SetExtendedStyle(long flags, bool enabled)
    {
        var handle = Handle;
        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        style = enabled ? style | flags : style & ~flags;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new IntPtr(style));
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WidgetHostViewModel host } header && header.CaptureMouse())
        {
            _draggedHost = host;
            _dragStartPointer = e.GetPosition(this);
            _dragStartPosition = new Point(host.Left, host.Top);
            e.Handled = true;
        }
    }

    private void OnHeaderMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedHost is null)
        {
            return;
        }

        // Positions are derived from the drag start so the widget stays under the cursor after hitting an edge.
        var offset = e.GetPosition(this) - _dragStartPointer;
        _draggedHost.MoveTo(_dragStartPosition.X + offset.X, _dragStartPosition.Y + offset.Y, RenderSize);
    }

    private void OnHeaderMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggedHost is null)
        {
            return;
        }

        _draggedHost = null;
        ((FrameworkElement)sender).ReleaseMouseCapture();
        LayoutCommitted?.Invoke(this, EventArgs.Empty);
    }

    private void OnResizeDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WidgetHostViewModel host })
        {
            host.ResizeBy(e.HorizontalChange, e.VerticalChange, RenderSize);
        }
    }

    private void OnResizeCompleted(object sender, DragCompletedEventArgs e) =>
        LayoutCommitted?.Invoke(this, EventArgs.Empty);

    private static bool HasAncestor<T>(DependencyObject? element)
        where T : DependencyObject
    {
        for (; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is T)
            {
                return true;
            }
        }

        return false;
    }
}
