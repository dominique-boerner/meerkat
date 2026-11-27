using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Meerkat.App.Interop;

namespace Meerkat.App.Overlay;

/// <summary>The side panel with the widget pages. Unlike the status bar it is a regular window that takes focus.</summary>
public partial class PanelWindow : Window
{
    private const double FallbackSlideDistance = 680;

    private static readonly Duration OpenDuration = TimeSpan.FromMilliseconds(240);
    private static readonly Duration CloseDuration = TimeSpan.FromMilliseconds(170);

    // Identifies the most recent animation, so that a superseded one does not hide a panel that was reopened.
    private int _animationVersion;

    private int _resizeStartCursor;
    private double _resizeStartWidth;

    private WidgetEntryViewModel? _dragCandidate;
    private Point _dragStart;

    public PanelWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Raised when the user presses Escape. Losing the focus does not dismiss the panel: it is meant to stay
    /// next to the work it informs about.
    /// </summary>
    public event EventHandler? Dismissed;

    /// <summary>
    /// Raised while the user drags the panel's edge, with the requested width in device-independent pixels;
    /// null asks for the default width.
    /// </summary>
    public event EventHandler<double?>? WidthRequested;

    /// <summary>Raised when the user finished resizing, so the width can be saved.</summary>
    public event EventHandler? WidthCommitted;

    /// <summary>Positions the window using physical pixels of the virtual screen.</summary>
    public void MoveToBounds(System.Drawing.Rectangle physicalBounds) => NativeMethods.SetWindowPos(
        new WindowInteropHelper(this).EnsureHandle(),
        NativeMethods.HwndTopmost,
        physicalBounds.X,
        physicalBounds.Y,
        physicalBounds.Width,
        physicalBounds.Height,
        NativeMethods.SwpNoActivate);

    /// <summary>Shows the panel, sliding it in from the screen edge.</summary>
    public void Open()
    {
        _animationVersion++;
        if (!IsVisible)
        {
            // Start off-screen; without this the panel would flash at its final position for one frame.
            Slide.BeginAnimation(TranslateTransform.XProperty, null);
            PanelRoot.BeginAnimation(OpacityProperty, null);
            Slide.X = SlideDistance;
            PanelRoot.Opacity = 0;
        }

        Show();
        Activate();
        Animate(targetOffset: 0, targetOpacity: 1, OpenDuration, EasingMode.EaseOut, onCompleted: null);
    }

    /// <summary>Slides the panel out and hides it afterwards.</summary>
    public void Dismiss(bool animated)
    {
        var version = ++_animationVersion;
        if (!animated)
        {
            Hide();
            return;
        }

        Animate(SlideDistance, targetOpacity: 0, CloseDuration, EasingMode.EaseIn, onCompleted: () =>
        {
            if (version == _animationVersion)
            {
                Hide();
            }
        });
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Keeps the panel out of Alt+Tab; it is reached through the hotkey and the tray instead.
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new IntPtr(style | NativeMethods.WsExToolWindow));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Dismissed?.Invoke(this, EventArgs.Empty);
        }
    }

    // A tab is a button: a click selects the widget. Pressing and moving it drags it to a new place instead;
    // taking the mouse capture away from the button is what keeps the drag from ending in a click.
    private void OnRailMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = (e.OriginalSource as FrameworkElement)?.DataContext as WidgetEntryViewModel;
        _dragStart = e.GetPosition(Rail);
    }

    private void OnRailMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is not { } entry || e.LeftButton != MouseButtonState.Pressed || DataContext is not PanelViewModel viewModel)
        {
            return;
        }

        var position = e.GetPosition(Rail);
        if (!entry.IsDragging)
        {
            if (Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            Rail.CaptureMouse();
            entry.IsDragging = true;
        }

        // The tab moves to the place of the one the pointer is over; the others make room right away.
        for (var index = 0; index < Rail.Items.Count; index++)
        {
            if (Rail.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container)
            {
                var top = container.TranslatePoint(default, Rail).Y;
                if (position.Y >= top && position.Y < top + container.ActualHeight)
                {
                    viewModel.MoveWidget(entry, index);
                    break;
                }
            }
        }
    }

    private void OnRailMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragCandidate is { IsDragging: true })
        {
            e.Handled = true;
            Rail.ReleaseMouseCapture();
        }

        _dragCandidate = null;
    }

    // Also reached when the drag is cut short, for example by switching to another window. The event of the
    // button, which loses the capture to the rail when the drag starts, passes through here too and is ignored.
    private void OnRailLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (e.OriginalSource != Rail)
        {
            return;
        }

        if (_dragCandidate is { IsDragging: true } entry)
        {
            entry.IsDragging = false;
            (DataContext as PanelViewModel)?.CommitOrder();
        }

        _dragCandidate = null;
    }

    private void OnResizeStarted(object sender, DragStartedEventArgs e)
    {
        _resizeStartCursor = GetCursorX();
        _resizeStartWidth = ActualWidth;
    }

    private void OnResizeDelta(object sender, DragDeltaEventArgs e)
    {
        // The edge being dragged moves with the window, so the drag is measured against the screen
        // instead of the window; that keeps the edge under the cursor without jitter.
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        WidthRequested?.Invoke(this, _resizeStartWidth + ((_resizeStartCursor - GetCursorX()) / scale));
    }

    private void OnResizeCompleted(object sender, DragCompletedEventArgs e) => WidthCommitted?.Invoke(this, EventArgs.Empty);

    private void OnResizeReset(object sender, MouseButtonEventArgs e)
    {
        WidthRequested?.Invoke(this, null);
        WidthCommitted?.Invoke(this, EventArgs.Empty);
    }

    private static int GetCursorX() => NativeMethods.GetCursorPos(out var position) ? position.X : 0;

    private double SlideDistance => PanelRoot.ActualWidth > 0 ? PanelRoot.ActualWidth : FallbackSlideDistance;

    private void Animate(double targetOffset, double targetOpacity, Duration duration, EasingMode easing, Action? onCompleted)
    {
        // Respect the Windows setting for people who turned animations off.
        if (!SystemParameters.ClientAreaAnimation)
        {
            Slide.BeginAnimation(TranslateTransform.XProperty, null);
            PanelRoot.BeginAnimation(OpacityProperty, null);
            Slide.X = targetOffset;
            PanelRoot.Opacity = targetOpacity;
            onCompleted?.Invoke();
            return;
        }

        // No explicit start value: an animation that interrupts another continues from where that one was.
        var slide = new DoubleAnimation(targetOffset, duration) { EasingFunction = new CubicEase { EasingMode = easing } };
        if (onCompleted is not null)
        {
            slide.Completed += (_, _) => onCompleted();
        }

        // The fade is shorter than the movement so the panel is readable while it is still arriving.
        var fade = new DoubleAnimation(targetOpacity, new Duration(TimeSpan.FromMilliseconds(duration.TimeSpan.TotalMilliseconds * 0.6)));

        Slide.BeginAnimation(TranslateTransform.XProperty, slide);
        PanelRoot.BeginAnimation(OpacityProperty, fade);
    }
}
