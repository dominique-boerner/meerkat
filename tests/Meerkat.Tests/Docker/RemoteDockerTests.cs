using Meerkat.Abstractions;
using Meerkat.Ssh;
using Meerkat.Tests.Server;
using Meerkat.Widgets.Docker.Containers;
using Meerkat.Widgets.Docker.Settings;
using Meerkat.Widgets.Docker.ViewModels;

namespace Meerkat.Tests.Docker;

public class RemoteDockerTests
{
    private const string ListAndStats = """
        {"ID":"3f2a9c1d7b6e","Image":"postgres:17","Names":"db","State":"running","Status":"Up 2 hours"}
        {"ID":"a1b2c3d4e5f6","Image":"nginx:1.27","Names":"web","State":"exited","Status":"Exited (0) 3 days ago"}
        ___MEERKAT_STATS___
        {"CPUPerc":"12.34%","Container":"3f2a9c1d7b6e","ID":"3f2a9c1d7b6e","MemPerc":"1.56%","MemUsage":"120.5MiB / 7.675GiB","Name":"db"}
        """;

    private static readonly SshTarget Target = new("build-01.example.com", "deploy", 2222, SshAuthentication.Agent, null);

    private readonly FakeSshClient _ssh = new();
    private readonly FakeContainerProvider _local = new();
    private readonly InMemoryWidgetSettingsStore _settingsStore = new();
    private readonly FakeOverlayState _overlayState = new();
    private readonly FakeOverlayCommands _overlayCommands = new();

    [Fact]
    public async Task Provider_FetchesListAndFiguresWithOneConnection()
    {
        _ssh.Result = new SshResult(0, ListAndStats, string.Empty, TimeSpan.Zero);
        var provider = new SshContainerProvider(_ssh, () => Target);

        // Before the first list nothing is known, and asking for figures must not open a connection.
        Assert.Empty(await provider.GetStatsAsync(CancellationToken.None));

        var containers = await provider.ListAsync(CancellationToken.None);
        var stats = await provider.GetStatsAsync(CancellationToken.None);

        Assert.Equal(["db", "web"], containers.Select(container => container.Name));
        Assert.Equal(12.34, Assert.Single(stats).CpuPercent);
        Assert.Equal(["run build-01.example.com Strict"], _ssh.Calls);
        Assert.Equal(
            "docker ps -a --format '{{json .}}' && echo ___MEERKAT_STATS___ && docker stats --no-stream --format '{{json .}}'",
            Assert.Single(_ssh.Commands));
    }

    [Fact]
    public async Task Provider_RunsActionsAndLogsOnTheHost()
    {
        _ssh.StreamLines.Add(new SshOutputLine("2026-11-27T15:42:00.000000000Z \u001b[32mready\u001b[0m", false));
        _ssh.StreamLines.Add(new SshOutputLine("2026-11-27T15:42:01.000000000Z failed", true));
        var provider = new SshContainerProvider(_ssh, () => Target);

        await provider.RestartAsync("3f2a9c1d7b6e", CancellationToken.None);
        await provider.StopAsync("web", CancellationToken.None);
        await provider.StartAsync("web", CancellationToken.None);
        var lines = new List<LogLine>();
        await foreach (var line in provider.StreamLogsAsync("db", CancellationToken.None))
        {
            lines.Add(line);
        }

        Assert.Equal(["docker restart 3f2a9c1d7b6e", "docker stop web", "docker start web", "docker logs -f --timestamps --tail 200 db"], _ssh.Commands);
        Assert.Equal(["ready", "failed"], lines.Select(line => line.Text));
        Assert.Equal([false, true], lines.Select(line => line.IsError));
    }

    [Theory]
    [InlineData("web; rm -rf /")]
    [InlineData("$(reboot)")]
    [InlineData("-f")]
    [InlineData("web db")]
    public async Task Provider_RefusesContainerReferencesTheShellCouldMisread(string reference)
    {
        var provider = new SshContainerProvider(_ssh, () => Target);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.StopAsync(reference, CancellationToken.None));

