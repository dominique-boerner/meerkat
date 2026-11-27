namespace Meerkat.Abstractions;

public interface IOverlayState
{
    /// <summary>False while the user has hidden Meerkat; widgets should stop polling then.</summary>
    bool IsVisible { get; }

    /// <summary>True while the panel with the widget pages is open.</summary>
    bool IsPanelOpen { get; }

    /// <summary>Display text of the hotkey that opens the panel, e.g. "Ctrl+Alt+M".</summary>
    string PanelHotkeyText { get; }

    event EventHandler? Changed;
}
