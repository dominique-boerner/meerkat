using System.IO;
using Meerkat.App.Settings;
using Meerkat.App.SettingsUi;
using Meerkat.Ssh;
using Meerkat.Tests.Docker;
using Meerkat.Tests.Git;
using Meerkat.Tests.Server;
using Meerkat.Widgets.Docker.Settings;
using Meerkat.Widgets.Server.Settings;
using DockerRefreshRate = Meerkat.Widgets.Docker.Settings.RefreshRate;
using ServerRefreshRate = Meerkat.Widgets.Server.Settings.RefreshRate;

namespace Meerkat.Tests.App;

/// <summary>Machines are entered once and used by the widgets that reach other machines over SSH.</summary>
public sealed class SharedMachinesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Meerkat.Tests", Guid.NewGuid().ToString("N"));
    private readonly InMemoryWidgetSettingsStore _store = new();
    private readonly InMemorySecretStore _secrets = new();
    private readonly FakeSshClient _ssh = new();
    private readonly MachineRegistry _registry;

    public SharedMachinesTests() => _registry = new MachineRegistry(_store, _secrets);

    private string FilePath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    // ---- The list ----

    [Fact]
    public void AMachine_IsStoredWithItsPasswordInTheSecretStore()
    {
        var changes = 0;
        _registry.Changed += (_, _) => changes++;
        var machine = new SshMachine { Name = "Production", Address = "prod.example.org", User = "deploy", Port = 2222, Authentication = SshAuthentication.Password };

        _registry.Save(machine, "s3cret");

        Assert.Same(machine, _registry.Find(machine.Id));
        Assert.Equal("deploy@prod.example.org:2222", machine.Destination);
        Assert.Equal("s3cret", _secrets.Read($"machine/{machine.Id}"));
        Assert.Equal("s3cret", _registry.GetTarget(machine).Password);
        Assert.Equal(1, changes);

        // Saving again without a new password keeps the stored one; another way to sign in lets go of it.
        _registry.Save(machine);
        Assert.True(_registry.HasPassword(machine));
        machine.Authentication = SshAuthentication.Agent;
        _registry.Save(machine);
        Assert.False(_registry.HasPassword(machine));

        _registry.Remove(machine.Id);
        Assert.Empty(_registry.Machines);
    }

    [Fact]
    public void MachinesOfTwoWidgets_GrowTogetherWhenTheyConnectTheSameWay()
    {
        var fromDocker = new SshMachine { Name = "build", Address = "build.example.org", User = "ci", Authentication = SshAuthentication.Password };
        var fromServer = new SshMachine { Name = "Build server", Address = "BUILD.example.org", User = "ci", Authentication = SshAuthentication.Password };
        var other = new SshMachine { Name = "prod", Address = "prod.example.org" };
        _secrets.Write("docker/old", "pw");

        var first = _registry.Adopt(fromDocker, "docker/old");
        var second = _registry.Adopt(fromServer, "server/old");
        var third = _registry.Adopt(other, "server/other");

        Assert.Equal(first, second);
        Assert.NotEqual(first, third);
        Assert.Equal(["build", "prod"], _registry.Machines.Select(machine => machine.Name));
        Assert.Equal("pw", _secrets.Read($"machine/{first}"));
        Assert.Null(_secrets.Read("docker/old"));
    }

    // ---- Taking over what the widgets kept for themselves ----

    [Fact]
    public void HostsOfEarlierVersions_MoveIntoTheSharedListOnce()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """
            {
              "WidgetSettings": {
                "docker": {
                  "Distribution": "Ubuntu",
                  "ListIntervalSeconds": 5,
                  "RemoteHosts": [
                    { "Id": "d1", "Name": "build", "Address": "build.example.org", "User": "ci", "Authentication": "Password", "IntervalSeconds": 20, "AllowActions": true, "ScanImages": true }
                  ],
                  "CollapsedSources": ["d1"]
                },
                "server": {
                  "DefaultIntervalSeconds": 30,
                  "Hosts": [
                    { "Id": "s1", "Name": "Build server", "Address": "build.example.org", "User": "ci", "Authentication": "Password", "Services": ["docker"], "DiskWarningPercent": 70, "DiskErrorPercent": 90, "IsPaused": true },
                    { "Id": "s2", "Name": "prod", "Address": "prod.example.org", "Authentication": "Agent", "Services": [] }
                  ]
                }
              }
            }
            """);
        _secrets.Write("docker/d1", "docker-password");
        _secrets.Write("server/s1", "server-password");
        var service = new SettingsService(FilePath);
        var registry = new MachineRegistry(service, _secrets);

        var docker = new DockerSettingsSource(service, registry).Current;
        var server = new ServerSettingsSource(service, registry).Current;

        // The machine both widgets knew is one machine now, under the id the first widget brought along.
        Assert.Equal(["d1", "s2"], registry.Machines.Select(machine => machine.Id));
        Assert.Equal("docker-password", _secrets.Read("machine/d1"));
        Assert.Null(_secrets.Read("docker/d1"));
        Assert.Null(_secrets.Read("server/s1"));

        var host = Assert.Single(docker.RemoteHosts);
        Assert.Equal(("d1", "build", "build.example.org", true, true), (host.Id, host.Name, host.Address, host.AllowActions, host.ScanImages));
        Assert.Equal(["d1"], docker.CollapsedSources);

        Assert.Equal(["d1", "s2"], server.Hosts.Select(watched => watched.Id));
        Assert.Equal((70d, 90d, true), (server.Hosts[0].DiskWarningPercent, server.Hosts[0].DiskErrorPercent, server.Hosts[0].IsPaused));
        Assert.Equal(["docker"], server.Hosts[0].Services);

        // The file holds the connection once and only says per widget what the machine is used for.
        var text = File.ReadAllText(FilePath);
        Assert.DoesNotContain("RemoteHosts", text);
        Assert.DoesNotContain("\"Hosts\"", text);
        Assert.Equal(2, text.Split("build.example.org").Length);

        // A second start finds nothing left to take over and comes to the same result.
        var again = new SettingsService(FilePath);
        var registryAgain = new MachineRegistry(again, _secrets);
        Assert.Equal(["d1"], new DockerSettingsSource(again, registryAgain).Current.RemoteHosts.Select(remote => remote.Id));
        Assert.Equal(2, new ServerSettingsSource(again, registryAgain).Current.Hosts.Count);
        Assert.Equal(2, registryAgain.Machines.Count);
    }

    [Fact]
    public void AChangedOrRemovedMachine_ReachesTheWidgetsThatUseIt()
    {
        var machine = new SshMachine { Name = "build", Address = "build.example.org" };
        _registry.Save(machine);
        _store.Save("docker", new DockerWidgetSettings { Machines = [new DockerMachineUse { MachineId = machine.Id }, new DockerMachineUse { MachineId = "gone" }] });
        var source = new DockerSettingsSource(_store, _registry);
        var changes = 0;
        source.Changed += (_, _) => changes++;
        Assert.Equal("build.example.org", Assert.Single(source.Current.RemoteHosts).Address);

        var moved = machine.Clone();
        moved.Address = "build-02.example.org";
        _registry.Save(moved);

        Assert.Equal("build-02.example.org", Assert.Single(source.Current.RemoteHosts).Address);
        Assert.Equal(1, changes);

        _registry.Remove(machine.Id);
        Assert.Empty(source.Current.RemoteHosts);
    }

    [Fact]
    public void SettingsChangedInTheFile_ReplaceWhatTheWidgetsLoaded()
    {
        var service = new SettingsService(FilePath);
        var source = new DockerSettingsSource(service, new MachineRegistry(service, _secrets));
        source.Update(new DockerWidgetSettings { Refresh = DockerRefreshRate.Fast });
        var changes = 0;
        source.Changed += (_, _) => changes++;

        File.WriteAllText(FilePath, File.ReadAllText(FilePath).Replace("\"Fast\"", "\"Economical\"").Replace("\"MaxLogLines\": 5000", "\"MaxLogLines\": 800"));
        Assert.True(service.ReloadIfChanged());

        Assert.Equal(1, changes);
        Assert.Equal(DockerRefreshRate.Economical, source.Current.Refresh);
        Assert.Equal(800, source.Current.EffectiveMaxLogLines);
    }

    // ---- Docker ----

    [Theory]
    [InlineData(DockerRefreshRate.Fast, 2, 2, 10)]
    [InlineData(DockerRefreshRate.Normal, 5, 3, 20)]
    [InlineData(DockerRefreshRate.Economical, 15, 10, 60)]
    public void TheRefreshRate_StandsForFixedIntervalsAndAsksOtherMachinesLessOften(DockerRefreshRate rate, double list, double stats, double remote)
    {
        var machine = new SshMachine { Address = "build.example.org" };
        _registry.Save(machine);
        var settings = new DockerWidgetSettings { Refresh = rate, Machines = [new DockerMachineUse { MachineId = machine.Id }] };
        var source = new DockerSettingsSource(_store, _registry);

        source.Update(settings);

        Assert.Equal(TimeSpan.FromSeconds(list), settings.ListInterval);
        Assert.Equal(TimeSpan.FromSeconds(stats), settings.StatsInterval);
        Assert.Equal(TimeSpan.FromSeconds(remote), DockerWidgetSettings.GetInterval(settings.RemoteHosts.Single()));
    }

    [Fact]
    public void ExactIntervals_CanStillBeSetInTheSettingsFile()
    {
        var machine = new SshMachine { Address = "build.example.org" };
        _registry.Save(machine);
        var settings = new DockerWidgetSettings
        {
            CustomListIntervalSeconds = 8,
            CustomStatsIntervalSeconds = 4,
            CustomRemoteIntervalSeconds = 45,
            Machines = [new DockerMachineUse { MachineId = machine.Id }],
        };
        new DockerSettingsSource(_store, _registry).Update(settings);

        Assert.Equal(TimeSpan.FromSeconds(8), settings.ListInterval);
        Assert.Equal(TimeSpan.FromSeconds(4), settings.StatsInterval);
        Assert.Equal(TimeSpan.FromSeconds(45), DockerWidgetSettings.GetInterval(settings.RemoteHosts.Single()));

        // What the page does not show survives a change made on the page.
        var page = new DockerSettingsPageViewModel(settings, _registry.Machines) { IsEconomical = true };
        var stored = page.ToSettings();
        Assert.Equal((8d, 4d, 45d, DockerRefreshRate.Economical), (stored.CustomListIntervalSeconds, stored.CustomStatsIntervalSeconds, stored.CustomRemoteIntervalSeconds, stored.Refresh));
    }

    [Fact]
    public void TheDockerPage_ChoosesWhatToDoWithEachMachine()
    {
        var build = new SshMachine { Name = "build", Address = "build.example.org", User = "ci" };
        var prod = new SshMachine { Name = "prod", Address = "prod.example.org" };
        _registry.Save(build);
        _registry.Save(prod);
        var settings = new DockerWidgetSettings { Machines = [new DockerMachineUse { MachineId = build.Id, AllowActions = true, IntervalSeconds = 45 }] };
        var page = new DockerSettingsPageViewModel(settings, _registry.Machines);
        var changes = 0;
        page.Changed += (_, _) => changes++;

        // The page lists what the widget uses; the rest of the shared list is offered for adding.
        var used = Assert.Single(page.Machines);
        Assert.Equal(("build", "ci@build.example.org", "start/stop allowed"), (used.Name, used.Destination, used.AccessText));
        Assert.Equal([prod], page.AvailableMachines);
        Assert.True(page.CanAddMachine);

        page.IsPickerOpen = true;
        page.AddMachineCommand.Execute(prod);

        Assert.False(page.IsPickerOpen);
        Assert.Equal(["build", "prod"], page.Machines.Select(option => option.Name));
        Assert.True(page.Machines[1].IsOpen);
        Assert.Equal("read-only", page.Machines[1].AccessText);
        Assert.False(page.CanAddMachine);
        Assert.Equal("Every machine is listed already. Enter more on the Machines page.", page.AddMachineHint);

        page.Machines[1].ScanImages = true;
        page.Machines[1].IsOpen = false;
        page.RemoveMachineCommand.Execute(used);

        // Adding, changing and removing are stored; folding the options up is not a change.
        Assert.Equal(3, changes);
        var use = Assert.Single(page.ToSettings().Machines);
        Assert.Equal((prod.Id, false, true), (use.MachineId, use.AllowActions, use.ScanImages));
        Assert.Equal("read-only · scanned", page.Machines[0].AccessText);
        Assert.Equal([build], page.AvailableMachines);

        // A machine entered meanwhile is offered, one that was removed from the list is gone here too.
        var staging = new SshMachine { Name = "staging", Address = "staging.example.org" };
        _registry.Save(staging);
        _registry.Remove(build.Id);
        page.SetMachines(_registry.Machines);
        Assert.Equal(["prod"], page.Machines.Select(option => option.Name));
        Assert.True(page.Machines[0].ScanImages);
        Assert.Equal([staging], page.AvailableMachines);
    }

    [Fact]
    public async Task AMachineOnTheDockerPage_CanBeAskedForItsDocker()
    {
        var build = new SshMachine { Name = "build", Address = "build.example.org" };
        _registry.Save(build);
        var answers = new Queue<Func<Task<string>>>([
            () => Task.FromResult("27.3.1"),
            () => Task.FromException<string>(new Meerkat.Widgets.Docker.Containers.ContainerProviderException(
                Meerkat.Widgets.Docker.Containers.ContainerFailureKind.DockerNotInstalled, "ssh build docker version", 127, "docker: command not found")),
        ]);
        var page = new DockerSettingsPageViewModel(new DockerWidgetSettings(), _registry.Machines, checkDocker: _ => answers.Dequeue()());
        Assert.Equal("Enter a machine on the Machines page first.", new DockerSettingsPageViewModel(new DockerWidgetSettings()).AddMachineHint);
        page.AddMachineCommand.Execute(build);
        var option = page.Machines.Single();

        await option.CheckCommand.ExecuteAsync(null);
        Assert.Equal(("Connected. Docker 27.3.1.", false), (option.CheckResult, option.IsCheckFailed));

        await option.CheckCommand.ExecuteAsync(null);
        Assert.True(option.IsCheckFailed);
        Assert.StartsWith("Docker not found.", option.CheckResult);
    }

    // ---- Server ----

    [Fact]
    public void TheServerPage_ChoosesWhatToWatchOnEachMachine()
    {
        var build = new SshMachine { Name = "build", Address = "build.example.org" };
        _registry.Save(build);
        var source = new ServerSettingsSource(_store, _registry);
        var page = new ServerSettingsPageViewModel(source.Current, _registry.Machines);
        page.Changed += (_, _) => source.Update(page.ToSettings());
        Assert.Empty(page.Machines);
        Assert.Equal([build], page.AvailableMachines);

        page.AddMachineCommand.Execute(build);
        var option = Assert.Single(page.Machines);
        Assert.Equal("no services", option.Summary);
        option.ServicesText = "docker, nginx";
        page.IsFast = true;

        Assert.True(option.IsOpen);
        var host = Assert.Single(source.Current.Hosts);
        Assert.Equal((build.Id, "build.example.org"), (host.Id, host.Address));
        Assert.Equal(["docker", "nginx"], host.Services);
        Assert.Equal(TimeSpan.FromSeconds(15), source.Current.GetInterval(host));
        Assert.Equal(ServerRefreshRate.Fast, source.Current.Refresh);

        // Entries that make no sense are pointed out and not stored.
        option.DiskWarningPercent = 99;
        Assert.NotNull(option.Error);
        Assert.Equal(85, source.Current.Hosts.Single().DiskWarningPercent);

        option.DiskErrorPercent = 100;
        Assert.Null(option.Error);
        Assert.Equal(99, source.Current.Hosts.Single().DiskWarningPercent);

        page.RemoveMachineCommand.Execute(option);
        Assert.Empty(source.Current.Hosts);
        Assert.Equal([build], page.AvailableMachines);
    }

    [Fact]
    public void PausingAMachineOnTheServerPage_IsRememberedWithTheMachine()
    {
        var build = new SshMachine { Address = "build.example.org" };
        _registry.Save(build);
        _store.Save("server", new ServerWidgetSettings { Machines = [new ServerMachineUse { MachineId = build.Id }] });
        var source = new ServerSettingsSource(_store, _registry);

        source.Current.Hosts.Single().IsPaused = true;
        source.Save();

        Assert.True(new ServerSettingsSource(_store, _registry).Current.Hosts.Single().IsPaused);
    }

    // ---- The page on which machines are entered ----

    [Fact]
    public async Task AMachine_IsAddedOnceItAnswered()
    {
        var page = new MachinesPageViewModel(_registry, _ssh, []);
        page.AddMachineCommand.Execute(null);
        page.Name = "Production";
        page.Hostname = " prod.example.org ";
        page.User = "deploy";
        page.Port = "2222";
        Assert.Equal("Add machine", page.FormTitle);

        await page.ConnectAndCheckCommand.ExecuteAsync(null);

        Assert.False(page.IsFormOpen);
        var machine = Assert.Single(page.Machines);
        Assert.Equal(("Production", "prod.example.org", "deploy", 2222), (machine.Name, machine.Address, machine.User, machine.Port));

        // The first connection is the moment the user vouches for the machine, so its key may be stored then.
        Assert.Equal(["run prod.example.org AcceptNew"], _ssh.Calls);
    }

    [Fact]
    public async Task AMachineThatDoesNotAnswer_CanBeKeptAnyway()
    {
        _ssh.Result = new SshResult(255, string.Empty, "ssh: connect to host prod.example.org port 22: Connection refused", TimeSpan.Zero);
        var page = new MachinesPageViewModel(_registry, _ssh, []);
        page.AddMachineCommand.Execute(null);
        page.Hostname = "prod.example.org";

        await page.ConnectAndCheckCommand.ExecuteAsync(null);

        Assert.True(page.IsFormOpen);
        Assert.True(page.IsFormError);
        Assert.Equal("Connection refused", page.FormStatus);
        Assert.True(page.CanSaveUnchecked);
        Assert.Empty(_registry.Machines);

        page.SaveUncheckedCommand.Execute(null);
        Assert.Equal("prod.example.org", Assert.Single(_registry.Machines).Name);
    }

    [Fact]
    public async Task APassword_IsCheckedAsTypedAndKeptOutOfTheSettings()
    {
        var page = new MachinesPageViewModel(_registry, _ssh, []);
        page.AddMachineCommand.Execute(null);
        page.Hostname = "prod.example.org";
        page.User = "deploy";
        page.UsesPassword = true;

        await page.ConnectAndCheckCommand.ExecuteAsync(null);
        Assert.Equal("Enter the password.", page.FormStatus);
        Assert.Empty(_ssh.Calls);

        page.Password = "s3cret";
        await page.ConnectAndCheckCommand.ExecuteAsync(null);

        var machine = Assert.Single(_registry.Machines);
        Assert.Equal("s3cret", _ssh.Targets.Single().Password);
        Assert.Equal("s3cret", _secrets.Read(machine.SecretKey));
        Assert.Equal(string.Empty, page.Password);

        // Editing shows that a password is stored without ever reading it back, and keeps it unless replaced.
        page.EditMachineCommand.Execute(machine);
        Assert.True(page.ShowStoredPasswordPlaceholder);
        Assert.Equal("Edit prod.example.org", page.FormTitle);
        page.Name = "Production";
        await page.ConnectAndCheckCommand.ExecuteAsync(null);
        Assert.Equal("s3cret", _ssh.Targets[^1].Password);
        Assert.Equal("Production", _registry.Machines.Single().Name);
        Assert.Equal(machine.Id, _registry.Machines.Single().Id);
    }

    [Theory]
    [InlineData("prod.example.org", "22a", "The port must be a number.")]
    [InlineData("-oProxyCommand=calc", "", "'-oProxyCommand=calc' is not a valid host name.")]
    public async Task EntriesTheSshCommandLineCouldMisread_AreRefusedBeforeConnecting(string hostname, string port, string expected)
    {
        var page = new MachinesPageViewModel(_registry, _ssh, []);
        page.AddMachineCommand.Execute(null);
        page.Hostname = hostname;
        page.Port = port;

        await page.ConnectAndCheckCommand.ExecuteAsync(null);

        Assert.Equal(expected, page.FormStatus);
        Assert.Empty(_ssh.Calls);
        Assert.Empty(_registry.Machines);
    }

    [Fact]
    public void AnAliasFromTheSshConfig_FillsInTheForm_AndAMachineCanBeRemoved()
    {
        var machine = new SshMachine { Name = "old", Address = "old.example.org", Authentication = SshAuthentication.Password };
        _registry.Save(machine, "pw");
        var page = new MachinesPageViewModel(_registry, _ssh, ["build", "prod"]);
        Assert.True(page.HasConfigHosts);

        page.AddMachineCommand.Execute(null);
        page.User = "someone";
        page.SelectedConfigHost = page.ConfigHosts[1];
        Assert.Equal(("prod", "prod", string.Empty, true), (page.Name, page.Hostname, page.User, page.UsesAgent));

        page.EditMachineCommand.Execute(machine);
        page.RemoveMachineCommand.Execute(null);

        Assert.False(page.IsFormOpen);
        Assert.Empty(page.Machines);
        Assert.False(page.HasMachines);
        Assert.Null(_secrets.Read(machine.SecretKey));
    }
}
