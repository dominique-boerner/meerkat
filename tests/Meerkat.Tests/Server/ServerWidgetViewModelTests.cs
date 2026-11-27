using Meerkat.Abstractions;
using Meerkat.Tests.Docker;
using Meerkat.Widgets.Server;
using Meerkat.Widgets.Server.Monitoring;
using Meerkat.Widgets.Server.Settings;
using Meerkat.Widgets.Server.Ssh;
using Meerkat.Widgets.Server.ViewModels;

namespace Meerkat.Tests.Server;

public class ServerWidgetViewModelTests
{
    private readonly FakeServerProbe _probe = new();
    private readonly InMemoryWidgetSettingsStore _settingsStore = new();
    private readonly FakeOverlayState _overlayState = new();
    private readonly FakeOverlayCommands _overlayCommands = new();
    private readonly HashSet<string> _storedPasswords = [];

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

    [Fact]
    public async Task SettingsPage_AddsAHostAfterASuccessfulCheck()
    {
        var page = CreatePage(new ServerWidgetSettings(), "dev-vm", "nas");
        _probe.Enqueue("build-01.example.com", FakeServerProbe.Snapshot(42, 1000, 900, new ServiceState("docker", "active"), new ServiceState("cron", "inactive")));

        page.AddHostCommand.Execute(null);
        page.Hostname = "build-01.example.com";
        page.User = "deploy";
        page.Port = "2222";
        page.ServicesText = "docker, cron";
        page.SelectedInterval = page.HostIntervals.Single(option => option.Seconds == 300);
        await page.ConnectAndCheckCommand.ExecuteAsync(null);

        Assert.False(page.IsFormOpen);
        Assert.Equal("build-01.example.com: connected (Ubuntu 24.04.5 LTS). Not active: cron.", page.LastCheckResult);

        // Adding a host is the moment its key is trusted for the first time.
        Assert.Equal(["probe build-01.example.com AcceptNew"], _probe.Calls);

        var row = Assert.Single(page.Hosts);
        Assert.Equal(("build-01.example.com", "every 5 min", "docker, cron"), (row.Name, row.IntervalText, row.ServicesText));
        var saved = Assert.Single(page.ToSettings().Hosts);
        Assert.Equal(("deploy", 2222, 300d), (saved.User, saved.Port, saved.IntervalSeconds));
        Assert.Equal(["docker", "cron"], saved.Services);
    }

    [Fact]
    public async Task SettingsPage_OffersToSaveAHostThatCannotBeReachedRightNow()
    {
        var page = CreatePage(new ServerWidgetSettings());
        _probe.Enqueue("nas", new ProbeException(ProbeFailureKind.Unreachable, "SSH timeout"));

        page.AddHostCommand.Execute(null);
        page.Hostname = "nas";
        await page.ConnectAndCheckCommand.ExecuteAsync(null);

        Assert.True(page.IsFormOpen);
        Assert.True(page.IsFormError);
        Assert.Equal("SSH timeout", page.FormStatus);
        Assert.True(page.CanSaveUnchecked);
        Assert.Empty(page.Hosts);

        page.SaveUncheckedCommand.Execute(null);

        Assert.False(page.IsFormOpen);
        Assert.Equal("nas", Assert.Single(page.Hosts).Name);
    }

    [Theory]
    [InlineData("-oProxyCommand=calc", "22", "docker", 85, 95, "not a valid host name")]
    [InlineData("nas", "ssh", "docker", 85, 95, "port must be a number")]
    [InlineData("nas", "", "docker,$(reboot)", 85, 95, "not a valid systemd unit name")]
    [InlineData("nas", "", "docker", 96, 95, "yellow disk threshold")]
    public async Task SettingsPage_RejectsInvalidEntriesBeforeConnecting(string hostname, string port, string services, double yellow, double red, string expectedError)
    {
        var page = CreatePage(new ServerWidgetSettings());
        page.AddHostCommand.Execute(null);
        page.Hostname = hostname;
        page.Port = port;
        page.ServicesText = services;
        page.DiskWarningPercent = yellow;
        page.DiskErrorPercent = red;

        await page.ConnectAndCheckCommand.ExecuteAsync(null);

        Assert.True(page.IsFormError);
        Assert.Contains(expectedError, page.FormStatus);
        Assert.Empty(_probe.Calls);
        Assert.Empty(page.Hosts);
    }

