using Meerkat.Abstractions;

namespace Meerkat.Widgets.Docker.Settings;

public sealed class DockerSettingsSource(IWidgetSettingsStore store)
{
    private DockerWidgetSettings? _current;

    public DockerWidgetSettings Current => _current ??= store.Load<DockerWidgetSettings>(DockerWidget.WidgetId);

    public void Update(DockerWidgetSettings settings)
    {
        _current = settings;
        store.Save(DockerWidget.WidgetId, settings);
    }
}
