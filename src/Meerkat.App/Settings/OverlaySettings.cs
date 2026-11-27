using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meerkat.App.Settings;

public sealed class OverlaySettings
{
    public const double DefaultPanelWidth = 680;
    public const double MinimumPanelWidth = 520;

    public string TogglePanelHotkey { get; set; } = "Ctrl+Alt+M";

    public string ToggleVisibilityHotkey { get; set; } = "Ctrl+Alt+H";

    /// <summary>Opacity of the status bar; the panel is always opaque.</summary>
    public double Opacity { get; set; } = 0.9;

    /// <summary>Display device name (e.g. \\.\DISPLAY2); null selects the primary monitor.</summary>
    public string? Monitor { get; set; }

    /// <summary>Width of the panel in device-independent pixels; the user changes it by dragging the panel's edge.</summary>
    public double PanelWidth { get; set; } = DefaultPanelWidth;

    // Below the minimum the widget pages no longer fit; the screen width is applied when the panel is placed.
    [JsonIgnore]
    public double EffectivePanelWidth => double.IsFinite(PanelWidth) ? Math.Max(MinimumPanelWidth, PanelWidth) : DefaultPanelWidth;

    public List<string> DisabledWidgets { get; set; } = [];

    public UpdateSettings Updates { get; set; } = new();

    public Dictionary<string, JsonElement> WidgetSettings { get; set; } = [];
}
