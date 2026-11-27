using System.IO;
using System.IO.Compression;
using CommunityToolkit.Mvvm.ComponentModel;
using Meerkat.App.Diagnostics;
using Meerkat.App.Settings;

namespace Meerkat.App.Updates;

public enum UpdateState
{
    /// <summary>This build cannot update itself.</summary>
    Unsupported,

    /// <summary>No newer version is known.</summary>
    Idle,
    Checking,

    /// <summary>A newer version exists but has not been downloaded.</summary>
    Available,
    Downloading,

    /// <summary>The newer version is downloaded, verified and ready to be installed.</summary>
    Ready,
    Failed,
}

public sealed partial class UpdateService(
    IReleaseFeed feed,
    IUpdateInstaller installer,
    SettingsService settings,
    UpdateEnvironment environment,
    ErrorLog errorLog) : ObservableObject
{
    private const string ExecutableName = "Meerkat.exe";
    private const string ExtractedDirectoryName = "app";
    private const string VerifiedMarkerName = ".verified";

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private CancellationTokenSource? _downloadCancellation;
    private string? _stagedExecutable;
    private bool _isChecking;

    [ObservableProperty]
    private UpdateState _state = installer.IsSupported ? UpdateState.Idle : UpdateState.Unsupported;

    [ObservableProperty]
    private ReleaseInfo? _release;

    [ObservableProperty]
    private long _downloadedBytes;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _installOnExit;

    /// <summary>Raised after the new executable is in place and the application should restart.</summary>
    public event EventHandler? RestartRequested;

    public SemanticVersion InstalledVersion => environment.InstalledVersion;

    /// <summary>Checks at startup and then once a day for as long as the application runs.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (State == UpdateState.Unsupported)
        {
            return;
        }

        RemoveObsoleteFiles();
        try
        {
            await Task.Delay(StartupDelay, cancellationToken);
            while (true)
            {
                if (settings.Current.Updates.CheckAutomatically)
                {
                    await CheckAsync(userInitiated: false);
                }

                await Task.Delay(CheckInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Application is shutting down.
        }
    }

    public async Task CheckAsync(bool userInitiated)
    {
        if (_isChecking || State is UpdateState.Unsupported or UpdateState.Downloading)
        {
            return;
        }

        _isChecking = true;
        var previousState = State;
        try
        {
            // Background checks stay invisible so that a known update does not flicker in the UI once a day.
            if (userInitiated)
            {
                ErrorMessage = null;
                State = UpdateState.Checking;
            }

            var preferences = settings.Current.Updates;
            var latest = await feed.GetLatestAsync(
                preferences.Channel == UpdateChannel.Prerelease,
                environment.IsSelfContained,
                CancellationToken.None);

            if (latest is null || latest.Version <= InstalledVersion)
            {
                Release = null;
                _stagedExecutable = null;
                State = UpdateState.Idle;
                return;
            }

            if (previousState == UpdateState.Ready && Release?.Version == latest.Version && _stagedExecutable is not null)
            {
                State = UpdateState.Ready;
                return;
            }

            Release = latest;
            _stagedExecutable = FindVerifiedDownload(latest);
            State = _stagedExecutable is null ? UpdateState.Available : UpdateState.Ready;
        }
        catch (Exception exception)
        {
            errorLog.Write("Update check failed", exception);
            if (userInitiated)
            {
                ErrorMessage = $"The update check failed: {exception.Message}";
                State = UpdateState.Failed;
            }
            else
            {
                // Being offline is normal; a silent check keeps whatever was known before.
                State = previousState;
            }

            return;
        }
        finally
        {
            _isChecking = false;
        }

        if (State == UpdateState.Available && settings.Current.Updates.DownloadInBackground)
        {
            await DownloadAsync();
        }
    }

    public async Task DownloadAsync()
    {
        if (Release is not { } release || State is not (UpdateState.Available or UpdateState.Failed))
        {
            return;
        }

        _downloadCancellation = new CancellationTokenSource();
        var cancellationToken = _downloadCancellation.Token;
        var progress = new Progress<long>(bytes => DownloadedBytes = bytes);

        DownloadedBytes = 0;
        ErrorMessage = null;
        State = UpdateState.Downloading;
        try
        {
            _stagedExecutable = await Task.Run(() => StageAsync(release, progress, cancellationToken), cancellationToken);
            State = UpdateState.Ready;
        }
        catch (OperationCanceledException)
        {
            State = UpdateState.Available;
        }
        catch (Exception exception)
        {
            errorLog.Write($"Downloading update {release.Version} failed", exception);
            ErrorMessage = $"The download failed: {exception.Message}";
            State = UpdateState.Failed;
        }
        finally
        {
            _downloadCancellation.Dispose();
            _downloadCancellation = null;
        }
    }

    public void CancelDownload() => _downloadCancellation?.Cancel();

    /// <summary>Installs the downloaded update and asks the application to restart.</summary>
    public void RestartAndInstall()
    {
        if (TryInstall())
        {
            RestartRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Installs a pending update while the application exits, if the user asked for that.</summary>
    public void InstallPendingOnExit()
    {
        if (InstallOnExit)
        {
            TryInstall();
        }
    }

    private bool TryInstall()
    {
        if (State != UpdateState.Ready || _stagedExecutable is null)
        {
            return false;
        }

        try
        {
            installer.Install(_stagedExecutable);
            return true;
        }
        catch (Exception exception)
        {
            errorLog.Write("Installing the update failed", exception);
            ErrorMessage = $"The update could not be installed: {exception.Message}";
            State = UpdateState.Failed;
            return false;
        }
    }

    private async Task<string> StageAsync(ReleaseInfo release, IProgress<long> progress, CancellationToken cancellationToken)
    {
        var directory = GetStagingDirectory(release);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);
        var archivePath = Path.Combine(directory, release.AssetName);
        await feed.DownloadAsync(release.AssetUrl, archivePath, progress, cancellationToken).ConfigureAwait(false);

        var checksums = await feed.GetTextAsync(release.ChecksumsUrl, cancellationToken).ConfigureAwait(false);
        var expectedHash = ChecksumFile.FindHash(checksums, release.AssetName)
            ?? throw new InvalidDataException($"The release provides no checksum for {release.AssetName}.");
        if (!string.Equals(expectedHash, ChecksumFile.ComputeHash(archivePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The downloaded file does not match its published checksum.");
        }

        var extractedDirectory = Path.Combine(directory, ExtractedDirectoryName);
        ZipFile.ExtractToDirectory(archivePath, extractedDirectory);
        File.Delete(archivePath);

        var executable = Path.Combine(extractedDirectory, ExecutableName);
        if (!File.Exists(executable))
        {
            throw new InvalidDataException($"The release archive does not contain {ExecutableName}.");
        }

        await File.WriteAllTextAsync(Path.Combine(directory, VerifiedMarkerName), expectedHash, cancellationToken).ConfigureAwait(false);
        return executable;
    }

    /// <summary>Reuses a download from an earlier session so it does not have to be fetched again.</summary>
    private string? FindVerifiedDownload(ReleaseInfo release)
    {
        var directory = GetStagingDirectory(release);
        var executable = Path.Combine(directory, ExtractedDirectoryName, ExecutableName);
        return File.Exists(Path.Combine(directory, VerifiedMarkerName)) && File.Exists(executable) ? executable : null;
    }

    private string GetStagingDirectory(ReleaseInfo release) =>
        Path.Combine(environment.StagingDirectory, release.Version.ToString());

    private void RemoveObsoleteFiles()
    {
        installer.RemoveLeftovers();
        try
        {
            if (!Directory.Exists(environment.StagingDirectory))
            {
                return;
            }

            foreach (var directory in Directory.GetDirectories(environment.StagingDirectory))
            {
                if (!SemanticVersion.TryParse(Path.GetFileName(directory), out var version) || version <= InstalledVersion)
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            errorLog.Write("Removing old update downloads failed", exception);
        }
    }
}
