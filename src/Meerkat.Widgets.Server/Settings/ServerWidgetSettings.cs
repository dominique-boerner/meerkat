using System.Text.Json.Serialization;
using Meerkat.Abstractions;
using Meerkat.Ssh;

namespace Meerkat.Widgets.Server.Settings;

/// <summary>How often the machines are checked. The user picks a pace; what it means in seconds is decided here.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RefreshRate>))]
public enum RefreshRate
{
    Fast,
    Normal,
    Economical,
}

/// <summary>What the Server widget watches on one of the machines the user entered.</summary>
public sealed class ServerMachineUse
{
    /// <summary>The id of the machine in the list all widgets share.</summary>
    public string MachineId { get; set; } = string.Empty;

    /// <summary>systemd units whose state is checked.</summary>
    public List<string> Services { get; set; } = [];

    /// <summary>Seconds between two checks; null follows the pace chosen for the widget. Only set by hand in the settings file.</summary>
    public double? IntervalSeconds { get; set; }

    public double DiskWarningPercent { get; set; } = 85;

    public double DiskErrorPercent { get; set; } = 95;

    public bool IsPaused { get; set; }
}

/// <summary>A machine of the shared list together with what this widget watches on it.</summary>
public sealed class ServerHost
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    /// <summary>A host name, an IP address or an alias from ~/.ssh/config.</summary>
    public string Address { get; set; } = string.Empty;

    public string? User { get; set; }

    public int? Port { get; set; }

    public SshAuthentication Authentication { get; set; } = SshAuthentication.Agent;

    public string? KeyFile { get; set; }

    /// <summary>systemd units whose state is checked.</summary>
    public List<string> Services { get; set; } = [];

    /// <summary>Null uses the widget's interval.</summary>
    public double? IntervalSeconds { get; set; }

    public double DiskWarningPercent { get; set; } = 85;

    public double DiskErrorPercent { get; set; } = 95;

    public bool IsPaused { get; set; }

    /// <summary>Under this key the machine's password is kept in the secret store, never in the settings file.</summary>
    [JsonIgnore]
    public string SecretKey => $"machine/{Id}";

    public static ServerHost From(SshMachine machine, ServerMachineUse use) => new()
    {
        Id = machine.Id,
        Name = machine.DisplayName,
        Address = machine.Address,
        User = machine.User,
        Port = machine.Port,
        Authentication = machine.Authentication,
        KeyFile = machine.KeyFile,
        Services = [.. use.Services],
        IntervalSeconds = use.IntervalSeconds,
        DiskWarningPercent = use.DiskWarningPercent,
        DiskErrorPercent = use.DiskErrorPercent,
        IsPaused = use.IsPaused,
    };

    /// <param name="password">Needed for password sign-in only; comes from the secret store.</param>
    public SshTarget ToTarget(string? password = null) => new(
        Address.Trim(),
        string.IsNullOrWhiteSpace(User) ? null : User.Trim(),
        Port,
        Authentication,
        string.IsNullOrWhiteSpace(KeyFile) ? null : KeyFile.Trim(),
        Authentication == SshAuthentication.Password ? password : null);
}

public sealed class ServerWidgetSettings
{
    private const double MinimumIntervalSeconds = 10;

    public RefreshRate Refresh { get; set; } = RefreshRate.Normal;

    /// <summary>
    /// Seconds between two checks, for whoever wants another figure than <see cref="Refresh"/> stands for.
    /// Not offered in the settings window; set it in the settings file.
    /// </summary>
    public double? CustomIntervalSeconds { get; set; }

    /// <summary>Which of the shared machines the widget watches, and what on them.</summary>
    public List<ServerMachineUse> Machines { get; set; } = [];

    /// <summary>
    /// The machines of <see cref="Machines"/> with all that is needed to reach them, put together by
    /// <see cref="ServerSettingsSource"/>. Not stored: the connection belongs to the shared list.
    /// </summary>
    [JsonIgnore]
    public List<ServerHost> Hosts { get; set; } = [];

    /// <summary>Hosts as the widget stored them before machines were shared; taken over into the shared list once.</summary>
    [JsonPropertyName("Hosts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ServerHost>? LegacyHosts { get; set; }

    [JsonIgnore]
    public double IntervalSeconds => CustomIntervalSeconds ?? Refresh switch
    {
        RefreshRate.Fast => 15,
        RefreshRate.Economical => 120,
        _ => 30,
    };

    // Every check opens an SSH connection; a floor keeps a typo from hammering the hosts.
    public TimeSpan GetInterval(ServerHost host)
    {
        var seconds = host.IntervalSeconds ?? IntervalSeconds;
        return TimeSpan.FromSeconds(double.IsFinite(seconds) ? Math.Max(MinimumIntervalSeconds, seconds) : MinimumIntervalSeconds);
    }
}

public sealed class ServerSettingsSource
{
    private const string LegacySecretPrefix = "server/";

    private readonly IWidgetSettingsStore _store;
    private readonly MachineRegistry? _machines;
    private ServerWidgetSettings? _current;

    /// <param name="machines">The machines all widgets share; without it the widget has nothing to watch.</param>
    public ServerSettingsSource(IWidgetSettingsStore store, MachineRegistry? machines = null)
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

    public ServerWidgetSettings Current => _current ??= Load();

    /// <summary>Persists a change made to the hosts of <see cref="Current"/> in place, without announcing new settings.</summary>
    public void Save()
    {
        // Pausing is done on the host as the widget page shows it; what is stored has to follow.
        foreach (var host in Current.Hosts)
        {
            if (Current.Machines.FirstOrDefault(use => use.MachineId == host.Id) is { } use)
            {
                use.IsPaused = host.IsPaused;
            }
        }

        _store.Save(ServerWidget.WidgetId, Current);
    }

    public void Update(ServerWidgetSettings settings)
    {
        Resolve(settings);
        _current = settings;
        _store.Save(ServerWidget.WidgetId, settings);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private ServerWidgetSettings Load()
    {
        var settings = _store.Load<ServerWidgetSettings>(ServerWidget.WidgetId);
        if (_machines is not null && settings.LegacyHosts is { } legacy)
        {
            Adopt(settings, legacy, _machines);
        }

        Resolve(settings);
        return settings;
    }

    /// <summary>Moves hosts the widget used to keep for itself into the list all widgets share.</summary>
    private void Adopt(ServerWidgetSettings settings, List<ServerHost> legacy, MachineRegistry machines)
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
            if (settings.Machines.All(use => use.MachineId != machineId))
            {
                settings.Machines.Add(new ServerMachineUse
                {
                    MachineId = machineId,
                    Services = [.. host.Services],
                    IntervalSeconds = host.IntervalSeconds,
                    DiskWarningPercent = host.DiskWarningPercent,
                    DiskErrorPercent = host.DiskErrorPercent,
                    IsPaused = host.IsPaused,
                });
            }
        }

        settings.LegacyHosts = null;
        _store.Save(ServerWidget.WidgetId, settings);
    }

    private void Resolve(ServerWidgetSettings settings)
    {
        if (_machines is null)
        {
            return;
        }

        settings.Hosts = [.. settings.Machines
            .Select(use => _machines.Find(use.MachineId) is { } machine ? ServerHost.From(machine, use) : null)
            .OfType<ServerHost>()];
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
