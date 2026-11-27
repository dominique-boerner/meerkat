using Meerkat.Abstractions;

namespace Meerkat.Widgets.Docker.Settings;

public sealed class DockerSettingsSource(IWidgetSettingsStore store)
{
    private DockerWidgetSettings? _current;

    public event EventHandler? Changed;

    public DockerWidgetSettings Current => _current ??= store.Load<DockerWidgetSettings>(DockerWidget.WidgetId);

    /// <summary>Persists a change made to <see cref="Current"/> in place, without announcing new settings.</summary>
    public void Save() => store.Save(DockerWidget.WidgetId, Current);

    public void Update(DockerWidgetSettings settings)
    {
        _current = settings;
        store.Save(DockerWidget.WidgetId, settings);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
