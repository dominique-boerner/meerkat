namespace Meerkat.Abstractions;

public interface IWidgetSettingsStore
{
    T Load<T>(string widgetId)
        where T : class, new();

    void Save<T>(string widgetId, T settings)
        where T : class;
}
