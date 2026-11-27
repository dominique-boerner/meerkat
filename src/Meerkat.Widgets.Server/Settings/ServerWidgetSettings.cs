using System.Text.Json.Serialization;
using Meerkat.Abstractions;
using Meerkat.Ssh;

namespace Meerkat.Widgets.Server.Settings;

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

    /// <summary>Null uses the widget's default interval.</summary>
    public double? IntervalSeconds { get; set; }

    public double DiskWarningPercent { get; set; } = 85;

    public double DiskErrorPercent { get; set; } = 95;

    public bool IsPaused { get; set; }

    /// <summary>Under this key the host's password is kept in the secret store, never in the settings file.</summary>
    [JsonIgnore]
    public string SecretKey => $"server/{Id}";

    /// <param name="password">Needed for password sign-in only; comes from the secret store.</param>
    public SshTarget ToTarget(string? password = null) => new(
        Address.Trim(),
        string.IsNullOrWhiteSpace(User) ? null : User.Trim(),
        Port,
        Authentication,
        string.IsNullOrWhiteSpace(KeyFile) ? null : KeyFile.Trim(),
        Authentication == SshAuthentication.Password ? password : null);

    public ServerHost Clone() => new()
    {
        Id = Id,
        Name = Name,
        Address = Address,
        User = User,
        Port = Port,
        Authentication = Authentication,
        KeyFile = KeyFile,
        Services = [.. Services],
        IntervalSeconds = IntervalSeconds,
        DiskWarningPercent = DiskWarningPercent,
        DiskErrorPercent = DiskErrorPercent,
        IsPaused = IsPaused,
    };
}

public sealed class ServerWidgetSettings
{
    private const double MinimumIntervalSeconds = 10;

    public double DefaultIntervalSeconds { get; set; } = 30;

    public List<ServerHost> Hosts { get; set; } = [];

    // Every check opens an SSH connection; a floor keeps a typo from hammering the hosts.
    public TimeSpan GetInterval(ServerHost host)
    {
        var seconds = host.IntervalSeconds ?? DefaultIntervalSeconds;
        return TimeSpan.FromSeconds(double.IsFinite(seconds) ? Math.Max(MinimumIntervalSeconds, seconds) : MinimumIntervalSeconds);
    }
}

public sealed class ServerSettingsSource(IWidgetSettingsStore store)
{
    private ServerWidgetSettings? _current;

    public event EventHandler? Changed;

    public ServerWidgetSettings Current => _current ??= store.Load<ServerWidgetSettings>(ServerWidget.WidgetId);

    /// <summary>Persists a change made to <see cref="Current"/> in place, without announcing new settings.</summary>
    public void Save() => store.Save(ServerWidget.WidgetId, Current);

    public void Update(ServerWidgetSettings settings)
    {
        _current = settings;
        store.Save(ServerWidget.WidgetId, settings);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
