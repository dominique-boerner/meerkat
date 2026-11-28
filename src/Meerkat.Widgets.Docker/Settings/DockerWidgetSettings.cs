using System.Text.Json.Serialization;
using Meerkat.Ssh;
using Meerkat.Widgets.Docker.Security;

namespace Meerkat.Widgets.Docker.Settings;

/// <summary>From which severity on a vulnerability is brought to the user's attention.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReportThreshold>))]
public enum ReportThreshold
{
    Critical,
    High,

    /// <summary>Vulnerabilities are listed on the Docker page, but never reported.</summary>
    Never,
}

/// <summary>A vulnerability the user decided to live with; it is neither counted nor reported any more.</summary>
public sealed class IgnoredVulnerability
{
    public string Id { get; set; } = string.Empty;

    /// <summary>The package it was found in when it was ignored; only shown, the id alone decides.</summary>
    public string Package { get; set; } = string.Empty;

    public DateTimeOffset IgnoredAt { get; set; }
}

public sealed class DockerSecuritySettings
{
    public const string DefaultDailyScanTime = "12:30";

    /// <summary>Whether the images of this computer are scanned. Other machines decide for themselves.</summary>
    public bool ScanLocalImages { get; set; } = true;

    public ReportThreshold ReportFrom { get; set; } = ReportThreshold.Critical;

    /// <summary>Time of day, "HH:mm", at which all images are scanned again.</summary>
    public string DailyScanTime { get; set; } = DefaultDailyScanTime;

    /// <summary>A vulnerability nobody has fixed yet leaves nothing to do, so by default it is not shown.</summary>
    public bool OnlyFixable { get; set; } = true;

    public bool SkipOnBattery { get; set; } = true;

    public List<IgnoredVulnerability> Ignored { get; set; } = [];

    [JsonIgnore]
    public TimeOnly EffectiveDailyScanTime => TryParseTime(DailyScanTime, out var time) ? time : TimeOnly.ParseExact(DefaultDailyScanTime, "HH:mm");

