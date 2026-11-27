using System.Text.Json;

namespace Meerkat.App.Settings;

public sealed class OverlaySettings
{
    public string ToggleModeHotkey { get; set; } = "Ctrl+Alt+O";

    public string ToggleVisibilityHotkey { get; set; } = "Ctrl+Alt+H";

    public double Opacity { get; set; } = 0.9;

    /// <summary>Display device name (e.g. \\.\DISPLAY2); null selects the primary monitor.</summary>
    public string? Monitor { get; set; }

    /// <summary>Widget and panel bounds per monitor key, because monitors differ in size and scaling.</summary>
    public Dictionary<string, Dictionary<string, WidgetLayout>> Layouts { get; set; } = [];

    public List<string> DisabledWidgets { get; set; } = [];

    public Dictionary<string, JsonElement> WidgetSettings { get; set; } = [];
}

/// <summary>Bounds in device-independent pixels, relative to the overlay's monitor.</summary>
public sealed class WidgetLayout
{
    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }
}
