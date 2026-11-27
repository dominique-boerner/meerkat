using System.ComponentModel;
using Meerkat.Widgets.Docker.Containers;
using Meerkat.Widgets.Docker.Processes;

namespace Meerkat.Tests.Docker;

public class WslCliContainerProviderTests
{
    [Fact]
    public async Task ListAsync_UsesDefaultDistributionWhenNoneIsConfigured()
    {
        var runner = new FakeProcessRunner();
        var provider = new WslCliContainerProvider(runner, () => null);

        await provider.ListAsync(CancellationToken.None);

        var call = Assert.Single(runner.Calls);
        Assert.Equal("wsl.exe", call.FileName);
        Assert.Equal(["--exec", "docker", "ps", "-a", "--format", "json"], call.Arguments);
    }

    [Fact]
    public async Task GetStatsAsync_TargetsConfiguredDistribution()
    {
        var runner = new FakeProcessRunner();
        var provider = new WslCliContainerProvider(runner, () => "Ubuntu-24.04");

        await provider.GetStatsAsync(CancellationToken.None);

        Assert.Equal(
            ["-d", "Ubuntu-24.04", "--exec", "docker", "stats", "--no-stream", "--format", "json"],
            Assert.Single(runner.Calls).Arguments);
    }

    [Theory]
    [InlineData("Cannot connect to the Docker daemon at unix:///var/run/docker.sock.", ContainerFailureKind.DaemonUnavailable)]
    [InlineData("Fehlercode: Wsl/Service/WSL_E_DISTRO_NOT_FOUND", ContainerFailureKind.WslUnavailable)]
    [InlineData("<3>WSL (12) ERROR: CreateProcessCommon:640: execvpe(docker) failed: No such file or directory", ContainerFailureKind.DockerNotInstalled)]
    [InlineData("permission denied while trying to connect", ContainerFailureKind.Other)]
    public async Task ListAsync_ClassifiesFailuresByTheirOutput(string stderr, ContainerFailureKind expectedKind)
    {
        var runner = new FakeProcessRunner { Result = new ProcessResult(1, string.Empty, stderr + "\n") };
        var provider = new WslCliContainerProvider(runner, () => "Ubuntu-24.04");

        var exception = await Assert.ThrowsAsync<ContainerProviderException>(
            () => provider.ListAsync(CancellationToken.None));

        Assert.Equal(expectedKind, exception.Kind);
        Assert.Equal(stderr, exception.Message);
        Assert.Equal(1, exception.ExitCode);
        Assert.Equal("wsl -d Ubuntu-24.04 docker ps -a", exception.Command);
    }

    [Fact]
    public async Task ListAsync_ReportsWslAsUnavailableWhenItCannotBeStarted()
    {
        var runner = new FakeProcessRunner { StartFailure = new Win32Exception(2) };
        var provider = new WslCliContainerProvider(runner, () => null);

        var exception = await Assert.ThrowsAsync<ContainerProviderException>(
            () => provider.ListAsync(CancellationToken.None));

        Assert.Equal(ContainerFailureKind.WslUnavailable, exception.Kind);
        Assert.Null(exception.ExitCode);
    }

    [Fact]
    public async Task StreamLogsAsync_FollowsWithTimestampsAndStripsAnsiSequences()
    {
        var runner = new FakeProcessRunner();
        runner.StreamLines.Add(new ProcessOutputLine("2026-11-27T12:14:02.118123456Z \u001b[32mready\u001b[0m", IsError: false));
        runner.StreamLines.Add(new ProcessOutputLine("2026-11-27T12:14:03.5Z boom", IsError: true));
        var provider = new WslCliContainerProvider(runner, () => null);

        var lines = new List<LogLine>();
        await foreach (var line in provider.StreamLogsAsync("3f2a9c1d7b6e", CancellationToken.None))
        {
            lines.Add(line);
        }

        Assert.Equal(
            [
                new LogLine(new DateTimeOffset(2026, 11, 27, 12, 14, 2, 118, TimeSpan.Zero).AddTicks(1234), "ready", false),
                new LogLine(new DateTimeOffset(2026, 11, 27, 12, 14, 3, 500, TimeSpan.Zero), "boom", true),
            ],
            lines);
        Assert.Equal(
            ["--exec", "docker", "logs", "-f", "--timestamps", "--tail", "200", "3f2a9c1d7b6e"],
            Assert.Single(runner.Calls).Arguments);
    }

    [Theory]
    [InlineData("--privileged")]
    [InlineData("abc; rm -rf /")]
    [InlineData("")]
    public async Task Actions_RejectContainerReferencesThatCouldBeMisreadAsOptions(string containerId)
    {
        var runner = new FakeProcessRunner();
        var provider = new WslCliContainerProvider(runner, () => null);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.RestartAsync(containerId, CancellationToken.None));

        Assert.Empty(runner.Calls);
    }
}
