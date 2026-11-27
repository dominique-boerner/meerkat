using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Meerkat.Widgets.Docker.Processes;

namespace Meerkat.Widgets.Docker.Containers;

public sealed partial class WslCliContainerProvider(IProcessRunner processRunner, Func<string?> distributionAccessor)
    : IContainerProvider
{
    public const string LogTailLines = "200";

    private const string WslExecutable = "wsl.exe";

    public async Task<IReadOnlyList<ContainerInfo>> ListAsync(CancellationToken cancellationToken) =>
        DockerOutputParser.ParseContainerList(
            await RunDockerAsync(["ps", "-a", "--format", "json"], cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<ContainerStats>> GetStatsAsync(CancellationToken cancellationToken) =>
        DockerOutputParser.ParseStats(
            await RunDockerAsync(["stats", "--no-stream", "--format", "json"], cancellationToken).ConfigureAwait(false));

    public async IAsyncEnumerable<LogLine> StreamLogsAsync(
        string containerId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var arguments = BuildArguments(["logs", "-f", "--timestamps", "--tail", LogTailLines, Validated(containerId)]);
        await foreach (var line in processRunner.StreamAsync(WslExecutable, arguments, cancellationToken)
                           .ConfigureAwait(false))
        {
            var text = AnsiEscapeSequence().Replace(line.Text, string.Empty);
            yield return DockerOutputParser.ParseLogLine(text, line.IsError, DateTimeOffset.Now);
        }
    }

    public Task StartAsync(string containerId, CancellationToken cancellationToken) =>
        RunDockerAsync(["start", Validated(containerId)], cancellationToken);

    public Task StopAsync(string containerId, CancellationToken cancellationToken) =>
        RunDockerAsync(["stop", Validated(containerId)], cancellationToken);

    public Task RestartAsync(string containerId, CancellationToken cancellationToken) =>
        RunDockerAsync(["restart", Validated(containerId)], cancellationToken);

    private async Task<string> RunDockerAsync(string[] dockerArguments, CancellationToken cancellationToken)
    {
        var commandText = DescribeCommand(dockerArguments);

        ProcessResult result;
        try
        {
            result = await processRunner
                .RunAsync(WslExecutable, BuildArguments(dockerArguments), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Win32Exception exception)
        {
            throw new ContainerProviderException(ContainerFailureKind.WslUnavailable, commandText, null, exception.Message);
        }

        if (result.ExitCode != 0)
        {
            var details = FirstNonEmpty(result.StandardError, result.StandardOutput)
                ?? $"exit code {result.ExitCode}";
            throw new ContainerProviderException(Classify(details), commandText, result.ExitCode, details);
        }

        return result.StandardOutput;
    }

    private static ContainerFailureKind Classify(string details)
    {
        if (Contains(details, "Cannot connect to the Docker daemon") || Contains(details, "docker daemon running"))
        {
            return ContainerFailureKind.DaemonUnavailable;
        }

        // wsl.exe reports its own failures with codes like "Wsl/Service/WSL_E_DISTRO_NOT_FOUND".
        if (Contains(details, "WSL_E_") || Contains(details, "Wsl/"))
        {
            return ContainerFailureKind.WslUnavailable;
        }

        if (Contains(details, "execvpe") || Contains(details, "No such file or directory"))
        {
            return ContainerFailureKind.DockerNotInstalled;
        }

        return ContainerFailureKind.Other;
    }

    private string DescribeCommand(string[] dockerArguments)
    {
        var distribution = distributionAccessor();
        var prefix = string.IsNullOrWhiteSpace(distribution) ? "wsl" : $"wsl -d {distribution.Trim()}";
        return $"{prefix} docker {string.Join(' ', dockerArguments.TakeWhile(argument => argument != "--format"))}";
    }

    private List<string> BuildArguments(string[] dockerArguments)
    {
        var arguments = new List<string>();
        var distribution = distributionAccessor();
        if (!string.IsNullOrWhiteSpace(distribution))
        {
            arguments.Add("-d");
            arguments.Add(distribution.Trim());
        }

        // --exec bypasses the Linux shell, so arguments are never subject to shell expansion.
        arguments.Add("--exec");
        arguments.Add("docker");
        arguments.AddRange(dockerArguments);
        return arguments;
    }

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
