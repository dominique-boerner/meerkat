using System.Windows;

namespace Meerkat.Abstractions;

public interface IOverlayWidget
{
    string Id { get; }

    string Title { get; }

    /// <summary>A glyph from the Segoe Fluent Icons font, shown next to the title in the panel.</summary>
    string Icon => "";

    /// <summary>The widget's page in the panel. Created lazily on the UI thread; return the same instance on every call.</summary>
    FrameworkElement View { get; }

    /// <summary>Optional content shown above the view, for example a summary and buttons.</summary>
    FrameworkElement? HeaderView => null;

    IWidgetSettingsPage? SettingsPage => null;

    /// <summary>The widget's entry in the status bar, or null if it has nothing to report yet.</summary>
    WidgetStatus? Status => null;

    /// <summary>
    /// Further entries in the status bar, for the rare widget that watches two things worth telling apart
    /// at a glance. Most widgets say all they have to say with <see cref="Status"/>.
    /// </summary>
    IReadOnlyList<WidgetStatus> AdditionalStatuses => [];

    /// <summary>Conditions that need attention right now. Errors appear in the status bar, warnings in the panel.</summary>
    IReadOnlyList<WidgetAlert> Alerts => [];

    /// <summary>Optional card shown below the status bar, or null if the widget keeps nothing in view.</summary>
    WidgetStatusCard? StatusCard => null;

    /// <summary>Global hotkeys of the widget. Read once when the widget is registered.</summary>
    IReadOnlyList<WidgetHotkey> Hotkeys => [];

    /// <summary>
    /// What the widget does on request, offered in the action search of the shell. Read on the UI thread
    /// every time the search opens, so the list may follow the widget's state; keep it cheap.
    /// </summary>
    IReadOnlyList<WidgetAction> Actions => [];

    /// <summary>Raise whenever <see cref="Status"/>, <see cref="AdditionalStatuses"/>, <see cref="Alerts"/> or <see cref="StatusCard"/> changed.</summary>
    event EventHandler? StatusChanged
    {
        add { }
        remove { }
    }

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
