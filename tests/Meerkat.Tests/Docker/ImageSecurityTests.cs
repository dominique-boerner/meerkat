using System.Text;
using Meerkat.Abstractions;
using Meerkat.Ssh;
using Meerkat.Tests.Notes;
using Meerkat.Tests.Server;
using Meerkat.Widgets.Docker.Security;
using Meerkat.Widgets.Docker.Settings;
using Meerkat.Widgets.Docker.ViewModels;

namespace Meerkat.Tests.Docker;

internal sealed class FakeDockerShell : IDockerShell
{
    public Queue<ShellResult> Results { get; } = new();

    public List<string> Scripts { get; } = [];

    public Exception? Failure { get; set; }

    public Task<ShellResult> RunAsync(string script, CancellationToken cancellationToken)
    {
        Scripts.Add(script);
        return Failure is null
            ? Task.FromResult(Results.Count > 0 ? Results.Dequeue() : new ShellResult(0, string.Empty, string.Empty))
            : Task.FromException<ShellResult>(Failure);
    }
}

internal sealed class FakeImageScanner : IImageScanner
{
    public ScannerInfo? Info { get; set; } = new("Trivy", "0.76.2", null);

    public Dictionary<string, List<Vulnerability>> Findings { get; } = [];

    public Dictionary<string, Exception> ScanFailures { get; } = [];

    public List<string> Scanned { get; } = [];

    public int Installs { get; private set; }

    public int DatabaseUpdates { get; private set; }

    public Exception? DetectFailure { get; set; }

    public Task<ScannerInfo?> DetectAsync(CancellationToken cancellationToken) =>
        DetectFailure is null ? Task.FromResult(Info) : Task.FromException<ScannerInfo?>(DetectFailure);

    public Task<IReadOnlyList<Vulnerability>> ScanAsync(string imageId, CancellationToken cancellationToken)
    {
        Scanned.Add(imageId);
        return ScanFailures.TryGetValue(imageId, out var failure)
            ? Task.FromException<IReadOnlyList<Vulnerability>>(failure)
            : Task.FromResult<IReadOnlyList<Vulnerability>>([.. Findings.GetValueOrDefault(imageId) ?? []]);
    }

    public Task UpdateDatabaseAsync(CancellationToken cancellationToken)
    {
        DatabaseUpdates++;
        return Task.CompletedTask;
    }

    public Task<ScannerInfo> InstallAsync(CancellationToken cancellationToken)
    {
        Installs++;
        Info = new ScannerInfo("Trivy", "0.76.2", null);
        return Task.FromResult(Info);
    }
}

internal sealed class FakeImageInventory : IImageInventory
{
    public List<ImageDetails> Images { get; } = [];

    public Task<IReadOnlyList<ImageDetails>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ImageDetails>>([.. Images]);
}

internal sealed class InMemoryScanStore : IScanStore
{
    public Dictionary<string, StoredScans> Sources { get; } = [];

    public StoredScans Load(string sourceId) => Sources.TryGetValue(sourceId, out var scans) ? scans : new StoredScans();

    public void Save(string sourceId, StoredScans scans) => Sources[sourceId] = scans;
}

public sealed class ImageSecurityTests
{
    private const string AppImage = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string DatabaseImage = "sha256:2222222222222222222222222222222222222222222222222222222222222222";

    private static readonly Vulnerability CriticalOpenSsl = new("CVE-2026-0001", VulnerabilitySeverity.Critical, "openssl", "3.0.13", "3.0.15", "Bad", "https://example.org/cve-1", "sha256:base1");
    private static readonly Vulnerability HighExpat = new("CVE-2026-0002", VulnerabilitySeverity.High, "libexpat1", "2.6.1", "2.6.4", null, null, "sha256:own1");
    private static readonly Vulnerability HighUnfixed = new("CVE-2026-0003", VulnerabilitySeverity.High, "zlib1g", "1.3", null, null, null, null);
    private static readonly Vulnerability MediumCurl = new("CVE-2026-0004", VulnerabilitySeverity.Medium, "curl", "8.0", "8.1", null, null, null);

    private readonly FakeImageScanner _scanner = new();
    private readonly FakeImageInventory _inventory = new();
    private readonly InMemoryScanStore _store = new();
    private readonly InMemoryWidgetSettingsStore _settingsStore = new();
    private readonly FakeOverlayState _overlayState = new();
    private readonly FakeOverlayCommands _overlayCommands = new();
    private readonly ManualTimeProvider _time = new(new DateTime(2026, 11, 28, 14, 0, 0));
    private readonly DockerSettingsSource _settings;
    private bool _onBattery;

