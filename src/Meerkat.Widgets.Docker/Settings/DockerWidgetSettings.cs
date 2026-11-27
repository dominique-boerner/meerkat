using System.Text.Json.Serialization;
using Meerkat.Ssh;

namespace Meerkat.Widgets.Docker.Settings;

/// <summary>A Docker instance on another machine, reached by running the docker CLI there over SSH.</summary>
public sealed class DockerHost
{
    public const double DefaultIntervalSeconds = 20;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    /// <summary>A host name, an IP address or an alias from ~/.ssh/config.</summary>
    public string Address { get; set; } = string.Empty;

    public string? User { get; set; }

    public int? Port { get; set; }

    public SshAuthentication Authentication { get; set; }

    public string? KeyFile { get; set; }

    public double IntervalSeconds { get; set; } = DefaultIntervalSeconds;

    /// <summary>
    /// Whether containers on this host may be started, stopped and restarted from Meerkat. Off unless the
    /// user switches it on: a slip of the mouse matters more on a server than on the own machine.
    /// </summary>
    public bool AllowActions { get; set; }

    /// <summary>Key under which the password is kept in the secret store.</summary>
    [JsonIgnore]
    public string SecretKey => $"docker/{Id}";

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Address : Name;

    public SshTarget ToTarget(string? password) => new(
        Address.Trim(),
        string.IsNullOrWhiteSpace(User) ? null : User.Trim(),
        Port,
        Authentication,
        string.IsNullOrWhiteSpace(KeyFile) ? null : KeyFile.Trim(),
        Authentication == SshAuthentication.Password ? password : null);

    public DockerHost Clone() => (DockerHost)MemberwiseClone();
}

public sealed class DockerWidgetSettings
{
    public const int DefaultMaxLogLines = 5000;

    private const double MinimumIntervalSeconds = 1;

    // Every poll of a remote host opens an SSH connection, which is far more costly than asking locally.
    private const double MinimumRemoteIntervalSeconds = 10;
    private const int MinimumLogLines = 100;
    private const int MaximumLogLines = 100_000;

    /// <summary>WSL distribution name; null or empty uses the default distribution.</summary>
    public string? Distribution { get; set; }

    public double ListIntervalSeconds { get; set; } = 5;

    public double StatsIntervalSeconds { get; set; } = 3;

    public int MaxLogLines { get; set; } = DefaultMaxLogLines;

    public List<DockerHost> RemoteHosts { get; set; } = [];

    /// <summary>Ids of the sources whose section the user folded up on the Docker page.</summary>
    public List<string> CollapsedSources { get; set; } = [];

    public static TimeSpan GetInterval(DockerHost host) => TimeSpan.FromSeconds(
        double.IsFinite(host.IntervalSeconds) ? Math.Max(MinimumRemoteIntervalSeconds, host.IntervalSeconds) : DockerHost.DefaultIntervalSeconds);

    [JsonIgnore]
    public TimeSpan ListInterval => ToInterval(ListIntervalSeconds);

    [JsonIgnore]
    public TimeSpan StatsInterval => ToInterval(StatsIntervalSeconds);

    [JsonIgnore]
    public int EffectiveMaxLogLines => Math.Clamp(MaxLogLines, MinimumLogLines, MaximumLogLines);

    // A hand-edited settings file must not be able to turn polling into a busy loop.
    private static TimeSpan ToInterval(double seconds) =>
        TimeSpan.FromSeconds(double.IsFinite(seconds) ? Math.Max(MinimumIntervalSeconds, seconds) : MinimumIntervalSeconds);
}
