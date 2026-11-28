using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Meerkat.App.Interop;

namespace Meerkat.App.Overlay;

/// <summary>
/// The always-visible status display. Its bar and cards can be clicked to open the panel, but the window
/// never becomes the active one, so the application the user works in keeps the keyboard focus.
/// </summary>
public partial class StatusBarWindow : Window
{
    // Gap between the bar and the cards, which each card brings along as its top margin.
    private const double CardGap = 6;

    // Wide enough for a title and a line of detail; cards with more text shorten it.
    private const double CardWidth = 390;

    private double _positionX = StatusBarPlacement.DefaultX;
    private double _positionY = StatusBarPlacement.DefaultY;

    private static readonly TimeSpan FlyOutDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan MenuCheckInterval = TimeSpan.FromMilliseconds(350);

    private Point? _dragStartCursor;
    private Point _dragStartBar;
    private bool _hasMoved;

    private FrameworkElement? _menuCardElement;

    private readonly DispatcherTimer _menuTimer;
    private AlertCardViewModel? _menuCard;
    private int _menuIdleChecks;

    public StatusBarWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyPlacement();
        _menuTimer = new DispatcherTimer(MenuCheckInterval, DispatcherPriority.Background, (_, _) => CloseMenuWhenLeft(), Dispatcher) { IsEnabled = false };
    }

    /// <summary>Raised when the user has dragged the bar by its handle to a new place, with the position as fractions of the screen.</summary>
    public event EventHandler<(double X, double Y)>? PositionChanged;

    /// <summary>Places the bar; see <see cref="StatusBarPlacement"/> for the meaning of the two fractions.</summary>
    public void SetPosition(double x, double y)
    {
        _positionX = x;
        _positionY = y;
        ApplyPlacement();
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

    private void OnBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The bar takes the room its chips need, however many widgets report. The cards keep a fixed
        // width so that long texts are shortened, and only widen to stay flush with a longer bar.
        CardsPanel.Width = Math.Max(CardWidth, Bar.ActualWidth);
        ApplyPlacement();
    }

    private void ApplyPlacement()
    {
        if (ActualWidth <= 0 || Bar.ActualWidth <= 0)
        {
            return;
        }

        var placement = StatusBarPlacement.Compute(_positionX, _positionY, new Size(ActualWidth, ActualHeight), Bar.RenderSize);
        var horizontal = placement.GrowsLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        Group.HorizontalAlignment = horizontal;
        Group.VerticalAlignment = placement.GrowsUp ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        Group.Margin = placement.Margin;
        Bar.HorizontalAlignment = horizontal;
        NoticeButton.HorizontalAlignment = horizontal;

        // Above the bar the gap belongs below the last card instead of above the first one.
        var dock = placement.GrowsUp ? Dock.Bottom : Dock.Top;
        DockPanel.SetDock(Bar, dock);
        DockPanel.SetDock(CardsPanel, dock);
        CardsPanel.Margin = placement.GrowsUp ? new Thickness(0, -CardGap, 0, CardGap) : default;
    }

    private void OnDismissClick(object sender, RoutedEventArgs e)
    {
        // The button lies in the card it puts away.
        if ((sender as FrameworkElement)?.Parent is FrameworkElement card)
        {
            FlyOut(card, viewModel => viewModel.DismissAlertCommand);
        }
    }

    /// <summary>Lets a card leave to the right and puts it away once it is gone.</summary>
    private void FlyOut(FrameworkElement card, Func<StatusBarViewModel, System.Windows.Input.ICommand> command)
    {
        if (card.DataContext is not AlertCardViewModel alert || DataContext is not StatusBarViewModel viewModel)
        {
            return;
        }

        // Out of reach while it leaves, so that a second click cannot put it away twice.
        card.IsHitTestVisible = false;
        var transform = new TranslateTransform();
        card.RenderTransform = transform;
        var slide = new DoubleAnimation(card.ActualWidth + 40, FlyOutDuration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        slide.Completed += (_, _) => command(viewModel).Execute(alert);
        transform.BeginAnimation(TranslateTransform.XProperty, slide);
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, FlyOutDuration));
    }

    private void OnCardRightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AlertCardViewModel card)
        {
            return;
        }

        _menuCard = card;
        _menuCardElement = (FrameworkElement)sender;
        _menuIdleChecks = 0;
        CardMenu.IsOpen = false;
        CardMenu.IsOpen = true;
        _menuTimer.Start();
        e.Handled = true;
    }

    private void OnCardMenuClick(object sender, RoutedEventArgs e)
    {
        var card = _menuCard;
        var element = _menuCardElement;
        CloseMenu();
        if (card is null || element is null || DataContext is not StatusBarViewModel viewModel)
        {
            return;
        }

        switch ((sender as FrameworkElement)?.Tag)
        {
            case "Dismiss":
                FlyOut(element, shown => shown.DismissAlertCommand);
                break;
            case "Hour":
                FlyOut(element, shown => shown.MuteAlertForAnHourCommand);
                break;
            case "Tomorrow":
                FlyOut(element, shown => shown.MuteAlertUntilTomorrowCommand);
                break;
            default:
                viewModel.OpenAlertCommand.Execute(card);
                break;
        }
    }

    /// <summary>
    /// A menu normally closes when the user clicks elsewhere. This window never becomes the active one, so
    /// it does not hear of clicks elsewhere; the menu closes once the pointer has left it and the cards instead.
    /// </summary>
    private void CloseMenuWhenLeft()
    {
        if (CardMenuContent.IsMouseOver || CardsPanel.IsMouseOver)
        {
            _menuIdleChecks = 0;
        }
        else if (++_menuIdleChecks >= 2)
        {
            CloseMenu();
        }
    }

    private void CloseMenu()
    {
        _menuTimer.Stop();
        _menuCard = null;
        _menuCardElement = null;
        CardMenu.IsOpen = false;
    }

    // Marking the press as handled keeps it from the bar, which would otherwise take it as the start of a click.
    private void OnGripMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartCursor = e.GetPosition(this);
        _dragStartBar = Bar.TranslatePoint(default, this);
        _hasMoved = false;
        Grip.CaptureMouse();
        e.Handled = true;
    }

    private void OnGripMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStartCursor is not { } start || !Grip.IsMouseCaptured)
        {
            return;
        }

        var offset = e.GetPosition(this) - start;
        _hasMoved = true;
        (_positionX, _positionY) = StatusBarPlacement.FromBarPosition(_dragStartBar + offset, new Size(ActualWidth, ActualHeight), Bar.RenderSize);
        ApplyPlacement();
    }

    private void OnGripMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragStartCursor = null;
        Grip.ReleaseMouseCapture();
        e.Handled = true;
        if (_hasMoved)
        {
            PositionChanged?.Invoke(this, (_positionX, _positionY));
        }
    }
}
