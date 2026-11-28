using Meerkat.Abstractions;
using Meerkat.Ssh;

namespace Meerkat.Widgets.Docker.Settings;

public sealed class DockerSettingsSource
{
    private const string LegacySecretPrefix = "docker/";

    private readonly IWidgetSettingsStore _store;
    private readonly MachineRegistry? _machines;
    private DockerWidgetSettings? _current;

    /// <param name="machines">The machines all widgets share; without it the widget only knows this computer.</param>
    public DockerSettingsSource(IWidgetSettingsStore store, MachineRegistry? machines = null)
    {
        _store = store;
        _machines = machines;
        store.Reloaded += (_, _) =>
        {
            _current = null;
            Changed?.Invoke(this, EventArgs.Empty);
        };
        if (machines is not null)
        {
            machines.Changed += OnMachinesChanged;
        }
    }

    public event EventHandler? Changed;

    public DockerWidgetSettings Current => _current ??= Load();

    /// <summary>Persists a change made to <see cref="Current"/> in place, without announcing new settings.</summary>
    public void Save() => _store.Save(DockerWidget.WidgetId, Current);

    public void Update(DockerWidgetSettings settings)
    {
        Resolve(settings);
        _current = settings;
        _store.Save(DockerWidget.WidgetId, settings);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private DockerWidgetSettings Load()
    {
        var settings = _store.Load<DockerWidgetSettings>(DockerWidget.WidgetId);
        if (_machines is not null && settings.LegacyRemoteHosts is { } legacy)
        {
            Adopt(settings, legacy, _machines);
        }

        Resolve(settings);
        return settings;
    }

    /// <summary>Moves hosts the widget used to keep for itself into the list all widgets share.</summary>
    private void Adopt(DockerWidgetSettings settings, List<DockerHost> legacy, MachineRegistry machines)
    {
        foreach (var host in legacy)
        {
            var machine = new SshMachine
            {
                Id = host.Id,
                Name = host.Name,
                Address = host.Address,
                User = host.User,
                Port = host.Port,
                Authentication = host.Authentication,
                KeyFile = host.KeyFile,
            };
            var machineId = machines.Adopt(machine, LegacySecretPrefix + host.Id);

            // The id may be that of a machine another widget brought along; what was kept under the old one follows.
            var folded = settings.CollapsedSources.IndexOf(host.Id);
            if (folded >= 0)
            {
                settings.CollapsedSources[folded] = machineId;
            }

            if (settings.Machines.All(use => use.MachineId != machineId))
            {
                settings.Machines.Add(new DockerMachineUse { MachineId = machineId, AllowActions = host.AllowActions, ScanImages = host.ScanImages });
            }
        }

        settings.LegacyRemoteHosts = null;
        _store.Save(DockerWidget.WidgetId, settings);
    }

    private void Resolve(DockerWidgetSettings settings)
    {
        if (_machines is null)
        {
            return;
        }

        settings.RemoteHosts = [.. settings.Machines
            .Where(use => use.ShowContainers)
            .Select(use => _machines.Find(use.MachineId) is { } machine ? DockerHost.From(machine, use, settings.RemoteIntervalSeconds) : null)
            .OfType<DockerHost>()];
    }

    private void OnMachinesChanged(object? sender, EventArgs e)
    {
        // While the settings are being loaded the machines change because this widget hands its own over.
        if (_current is null)
        {
            return;
        }

        Resolve(_current);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}