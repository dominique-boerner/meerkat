using System.Windows;

namespace Meerkat.Abstractions;

/// <summary>A secondary, independently movable surface of a widget, e.g. a log viewer.</summary>
public sealed record OverlayPanel(
    string Id,
    string Title,
    FrameworkElement View,
    FrameworkElement? HeaderView,
    Rect DefaultBounds);

public interface IOverlayPanelHost
{
    /// <summary>Shows the panel until the returned handle is disposed. Must be called on the UI thread.</summary>
    IDisposable Show(OverlayPanel panel);
}
