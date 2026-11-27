namespace Meerkat.App.Settings;

public sealed class LayoutService(SettingsService settings)
{
    private const string PrimaryMonitorKey = "primary";

    public WidgetLayout? Find(string hostId) =>
        settings.Current.Layouts.TryGetValue(CurrentMonitorKey, out var layouts) && layouts.TryGetValue(hostId, out var layout)
            ? layout
            : null;

    public void Store(IEnumerable<(string HostId, WidgetLayout Layout)> hosts)
    {
        if (!settings.Current.Layouts.TryGetValue(CurrentMonitorKey, out var layouts))
        {
            layouts = [];
            settings.Current.Layouts[CurrentMonitorKey] = layouts;
        }

        foreach (var (hostId, layout) in hosts)
        {
            layouts[hostId] = layout;
        }

        settings.Save();
    }

    /// <summary>Forgets the stored bounds of a widget and its panels on every monitor.</summary>
    public static void Reset(OverlaySettings overlaySettings, string widgetId)
    {
        foreach (var layouts in overlaySettings.Layouts.Values)
        {
            foreach (var hostId in layouts.Keys.Where(id => id == widgetId || id.StartsWith(widgetId + ".", StringComparison.Ordinal)).ToList())
            {
                layouts.Remove(hostId);
            }
        }
    }

    private string CurrentMonitorKey => settings.Current.Monitor ?? PrimaryMonitorKey;
}
