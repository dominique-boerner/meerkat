using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Meerkat.Ssh;

namespace Meerkat.Widgets.Docker.Containers;

/// <summary>
/// Reads the containers of another machine by running the docker CLI there over SSH. Nothing is installed
/// on the host and no port besides SSH is needed; the user Meerkat signs in with must be allowed to use Docker.
/// </summary>
public sealed partial class SshContainerProvider(ISshClient ssh, Func<SshTarget> targetAccessor) : IContainerProvider
{
    // The template form of "--format json" is understood by every Docker version, the keyword only by recent ones.
    private const string JsonFormat = "--format '{{json .}}'";
    private const string StatsMarker = "___MEERKAT_STATS___";

    private IReadOnlyList<ContainerStats> _latestStats = [];

    /// <summary>
    /// Every call opens an SSH connection, so the list and the figures are fetched together and the
    /// figures are handed out by <see cref="GetStatsAsync"/> without asking the host again.
    /// </summary>
    public async Task<IReadOnlyList<ContainerInfo>> ListAsync(CancellationToken cancellationToken)
    {
        var output = await RunAsync(
            $"docker ps -a {JsonFormat} && echo {StatsMarker} && docker stats --no-stream {JsonFormat}",
            "docker ps -a",
            HostKeyPolicy.Strict,
            cancellationToken).ConfigureAwait(false);

        var parts = output.Split(StatsMarker, 2);
        _latestStats = parts.Length > 1 ? DockerOutputParser.ParseStats(parts[1]) : [];
        return DockerOutputParser.ParseContainerList(parts[0]);
    }

    public Task<IReadOnlyList<ContainerStats>> GetStatsAsync(CancellationToken cancellationToken) => Task.FromResult(_latestStats);

    public async IAsyncEnumerable<LogLine> StreamLogsAsync(string containerId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var command = $"docker logs -f --timestamps --tail {WslCliContainerProvider.LogTailLines} {Validated(containerId)}";
        await foreach (var line in ssh.StreamAsync(targetAccessor(), command, HostKeyPolicy.Strict, cancellationToken).ConfigureAwait(false))
        {
            var text = AnsiEscapeSequence().Replace(line.Text, string.Empty);
            yield return DockerOutputParser.ParseLogLine(text, line.IsError, DateTimeOffset.Now);
        }
    }

    public Task StartAsync(string containerId, CancellationToken cancellationToken) => RunActionAsync("start", containerId, cancellationToken);

    public Task StopAsync(string containerId, CancellationToken cancellationToken) => RunActionAsync("stop", containerId, cancellationToken);

    public Task RestartAsync(string containerId, CancellationToken cancellationToken) => RunActionAsync("restart", containerId, cancellationToken);

    /// <summary>
    /// Connects once and returns the version of the Docker daemon. Used when a host is added: that is the
    /// moment the user confirms the host, so its key may be stored then, and only then.
    /// </summary>
    public async Task<string> CheckAsync(CancellationToken cancellationToken) =>
        (await RunAsync("docker version --format '{{.Server.Version}}'", "docker version", HostKeyPolicy.AcceptNew, cancellationToken).ConfigureAwait(false)).Trim();

    private Task RunActionAsync(string action, string containerId, CancellationToken cancellationToken) =>
        RunAsync($"docker {action} {Validated(containerId)}", $"docker {action} {containerId}", HostKeyPolicy.Strict, cancellationToken);

    private async Task<string> RunAsync(string command, string commandText, HostKeyPolicy policy, CancellationToken cancellationToken)
    {
        var target = targetAccessor();
        var description = $"ssh {target.Address} {commandText}";

        SshResult result;
        try
        {
            result = await ssh.RunAsync(target, command, policy, cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception exception)
        {
            throw new ContainerProviderException(ContainerFailureKind.SshUnavailable, description, null, exception.Message);
        }

        if (result.ExitCode == 0)
        {
            return result.Output;
        }

        var details = FirstNonEmpty(result.Error, result.Output) ?? $"exit code {result.ExitCode}";

        // ssh reports its own failures with one exit code; every other code comes from the command on the host.
        if (result.ExitCode is SshFailure.ConnectionFailedExitCode or < 0)
        {
            var failure = SshFailure.Classify(result.Error);
            var kind = failure.Kind switch
            {
                SshFailureKind.HostKeyChanged => ContainerFailureKind.HostKeyChanged,
                SshFailureKind.HostKeyUnknown => ContainerFailureKind.HostKeyUnknown,
                SshFailureKind.AuthenticationFailed => ContainerFailureKind.AuthenticationFailed,
                _ => ContainerFailureKind.HostUnreachable,
            };
            var message = failure.Fingerprint is null ? failure.Message : $"{failure.Message} New fingerprint: {failure.Fingerprint}";
            throw new ContainerProviderException(kind, description, result.ExitCode, message);
        }

        throw new ContainerProviderException(Classify(details, result.ExitCode), description, result.ExitCode, details);
    }

    private static ContainerFailureKind Classify(string details, int exitCode)
    {
        if (Contains(details, "permission denied") && Contains(details, "docker"))
        {
            return ContainerFailureKind.PermissionDenied;
        }

        if (Contains(details, "Cannot connect to the Docker daemon") || Contains(details, "docker daemon running"))
        {
            return ContainerFailureKind.DaemonUnavailable;
        }

        // 127 is the shell's code for a command it does not know.
        if (exitCode == 127 || Contains(details, "command not found"))
        {
            return ContainerFailureKind.DockerNotInstalled;
        }

        return ContainerFailureKind.Other;
    }

    // The reference becomes part of a command line for the shell of the host, so only harmless characters pass.
    private static string Validated(string containerId) =>
        ContainerReference().IsMatch(containerId)
            ? containerId
            : throw new ArgumentException($"Invalid container reference '{containerId}'.", nameof(containerId));

    private static bool Contains(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static string? FirstNonEmpty(params string[] candidates) =>
        candidates.Select(candidate => candidate.Trim()).FirstOrDefault(candidate => candidate.Length > 0);

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex ContainerReference();

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscapeSequence();
}
