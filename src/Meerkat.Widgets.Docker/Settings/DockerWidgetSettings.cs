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

    /// <summary>
    /// Whether the images of this host are scanned for vulnerabilities. Off unless the user switches it on:
    /// a scan takes processor time that a server may need for the work it is there for.
    /// </summary>
    public bool ScanImages { get; set; }

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

    public DockerSecuritySettings Security { get; set; } = new();

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
