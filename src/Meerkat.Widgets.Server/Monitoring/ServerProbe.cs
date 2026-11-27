using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Meerkat.Abstractions;
using Meerkat.Widgets.Server.Settings;
using Meerkat.Widgets.Server.Ssh;

namespace Meerkat.Widgets.Server.Monitoring;

/// <summary>Cumulative CPU time counters; usage is the difference between two samples.</summary>
public readonly record struct CpuTimes(long Total, long Idle)
{
    /// <summary>Returns the share of non-idle time since the earlier sample in percent, or null if no time passed.</summary>
    public double? UsageSince(CpuTimes earlier)
    {
        var total = Total - earlier.Total;
        return total > 0 ? Math.Clamp(100.0 * (total - (Idle - earlier.Idle)) / total, 0, 100) : null;
    }
}

public sealed record ServiceState(string Name, string State)
{
    public bool IsActive => State == "active";
}

public sealed record HostSnapshot(
    string OsName,
    TimeSpan Uptime,
    double Load1,
    double Load5,
    double Load15,
    int Cores,
    CpuTimes Cpu,
    long MemoryTotalBytes,
    long MemoryAvailableBytes,
    long DiskTotalBytes,
    long DiskUsedBytes,
    IReadOnlyList<ServiceState> Services,
    TimeSpan ResponseTime)
{
    public double MemoryUsedPercent => MemoryTotalBytes > 0 ? 100.0 * (MemoryTotalBytes - MemoryAvailableBytes) / MemoryTotalBytes : 0;

    public double DiskUsedPercent => DiskTotalBytes > 0 ? 100.0 * DiskUsedBytes / DiskTotalBytes : 0;
}

public enum ProbeFailureKind
{
    Unreachable,

    /// <summary>The host presents a different key than the one in known_hosts.</summary>
    HostKeyChanged,

    /// <summary>The host is not in known_hosts yet.</summary>
    HostKeyUnknown,
    AuthenticationFailed,

    /// <summary>The connection worked but the host did not answer as a Linux system would.</summary>
    Unsupported,
}

public sealed class ProbeException(ProbeFailureKind kind, string message, string? fingerprint = null) : Exception(message)
{
    public ProbeFailureKind Kind { get; } = kind;

    /// <summary>For a changed host key: the fingerprint of the key the host presents now.</summary>
    public string? Fingerprint { get; } = fingerprint;
}

public interface IServerProbe
{
    /// <param name="password">Overrides the stored password, for checking a host before its password is saved.</param>
    Task<HostSnapshot> ProbeAsync(ServerHost host, HostKeyPolicy policy, CancellationToken cancellationToken, string? password = null);

    /// <summary>Replaces the stored host key with the one the host presents now.</summary>
    Task<HostSnapshot> TrustHostKeyAsync(ServerHost host, CancellationToken cancellationToken);

    void OpenTerminal(ServerHost host);
}

/// <summary>Collects a host's vital signs with a single SSH call that runs a small read-only shell script.</summary>
public sealed partial class ServerProbe(ISshClient sshClient, IWidgetSecretStore secrets) : IServerProbe
{
    private const long BytesPerKibibyte = 1024;

