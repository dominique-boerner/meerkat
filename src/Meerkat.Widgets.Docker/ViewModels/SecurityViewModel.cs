using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meerkat.Abstractions;
using Meerkat.Widgets.Docker.Security;
using Meerkat.Widgets.Docker.Settings;

namespace Meerkat.Widgets.Docker.ViewModels;

/// <summary>A vulnerability grave enough to be reported, counted once however many images it was found in.</summary>
public sealed record FindingViewModel(VulnerabilitySeverity Severity, string Package, bool HasFix, int ImageCount, DateTimeOffset FirstSeen)
{
    public bool IsCritical => Severity == VulnerabilitySeverity.Critical;
}

/// <summary>
/// The security tab of the Docker page: the images in use on every Docker system that is scanned, and
/// what is reported about them to the shell.
/// </summary>
public sealed partial class SecurityViewModel(
    DockerSettingsSource settingsSource,
    Func<DockerHost?, bool, SecuritySourceViewModel> sourceFactory,
    IOverlayCommands overlayCommands,
    TimeProvider timeProvider) : ObservableObject
{
    private const string StatusLabel = "Images";

    private bool _isStarted;

    /// <summary>The image whose vulnerabilities are listed below the table.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ImageRowViewModel? _selectedImage;

    [ObservableProperty]
    private string _selectionTitle = string.Empty;

    /// <summary>Says how many lesser vulnerabilities the list leaves out.</summary>
    [ObservableProperty]
    private string? _selectionHint;

    /// <summary>Scanner and times of the only system; with several, each section says it for itself.</summary>
    [ObservableProperty]
    private string _metaText = string.Empty;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _canScan;

    /// <summary>True while no system is scanned at all.</summary>
    [ObservableProperty]
    private bool _isSwitchedOff;

    /// <summary>True while something is reported; marks the tab.</summary>
    [ObservableProperty]
    private bool _hasAttention;

    /// <summary>Raised when <see cref="Status"/> or <see cref="Alerts"/> changed.</summary>
    public event EventHandler? StatusChanged;

    public ObservableCollection<SecuritySourceViewModel> Sources { get; } = [];

    public ObservableCollection<VulnerabilityRowViewModel> SelectedVulnerabilities { get; } = [];

    public bool HasSelection => SelectedImage is not null;

    /// <summary>A second entry in the status bar, next to the one for the containers; null while nothing was scanned.</summary>
    public WidgetStatus? Status { get; private set; }

    public IReadOnlyList<WidgetAlert> Alerts { get; private set; } = [];

    public void Start()
    {
        if (_isStarted)
        {
            return;
        }

        _isStarted = true;
        settingsSource.Changed += OnSettingsChanged;
        AddSources();
    }

    public async Task StopAsync()
    {
        if (!_isStarted)
        {
            return;
        }

        _isStarted = false;
        settingsSource.Changed -= OnSettingsChanged;
        await RemoveSourcesAsync();
    }

    [RelayCommand]
    private Task ScanNowAsync() => Task.WhenAll(Sources.ToList().Select(async source =>
    {
        try
        {
            await source.ScanAsync(onlyNew: false, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // The system was removed while it was being scanned.
        }
    }));

    [RelayCommand]
    private void SelectImage(ImageRowViewModel image)
    {
        // A second click on the selected image folds its list up again.
        SelectedImage = SelectedImage == image ? null : image;
        Update();
    }

    [RelayCommand]
    private void IgnoreRow(VulnerabilityRowViewModel row) => IgnoreVulnerability(row.Id, row.Package);

    [RelayCommand]
    private void OpenVulnerability(VulnerabilityRowViewModel row)
    {
        if (Uri.TryCreate(row.Vulnerability.Url, UriKind.Absolute, out var url))
        {
            overlayCommands.OpenUrl(url);
        }
    }

    [RelayCommand]
    private void OpenSettings() => overlayCommands.OpenSettings();

    private void IgnoreVulnerability(string id, string package)
    {
        var security = settingsSource.Current.Security;
        if (!security.IsIgnored(id))
        {
            security.Ignored.Add(new IgnoredVulnerability { Id = id, Package = package, IgnoredAt = timeProvider.GetLocalNow() });

            // Saved without announcing new settings, which would rebuild the systems and stop a running scan.
            settingsSource.Save();
        }

        foreach (var source in Sources)
        {
            source.ApplySettings();
        }
    }

    private void AddSources()
    {
        // Every system the containers page shows gets its section here too, in the same order, whether its
        // images are scanned or not: the two pages are two views of the same systems.
        var settings = settingsSource.Current;
        AddSource(sourceFactory(null, settings.Security.ScanLocalImages));
        foreach (var host in settings.RemoteHosts)
        {
            AddSource(sourceFactory(host, host.ScanImages));
        }

        foreach (var source in Sources)
        {
            source.ShowHeading = Sources.Count > 1;
        }

        Update();
    }

    private void AddSource(SecuritySourceViewModel source)
    {
        source.IsExpanded = !settingsSource.Current.CollapsedImageSources.Contains(source.Id);
        source.Changed += OnSourceChanged;
        source.PropertyChanged += OnSourcePropertyChanged;
        Sources.Add(source);
        source.Start();
    }

    private void OnSourcePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not SecuritySourceViewModel source || e.PropertyName != nameof(SecuritySourceViewModel.IsExpanded))
        {
            return;
        }

        var collapsed = settingsSource.Current.CollapsedImageSources;
        if (collapsed.Contains(source.Id) == !source.IsExpanded)
        {
            return;
        }

        if (source.IsExpanded)
        {
            collapsed.Remove(source.Id);
        }
        else
        {
            collapsed.Add(source.Id);
        }

        // Saved without announcing new settings, which would rebuild the systems and stop a running scan.
        settingsSource.Save();
    }

    private async Task RemoveSourcesAsync()
    {
        var removed = Sources.ToList();
        Sources.Clear();
        SelectedImage = null;
        foreach (var source in removed)
        {
            source.Changed -= OnSourceChanged;
            source.PropertyChanged -= OnSourcePropertyChanged;
        }

        await Task.WhenAll(removed.Select(source => source.StopAsync()));
    }

    private async void OnSettingsChanged(object? sender, EventArgs e)
    {
        // Systems may have been added or removed and every choice about scanning may be another one.
        await RemoveSourcesAsync();
        if (_isStarted)
        {
            AddSources();
        }
    }

    private void OnSourceChanged(object? sender, EventArgs e) => Update();

    /// <summary>Derives everything the tab and the shell show from the images of the systems.</summary>
    private void Update()
    {
        var images = Sources.Where(source => source.State == ScannerState.Ready).SelectMany(source => source.Images).ToList();
        if (SelectedImage is { } selected && !images.Contains(selected))
        {
            SelectedImage = null;
        }

        foreach (var image in Sources.SelectMany(source => source.Images))
        {
            image.IsSelected = image == SelectedImage;
        }

        foreach (var source in Sources)
        {
            source.RefreshSummary();
        }

        UpdateSelection();

        var findings = BuildFindings();
        HasAttention = findings.Count > 0;
        IsScanning = Sources.Any(source => source.IsScanning);
        CanScan = Sources.Any(source => source.State == ScannerState.Ready) && !IsScanning;
        MetaText = Sources.Count == 1 ? Sources[0].MetaText : string.Empty;
        IsSwitchedOff = Sources.All(source => source.State == ScannerState.Off);

        UpdateStatus(images, findings);
    }

    private void UpdateSelection()
    {
        if (SelectedImage is not { } image)
        {
            SelectedVulnerabilities.Clear();
            SelectionHint = null;
            return;
        }

        // The list answers "what must I do"; the many lesser findings would bury that.
        var grave = image.Shown.Where(vulnerability => vulnerability.Severity >= VulnerabilitySeverity.High).ToList();
        Replace(SelectedVulnerabilities, grave.Select(vulnerability => new VulnerabilityRowViewModel(vulnerability)));

        var filter = settingsSource.Current.Security.OnlyFixable ? " · ONLY VULNERABILITIES WITH A FIX" : string.Empty;
        SelectionTitle = $"{image.Name.ToUpperInvariant()}{filter}";

        var lesser = image.Shown.Count - grave.Count;
        SelectionHint = image.ScanError is { } error ? $"The last scan failed: {error}"
            : !image.IsScanned ? "This image has not been scanned yet."
            : (grave.Count, lesser) switch
            {
                (0, 0) => "Nothing found.",
                (0, _) => $"Nothing critical or high. {lesser} of medium or low severity not listed.",
                (_, 0) => null,
                _ => $"{lesser} more of medium or low severity not listed.",
            };
    }

    /// <summary>The vulnerabilities grave enough to be reported, each once however many images it is in.</summary>
    private List<FindingViewModel> BuildFindings()
    {
        var reported =
            from source in Sources
            where source.State == ScannerState.Ready
            from image in source.Images
            from vulnerability in image.Reported
            select (Source: source, Image: image, Vulnerability: vulnerability);

        return [.. reported
            .GroupBy(entry => entry.Vulnerability.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var entries = group.ToList();
                return new FindingViewModel(
                    entries.Max(entry => entry.Vulnerability.Severity),
                    Join([.. entries.Select(entry => entry.Vulnerability.Package).Distinct()]),
                    entries.Any(entry => entry.Vulnerability.HasFix),
                    entries.Select(entry => (entry.Source.Id, entry.Image.Id)).Distinct().Count(),
                    entries.Min(entry => entry.Source.GetFirstSeen(group.Key)));
            })
            .OrderByDescending(finding => finding.Severity)
            .ThenByDescending(finding => finding.FirstSeen)];
    }
    private void UpdateStatus(IReadOnlyList<ImageRowViewModel> images, IReadOnlyList<FindingViewModel> findings)
    {
        var scanned = images.Where(image => image.IsScanned).ToList();
        var severity = findings.Count == 0 ? WidgetSeverity.Ok
            : findings.Any(finding => finding.IsCritical) ? WidgetSeverity.Error
            : WidgetSeverity.Warning;

        Status = scanned.Count == 0
            ? null
            : new WidgetStatus(StatusLabel, $"{scanned.Count(image => image.Reported.Count == 0)}/{images.Count}", severity);

        var alerts = new List<WidgetAlert>();
        if (findings.Count > 0)
        {
            var gravest = findings[0].Severity;
            var worst = findings.Where(finding => finding.Severity == gravest).ToList();
            var severityText = gravest.ToString().ToLowerInvariant();
            var imageCount = worst.Count == 1
                ? worst[0].ImageCount
                : images.Count(image => image.Reported.Any(vulnerability => vulnerability.Severity == gravest));
            var imagesText = imageCount == 1 ? "1 image" : $"{imageCount} images";
            var fixText = worst.All(finding => finding.HasFix) ? (worst.Count == 1 ? "fix available" : "fixes available")
                : worst.Any(finding => finding.HasFix) ? "some fixes available"
                : "no fix yet";

            alerts.Add(new WidgetAlert(
                severity,
                worst.Count == 1
                    ? $"{char.ToUpperInvariant(severityText[0])}{severityText[1..]} vulnerability in {imagesText}"
                    : $"{worst.Count} {severityText} vulnerabilities in {imagesText}",
                $"{Join([.. worst.Select(finding => finding.Package).Distinct()])} · {fixText}",
                worst.Max(finding => finding.FirstSeen),

                // A grave vulnerability is news even when it is not critical: the user asked to be told.
                Prominent: true,

                // A shield: this is about security, not about whether Docker runs.
                Icon: "\uEA18"));
        }

        alerts.AddRange(Sources
            .Where(source => source is { State: ScannerState.Ready, StaleDatabaseText: not null })
            .Select(source => new WidgetAlert(
                WidgetSeverity.Warning,
                source.StaleDatabaseText!,
                Sources.Count > 1 ? $"Results may be incomplete · {source.Label}" : "Results may be incomplete",
                source.Scanner?.DatabaseUpdatedAt ?? timeProvider.GetLocalNow())));

        Alerts = alerts;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"a", "a and b", "a, b and c", and beyond that "a, b and 3 more".</summary>
    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        3 => $"{items[0]}, {items[1]} and {items[2]}",
        _ => $"{items[0]}, {items[1]} and {items.Count - 2} more",
    };

    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        var updated = items.ToList();

        // Leaving an unchanged list alone keeps what is shown from flickering on every update.
        if (collection.SequenceEqual(updated))
        {
            return;
        }

        collection.Clear();
        foreach (var item in updated)
        {
            collection.Add(item);
        }
    }
}
