using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meerkat.App.Settings;

namespace Meerkat.App.Updates;

/// <summary>Presents the update state to the overlay card, the settings page and the tray menu.</summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private const int MaxHighlights = 3;
    private const double BytesPerMegabyte = 1024 * 1024;

    private readonly UpdateService _service;
    private readonly SettingsService _settings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCardVisible))]
    private bool _isCardDismissed;

    public UpdateViewModel(UpdateService service, SettingsService settings)
    {
        _service = service;
        _settings = settings;
        _service.PropertyChanged += (_, _) => Refresh();
    }

    public bool IsSupported => _service.State != UpdateState.Unsupported;

    public bool IsAvailable => _service.State == UpdateState.Available;

    public bool IsDownloading => _service.State == UpdateState.Downloading;

    public bool IsReady => _service.State == UpdateState.Ready;

    /// <summary>True while a newer version is known, whether downloaded or not.</summary>
    public bool HasUpdate => _service.State is UpdateState.Available or UpdateState.Downloading or UpdateState.Ready;

    public bool IsCardVisible => IsReady && !IsCardDismissed && !_service.InstallOnExit;

    /// <summary>The install action is shown while downloading too, disabled, so the layout does not jump.</summary>
    public bool IsInstallActionVisible => IsDownloading || IsReady;

    public bool CanDownload => _service.Release is not null && _service.State is UpdateState.Available or UpdateState.Failed;

    public bool CanCheck => _service.State is UpdateState.Idle or UpdateState.Failed;

    public string Headline => _service.State switch
    {
        UpdateState.Unsupported => "Updates are unavailable in this build",
        UpdateState.Checking => "Checking for updates …",
        UpdateState.Available or UpdateState.Downloading => $"Version {VersionText} is available",
        UpdateState.Ready => $"Version {VersionText} is ready to install",
        UpdateState.Failed => "Update failed",
        _ => "Meerkat is up to date",
    };

    public string Subtitle => $"Installed: {_service.InstalledVersion} · Channel: {ChannelText}";

    public string StatusText => _service.State switch
    {
        UpdateState.Unsupported => "Only the published single-file releases can update themselves.",
        UpdateState.Downloading => "Downloading …",
        UpdateState.Ready when _service.InstallOnExit => "Will be installed when Meerkat exits.",
        UpdateState.Failed => _service.ErrorMessage ?? string.Empty,
        _ => string.Empty,
    };

    public bool HasStatus => StatusText.Length > 0;

    public string ProgressText
    {
        get
        {
            var culture = CultureInfo.CurrentCulture;
            var total = _service.Release?.AssetSize ?? 0;
            return $"{(_service.DownloadedBytes / BytesPerMegabyte).ToString("0.0", culture)}"
                + $" / {(total / BytesPerMegabyte).ToString("0.0", culture)} MB";
        }
    }

    public double ProgressPercent =>
        _service.Release is { AssetSize: > 0 } release
            ? Math.Clamp(100.0 * _service.DownloadedBytes / release.AssetSize, 0, 100)
            : 0;

    public string CardTitle => $"Meerkat {VersionText} is ready";

    public string CardSubtitle => $"Installed: {_service.InstalledVersion} · downloaded";

    public IReadOnlyList<string> Highlights =>
        ReleaseNotesFormatter.GetHighlights(_service.Release?.Notes ?? string.Empty, MaxHighlights);

    public string ReleaseNotesTitle => $"What's new in {VersionText}";

    public string ReleaseNotes
    {
        get
        {
            var notes = ReleaseNotesFormatter.ToPlainText(_service.Release?.Notes ?? string.Empty);
            return notes.Length > 0 ? notes : "No release notes were provided for this version.";
        }
    }

    /// <summary>Short status for places that only have room for a hint; null when there is nothing to report.</summary>
    public string? Notice => _service.State switch
    {
        UpdateState.Ready => $"Update {VersionText} ready",
        UpdateState.Available or UpdateState.Downloading => $"Update {VersionText} available",
        _ => null,
    };

    public string? TrayActionText => _service.State switch
    {
        UpdateState.Ready => $"Install update {VersionText}",
        UpdateState.Available => $"Download update {VersionText}",
        UpdateState.Downloading => $"Downloading update {VersionText} …",
        _ => null,
    };

    private string VersionText => _service.Release?.Version.ToString() ?? string.Empty;

    private string ChannelText => _settings.Current.Updates.Channel == UpdateChannel.Prerelease ? "Pre-release" : "Stable";

    /// <summary>Runs whatever the tray entry currently offers.</summary>
    public void RunTrayAction()
    {
        if (IsReady)
        {
            _service.RestartAndInstall();
        }
        else if (IsAvailable)
        {
            _ = _service.DownloadAsync();
        }
    }

    public void Refresh()
    {
        // Every text here derives from the service state, so all bindings are refreshed together.
        OnPropertyChanged(string.Empty);
        CheckNowCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        CancelDownloadCommand.NotifyCanExecuteChanged();
        RestartAndInstallCommand.NotifyCanExecuteChanged();
        InstallOnExitCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckNowAsync() => _service.CheckAsync(userInitiated: true);

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private Task DownloadAsync() => _service.DownloadAsync();

    [RelayCommand(CanExecute = nameof(IsDownloading))]
    private void CancelDownload() => _service.CancelDownload();

    [RelayCommand(CanExecute = nameof(IsReady))]
    private void RestartAndInstall() => _service.RestartAndInstall();

    [RelayCommand(CanExecute = nameof(IsReady))]
    private void InstallOnExit()
    {
        _service.InstallOnExit = true;
        IsCardDismissed = true;
    }

    [RelayCommand]
    private void DismissCard() => IsCardDismissed = true;

    [RelayCommand]
    private void OpenReleaseNotes()
    {
        // Only ever hand a web address to the shell, never an arbitrary string from the release data.
        if (_service.Release?.PageUrl is { } url && url.StartsWith("https://github.com/", StringComparison.Ordinal))
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }
}
