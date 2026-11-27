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

    event EventHandler? Changed;
}
