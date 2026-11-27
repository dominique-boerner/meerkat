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
public sealed record WidgetAlert(WidgetSeverity Severity, string Title, string Detail, DateTimeOffset Since);
