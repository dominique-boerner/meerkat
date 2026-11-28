using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;

namespace Meerkat.App.Actions;

/// <summary>
/// The window of the action search. Unlike the status bar it takes the keyboard, as it is typed into,
/// and it goes away as soon as the user turns to something else.
/// </summary>
public partial class ActionPaletteWindow : Window
{
    private readonly ActionPaletteViewModel _viewModel;
    private DispatcherTimer? _closeTimer;
    private bool _isClosing;

    public ActionPaletteWindow(ActionPaletteViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        UpdatePlaceholder();

        viewModel.CloseRequested += OnCloseRequested;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += (_, _) =>
        {
            Activate();
            Input.Focus();
        };
        Closing += (_, _) => _isClosing = true;
        Closed += (_, _) =>
        {
            _closeTimer?.Stop();
            viewModel.CloseRequested -= OnCloseRequested;
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.Dispose();
        };
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        // The palette belongs to the moment it was called up; left behind it would only cover the screen.
        CloseOnce();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                _viewModel.Move(1);
                break;
            case Key.Up:
                _viewModel.Move(-1);
                break;
            case Key.Enter:
                _ = _viewModel.AcceptAsync();
                break;
            case Key.Escape:
                _viewModel.Back();
                break;

            // An empty field has nothing left to delete, so the key takes back the step before it instead.
            case Key.Back when Input.Text.Length == 0 && _viewModel.Stage == PaletteStage.Targets:
                _viewModel.Back();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e) => UpdatePlaceholder();

    private void UpdatePlaceholder() =>
        PlaceholderText.Visibility = Input.Text.Length == 0 && _viewModel.IsChoosing ? Visibility.Visible : Visibility.Collapsed;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActionPaletteViewModel.SelectedRow))
        {
            // The containers of a list that was just replaced do not exist yet.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, BringSelectionIntoView);
        }
        else if (e.PropertyName == nameof(ActionPaletteViewModel.Stage))
        {
            UpdatePlaceholder();
            Input.Focus();
        }
    }

    private void BringSelectionIntoView()
    {
        if (_viewModel.SelectedRow is not { } row)
        {
            return;
        }

        if (_viewModel.Rows.FirstOrDefault(candidate => candidate.IsAvailable) == row)
        {
            // Above the first row there is only its heading, which belongs into view with it.
            RowsScroll.ScrollToTop();
        }
        else
        {
            (RowsList.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement)?.BringIntoView();
        }
    }


    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PaletteRowViewModel { IsAvailable: true } row)
        {
            _viewModel.Select(row);
            _ = _viewModel.AcceptAsync();
        }
    }

    private void OnAcceptClick(object sender, RoutedEventArgs e) => _ = _viewModel.AcceptAsync();

    private void OnBackClick(object sender, RoutedEventArgs e) => _viewModel.Back();

    private void OnCloseRequested(object? sender, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            CloseOnce();
            return;
        }

        _closeTimer = new DispatcherTimer(delay, DispatcherPriority.Normal, (_, _) => CloseOnce(), Dispatcher);
    }

    private void CloseOnce()
    {
        if (!_isClosing)
        {
            _isClosing = true;
            _closeTimer?.Stop();
            Close();
        }
    }
}

/// <summary>Shows a title with the letters the search matched in the accent colour.</summary>
public static class HighlightedText
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.RegisterAttached(
        "Segments",
        typeof(IReadOnlyList<TextSegment>),
        typeof(HighlightedText),
        new PropertyMetadata(null, OnSegmentsChanged));

    public static IReadOnlyList<TextSegment>? GetSegments(DependencyObject element) => (IReadOnlyList<TextSegment>?)element.GetValue(SegmentsProperty);

    public static void SetSegments(DependencyObject element, IReadOnlyList<TextSegment>? value) => element.SetValue(SegmentsProperty, value);

    private static void OnSegmentsChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock textBlock)
        {
            return;
        }

        textBlock.Inlines.Clear();
        foreach (var segment in (IReadOnlyList<TextSegment>?)e.NewValue ?? [])
        {
            var run = new Run(segment.Text);
            if (segment.IsMatch)
            {
                run.SetResourceReference(TextElement.ForegroundProperty, "Overlay.Accent");
            }

            textBlock.Inlines.Add(run);
        }
    }
}

public sealed class InvertBoolean : IValueConverter
{
    public static InvertBoolean Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is false;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is false;
}
