using System.IO;
using Meerkat.App.Updates;

namespace Meerkat.Tests.App;

public sealed class UpdateServiceTests : IDisposable
{
    private readonly UpdateTestContext _context = new();

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task Check_DownloadsAndVerifiesNewerVersionInTheBackground()
    {
        _context.Feed.Latest = FakeReleaseFeed.CreateRelease("1.1.0", "## New\n* Faster polling\n* Tray badge\n");

        await _context.Service.CheckAsync(userInitiated: false);

        Assert.Equal(UpdateState.Ready, _context.Service.State);
        Assert.Equal("Update 1.1.0 ready", _context.ViewModel.Notice);
        Assert.Equal("Install update 1.1.0", _context.ViewModel.TrayActionText);
        Assert.Equal("Meerkat 1.1.0 is ready", _context.ViewModel.CardTitle);
        Assert.Equal(["Faster polling", "Tray badge"], _context.ViewModel.Highlights);
        Assert.True(_context.ViewModel.IsCardVisible);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.9.0")]
    public async Task Check_ReportsUpToDateWhenNoNewerVersionExists(string latestVersion)
    {
        _context.Feed.Latest = FakeReleaseFeed.CreateRelease(latestVersion);

        await _context.Service.CheckAsync(userInitiated: true);

        Assert.Equal(UpdateState.Idle, _context.Service.State);
        Assert.Null(_context.ViewModel.Notice);
        Assert.Equal("Meerkat is up to date", _context.ViewModel.Headline);
        Assert.Equal(0, _context.Feed.Downloads);
    }

    [Fact]
    public async Task Check_OnlyAnnouncesTheUpdateWhenBackgroundDownloadIsOff()
    {
        _context.Settings.Current.Updates.DownloadInBackground = false;
        _context.Feed.Latest = FakeReleaseFeed.CreateRelease("1.1.0");

        await _context.Service.CheckAsync(userInitiated: false);

        Assert.Equal(UpdateState.Available, _context.Service.State);
        Assert.Equal("Update 1.1.0 available", _context.ViewModel.Notice);
        Assert.False(_context.ViewModel.IsCardVisible);

        await _context.ViewModel.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(UpdateState.Ready, _context.Service.State);
    }

    [Fact]
    public async Task Download_IsRejectedWhenTheChecksumDoesNotMatch()
    {
        _context.Feed.Latest = FakeReleaseFeed.CreateRelease("1.1.0");
        _context.Feed.PublishedHash = new string('0', 64);

        await _context.Service.CheckAsync(userInitiated: false);

        Assert.Equal(UpdateState.Failed, _context.Service.State);
        Assert.Contains("checksum", _context.Service.ErrorMessage);

        _context.Service.RestartAndInstall();
        Assert.Empty(_context.Installer.Installed);
    }

    [Fact]
    public async Task Check_FailsQuietlyInTheBackgroundButReportsManualChecks()
    {
        _context.Feed.CheckFailure = new InvalidOperationException("offline");

        await _context.Service.CheckAsync(userInitiated: false);
        Assert.Equal(UpdateState.Idle, _context.Service.State);

        await _context.Service.CheckAsync(userInitiated: true);
        Assert.Equal(UpdateState.Failed, _context.Service.State);
        Assert.Contains("offline", _context.ViewModel.StatusText);
    }

    [Fact]
    public async Task Check_ReusesAVerifiedDownloadFromAnEarlierSession()
    {
        _context.Feed.Latest = FakeReleaseFeed.CreateRelease("1.1.0");
        await _context.Service.CheckAsync(userInitiated: false);

        using var nextSession = new UpdateTestContext(previousSession: _context);
        nextSession.Feed.Latest = _context.Feed.Latest;
        await nextSession.Service.CheckAsync(userInitiated: false);

        Assert.Equal(UpdateState.Ready, nextSession.Service.State);
        Assert.Equal(0, nextSession.Feed.Downloads);
    }

    [Fact]
    public async Task RestartAndInstall_InstallsTheStagedExecutableAndRequestsRestart()
    {
        _context.Feed.Latest = FakeReleaseFeed.CreateRelease("1.1.0");
        await _context.Service.CheckAsync(userInitiated: false);
        var restartRequested = false;
        _context.Service.RestartRequested += (_, _) => restartRequested = true;

        _context.ViewModel.RestartAndInstallCommand.Execute(null);

        var installed = Assert.Single(_context.Installer.Installed);
        Assert.Equal("new version", File.ReadAllText(installed));
        Assert.True(restartRequested);
    }

    [Fact]
    public async Task RestartAndInstall_DoesNotRestartWhenInstallationFails()
    {
        _context.Feed.Latest = FakeReleaseFeed.CreateRelease("1.1.0");
        _context.Installer.InstallFailure = new UnauthorizedAccessException("read-only");
        await _context.Service.CheckAsync(userInitiated: false);
        var restartRequested = false;
        _context.Service.RestartRequested += (_, _) => restartRequested = true;

        _context.Service.RestartAndInstall();

        Assert.False(restartRequested);
        Assert.Equal(UpdateState.Failed, _context.Service.State);
        Assert.Contains("read-only", _context.Service.ErrorMessage);
    }

    [Fact]
    public async Task InstallOnExit_InstallsOnlyAfterTheUserChoseIt()
    {
        _context.Feed.Latest = FakeReleaseFeed.CreateRelease("1.1.0");
        await _context.Service.CheckAsync(userInitiated: false);

        _context.Service.InstallPendingOnExit();
        Assert.Empty(_context.Installer.Installed);

        _context.ViewModel.InstallOnExitCommand.Execute(null);
        Assert.False(_context.ViewModel.IsCardVisible);
        Assert.Equal("Will be installed when Meerkat exits.", _context.ViewModel.StatusText);

        _context.Service.InstallPendingOnExit();
        Assert.Single(_context.Installer.Installed);
    }

    [Fact]
    public async Task UnsupportedBuild_NeverChecks()
    {
        using var unsupported = new UpdateTestContext(isSupported: false);
        unsupported.Feed.Latest = FakeReleaseFeed.CreateRelease("1.1.0");

        await unsupported.Service.CheckAsync(userInitiated: true);

        Assert.Equal(UpdateState.Unsupported, unsupported.Service.State);
        Assert.False(unsupported.ViewModel.IsSupported);
        Assert.Null(unsupported.ViewModel.Notice);
    }
}
