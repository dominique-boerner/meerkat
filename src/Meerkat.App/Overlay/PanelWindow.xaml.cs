using System.Windows;
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

    public PanelWindow()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user leaves the panel, by pressing Escape or by switching to another window.</summary>
    public event EventHandler? Dismissed;

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

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        // A pinned panel is meant to stay next to the work it informs about.
        if (DataContext is PanelViewModel { IsPinned: false })
        {
            Dismissed?.Invoke(this, EventArgs.Empty);
        }
    }

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
