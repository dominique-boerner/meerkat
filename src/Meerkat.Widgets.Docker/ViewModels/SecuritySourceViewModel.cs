using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meerkat.Abstractions;
using Meerkat.Widgets.Docker.Security;
using Meerkat.Widgets.Docker.Settings;

namespace Meerkat.Widgets.Docker.ViewModels;

public enum ScannerState
{
    /// <summary>Not asked yet.</summary>
    Checking,

    /// <summary>The system answers, but has no scanner.</summary>
    Missing,
    Ready,

    /// <summary>The system or its Docker could not be asked.</summary>
    Failed,
}

/// <param name="Id">The same id the containers of the system are kept under, <see cref="ContainerSourceOptions.LocalId"/> for this computer.</param>
/// <param name="Label">The WSL distribution or the host; read anew when shown, because settings may change.</param>
public sealed record SecuritySourceOptions(string Id, Func<string> Label, bool IsRemote);

/// <summary>The images of one Docker system and what the scanner found in them.</summary>
public sealed partial class SecuritySourceViewModel(
    IImageScanner scanner,
    IImageInventory inventory,
    IScanStore store,
    DockerSettingsSource settingsSource,
    SecuritySourceOptions options,
    IOverlayState overlayState,
    TimeProvider timeProvider,
    Func<bool>? isOnBattery = null) : ObservableObject
{
    private static readonly TimeSpan LoopInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    // After a failure the system is left alone for a while instead of being asked again every minute.
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StaleDatabaseAge = TimeSpan.FromDays(3);

    // One piece of work at a time: a scan started by hand must not run into the one the schedule started.
    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly StoredScans _scans = store.Load(options.Id);

    private CancellationTokenSource? _cancellation;
    private Task _loop = Task.CompletedTask;
    private DateTimeOffset? _lastRefresh;
    private DateTimeOffset? _retryAfter;

    [ObservableProperty]
    private ScannerState _state = ScannerState.Checking;

    [ObservableProperty]
    private string _label = options.Label();

    /// <summary>Set while several systems share the page, which then needs a heading per system.</summary>
    [ObservableProperty]
    private bool _showHeading;

    /// <summary>Scanner, age of its database and time of the last scan, in one line.</summary>
    [ObservableProperty]
    private string _metaText = string.Empty;

    /// <summary>Set when the database is too old to trust that nothing is missed.</summary>
    [ObservableProperty]
    private string? _staleDatabaseText;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _errorDetail;

    /// <summary>Raised when the images or what is known about them changed.</summary>
    public event EventHandler? Changed;

    public ObservableCollection<ImageRowViewModel> Images { get; } = [];

    public string Id => options.Id;

    public bool IsRemote => options.IsRemote;

    public ScannerInfo? Scanner { get; private set; }

    public string InstallDescription => TrivyScanner.InstallDescription;

    /// <summary>When a vulnerability was first found on this system; now, should it be unknown.</summary>
    public DateTimeOffset GetFirstSeen(string vulnerabilityId) =>
        _scans.FirstSeen.TryGetValue(vulnerabilityId, out var firstSeen) ? firstSeen : timeProvider.GetLocalNow();

    public void Start()
    {
        if (_cancellation is not null)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        _loop = RunLoopAsync(_cancellation.Token);
    }

    public async Task StopAsync()
    {
        if (_cancellation is null)
        {
            return;
        }

        await _cancellation.CancelAsync();
        await _loop;
        _cancellation.Dispose();
        _cancellation = null;
    }

    /// <summary>Does what is due right now: looking for the scanner and new images, and scanning on schedule.</summary>
    public async Task RunDueWorkAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetLocalNow();
        if (_retryAfter is { } retryAfter && now < retryAfter)
        {
            UpdateTimes();
            return;
        }

        if (_lastRefresh is not { } lastRefresh || now - lastRefresh >= RefreshInterval)
        {
            await RefreshAsync(cancellationToken);
        }

        UpdateTimes();
        if (State != ScannerState.Ready)
        {
            return;
        }

        // Scanning is hard work for the own machine; another machine has its own power supply.
        var security = settingsSource.Current.Security;
        if (!options.IsRemote && security.SkipOnBattery && isOnBattery?.Invoke() == true)
        {
            return;
        }

        if (IsDailyScanDue(now, _scans.LastFullScan, security.EffectiveDailyScanTime))
        {
            await ScanAsync(onlyNew: false, cancellationToken);
        }
        else if (Images.Any(image => image is { IsScanned: false, ScanError: null }))
        {
            await ScanAsync(onlyNew: true, cancellationToken);
        }
    }

    /// <summary>Whether the time of the daily scan has passed since all images were last scanned.</summary>
    public static bool IsDailyScanDue(DateTimeOffset now, DateTimeOffset? lastFullScan, TimeOnly timeOfDay)
    {
        if (lastFullScan is not { } last)
        {
            return true;
        }

        var today = new DateTimeOffset(now.Date + timeOfDay.ToTimeSpan(), now.Offset);
        var latestScheduled = now >= today ? today : today.AddDays(-1);
        return last < latestScheduled;
    }

    /// <summary>Looks for the scanner and for the images in use, and shows what is known about them.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _work.WaitAsync(cancellationToken);
        try
        {
            await RefreshCoreAsync(cancellationToken);
        }
        finally
        {
            _work.Release();
        }
    }

    /// <param name="onlyNew">Scans only images without a result; otherwise all of them.</param>
    public async Task ScanAsync(bool onlyNew, CancellationToken cancellationToken)
    {
        await _work.WaitAsync(cancellationToken);
        IsScanning = true;
        try
        {
            if (State != ScannerState.Ready || !onlyNew)
            {
                // A scan asked for by hand should see the images as they are now, not as of minutes ago.
                await RefreshCoreAsync(cancellationToken);
            }

            if (State != ScannerState.Ready)
            {
                return;
            }

            var reachedAll = true;
            foreach (var image in Images.Where(image => !onlyNew || !image.IsScanned).ToList())
            {
                if (!await ScanImageAsync(image, cancellationToken))
                {
                    reachedAll = false;
                    break;
                }
            }

            if (reachedAll && !onlyNew)
            {
                _scans.LastFullScan = timeProvider.GetLocalNow();
            }

            Persist();

            // The scan may have fetched a newer database.
            if (reachedAll)
            {
                Scanner = await TryDetectAsync(cancellationToken) ?? Scanner;
            }
        }
        finally
        {
            IsScanning = false;
            _work.Release();
            UpdateTimes();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Applies the user's choices about what to show to results that are already there.</summary>
    public void ApplySettings()
    {
        var now = timeProvider.GetLocalNow();
        foreach (var image in Images)
        {
            image.Apply(_scans.Images.GetValueOrDefault(image.Id), settingsSource.Current.Security, now);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        var cancellationToken = _cancellation?.Token ?? CancellationToken.None;
        IsInstalling = true;
        try
        {
            await _work.WaitAsync(cancellationToken);
            try
            {
                Scanner = await scanner.InstallAsync(cancellationToken);
                SetError(null);
            }
            finally
            {
                _work.Release();
            }

            _retryAfter = null;
            await RefreshAsync(cancellationToken);
            Changed?.Invoke(this, EventArgs.Empty);

            // The first scan follows at once: whoever installs a scanner wants to see what it finds.
            await ScanAsync(onlyNew: false, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Widget was stopped during the installation.
        }
        catch (Exception exception) when (exception is ScannerException or DockerShellException)
        {
            SetError(exception.Message, (exception as ScannerException)?.Detail);
        }
        finally
        {
            IsInstalling = false;
        }
    }

    [RelayCommand]
    private async Task UpdateDatabaseAsync()
    {
        var cancellationToken = _cancellation?.Token ?? CancellationToken.None;
        try
        {
            await _work.WaitAsync(cancellationToken);
            IsScanning = true;
            try
            {
                await scanner.UpdateDatabaseAsync(cancellationToken);
                Scanner = await scanner.DetectAsync(cancellationToken) ?? Scanner;
                SetError(null);
            }
            finally
            {
                IsScanning = false;
                _work.Release();
            }

            // Results found with the old database are worth no more than the database was.
            await ScanAsync(onlyNew: false, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Widget was stopped during the update.
        }
        catch (Exception exception) when (exception is ScannerException or DockerShellException)
        {
            SetError(exception.Message, (exception as ScannerException)?.Detail);
            UpdateTimes();
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await WaitUntilVisibleAsync(cancellationToken);
                await RunDueWorkAsync(cancellationToken);
                await Task.Delay(LoopInterval, timeProvider, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Widget was stopped or the systems were reloaded.
        }
    }

    private async Task WaitUntilVisibleAsync(CancellationToken cancellationToken)
    {
        while (!overlayState.IsVisible)
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged(object? sender, EventArgs e) => changed.TrySetResult();

            overlayState.Changed += OnChanged;
            try
            {
                // Re-check after subscribing so a change between the loop condition and the subscription is not missed.
                if (!overlayState.IsVisible)
                {
                    await changed.Task.WaitAsync(cancellationToken);
                }
            }
            finally
            {
                overlayState.Changed -= OnChanged;
            }
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetLocalNow();
        _lastRefresh = now;
        Label = options.Label();

        try
        {
            Scanner = await scanner.DetectAsync(cancellationToken);
            if (Scanner is null)
            {
                Images.Clear();
                State = ScannerState.Missing;
                SetError(null);
                return;
            }

            var images = await inventory.ListAsync(cancellationToken);
            var existing = Images.ToDictionary(row => row.Id);
            for (var index = 0; index < images.Count; index++)
            {
                if (existing.Remove(images[index].Id, out var row))
                {
                    row.Update(images[index]);
                    var currentIndex = Images.IndexOf(row);
                    if (currentIndex != index)
                    {
                        Images.Move(currentIndex, index);
                    }
                }
                else
                {
                    row = new ImageRowViewModel(images[index]);
                    Images.Insert(index, row);
                }

                row.Apply(_scans.Images.GetValueOrDefault(row.Id), settingsSource.Current.Security, now);
            }

            foreach (var removed in existing.Values)
            {
                Images.Remove(removed);
            }

            State = ScannerState.Ready;
            _retryAfter = null;
            SetError(null);
        }
        catch (Exception exception) when (exception is ScannerException or DockerShellException)
        {
            State = ScannerState.Failed;
            _retryAfter = now + RetryInterval;
            SetError(exception.Message, (exception as ScannerException)?.Detail);
        }
        finally
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Returns false when the system itself stopped answering, which ends the scan.</summary>
    private async Task<bool> ScanImageAsync(ImageRowViewModel image, CancellationToken cancellationToken)
    {
        image.IsScanning = true;
        try
        {
            var vulnerabilities = await scanner.ScanAsync(image.Id, cancellationToken);
            var now = timeProvider.GetLocalNow();
            _scans.Images[image.Id] = new StoredImageScan { ScannedAt = now, Vulnerabilities = [.. vulnerabilities] };
            foreach (var vulnerability in vulnerabilities)
            {
                _scans.FirstSeen.TryAdd(vulnerability.Id, now);
            }

            image.IsScanning = false;
            image.Apply(_scans.Images[image.Id], settingsSource.Current.Security, now);
            return true;
        }
        catch (ScannerException exception)
        {
            // One image that cannot be scanned must not keep the others from being scanned.
            image.IsScanning = false;
            image.ScanError = exception.Detail;
            image.UpdateTimes(timeProvider.GetLocalNow());
            return true;
        }
        catch (DockerShellException exception)
        {
            image.IsScanning = false;
            image.UpdateTimes(timeProvider.GetLocalNow());
            _retryAfter = timeProvider.GetLocalNow() + RetryInterval;
            SetError(exception.Message);
            return false;
        }
        finally
        {
            image.IsScanning = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<ScannerInfo?> TryDetectAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await scanner.DetectAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is ScannerException or DockerShellException)
        {
            return null;
        }
    }

    /// <summary>Forgets what is no longer of use, so the file does not grow with every image that ever ran.</summary>
    private void Persist()
    {
        var inUse = Images.Select(image => image.Id).ToHashSet();
        foreach (var id in _scans.Images.Keys.Where(id => !inUse.Contains(id)).ToList())
        {
            _scans.Images.Remove(id);
        }

        var found = _scans.Images.Values.SelectMany(scan => scan.Vulnerabilities).Select(vulnerability => vulnerability.Id).ToHashSet();
        foreach (var id in _scans.FirstSeen.Keys.Where(id => !found.Contains(id)).ToList())
        {
            _scans.FirstSeen.Remove(id);
        }

        store.Save(options.Id, _scans);
    }

    private void UpdateTimes()
    {
        var now = timeProvider.GetLocalNow();
        foreach (var image in Images)
        {
            image.UpdateTimes(now);
        }

        if (Scanner is not { } info)
        {
            MetaText = string.Empty;
            StaleDatabaseText = null;
            return;
        }

        var parts = new List<string> { $"{info.Name} {info.Version}".Trim() };
        if (info.DatabaseUpdatedAt is { } updatedAt)
        {
            parts.Add($"database {AgeFormatter.Format(now - updatedAt)}");
        }

        var lastScan = _scans.Images.Count > 0 ? _scans.Images.Values.Max(scan => scan.ScannedAt) : (DateTimeOffset?)null;
        if (lastScan is { } scannedAt)
        {
            parts.Add($"last scan {AgeFormatter.Format(now - scannedAt)}");
        }

        MetaText = string.Join(" · ", parts);
        StaleDatabaseText = info.DatabaseUpdatedAt is { } built && now - built >= StaleDatabaseAge
            ? $"Vulnerability database is {(int)(now - built).TotalDays} days old"
            : null;
    }

    partial void OnIsScanningChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnMetaTextChanged(string value) => Changed?.Invoke(this, EventArgs.Empty);

    private void SetError(string? message, string? detail = null)
    {
        ErrorMessage = message;
        ErrorDetail = detail;
    }
}
