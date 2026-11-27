using System.Text.Json;

namespace Meerkat.App.Settings;

public sealed class OverlaySettings
{
    public string TogglePanelHotkey { get; set; } = "Ctrl+Alt+M";

    public string ToggleVisibilityHotkey { get; set; } = "Ctrl+Alt+H";

    /// <summary>Opacity of the status bar; the panel is always opaque.</summary>
    public double Opacity { get; set; } = 0.9;

    /// <summary>Display device name (e.g. \\.\DISPLAY2); null selects the primary monitor.</summary>
    public string? Monitor { get; set; }

    public List<string> DisabledWidgets { get; set; } = [];

    public UpdateSettings Updates { get; set; } = new();

    public Dictionary<string, JsonElement> WidgetSettings { get; set; } = [];
}