    public ImageSecurityTests()
    {
        _settings = new DockerSettingsSource(_settingsStore);
        _inventory.Images.Add(new ImageDetails(AppImage, "shop-app:dev", IsOwnBuild: true, "eclipse-temurin:21-jre", new HashSet<string> { "sha256:base1" }));
        _inventory.Images.Add(new ImageDetails(DatabaseImage, "postgres:16-alpine", IsOwnBuild: false, null, new HashSet<string>()));
        _scanner.Findings[AppImage] = [CriticalOpenSsl, HighExpat, HighUnfixed, MediumCurl];
        _scanner.Findings[DatabaseImage] = [];
    }

    // ---- Reading what the tools print ----

    [Fact]
    public void A_scan_result_is_read_with_severity_fix_and_layer()
    {
        const string Json = """
            {"Results":[{"Target":"x","Vulnerabilities":[
              {"VulnerabilityID":"CVE-2026-1","PkgName":"libexpat","InstalledVersion":"2.10.0-r0","FixedVersion":"2.11.0-r0","Severity":"HIGH",
               "Title":"Out of bounds","PrimaryURL":"https://avd.aquasec.com/nvd/cve-2026-1","Layer":{"DiffID":"sha256:abc"}},
              {"VulnerabilityID":"CVE-2026-2","PkgName":"busybox","InstalledVersion":"1.0","Severity":"CRITICAL"},
              {"VulnerabilityID":"CVE-2026-2","PkgName":"busybox","InstalledVersion":"1.0","Severity":"CRITICAL"}
            ]},{"Target":"no findings here"}]}
            """;

        var vulnerabilities = TrivyOutputParser.ParseScan(Json);

        Assert.Equal(2, vulnerabilities.Count);
        Assert.Equal(new Vulnerability("CVE-2026-1", VulnerabilitySeverity.High, "libexpat", "2.10.0-r0", "2.11.0-r0", "Out of bounds", "https://avd.aquasec.com/nvd/cve-2026-1", "sha256:abc"), vulnerabilities[0]);
        Assert.Equal(VulnerabilitySeverity.Critical, vulnerabilities[1].Severity);
        Assert.False(vulnerabilities[1].HasFix);
    }

    [Fact]
    public void A_scan_without_results_finds_nothing()
    {
        Assert.Empty(TrivyOutputParser.ParseScan("""{"SchemaVersion":2}"""));
    }

    [Fact]
    public void The_version_tells_the_age_of_the_database()
    {
        var info = TrivyOutputParser.ParseVersion("""{"Version":"0.76.2","VulnerabilityDB":{"UpdatedAt":"2026-11-28T07:04:51.381284779Z"}}""");

        Assert.Equal("0.76.2", info.Version);
        Assert.Equal(new DateTimeOffset(2026, 11, 28, 7, 4, 51, TimeSpan.Zero), info.DatabaseUpdatedAt!.Value.AddTicks(-info.DatabaseUpdatedAt.Value.Ticks % TimeSpan.TicksPerSecond));
        Assert.Null(TrivyOutputParser.ParseVersion("""{"Version":"0.76.2"}""").DatabaseUpdatedAt);
    }

    [Fact]
    public void Only_images_in_use_are_listed_and_a_base_image_is_found_by_its_layers()
    {
        var output = """
            APP_IMAGE
            DATABASE_IMAGE
            MARK
            [
              {"Id":"APP_IMAGE","RepoTags":["shop-app:dev"],"RepoDigests":[],"RootFS":{"Layers":["l1","l2","l3"]},"Config":{"Labels":null}},
              {"Id":"DATABASE_IMAGE","RepoTags":["postgres:16-alpine"],"RepoDigests":["postgres@sha256:abc"],"RootFS":{"Layers":["p1"]}},
              {"Id":"sha256:base","RepoTags":["eclipse-temurin:21-jre"],"RepoDigests":["eclipse-temurin@sha256:def"],"RootFS":{"Layers":["l1","l2"]}},
              {"Id":"sha256:unused","RepoTags":["alpine:3"],"RepoDigests":["alpine@sha256:123"],"RootFS":{"Layers":["l1"]}}
            ]
            """;

        var images = DockerImageParser.Parse(Fill(output), "MARK");

        Assert.Equal(["postgres:16-alpine", "shop-app:dev"], images.Select(image => image.Name));
        var app = images[1];
        Assert.True(app.IsOwnBuild);
        Assert.Equal("eclipse-temurin:21-jre", app.BaseImage);
        Assert.Equal(["l1", "l2"], app.BaseLayers.Order());
        Assert.False(images[0].IsOwnBuild);
        Assert.Null(images[0].BaseImage);
    }