    public async Task<HostSnapshot> ProbeAsync(ServerHost host, HostKeyPolicy policy, CancellationToken cancellationToken, string? password = null)
    {
        var target = ToTarget(host, password);
        if (target.Authentication == SshAuthentication.Password && string.IsNullOrEmpty(target.Password))
        {
            throw new ProbeException(ProbeFailureKind.AuthenticationFailed, "No password is stored for this host");
        }

        SshResult result;
        try
        {
            result = await sshClient.RunAsync(target, BuildScript(host.Services), policy, cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception)
        {
            throw new ProbeException(ProbeFailureKind.Unreachable, "The OpenSSH client (ssh.exe) was not found on this computer.");
        }
        catch (ArgumentException exception)
        {
            throw new ProbeException(ProbeFailureKind.Unreachable, exception.Message);
        }

        if (result.ExitCode != 0 && !result.Output.Contains(EndMarker, StringComparison.Ordinal))
        {
            throw ClassifyFailure(result.Error);
        }

        return ParseSnapshot(result.Output, result.Duration);
    }

    public async Task<HostSnapshot> TrustHostKeyAsync(ServerHost host, CancellationToken cancellationToken)
    {
        await sshClient.ForgetHostKeyAsync(ToTarget(host, password: null), cancellationToken).ConfigureAwait(false);
        return await ProbeAsync(host, HostKeyPolicy.AcceptNew, cancellationToken).ConfigureAwait(false);
    }

    public void OpenTerminal(ServerHost host) => sshClient.OpenTerminal(host.ToTarget());

    private SshTarget ToTarget(ServerHost host, string? password) =>
        host.ToTarget(host.Authentication == SshAuthentication.Password ? password ?? secrets.Read(host.SecretKey) : null);

    private const string EndMarker = "##end";

    /// <summary>
    /// Builds the script that runs on the host. It only reads from /proc, df and systemctl, and it avoids
    /// double quotes so that it survives every layer of quoting unchanged.
    /// </summary>
    public static string BuildScript(IEnumerable<string> services)
    {
        var units = services.Select(service => service.Trim()).Where(service => service.Length > 0).ToList();
        foreach (var unit in units.Where(unit => !ServiceName().IsMatch(unit)))
        {
            // Unit names become part of a shell command, so anything that is not a plain name is refused.
            throw new ProbeException(ProbeFailureKind.Unsupported, $"'{unit}' is not a valid systemd unit name.");
        }

        var serviceCheck = units.Count == 0
            ? string.Empty
            : $" for s in {string.Join(' ', units)}; do st=$(systemctl is-active $s 2>/dev/null); echo $s ${{st:-unknown}}; done;";

        return "echo '##os'; (. /etc/os-release 2>/dev/null; echo $PRETTY_NAME);"
            + " echo '##uptime'; cat /proc/uptime;"
            + " echo '##load'; cat /proc/loadavg;"
            + " echo '##cores'; nproc;"
            + " echo '##stat'; head -n 1 /proc/stat;"
            + " echo '##mem'; grep -E '^(MemTotal|MemAvailable):' /proc/meminfo;"
            + " echo '##disk'; df -kP / | tail -n 1;"
            + $" echo '##services';{serviceCheck}"
            + $" echo '{EndMarker}'";
    }

    public static HostSnapshot ParseSnapshot(string output, TimeSpan responseTime)
    {
        var sections = ReadSections(output);
        try
        {
            var load = Fields(sections, "load");
            var memory = sections["mem"].ToDictionary(
                line => line.Split(':')[0].Trim(),
                line => Number(line.Split(':', 2)[1].Trim().Split(' ')[0]) * BytesPerKibibyte);
            var disk = Fields(sections, "disk");
            var stat = Fields(sections, "stat").Skip(1).Select(Number).ToArray();

            // user, nice, system, idle, iowait, irq, softirq, steal; the guest columns are already part of user.
            var counters = stat.Take(8).ToArray();
            var idle = counters[3] + (counters.Length > 4 ? counters[4] : 0);

            return new HostSnapshot(
                sections["os"].FirstOrDefault(line => line.Length > 0) ?? "Linux",
                TimeSpan.FromSeconds(double.Parse(Fields(sections, "uptime")[0], CultureInfo.InvariantCulture)),
                double.Parse(load[0], CultureInfo.InvariantCulture),
                double.Parse(load[1], CultureInfo.InvariantCulture),
                double.Parse(load[2], CultureInfo.InvariantCulture),
                (int)Number(Fields(sections, "cores")[0]),
                new CpuTimes(counters.Sum(), idle),
                memory["MemTotal"],
                memory["MemAvailable"],
                Number(disk[^5]) * BytesPerKibibyte,
                Number(disk[^4]) * BytesPerKibibyte,
                [.. sections["services"]
                    .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Where(parts => parts.Length == 2)
                    .Select(parts => new ServiceState(parts[0], parts[1]))],
                responseTime);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or FormatException or IndexOutOfRangeException or InvalidOperationException or OverflowException)
        {
            throw new ProbeException(ProbeFailureKind.Unsupported, "The host answered, but not like a Linux system with /proc.");
        }
    }

    public static ProbeException ClassifyFailure(string error)
    {
        if (error.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.Ordinal))
        {
            var fingerprint = Fingerprint().Match(error);
            return new ProbeException(ProbeFailureKind.HostKeyChanged, "The host key has changed.", fingerprint.Success ? fingerprint.Value : null);
        }

        if (error.Contains("Host key verification failed", StringComparison.Ordinal)
            || error.Contains("host key is known", StringComparison.Ordinal))
        {
            return new ProbeException(ProbeFailureKind.HostKeyUnknown, "The host key is not trusted yet.");
        }

        if (error.Contains("Permission denied", StringComparison.Ordinal))
        {
            return new ProbeException(ProbeFailureKind.AuthenticationFailed, "Authentication failed");
        }

        return new ProbeException(ProbeFailureKind.Unreachable, DescribeConnectionError(error));
    }

    private static string DescribeConnectionError(string error)
    {
        if (error.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "SSH timeout";
        }

        if (error.Contains("Could not resolve hostname", StringComparison.Ordinal))
        {
            return "Host name not found";
        }

        if (error.Contains("Connection refused", StringComparison.Ordinal))
        {
            return "Connection refused";
        }

        // Fall back to what ssh said, without its "ssh: " prefix and without warnings that precede the error.
        var line = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(candidate => !candidate.StartsWith("Warning:", StringComparison.Ordinal));
        return line is null ? "SSH connection failed" : line.Replace("ssh: ", string.Empty, StringComparison.Ordinal);
    }

    private static Dictionary<string, List<string>> ReadSections(string output)
    {
        var sections = new Dictionary<string, List<string>>();
        List<string>? current = null;
        foreach (var line in output.Split('\n').Select(line => line.Trim()))
        {
            if (line.StartsWith("##", StringComparison.Ordinal))
            {
                current = [];
                sections[line[2..]] = current;
            }
            else if (line.Length > 0)
            {
                current?.Add(line);
            }
        }

        return sections;
    }

    private static string[] Fields(Dictionary<string, List<string>> sections, string name) =>
        sections[name][0].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

    private static long Number(string text) => long.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9@._:\-]*$")]
    private static partial Regex ServiceName();

    [GeneratedRegex(@"SHA256:[A-Za-z0-9+/=]+")]
    private static partial Regex Fingerprint();
}
