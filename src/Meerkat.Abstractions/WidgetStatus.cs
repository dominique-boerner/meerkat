namespace Meerkat.Abstractions;

public enum WidgetSeverity
{
    Ok,

    /// <summary>Worth knowing, but nothing is broken. Shown in the panel only.</summary>
    Warning,

    /// <summary>Something is broken. Shown in the status bar even while the panel is closed.</summary>
    Error,
}

/// <summary>What a widget contributes to the status bar: a label and a compact figure such as "6/6".</summary>
public sealed record WidgetStatus(string Label, string Summary, WidgetSeverity Severity);

/// <summary>Something that needs the user's attention.</summary>
/// <param name="Since">When the condition was first observed; shown as its age.</param>
/// <param name="Prominent">
/// Shows a warning as a card in the status bar, as errors always are. Meant for the rare warning that
/// explains trouble the user is about to run into, such as a disturbance of a service they depend on.
/// </param>
public sealed record WidgetAlert(WidgetSeverity Severity, string Title, string Detail, DateTimeOffset Since, bool Prominent = false);

/// <summary>
/// Something the user chose to keep in view below the status bar, such as a task list. The status bar
/// only opens the widget's page when a card is clicked, so a card can be read, not operated.
/// </summary>
public sealed record WidgetStatusCard(string Title, IReadOnlyList<WidgetStatusCardLine> Lines);

/// <param name="IsDone">Shown struck through.</param>
public sealed record WidgetStatusCardLine(string Text, bool IsDone = false);

/// <summary>A global hotkey a widget offers. The shell registers it and lets the user change the combination.</summary>
/// <param name="Id">Identifies the hotkey within the widget; the user's combination is stored under it.</param>
/// <param name="Title">What the hotkey does, shown in the settings.</param>
/// <param name="DefaultGesture">Combination used until the user chooses another one, e.g. "Ctrl+Alt+N".</param>
/// <param name="Handler">Called on the UI thread when the hotkey is pressed.</param>
public sealed record WidgetHotkey(string Id, string Title, string DefaultGesture, Action Handler);