    public static bool TryParseTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text?.Trim(), ["HH:mm", "H:mm"], out time);

    public bool IsIgnored(string vulnerabilityId) => Ignored.Any(entry => string.Equals(entry.Id, vulnerabilityId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a vulnerability is shown at all, given what the user chose to leave out.</summary>
    public bool IsShown(Vulnerability vulnerability) => (!OnlyFixable || vulnerability.HasFix) && !IsIgnored(vulnerability.Id);

    /// <summary>Whether a vulnerability that is shown is also grave enough to be reported.</summary>
    public bool IsReported(Vulnerability vulnerability) => IsShown(vulnerability) && ReportFrom switch
    {
        ReportThreshold.Critical => vulnerability.Severity >= VulnerabilitySeverity.Critical,
        ReportThreshold.High => vulnerability.Severity >= VulnerabilitySeverity.High,
        _ => false,
    };

    public DockerSecuritySettings Clone() => new()
    {
        ScanLocalImages = ScanLocalImages,
        ReportFrom = ReportFrom,
        DailyScanTime = DailyScanTime,
        OnlyFixable = OnlyFixable,
        SkipOnBattery = SkipOnBattery,
        Ignored = [.. Ignored],
    };
}

/// <summary>How often Docker is asked. The user picks a pace; what it means in seconds is decided here.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RefreshRate>))]
public enum RefreshRate
{
    Fast,
    Normal,
    Economical,
}

/// <summary>What the Docker widget does with one of the machines the user entered.</summary>
public sealed class DockerMachineUse
{
    /// <summary>The id of the machine in the list all widgets share.</summary>
    public string MachineId { get; set; } = string.Empty;

    public bool ShowContainers { get; set; } = true;

    /// <summary>
    /// Whether containers on this machine may be started, stopped and restarted from Meerkat. Off unless the
    /// user switches it on: a slip of the mouse matters more on a server than on the own machine.
    /// </summary>
    public bool AllowActions { get; set; }

    /// <summary>
    /// Whether the images of this machine are scanned for vulnerabilities. Off unless the user switches it on:
    /// a scan takes processor time that a server may need for the work it is there for.
    /// </summary>
    public bool ScanImages { get; set; }

    /// <summary>Seconds between two requests; null follows the pace chosen for the widget. Only set by hand in the settings file.</summary>
    public double? IntervalSeconds { get; set; }
}

/// <summary>
/// A Docker instance on another machine, reached by running the docker CLI there over SSH: a machine of
/// the shared list together with what this widget does with it.
/// </summary>
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

    public bool AllowActions { get; set; }

    public bool ScanImages { get; set; }

    /// <summary>Key under which the machine's password is kept in the secret store.</summary>
    [JsonIgnore]
    public string SecretKey => $"machine/{Id}";

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Address : Name;

    public static DockerHost From(SshMachine machine, DockerMachineUse use, double defaultIntervalSeconds) => new()
    {
        Id = machine.Id,
        Name = machine.Name,
        Address = machine.Address,
        User = machine.User,
        Port = machine.Port,
        Authentication = machine.Authentication,
        KeyFile = machine.KeyFile,
        IntervalSeconds = use.IntervalSeconds ?? defaultIntervalSeconds,
        AllowActions = use.AllowActions,
        ScanImages = use.ScanImages,
    };

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

    public RefreshRate Refresh { get; set; } = RefreshRate.Normal;

    /// <summary>
    /// Seconds between two requests, for whoever wants other figures than <see cref="Refresh"/> stands for.
    /// Not offered in the settings window; set them in the settings file.
    /// </summary>
    public double? CustomListIntervalSeconds { get; set; }

    public double? CustomStatsIntervalSeconds { get; set; }

    /// <summary>For other machines, which are asked less often because every request opens a connection.</summary>
    public double? CustomRemoteIntervalSeconds { get; set; }

    /// <summary>Not offered in the settings window: hardly anybody needs another figure.</summary>
    public int MaxLogLines { get; set; } = DefaultMaxLogLines;

    /// <summary>Which of the shared machines the widget uses, and for what.</summary>
    public List<DockerMachineUse> Machines { get; set; } = [];

    /// <summary>
    /// The machines of <see cref="Machines"/> with all that is needed to reach them, put together by
    /// <see cref="DockerSettingsSource"/>. Not stored: the connection belongs to the shared list.
    /// </summary>
    [JsonIgnore]
    public List<DockerHost> RemoteHosts { get; set; } = [];

    /// <summary>Hosts as the widget stored them before machines were shared; taken over into the shared list once.</summary>
    [JsonPropertyName("RemoteHosts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DockerHost>? LegacyRemoteHosts { get; set; }

    /// <summary>Ids of the sources whose section the user folded up on the Docker page.</summary>
    public List<string> CollapsedSources { get; set; } = [];

    /// <summary>The same for the page with the images, where each system can be folded up on its own.</summary>
    public List<string> CollapsedImageSources { get; set; } = [];

    public DockerSecuritySettings Security { get; set; } = new();

    public static TimeSpan GetInterval(DockerHost host) => TimeSpan.FromSeconds(
        double.IsFinite(host.IntervalSeconds) ? Math.Max(MinimumRemoteIntervalSeconds, host.IntervalSeconds) : DockerHost.DefaultIntervalSeconds);

    [JsonIgnore]
    public TimeSpan ListInterval => ToInterval(CustomListIntervalSeconds ?? Refresh switch
    {
        RefreshRate.Fast => 2,
        RefreshRate.Economical => 15,
        _ => 5,
    });

    [JsonIgnore]
    public TimeSpan StatsInterval => ToInterval(CustomStatsIntervalSeconds ?? Refresh switch
    {
        RefreshRate.Fast => 2,
        RefreshRate.Economical => 10,
        _ => 3,
    });

    [JsonIgnore]
    public double RemoteIntervalSeconds => CustomRemoteIntervalSeconds ?? Refresh switch
    {
        RefreshRate.Fast => 10,
        RefreshRate.Economical => 60,
        _ => DockerHost.DefaultIntervalSeconds,
    };

    [JsonIgnore]
    public int EffectiveMaxLogLines => Math.Clamp(MaxLogLines, MinimumLogLines, MaximumLogLines);

    // A hand-edited settings file must not be able to turn polling into a busy loop.
    private static TimeSpan ToInterval(double seconds) =>
        TimeSpan.FromSeconds(double.IsFinite(seconds) ? Math.Max(MinimumIntervalSeconds, seconds) : MinimumIntervalSeconds);
}