    [Fact]
    public void SettingsPage_EditsRemovesAndPrefillsFromTheSshConfig()
    {
        var settings = new ServerWidgetSettings { DefaultIntervalSeconds = 60, Hosts = [Host("build-01", "docker"), Host("nas")] };
        var page = CreatePage(settings, "dev-vm");

        Assert.Equal("every 1 min", page.DefaultInterval.DisplayName);
        Assert.True(page.HasConfigHosts);

        page.EditHostCommand.Execute(page.Hosts[0]);
        Assert.True(page.IsEditing);
        Assert.Equal(("Edit host", "build-01", "docker"), (page.FormTitle, page.Hostname, page.ServicesText));
        page.IsPaused = true;
        page.SaveUncheckedCommand.Execute(null);
        Assert.Equal("paused", page.Hosts[0].IntervalText);
        Assert.Equal(settings.Hosts[0].Id, page.Hosts[0].Host.Id);

        page.EditHostCommand.Execute(page.Hosts[1]);
        page.RemoveHostCommand.Execute(null);
        Assert.Equal(["build-01"], page.Hosts.Select(row => row.Name));

        page.AddHostCommand.Execute(null);
        page.User = "someone";
        page.SelectedConfigHost = page.ConfigHosts[0];
        Assert.Equal(("dev-vm", "dev-vm", string.Empty), (page.Name, page.Hostname, page.User));

        // Editing works on copies, so cancelling the settings window leaves the stored hosts untouched.
        Assert.False(settings.Hosts[0].IsPaused);
        Assert.Equal(2, settings.Hosts.Count);
    }

    [Fact]
    public void SettingsPage_NewHostStartsWithExactlyOneSignInMethodSelected()
    {
        var page = CreatePage(new ServerWidgetSettings());

        page.AddHostCommand.Execute(null);
        Assert.Equal([true, false, false], new[] { page.UsesAgent, page.UsesKeyFile, page.UsesPassword });

        // A radio button that loses the selection reports "false"; that must not change the choice.
        page.UsesKeyFile = true;
        page.UsesAgent = false;
        Assert.Equal([false, true, false], new[] { page.UsesAgent, page.UsesKeyFile, page.UsesPassword });

        page.UsesPassword = true;
        page.UsesKeyFile = false;
        Assert.Equal(SshAuthentication.Password, page.Authentication);
    }

    [Fact]
    public async Task SettingsPage_ChecksWithTheEnteredPasswordAndKeepsItOutOfTheSettings()
    {
        var page = CreatePage(new ServerWidgetSettings());

        page.AddHostCommand.Execute(null);
        page.Hostname = "nas";
        page.UsesPassword = true;
        await page.ConnectAndCheckCommand.ExecuteAsync(null);
        Assert.Equal("Enter the password.", page.FormStatus);

        page.Password = "s3cret";
        await page.ConnectAndCheckCommand.ExecuteAsync(null);
        Assert.Equal("Enter the user name that belongs to the password.", page.FormStatus);
        Assert.Empty(_probe.Calls);

        page.User = "admin";
        await page.ConnectAndCheckCommand.ExecuteAsync(null);

        Assert.False(page.IsFormOpen);
        Assert.Equal(["s3cret"], _probe.Passwords);
        var host = Assert.Single(page.ToSettings().Hosts);
        Assert.Equal(SshAuthentication.Password, host.Authentication);
        Assert.Equal("s3cret", page.PendingPasswords[host.Id]);
        Assert.Equal(string.Empty, page.Password);
        Assert.DoesNotContain("s3cret", System.Text.Json.JsonSerializer.Serialize(page.ToSettings()));
    }

    [Fact]
    public async Task SettingsPage_KeepsAStoredPasswordUnlessANewOneIsEntered()
    {
        var host = new ServerHost { Name = "nas", Address = "nas", User = "admin", Authentication = SshAuthentication.Password };
        _storedPasswords.Add(host.Id);
        var page = CreatePage(new ServerWidgetSettings { Hosts = [host] });

        page.EditHostCommand.Execute(page.Hosts[0]);
        Assert.True(page.HasStoredPassword);
        Assert.True(page.ShowStoredPasswordPlaceholder);

        await page.ConnectAndCheckCommand.ExecuteAsync(null);

        // Nothing was typed, so the probe falls back to the stored password and nothing new is written.
        Assert.Equal([null], _probe.Passwords);
        Assert.Empty(page.PendingPasswords);
    }

    private ServerSettingsPageViewModel CreatePage(ServerWidgetSettings settings, params string[] configAliases) =>
        new(settings, _probe, configAliases, host => _storedPasswords.Contains(host.Id));

    private ServerWidgetViewModel CreateViewModel(params ServerHost[] hosts)
    {
        _settingsStore.Save(ServerWidget.WidgetId, new ServerWidgetSettings { Hosts = [.. hosts] });
        return new ServerWidgetViewModel(_probe, new ServerSettingsSource(_settingsStore), _overlayState, _overlayCommands, TimeProvider.System);
    }

    private static ServerHost Host(string name, params string[] services) =>
        new() { Name = name, Address = name, Services = [.. services] };
}
