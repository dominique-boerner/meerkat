namespace Meerkat.Abstractions;

public interface IWidgetSettingsStore
{
    /// <summary>
    /// Raised on the UI thread when the settings were changed from outside, such as by editing the settings
    /// file by hand. Whatever was loaded before is out of date then and has to be loaded again.
    /// </summary>
    event EventHandler? Reloaded
    {
        add { }
        remove { }
    }

    T Load<T>(string widgetId)
        where T : class, new();

    void Save<T>(string widgetId, T settings)
        where T : class;
}