    [Fact]
    public void An_image_that_names_its_parent_was_built_here_even_if_it_has_a_digest()
    {
        var output = """
            APP_IMAGE
            MARK
            [{"Id":"APP_IMAGE","RepoTags":["shop-app:dev"],"RepoDigests":["shop-app@sha256:abc"],"Parent":"sha256:base","RootFS":{"Layers":["l1"]}}]
            """;

        Assert.True(DockerImageParser.Parse(Fill(output), "MARK").Single().IsOwnBuild);
    }

    [Fact]
    public void An_image_without_a_tag_is_named_by_its_short_id()
    {
        var output = """
            APP_IMAGE
            MARK
            [{"Id":"APP_IMAGE","RepoTags":[],"RepoDigests":[],"RootFS":{"Layers":["l1"]}}]
            """;

        Assert.Equal("111111111111", DockerImageParser.Parse(Fill(output), "MARK").Single().Name);
    }

    // ---- Talking to the system ----

    [Fact]
    public async Task A_script_reaches_wsl_encoded_so_nothing_on_the_way_can_change_it()
    {
        var runner = new FakeProcessRunner { Result = new(0, "out", "err") };
        var shell = new WslDockerShell(runner, () => "Ubuntu");

        var result = await shell.RunAsync("echo \"$HOME\"\r\nls", CancellationToken.None);

        Assert.Equal(new ShellResult(0, "out", "err"), result);
        var (fileName, arguments) = Assert.Single(runner.Calls);
        Assert.Equal("wsl.exe", fileName);
        Assert.Equal(["-d", "Ubuntu", "--exec", "sh", "-c"], arguments.Take(5));
        Assert.Equal("echo \"$HOME\"\nls", Decode(arguments[5]));
    }

