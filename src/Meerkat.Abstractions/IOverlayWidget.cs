using System.Windows;

namespace Meerkat.Abstractions;

public interface IOverlayWidget
{
    string Id { get; }

    string Title { get; }

    /// <summary>Created lazily on the UI thread and hosted exactly once by the shell.</summary>
    FrameworkElement View { get; }

    /// <summary>Optional content shown next to the title in the widget header (status text, buttons).</summary>
    FrameworkElement? HeaderView => null;

    Size DefaultSize => new(460, 320);

    IWidgetSettingsPage? SettingsPage => null;

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
