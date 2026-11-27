using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Meerkat.App.Settings;

namespace Meerkat.App.Overlay;

public sealed record WidgetFailureViewModel(string Detail, ICommand RestartCommand, ICommand OpenLogCommand);

public sealed partial class WidgetHostViewModel(string id, string title, WidgetLayout defaultLayout) : ObservableObject
{
    public const double MinWidth = 260;
    public const double MinHeight = 140;

    [ObservableProperty]
    private double _left;

    [ObservableProperty]
    private double _top;

    [ObservableProperty]
    private double _width = MinWidth;

    [ObservableProperty]
    private double _height = MinHeight;

    [ObservableProperty]
    private object? _content;

    [ObservableProperty]
    private object? _headerContent;

    [ObservableProperty]
    private WidgetFailureViewModel? _failure;

    [ObservableProperty]
    private bool _isInteractive;

    public string Id { get; } = id;

    public string Title { get; } = title;

    public WidgetLayout DefaultLayout { get; } = defaultLayout;

    public void MoveTo(double left, double top, Size bounds)
    {
        Left = ClampPosition(left, Width, bounds.Width);
        Top = ClampPosition(top, Height, bounds.Height);
    }

    public void ResizeBy(double deltaX, double deltaY, Size bounds)
    {
        Width = Math.Clamp(Width + deltaX, MinWidth, Math.Max(MinWidth, bounds.Width - Left));
        Height = Math.Clamp(Height + deltaY, MinHeight, Math.Max(MinHeight, bounds.Height - Top));
    }

    /// <summary>Pulls the host back into view, e.g. after switching to a smaller monitor.</summary>
    public void ClampTo(Size bounds)
    {
        Width = Math.Clamp(Width, MinWidth, Math.Max(MinWidth, bounds.Width));
        Height = Math.Clamp(Height, MinHeight, Math.Max(MinHeight, bounds.Height));
        MoveTo(Left, Top, bounds);
    }

    public void ApplyLayout(WidgetLayout layout)
    {
        Left = layout.Left;
        Top = layout.Top;
        Width = Math.Max(MinWidth, layout.Width);
        Height = Math.Max(MinHeight, layout.Height);
    }

    public WidgetLayout ToLayout() => new() { Left = Left, Top = Top, Width = Width, Height = Height };

    private static double ClampPosition(double value, double size, double available) =>
        Math.Clamp(value, 0, Math.Max(0, available - size));
}
