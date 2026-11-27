using System.Globalization;

namespace Meerkat.Widgets.Docker.Containers;

public sealed record ContainerInfo(string Id, string Name, string Image, string State, string Status);

public sealed record ContainerStats(
    string Id,
    string Name,
    double? CpuPercent,
    long? MemoryUsageBytes,
    long? MemoryLimitBytes,
    double? MemoryPercent);

public sealed record LogLine(DateTimeOffset Timestamp, string Text, bool IsError)
{
    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public string StreamLabel => IsError ? "ERR" : "OUT";
}

public enum ContainerFailureKind
{
    Other,

    /// <summary>wsl.exe could not be started or the distribution is missing or fails to boot.</summary>
    WslUnavailable,

    /// <summary>The distribution answers, but the Docker daemon does not.</summary>
    DaemonUnavailable,

    /// <summary>The distribution has no docker CLI.</summary>
    DockerNotInstalled,
}

public sealed class ContainerProviderException(ContainerFailureKind kind, string command, int? exitCode, string details)
    : Exception(details)
{
    public ContainerFailureKind Kind { get; } = kind;

    /// <summary>Human readable command line that failed.</summary>
    public string Command { get; } = command;

    public int? ExitCode { get; } = exitCode;
}
