using Meerkat.Abstractions;

namespace Meerkat.App.Overlay;

public sealed class OverlayState : IOverlayState
{
    public bool IsVisible { get; private set; }

    public bool IsPanelOpen { get; private set; }

    public string PanelHotkeyText { get; private set; } = string.Empty;

    public string VisibilityHotkeyText { get; private set; } = string.Empty;

    public event EventHandler? Changed;

    public void SetVisible(bool isVisible)
    {
        if (IsVisible != isVisible)
        {
            IsVisible = isVisible;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetPanelOpen(bool isPanelOpen)
    {
        if (IsPanelOpen != isPanelOpen)
        {
            IsPanelOpen = isPanelOpen;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetHotkeyTexts(string panelHotkeyText, string visibilityHotkeyText)
    {
        PanelHotkeyText = panelHotkeyText;
        VisibilityHotkeyText = visibilityHotkeyText;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
