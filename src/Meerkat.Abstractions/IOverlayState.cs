namespace Meerkat.Abstractions;

public enum OverlayMode
{
    /// <summary>Clicks pass through the overlay.</summary>
    Display,

    /// <summary>Widgets can be operated, moved and resized.</summary>
    Interactive,
}

public interface IOverlayState
{
    bool IsVisible { get; }

    OverlayMode Mode { get; }

    /// <summary>Display text of the hotkey that toggles the mode, e.g. "Ctrl+Alt+O".</summary>
    string ModeHotkeyText { get; }

    /// <summary>
    /// A short message from the shell, e.g. "Update 1.1.0 ready", or null. Widgets may show it where they
    /// have room for a hint; in display mode this is the only way the shell can reach the user.
    /// </summary>
    string? Notice { get; }

    event EventHandler? Changed;
}
