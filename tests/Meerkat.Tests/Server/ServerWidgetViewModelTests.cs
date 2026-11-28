using Meerkat.Abstractions;
using Meerkat.Tests.Docker;
using Meerkat.Widgets.Server;
using Meerkat.Widgets.Server.Monitoring;
using Meerkat.Widgets.Server.Settings;
using Meerkat.Ssh;
using Meerkat.Widgets.Server.ViewModels;

namespace Meerkat.Tests.Server;

public class ServerWidgetViewModelTests
{
    private readonly FakeServerProbe _probe = new();
    private readonly InMemoryWidgetSettingsStore _settingsStore = new();
    private readonly FakeOverlayState _overlayState = new();
    private readonly FakeOverlayCommands _overlayCommands = new();

    [Fact]
    public async Task WithoutHosts_ReportsNothingAndInvitesToAddSome()
    {
        var viewModel = CreateViewModel();

        viewModel.Start();
        await viewModel.StopAsync();

        Assert.True(viewModel.IsEmpty);
        Assert.Equal("0 hosts", viewModel.Summary);
        Assert.Null(viewModel.Status);
        Assert.Empty(viewModel.Alerts);
    }

    [Fact]
    public async Task HealthyHost_IsSummarizedInOneLine()
    {
        var viewModel = CreateViewModel(Host("build-01"));
        viewModel.Start();
        var host = viewModel.Hosts[0];
        _probe.Enqueue("build-01", FakeServerProbe.Snapshot(cpuTotal: 1000, cpuIdle: 900));
        _probe.Enqueue("build-01", FakeServerProbe.Snapshot(cpuTotal: 2000, cpuIdle: 1560));

        await viewModel.CheckAsync(host, CancellationToken.None);
        Assert.Equal("–", host.CpuText);
        await viewModel.CheckAsync(host, CancellationToken.None);
        await viewModel.StopAsync();

        Assert.Equal(HostState.Online, host.State);
        Assert.Equal(WidgetSeverity.Ok, host.Severity);
        Assert.False(host.IsExpanded);
        Assert.Equal("34 %", host.CpuText);
        Assert.Equal([34d], host.CpuHistory);
        Assert.Equal("Ubuntu 24.04.5 LTS · up 41 days", host.OsText);
        Assert.Equal("8 cores", host.CoresText);
        Assert.Equal("2.14 · 1.87 · 1.42", host.LoadText.Replace(',', '.'));
        Assert.Equal("18.6 / 32 GiB", host.RamText.Replace(',', '.'));
        Assert.Equal("210 / 500 GB · 42 %", host.DiskText);
        Assert.Equal(["41 days", "Load 2.14", "RAM 58 %", "Disk 42 %"], new[] { host.CompactUptime, host.CompactLoad.Replace(',', '.'), host.CompactRam, host.CompactDisk });
        Assert.StartsWith("SSH agent · response 23 ms · checked ", host.FooterText);

        Assert.Equal(new WidgetStatus("Server", "1/1", WidgetSeverity.Ok), viewModel.Status);
        Assert.Empty(viewModel.Alerts);

        // Polling must never accept a host key on its own.
        Assert.All(_probe.Calls, call => Assert.EndsWith("Strict", call));
    }

    [Fact]
    public async Task FullDiskAndStoppedService_AreHintsAndOpenTheHost()
    {
        var viewModel = CreateViewModel(Host("build-01", "docker", "node-exporter"));
        viewModel.Start();
        var host = viewModel.Hosts[0];
        _probe.Enqueue("build-01", FakeServerProbe.Snapshot(91, 1000, 900, new ServiceState("docker", "active"), new ServiceState("node-exporter", "inactive")));

        await viewModel.CheckAsync(host, CancellationToken.None);
        await viewModel.StopAsync();

        Assert.Equal(WidgetSeverity.Warning, host.Severity);
        Assert.Equal(WidgetSeverity.Warning, host.DiskSeverity);
        Assert.True(host.IsExpanded);
        Assert.Equal([true, false], host.Services.Select(service => service.IsActive));
        Assert.Equal(new WidgetStatus("Server", "1/1", WidgetSeverity.Warning), viewModel.Status);
        Assert.Equal(
            ["build-01: disk 91 % full", "build-01: node-exporter is inactive"],
            viewModel.Alerts.Select(alert => alert.Title));
        Assert.All(viewModel.Alerts, alert => Assert.Equal(WidgetSeverity.Warning, alert.Severity));
    }

    [Fact]
    public async Task DiskAboveTheRedThreshold_IsAnError()
    {
        var viewModel = CreateViewModel(Host("build-01"));
        viewModel.Start();
        _probe.Enqueue("build-01", FakeServerProbe.Snapshot(diskUsedPercent: 97));

        await viewModel.CheckAsync(viewModel.Hosts[0], CancellationToken.None);
        await viewModel.StopAsync();

        Assert.Equal(WidgetSeverity.Error, viewModel.Hosts[0].DiskSeverity);
        Assert.Equal(WidgetSeverity.Error, viewModel.Status?.Severity);
    }

