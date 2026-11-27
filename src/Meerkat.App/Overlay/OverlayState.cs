using Meerkat.Abstractions;

namespace Meerkat.App.Overlay;

public sealed class OverlayState : IOverlayState
{
    public bool IsVisible { get; private set; }

    public OverlayMode Mode { get; private set; } = OverlayMode.Display;

    public string ModeHotkeyText { get; private set; } = string.Empty;

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

    public void SetMode(OverlayMode mode)
    {
        if (Mode != mode)
        {
            Mode = mode;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public string? Notice { get; private set; }

    public void SetNotice(string? notice)
    {
        if (Notice != notice)
        {
            Notice = notice;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetHotkeyTexts(string modeHotkeyText, string visibilityHotkeyText)
    {
        ModeHotkeyText = modeHotkeyText;
        VisibilityHotkeyText = visibilityHotkeyText;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