    [Fact]
    public async Task Over_ssh_the_script_reports_its_own_exit_code()
    {
        var ssh = new FakeSshClient();
        ssh.StreamLines.Add(new SshOutputLine("first", false));
        ssh.StreamLines.Add(new SshOutputLine("a warning", true));
        ssh.StreamLines.Add(new SshOutputLine("___MEERKAT_EXIT___:3", false));
        var shell = new SshDockerShell(ssh, () => new SshTarget("build.example.org", "dev", null, SshAuthentication.Agent, null));

        var result = await shell.RunAsync("docker ps", CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("first", result.Output.Trim());
        Assert.Equal("a warning", result.Error.Trim());
        Assert.EndsWith("; echo ___MEERKAT_EXIT___:$?", ssh.Commands.Single());
    }

    [Fact]
    public async Task A_connection_that_breaks_is_told_apart_from_a_script_that_fails()
    {
        var ssh = new FakeSshClient();
        ssh.StreamLines.Add(new SshOutputLine("ssh: connect to host build.example.org port 22: Connection refused", true));
        var shell = new SshDockerShell(ssh, () => new SshTarget("build.example.org", "dev", null, SshAuthentication.Agent, null));

        var exception = await Assert.ThrowsAsync<DockerShellException>(() => shell.RunAsync("docker ps", CancellationToken.None));

        Assert.Equal("Connection refused", exception.Message);
    }

    [Fact]
    public async Task A_system_without_trivy_has_no_scanner()
    {
        var shell = new FakeDockerShell();
        shell.Results.Enqueue(new ShellResult(127, string.Empty, string.Empty));

        Assert.Null(await new TrivyScanner(shell).DetectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_scan_falls_back_to_the_database_at_hand_and_takes_only_real_image_ids()
    {
        var shell = new FakeDockerShell();
        shell.Results.Enqueue(new ShellResult(0, """{"Results":[]}""", string.Empty));
        var scanner = new TrivyScanner(shell);

        Assert.Empty(await scanner.ScanAsync(AppImage, CancellationToken.None));

        Assert.Contains($"{AppImage} || ", shell.Scripts.Single());
        Assert.Contains($"--skip-db-update {AppImage}", shell.Scripts.Single());
        await Assert.ThrowsAsync<ArgumentException>(() => scanner.ScanAsync("nginx; rm -rf /", CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_scan_reports_what_trivy_printed()
    {
        var shell = new FakeDockerShell();
        shell.Results.Enqueue(new ShellResult(1, string.Empty, "FATAL image scan error"));

        var exception = await Assert.ThrowsAsync<ScannerException>(() => new TrivyScanner(shell).ScanAsync(AppImage, CancellationToken.None));

        Assert.Equal("FATAL image scan error", exception.Detail);
    }

    [Fact]
    public async Task The_installation_checks_the_download_and_stays_in_the_home_directory()
    {
        var shell = new FakeDockerShell();
        shell.Results.Enqueue(new ShellResult(0, """{"Version":"0.76.2"}""", string.Empty));

        var info = await new TrivyScanner(shell).InstallAsync(CancellationToken.None);

        Assert.Equal("0.76.2", info.Version);
        var script = shell.Scripts.Single();
        Assert.Contains("sha256sum -c", script);
        Assert.Contains($"releases/download/v{TrivyScanner.InstalledVersion}/", script);
        Assert.Contains("$HOME/.local/share/meerkat/trivy", script);
        Assert.DoesNotContain("sudo", script);
    }

    // ---- One system ----

    [Fact]
    public async Task Without_a_scanner_nothing_is_listed_and_installing_one_scans_at_once()
    {
        _scanner.Info = null;
        var source = CreateSource();

        await source.RunDueWorkAsync(CancellationToken.None);

        Assert.Equal(ScannerState.Missing, source.State);
        Assert.Empty(source.Images);
        Assert.Empty(_scanner.Scanned);

        await source.InstallCommand.ExecuteAsync(null);

        Assert.Equal(1, _scanner.Installs);
        Assert.Equal(ScannerState.Ready, source.State);
        Assert.Equal([AppImage, DatabaseImage], _scanner.Scanned);
    }

    [Fact]
    public async Task The_first_run_scans_every_image_and_counts_what_the_user_wants_to_see()
    {
        var source = CreateSource();

        await source.RunDueWorkAsync(CancellationToken.None);

        var app = source.Images.Single(image => image.Id == AppImage);
        Assert.True(app.IsScanned);
        Assert.Equal(1, app.CriticalCount);

        // The high one without a fix is left out by default.
        Assert.Equal(1, app.HighCount);
        Assert.Equal("own image · base eclipse-temurin:21-jre", app.Subtitle);
        Assert.Equal("just now", app.ScanText);
        Assert.Equal([CriticalOpenSsl], app.Reported);
        Assert.NotNull(_store.Sources[ContainerSourceOptions.LocalId].LastFullScan);
    }

    [Fact]
    public async Task Showing_vulnerabilities_without_a_fix_needs_no_new_scan()
    {
        var source = CreateSource();
        await source.RunDueWorkAsync(CancellationToken.None);
        _scanner.Scanned.Clear();

        _settings.Current.Security.OnlyFixable = false;
        source.ApplySettings();

        Assert.Equal(2, source.Images.Single(image => image.Id == AppImage).HighCount);
        Assert.Empty(_scanner.Scanned);
    }

    [Fact]
    public async Task Between_the_daily_scans_only_new_images_are_scanned()
    {
        var source = CreateSource();
        await source.RunDueWorkAsync(CancellationToken.None);
        _scanner.Scanned.Clear();

        const string NewImage = "sha256:3333333333333333333333333333333333333333333333333333333333333333";
        _inventory.Images.Add(new ImageDetails(NewImage, "redis:7.4", IsOwnBuild: false, null, new HashSet<string>()));
        _time.Now = _time.Now.AddMinutes(6);
        await source.RunDueWorkAsync(CancellationToken.None);

        Assert.Equal([NewImage], _scanner.Scanned);
    }

    [Fact]
    public async Task The_daily_scan_runs_once_its_time_has_passed()
    {
        _settings.Current.Security.DailyScanTime = "12:30";
        var source = CreateSource();
        await source.RunDueWorkAsync(CancellationToken.None);
        _scanner.Scanned.Clear();

        _time.Now = new DateTime(2026, 11, 29, 12, 20, 0);
        await source.RunDueWorkAsync(CancellationToken.None);
        Assert.Empty(_scanner.Scanned);

        _time.Now = new DateTime(2026, 11, 29, 12, 31, 0);
        await source.RunDueWorkAsync(CancellationToken.None);
        Assert.Equal(2, _scanner.Scanned.Count);
    }

    [Theory]
    [InlineData("2026-11-28T14:00", null, true)]
    [InlineData("2026-11-28T14:00", "2026-11-28T12:31", false)]
    [InlineData("2026-11-28T14:00", "2026-11-28T09:00", true)]
    [InlineData("2026-11-28T11:00", "2026-11-27T13:00", false)]
    [InlineData("2026-11-28T11:00", "2026-11-27T11:00", true)]
    public void The_daily_scan_is_due_when_its_time_lies_between_the_last_scan_and_now(string now, string? lastScan, bool expected)
    {
        var last = lastScan is null ? (DateTimeOffset?)null : new DateTimeOffset(DateTime.Parse(lastScan), TimeSpan.Zero);

        Assert.Equal(expected, SecuritySourceViewModel.IsDailyScanDue(new DateTimeOffset(DateTime.Parse(now), TimeSpan.Zero), last, new TimeOnly(12, 30)));
    }

    [Fact]
    public async Task On_battery_this_computer_is_not_scanned_unless_asked_to()
    {
        _onBattery = true;
        var source = CreateSource();

        await source.RunDueWorkAsync(CancellationToken.None);
        Assert.Empty(_scanner.Scanned);
        Assert.Equal(2, source.Images.Count);

        await source.ScanAsync(onlyNew: false, CancellationToken.None);
        Assert.Equal(2, _scanner.Scanned.Count);
    }

    [Fact]
    public async Task Another_machine_is_scanned_whatever_powers_this_one()
    {
        _onBattery = true;
        var source = CreateSource(isRemote: true);

        await source.RunDueWorkAsync(CancellationToken.None);

        Assert.Equal(2, _scanner.Scanned.Count);
    }

    [Fact]
    public async Task One_image_that_cannot_be_scanned_does_not_stop_the_others_and_is_not_retried_every_minute()
    {
        _scanner.ScanFailures[DatabaseImage] = new ScannerException("The scan failed.", "unsupported image");
        var source = CreateSource();

        await source.RunDueWorkAsync(CancellationToken.None);

        var database = source.Images.Single(image => image.Id == DatabaseImage);
        Assert.Equal("unsupported image", database.ScanError);
        Assert.Equal("failed", database.ScanText);
        Assert.True(source.Images.Single(image => image.Id == AppImage).IsScanned);

        _scanner.Scanned.Clear();
        _time.Now = _time.Now.AddMinutes(1);
        await source.RunDueWorkAsync(CancellationToken.None);
        Assert.Empty(_scanner.Scanned);
    }

    [Fact]
    public async Task A_system_that_stops_answering_is_left_alone_for_a_while()
    {
        _scanner.DetectFailure = new DockerShellException("Connection refused");
        var source = CreateSource(isRemote: true);

        await source.RunDueWorkAsync(CancellationToken.None);
        Assert.Equal(ScannerState.Failed, source.State);
        Assert.Equal("Connection refused", source.ErrorMessage);

        _scanner.DetectFailure = null;
        _time.Now = _time.Now.AddMinutes(5);
        await source.RunDueWorkAsync(CancellationToken.None);
        Assert.Equal(ScannerState.Failed, source.State);

        _time.Now = _time.Now.AddMinutes(11);
        await source.RunDueWorkAsync(CancellationToken.None);
        Assert.Equal(ScannerState.Ready, source.State);
    }

    [Fact]
    public async Task Results_survive_a_restart_and_forget_images_that_are_gone()
    {
        var source = CreateSource();
        await source.RunDueWorkAsync(CancellationToken.None);
        _scanner.Scanned.Clear();

        _inventory.Images.RemoveAll(image => image.Id == DatabaseImage);
        _time.Now = _time.Now.AddMinutes(10);
        var restarted = CreateSource();
        await restarted.RunDueWorkAsync(CancellationToken.None);

        Assert.Empty(_scanner.Scanned);
        Assert.Equal(1, restarted.Images.Single().CriticalCount);
        Assert.Equal("10 min ago", restarted.Images.Single().ScanText);

        await restarted.ScanAsync(onlyNew: false, CancellationToken.None);
        Assert.Equal([AppImage], _store.Sources[ContainerSourceOptions.LocalId].Images.Keys);
    }

    [Fact]
    public async Task An_old_database_is_pointed_out_and_updating_it_scans_again()
    {
        _scanner.Info = new ScannerInfo("Trivy", "0.76.2", _time.GetUtcNow().AddDays(-5));
        var source = CreateSource();
        await source.RunDueWorkAsync(CancellationToken.None);

        Assert.Equal("Vulnerability database is 5 days old", source.StaleDatabaseText);
        Assert.Equal("Trivy 0.76.2 · database 5 d ago · last scan just now", source.MetaText);

        _scanner.Scanned.Clear();
        _scanner.Info = new ScannerInfo("Trivy", "0.76.2", _time.GetUtcNow());
        await source.UpdateDatabaseCommand.ExecuteAsync(null);

        Assert.Equal(1, _scanner.DatabaseUpdates);
        Assert.Null(source.StaleDatabaseText);
        Assert.Equal(2, _scanner.Scanned.Count);
    }

    // ---- The tab and the shell ----

    [Fact]
    public async Task A_critical_vulnerability_is_reported_to_the_shell()
    {
        var (security, source) = CreateSecurity();
        await source.RunDueWorkAsync(CancellationToken.None);

        Assert.True(security.HasAttention);

        Assert.Equal(new WidgetStatus("Images", "1/2", WidgetSeverity.Error), security.Status);
        var alert = Assert.Single(security.Alerts);
        Assert.Equal(WidgetSeverity.Error, alert.Severity);
        Assert.Equal("Critical vulnerability in 1 image", alert.Title);
        Assert.Equal("openssl · fix available", alert.Detail);
    }

    [Fact]
    public async Task A_vulnerability_in_several_images_is_one_finding()
    {
        _scanner.Findings[DatabaseImage] = [CriticalOpenSsl with { LayerId = null }];
        var (security, source) = CreateSecurity();
        await source.RunDueWorkAsync(CancellationToken.None);

        Assert.Equal("Critical vulnerability in 2 images", security.Alerts.Single().Title);
        Assert.Equal("0/2", security.Status!.Summary);
    }

    [Fact]
    public async Task An_image_from_a_registry_has_no_line_of_its_own()
    {
        _scanner.Findings[AppImage] = [];
        _scanner.Findings[DatabaseImage] = [CriticalOpenSsl];
        var (_, source) = CreateSecurity();
        await source.RunDueWorkAsync(CancellationToken.None);

        Assert.Null(source.Images.Single(image => image.Id == DatabaseImage).Subtitle);
    }

    [Fact]
    public async Task The_report_remembers_since_when_a_vulnerability_is_known()
    {
        var (security, source) = CreateSecurity();
        await source.RunDueWorkAsync(CancellationToken.None);

        _time.Now = _time.Now.AddHours(25);
        await source.RunDueWorkAsync(CancellationToken.None);

        Assert.Equal(new DateTimeOffset(2026, 11, 28, 14, 0, 0, TimeSpan.Zero), security.Alerts.Single().Since);
    }

    [Fact]
    public async Task Ignoring_a_vulnerability_takes_it_out_of_everything_and_is_remembered()
    {
        var (security, source) = CreateSecurity();
        await source.RunDueWorkAsync(CancellationToken.None);

        security.SelectImageCommand.Execute(source.Images.Single(image => image.Id == AppImage));
        security.IgnoreRowCommand.Execute(security.SelectedVulnerabilities.Single(row => row.Id == "CVE-2026-0001"));

        Assert.DoesNotContain(security.SelectedVulnerabilities, row => row.Id == "CVE-2026-0001");
        Assert.Empty(security.Alerts);
        Assert.False(security.HasAttention);
        Assert.Equal(new WidgetStatus("Images", "2/2", WidgetSeverity.Ok), security.Status);
        Assert.Equal(0, source.Images.Single(image => image.Id == AppImage).CriticalCount);
        var ignored = Assert.Single(_settingsStore.Load<DockerWidgetSettings>("docker").Security.Ignored);
        Assert.Equal(("CVE-2026-0001", "openssl"), (ignored.Id, ignored.Package));
    }

    [Fact]
    public async Task Reporting_from_high_on_warns_without_calling_it_an_error()
    {
        _scanner.Findings[AppImage] = [HighExpat];
        _settings.Current.Security.ReportFrom = ReportThreshold.High;
        var (security, source) = CreateSecurity();
        await source.RunDueWorkAsync(CancellationToken.None);

        var alert = Assert.Single(security.Alerts);
        Assert.Equal((WidgetSeverity.Warning, true), (alert.Severity, alert.Prominent));
        Assert.Equal("High vulnerability in 1 image", alert.Title);
    }

    [Fact]
    public async Task With_reporting_off_findings_are_listed_but_nobody_is_bothered()
    {
        _settings.Current.Security.ReportFrom = ReportThreshold.Never;
        var (security, source) = CreateSecurity();
        await source.RunDueWorkAsync(CancellationToken.None);

        Assert.Empty(security.Alerts);
        Assert.Equal(new WidgetStatus("Images", "2/2", WidgetSeverity.Ok), security.Status);
        Assert.Equal(1, source.Images.Single(image => image.Id == AppImage).CriticalCount);
    }

    [Fact]
    public async Task Selecting_an_image_lists_its_grave_vulnerabilities_and_says_what_is_left_out()
    {
        _settings.Current.Security.OnlyFixable = false;
        var (security, source) = CreateSecurity();
        await source.RunDueWorkAsync(CancellationToken.None);
        var app = source.Images.Single(image => image.Id == AppImage);

        security.SelectImageCommand.Execute(app);

        Assert.True(app.IsSelected);
        Assert.Equal("SHOP-APP:DEV", security.SelectionTitle);
        Assert.Equal(["CVE-2026-0001", "CVE-2026-0002", "CVE-2026-0003"], security.SelectedVulnerabilities.Select(row => row.Id));
        Assert.Equal("–", security.SelectedVulnerabilities[2].FixedVersion);
        Assert.Equal("1 more of medium or low severity not listed.", security.SelectionHint);

        security.OpenVulnerabilityCommand.Execute(security.SelectedVulnerabilities[0]);
        Assert.Equal(new Uri("https://example.org/cve-1"), _overlayCommands.OpenedUrls.Single());

        security.SelectImageCommand.Execute(app);
        Assert.False(security.HasSelection);
        Assert.False(app.IsSelected);
    }

    [Fact]
    public async Task Every_system_has_its_section_but_only_those_the_user_chose_are_scanned()
    {
        var build = new DockerHost { Name = "build", Address = "build.example.org", ScanImages = true };
        var prod = new DockerHost { Name = "prod", Address = "prod.example.org" };
        _settings.Current.RemoteHosts.Add(build);
        _settings.Current.RemoteHosts.Add(prod);
        _settings.Current.Security.ScanLocalImages = false;
        _settings.Current.CollapsedImageSources.Add(prod.Id);
        var security = new SecurityViewModel(
            _settings,
            (host, isScanned) => CreateSource(isRemote: host is not null, id: host?.Id ?? ContainerSourceOptions.LocalId, isScanned, host?.Name ?? "Ubuntu"),
            _overlayCommands,
            _time);

        security.Start();
        await security.Sources[1].RunDueWorkAsync(CancellationToken.None);

        // The same systems in the same order as on the containers page, each with a heading to fold it by.
        Assert.Equal(["Ubuntu", "build", "prod"], security.Sources.Select(source => source.Label));
        Assert.All(security.Sources, source => Assert.True(source.ShowHeading));
        Assert.Equal([ScannerState.Off, ScannerState.Ready, ScannerState.Off], security.Sources.Select(source => source.State));
        Assert.Equal(["not scanned", "1 of 2 without findings", "not scanned"], security.Sources.Select(source => source.Summary));
        Assert.Equal([WidgetSeverity.Ok, WidgetSeverity.Error, WidgetSeverity.Ok], security.Sources.Select(source => source.Severity));
        Assert.Equal("1/2", security.Status!.Summary);
        Assert.False(security.IsSwitchedOff);

        // A system that is not scanned is not asked anything, not even by "Scan now".
        _scanner.Scanned.Clear();
        await security.ScanNowCommand.ExecuteAsync(null);
        Assert.Equal(2, _scanner.Scanned.Count);

        // Folding is remembered per system, apart from the containers page.
        Assert.Equal([true, true, false], security.Sources.Select(source => source.IsExpanded));
        Assert.False(security.Sources[2].IsContentVisible);
        security.Sources[1].ToggleExpandedCommand.Execute(null);
        security.Sources[2].ToggleExpandedCommand.Execute(null);
        Assert.Equal([build.Id], _settingsStore.Load<DockerWidgetSettings>("docker").CollapsedImageSources);
        Assert.Empty(_settings.Current.CollapsedSources);
        await security.StopAsync();
    }

    [Fact]
    public void A_lone_system_has_no_heading_and_cannot_be_folded_away()
    {
        _settings.Current.CollapsedImageSources.Add(ContainerSourceOptions.LocalId);
        var (security, source) = CreateSecurity();

        Assert.False(source.ShowHeading);
        Assert.True(source.IsContentVisible);
        Assert.Single(security.Sources);
    }

    [Fact]
    public async Task The_docker_page_adds_the_images_to_what_it_reports()
    {
        var (security, source) = CreateSecurity(start: false);
        var page = new DockerWidgetViewModel(new FakeContainerProvider(), _overlayState, _overlayCommands, _settings, _time, security: security);
        page.Start();
        await source.RunDueWorkAsync(CancellationToken.None);

        Assert.Equal("Images", Assert.Single(page.AdditionalStatuses).Label);
        Assert.Contains(page.Alerts, alert => alert.Title == "Critical vulnerability in 1 image");

        page.ShowSecurityCommand.Execute(null);
        Assert.True(page.IsSecurityTab);
        Assert.False(page.IsContainersTab);
        await page.StopAsync();
    }

    // ---- Settings ----

    [Fact]
    public void Security_settings_round_trip_through_the_page()
    {
        var settings = new DockerWidgetSettings();
        settings.Security.Ignored.Add(new IgnoredVulnerability { Id = "CVE-2026-0001", Package = "openssl" });
        settings.Security.Ignored.Add(new IgnoredVulnerability { Id = "CVE-2026-0002", Package = "libexpat1" });
        var page = new DockerSettingsPageViewModel(settings);

        Assert.Equal("Ignored vulnerabilities (2)", page.IgnoredLinkText);
        page.RestoreIgnoredCommand.Execute(page.Ignored[0]);
        page.SelectedThreshold = page.Thresholds.Single(option => option.Threshold == ReportThreshold.High);
        page.DailyScanTime = " 7:05 ";
        page.OnlyFixable = false;
        page.SkipOnBattery = false;
        page.ScanLocalImages = false;

        var security = page.ToSettings().Security;

        Assert.Equal(ReportThreshold.High, security.ReportFrom);
        Assert.Equal("07:05", security.DailyScanTime);
        Assert.Equal(new TimeOnly(7, 5), security.EffectiveDailyScanTime);
        Assert.False(security.OnlyFixable);
        Assert.False(security.SkipOnBattery);
        Assert.False(security.ScanLocalImages);
        Assert.Equal("CVE-2026-0002", Assert.Single(security.Ignored).Id);
        Assert.Equal("Ignored vulnerabilities (1)", page.IgnoredLinkText);
    }

    [Fact]
    public void A_time_that_is_none_is_pointed_out_and_not_stored()
    {
        var page = new DockerSettingsPageViewModel(new DockerWidgetSettings());
        var changes = 0;
        page.Changed += (_, _) => changes++;

        page.DailyScanTime = "noon";

        Assert.NotNull(page.DailyScanTimeError);
        Assert.Equal(0, changes);
        Assert.Equal("12:30", page.ToSettings().Security.DailyScanTime);

        page.DailyScanTime = "6:45";
        Assert.Null(page.DailyScanTimeError);
        Assert.Equal(1, changes);
        Assert.Equal("06:45", page.ToSettings().Security.DailyScanTime);
    }

    [Fact]
    public async Task Installing_a_scanner_from_the_settings_switches_scanning_on_for_that_system()
    {
        var scanner = new FakeImageScanner { Info = null };
        var switchedOn = false;
        var status = new ScannerStatusViewModel(scanner, () => switchedOn = true);

        await status.DetectAsync();
        Assert.Equal("no scanner", status.Text);
        Assert.True(status.CanInstall);

        await status.InstallCommand.ExecuteAsync(null);

        Assert.Equal("Trivy 0.76.2", status.Text);
        Assert.False(status.CanInstall);
        Assert.True(switchedOn);
    }

    private SecuritySourceViewModel CreateSource(bool isRemote = false, string id = ContainerSourceOptions.LocalId, bool isScanned = true, string? label = null) => new(
        _scanner,
        _inventory,
        _store,
        _settings,
        new SecuritySourceOptions(id, () => label ?? (isRemote ? "build" : "Ubuntu"), isRemote),
        _overlayState,
        _time,
        () => _onBattery)
    {
        IsScanned = isScanned,
    };

    private (SecurityViewModel Security, SecuritySourceViewModel Source) CreateSecurity(bool start = true)
    {
        // The overlay stays hidden, so the source's own loop waits and the tests decide when work is done.
        var source = CreateSource();
        var security = new SecurityViewModel(_settings, (_, _) => source, _overlayCommands, _time);
        if (start)
        {
            security.Start();
        }

        return (security, source);
    }

    private static string Fill(string template) => template.Replace("APP_IMAGE", AppImage).Replace("DATABASE_IMAGE", DatabaseImage);

    private static string Decode(string command)
    {
        var encoded = command.Split(' ')[1];
        return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    }
}