        Assert.Empty(_ssh.Calls);
    }

    [Theory]
    [InlineData(255, "ssh: connect to host build-01 port 22: Connection refused", ContainerFailureKind.HostUnreachable)]
    [InlineData(255, "deploy@build-01: Permission denied (publickey).", ContainerFailureKind.AuthenticationFailed)]
    [InlineData(255, "No ED25519 host key is known for build-01 and you have requested strict checking.\nHost key verification failed.", ContainerFailureKind.HostKeyUnknown)]
    [InlineData(255, "@ WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED! @\nSHA256:abcDEF123+/=", ContainerFailureKind.HostKeyChanged)]
    [InlineData(-1, "The connection timed out.", ContainerFailureKind.HostUnreachable)]
    [InlineData(1, "permission denied while trying to connect to the Docker daemon socket at unix:///var/run/docker.sock", ContainerFailureKind.PermissionDenied)]
    [InlineData(1, "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?", ContainerFailureKind.DaemonUnavailable)]
    [InlineData(127, "bash: line 1: docker: command not found", ContainerFailureKind.DockerNotInstalled)]
    [InlineData(1, "Error response from daemon: something else", ContainerFailureKind.Other)]
    public async Task Provider_TellsConnectionProblemsFromDockerProblems(int exitCode, string error, ContainerFailureKind expected)
    {
        _ssh.Result = new SshResult(exitCode, string.Empty, error, TimeSpan.Zero);
        var provider = new SshContainerProvider(_ssh, () => Target);

        var exception = await Assert.ThrowsAsync<ContainerProviderException>(() => provider.ListAsync(CancellationToken.None));

        Assert.Equal(expected, exception.Kind);
        Assert.Equal("ssh build-01.example.com docker ps -a", exception.Command);

        // Every kind has a card that says what is wrong in the user's terms.
        var card = ContainerFailureViewModel.From(exception, "build-01");
        Assert.NotEqual("Docker query failed", expected == ContainerFailureKind.Other ? string.Empty : card.Title);
    }

    [Fact]
    public async Task Provider_AcceptsANewHostKeyOnlyWhenTheHostIsChecked()
    {
        _ssh.Result = new SshResult(0, "27.3.1\n", string.Empty, TimeSpan.Zero);
        var provider = new SshContainerProvider(_ssh, () => Target);

        Assert.Equal("27.3.1", await provider.CheckAsync(CancellationToken.None));
        _ssh.Result = new SshResult(0, ListAndStats, string.Empty, TimeSpan.Zero);
        await provider.ListAsync(CancellationToken.None);

        Assert.Equal(["run build-01.example.com AcceptNew", "run build-01.example.com Strict"], _ssh.Calls);
    }

    [Fact]
    public async Task Widget_AddsUpLocalAndRemoteContainers()
    {
        var remote = new FakeContainerProvider();
        _local.Containers.Add(new ContainerInfo("l1", "postgres", "postgres:17", "running", "Up"));
        _local.Containers.Add(new ContainerInfo("l2", "redis", "redis:7", "exited", "Exited (0)"));
        remote.Containers.Add(new ContainerInfo("r1", "api", "shop/api", "running", "Up"));
        remote.Containers.Add(new ContainerInfo("r2", "worker", "shop/worker", "restarting", "Restarting (1) 5 seconds ago"));
        var viewModel = CreateWidget(remote, new DockerHost { Name = "build-01", Address = "build-01.example.com" });

        viewModel.Start();
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(["Default distribution", "build-01"], viewModel.Sources.Select(source => source.Label));
        Assert.All(viewModel.Sources, source => Assert.True(source.ShowHeading));
        Assert.Equal(new WidgetStatus("Docker", "2/4", WidgetSeverity.Warning), viewModel.Status);
        Assert.Equal("2 of 4 running", viewModel.Summary);
        Assert.Equal("2 sources", viewModel.SourceLabel);

        // A restart loop on another machine says where it happens.
        var alert = Assert.Single(viewModel.Alerts);
        Assert.Equal("worker keeps restarting", alert.Title);
        Assert.Equal("Restarting (1) 5 seconds ago · build-01", alert.Detail);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task Widget_KeepsShowingTheOthersWhenOneHostFails()
    {
        var remote = new FakeContainerProvider
        {
            ListFailure = new ContainerProviderException(ContainerFailureKind.HostUnreachable, "ssh build-01 docker ps -a", 255, "Connection refused"),
        };
        _local.Containers.Add(new ContainerInfo("l1", "postgres", "postgres:17", "running", "Up"));
        var viewModel = CreateWidget(remote, new DockerHost { Name = "build-01", Address = "build-01.example.com" });

        viewModel.Start();
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(new WidgetStatus("Docker", "1/1", WidgetSeverity.Error), viewModel.Status);
        var alert = Assert.Single(viewModel.Alerts);
        Assert.Equal("build-01 not reachable", alert.Title);
        Assert.Null(viewModel.Sources[0].Failure);
        Assert.Single(viewModel.Sources[0].Containers);
        Assert.NotNull(viewModel.Sources[1].Failure);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task RemoteHost_IsReadOnlyUnlessActionsWereAllowed()
    {
        var readOnlyProvider = new FakeContainerProvider();
        var readOnly = CreateWidget(readOnlyProvider, new DockerHost { Name = "prod", Address = "prod.example.com" });
        readOnly.Start();
        var row = new ContainerRowViewModel(new ContainerInfo("abc", "web", "nginx", "running", "Up"));

        var source = readOnly.Sources[1];
        Assert.False(source.CanControl);
        Assert.False(source.StopContainerCommand.CanExecute(row));
        Assert.False(source.RestartContainerCommand.CanExecute(row));
        Assert.True(readOnly.Sources[0].CanControl);
        await readOnly.StopAsync();

        var allowedProvider = new FakeContainerProvider();
        var allowed = CreateWidget(allowedProvider, new DockerHost { Name = "test", Address = "test.example.com", AllowActions = true });
        allowed.Start();
        await allowed.Sources[1].RestartContainerCommand.ExecuteAsync(row);

        Assert.Equal(["restart abc"], allowedProvider.Actions);
        Assert.Empty(_local.Actions);
        await allowed.StopAsync();
    }

    [Fact]
    public async Task Widget_ShowsOneLogAtATimeAcrossSources()
    {
        var viewModel = CreateWidget(new FakeContainerProvider(), new DockerHost { Name = "build-01", Address = "build-01.example.com" });
        viewModel.Start();
        var local = new ContainerRowViewModel(new ContainerInfo("l1", "postgres", "postgres:17", "running", "Up"));
        var remote = new ContainerRowViewModel(new ContainerInfo("r1", "api", "shop/api", "running", "Up"));

        viewModel.Sources[0].ShowLogsCommand.Execute(local);
        Assert.Equal("postgres", viewModel.LogPanel?.ContainerName);

        viewModel.Sources[1].ShowLogsCommand.Execute(remote);
        Assert.Equal("api", viewModel.LogPanel?.ContainerName);
        Assert.Equal("ssh build-01.example.com docker logs -f --tail 200 api", viewModel.LogPanel?.CommandText);
        Assert.Null(viewModel.Sources[0].LogPanel);

        viewModel.LogPanel?.CloseCommand.Execute(null);
        Assert.Null(viewModel.LogPanel);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task FoldedSection_IsRememberedAndKeepsReporting()
    {
        var remote = new FakeContainerProvider();
        remote.Containers.Add(new ContainerInfo("r1", "worker", "shop/worker", "restarting", "Restarting (1) 5 seconds ago"));
        var host = new DockerHost { Name = "build-01", Address = "build-01.example.com" };
        var viewModel = CreateWidget(remote, host);
        viewModel.Start();
        var source = viewModel.Sources[1];
        Assert.True(source.IsExpanded);
        Assert.True(source.IsContentVisible);

        source.ToggleExpandedCommand.Execute(null);

        Assert.False(source.IsContentVisible);
        Assert.Equal([host.Id], _settingsStore.Load<DockerWidgetSettings>("docker").CollapsedSources);

        // Out of sight is not out of mind: the status bar still learns about the restart loop.
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.Single(viewModel.Alerts);
        Assert.Equal(WidgetSeverity.Warning, source.Severity);
        await viewModel.StopAsync();

        var restarted = new DockerWidgetViewModel(_local, _overlayState, _overlayCommands, new DockerSettingsSource(_settingsStore), TimeProvider.System, _ => remote);
        restarted.Start();
        Assert.True(restarted.Sources[0].IsExpanded);
        Assert.False(restarted.Sources[1].IsExpanded);

        restarted.Sources[1].ToggleExpandedCommand.Execute(null);
        Assert.Empty(_settingsStore.Load<DockerWidgetSettings>("docker").CollapsedSources);
        await restarted.StopAsync();
    }

    [Fact]
    public async Task LoneSource_IsAlwaysShownEvenIfItWasFoldedBefore()
    {
        _settingsStore.Save("docker", new DockerWidgetSettings { CollapsedSources = [ContainerSourceOptions.LocalId] });
        var viewModel = new DockerWidgetViewModel(_local, _overlayState, _overlayCommands, new DockerSettingsSource(_settingsStore), TimeProvider.System);

        viewModel.Start();

        // Without a heading there would be no way to unfold it again.
        Assert.False(viewModel.Sources[0].IsExpanded);
        Assert.False(viewModel.Sources[0].ShowHeading);
        Assert.True(viewModel.Sources[0].IsContentVisible);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task Widget_FollowsTheHostsInTheSettings()
    {
        var settings = new DockerSettingsSource(_settingsStore);
        var created = new List<string>();
        var viewModel = new DockerWidgetViewModel(_local, _overlayState, _overlayCommands, settings, TimeProvider.System, host =>
        {
            created.Add(host.Name);
            return new FakeContainerProvider();
        });
        viewModel.Start();
        Assert.Single(viewModel.Sources);
        Assert.False(viewModel.Sources[0].ShowHeading);
        var local = viewModel.Sources[0];

        settings.Update(new DockerWidgetSettings { RemoteHosts = [new DockerHost { Name = "a", Address = "a.example.com" }, new DockerHost { Name = "b", Address = "b.example.com" }] });
        await WaitUntilAsync(() => viewModel.Sources.Count == 3);
        Assert.Same(local, viewModel.Sources[0]);
        Assert.Equal(["a", "b"], created);

        settings.Update(new DockerWidgetSettings());
        await WaitUntilAsync(() => viewModel.Sources.Count == 1);
        Assert.False(viewModel.Sources[0].ShowHeading);
        await viewModel.StopAsync();
    }

    private DockerWidgetViewModel CreateWidget(FakeContainerProvider remote, DockerHost host)
    {
        _settingsStore.Save("docker", new DockerWidgetSettings { RemoteHosts = [host] });
        return new DockerWidgetViewModel(_local, _overlayState, _overlayCommands, new DockerSettingsSource(_settingsStore), TimeProvider.System, _ => remote);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