    [Fact]
    public async Task UnreachableHost_IsAnErrorThatKeepsItsStartTimeAndMovesToTheTop()
    {
        var viewModel = CreateViewModel(Host("build-01"), Host("staging-db"));
        viewModel.Start();
        var staging = viewModel.Hosts.Single(host => host.Name == "staging-db");
        var build = viewModel.Hosts.Single(host => host.Name == "build-01");
        _probe.Enqueue("staging-db", FakeServerProbe.Snapshot());
        _probe.Enqueue("staging-db", new ProbeException(ProbeFailureKind.Unreachable, "SSH timeout"));
        _probe.Enqueue("staging-db", new ProbeException(ProbeFailureKind.Unreachable, "SSH timeout"));

        await viewModel.CheckAsync(build, CancellationToken.None);
        await viewModel.CheckAsync(staging, CancellationToken.None);
        await viewModel.CheckAsync(staging, CancellationToken.None);
        var firstSeen = Assert.Single(viewModel.Alerts).Since;
        await viewModel.RecheckCommand.ExecuteAsync(staging);
        await viewModel.StopAsync();

        Assert.Equal(HostState.Unreachable, staging.State);
        Assert.True(staging.IsConnectionFailure);
        Assert.StartsWith("Not reachable · SSH timeout for ", staging.FailureText);
        Assert.Contains(" · last contact ", staging.FailureText);
        Assert.False(staging.IsBusy);

        Assert.Equal(new WidgetStatus("Server", "1/2", WidgetSeverity.Error), viewModel.Status);
        var alert = Assert.Single(viewModel.Alerts);
        Assert.Equal((WidgetSeverity.Error, "staging-db is not reachable", "SSH timeout"), (alert.Severity, alert.Title, alert.Detail));
        Assert.Equal(firstSeen, alert.Since);
        Assert.Equal(["staging-db", "build-01"], viewModel.Hosts.Select(host => host.Name));
    }

    [Fact]
    public async Task ChangedHostKey_BlocksTheHostUntilTheUserTrustsTheNewKey()
    {
        var viewModel = CreateViewModel(Host("raspi-home"));
        viewModel.Start();
        var host = viewModel.Hosts[0];
        _probe.Enqueue("raspi-home", new ProbeException(ProbeFailureKind.HostKeyChanged, "The host key has changed.", "SHA256:abc"));

        await viewModel.CheckAsync(host, CancellationToken.None);

        Assert.Equal(HostState.HostKeyChanged, host.State);
        Assert.True(host.IsHostKeyProblem);
        Assert.Equal("SHA256:abc", host.Fingerprint);
        Assert.Equal("raspi-home: host key has changed", host.FailureTitle);
        Assert.Equal("raspi-home: host key has changed", Assert.Single(viewModel.Alerts).Title);

        await viewModel.TrustHostKeyCommand.ExecuteAsync(host);
        await viewModel.StopAsync();

        Assert.Equal(["probe raspi-home Strict", "trust raspi-home"], _probe.Calls);
        Assert.Equal(HostState.Online, host.State);
        Assert.Null(host.Fingerprint);
        Assert.Empty(viewModel.Alerts);
    }

    [Fact]
    public async Task PausedHost_IsNeitherCheckedNorReported()
    {
        var viewModel = CreateViewModel(Host("raspi-home"));
        viewModel.Start();
        var host = viewModel.Hosts[0];
        _probe.Enqueue("raspi-home", new ProbeException(ProbeFailureKind.Unreachable, "SSH timeout"));
        await viewModel.CheckAsync(host, CancellationToken.None);

        viewModel.PauseHostCommand.Execute(host);
        _probe.Calls.Clear();
        await viewModel.CheckAsync(host, CancellationToken.None);
        await viewModel.StopAsync();

        Assert.Equal(HostState.Paused, host.State);
        Assert.Empty(_probe.Calls);
        Assert.Null(viewModel.Status);
        Assert.Empty(viewModel.Alerts);
        Assert.True(_settingsStore.Load<ServerWidgetSettings>(ServerWidget.WidgetId).Hosts[0].IsPaused);
    }

    [Fact]
    public void Commands_OpenATerminalAndTheSettings()
    {
        var viewModel = CreateViewModel(Host("build-01"));
        viewModel.Start();

        viewModel.OpenTerminalCommand.Execute(viewModel.Hosts[0]);
        viewModel.OpenSettingsCommand.Execute(null);
        viewModel.ToggleExpandedCommand.Execute(viewModel.Hosts[0]);

        Assert.Contains("terminal build-01", _probe.Calls);
        Assert.Equal(1, _overlayCommands.OpenSettingsCalls);
        Assert.True(viewModel.Hosts[0].IsExpanded);
    }

    private ServerWidgetViewModel CreateViewModel(params ServerHost[] hosts)
    {
        _settingsStore.Save(ServerWidget.WidgetId, new ServerWidgetSettings { Hosts = [.. hosts] });
        return new ServerWidgetViewModel(_probe, new ServerSettingsSource(_settingsStore), _overlayState, _overlayCommands, TimeProvider.System);
    }

    private static ServerHost Host(string name, params string[] services) =>
        new() { Name = name, Address = name, Services = [.. services] };
}
