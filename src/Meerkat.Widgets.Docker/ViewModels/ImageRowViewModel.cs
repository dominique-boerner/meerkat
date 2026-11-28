using CommunityToolkit.Mvvm.ComponentModel;
using Meerkat.Widgets.Docker.Security;
using Meerkat.Widgets.Docker.Settings;

namespace Meerkat.Widgets.Docker.ViewModels;

internal static class AgeFormatter
{
    public static string Format(TimeSpan age) => age switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalHours: < 1 } => $"{(int)age.TotalMinutes} min ago",
        { TotalDays: < 1 } => $"{(int)age.TotalHours} h ago",
        _ => $"{(int)age.TotalDays} d ago",
    };
}

/// <summary>One vulnerability of the image the user selected.</summary>
public sealed record VulnerabilityRowViewModel(Vulnerability Vulnerability)
{
    public string Id => Vulnerability.Id;

    public string SeverityText => Vulnerability.Severity.ToString().ToLowerInvariant();

    public bool IsCritical => Vulnerability.Severity == VulnerabilitySeverity.Critical;

    public string Package => Vulnerability.Package;

    public string InstalledVersion => Vulnerability.InstalledVersion;

    public string FixedVersion => Vulnerability.FixedVersion ?? "–";

    public string? Title => Vulnerability.Title;
}

/// <summary>An image in use and what the last scan found in it.</summary>
public sealed partial class ImageRowViewModel(ImageDetails details) : ObservableObject
{
    private const string NoValue = "·";

    private StoredImageScan? _scan;

    [ObservableProperty]
    private string _name = details.Name;

    [ObservableProperty]
    private string? _subtitle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCritical))]
    private int _criticalCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHigh))]
    private int _highCount;

    /// <summary>When the image was scanned, or that it is being scanned right now.</summary>
    [ObservableProperty]
    private string _scanText = NoValue;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isScanned;

    /// <summary>Why the last scan of this image failed; null after a scan that worked.</summary>
    [ObservableProperty]
    private string? _scanError;

    [ObservableProperty]
    private bool _isSelected;

    public ImageDetails Details { get; private set; } = details;

    public string Id => Details.Id;

    public bool HasCritical => CriticalCount > 0;

    public bool HasHigh => HighCount > 0;

    /// <summary>The vulnerabilities the user wants to see, gravest first.</summary>
    public IReadOnlyList<Vulnerability> Shown { get; private set; } = [];

    /// <summary>Those of <see cref="Shown"/> that are grave enough to be reported.</summary>
    public IReadOnlyList<Vulnerability> Reported { get; private set; } = [];

    public void Update(ImageDetails details)
    {
        Details = details;
        Name = details.Name;
    }

    public void Apply(StoredImageScan? scan, DockerSecuritySettings settings, DateTimeOffset now)
    {
        _scan = scan;
        if (scan is not null)
        {
            ScanError = null;
        }

        IsScanned = scan is not null;
        Shown = [.. (scan?.Vulnerabilities ?? [])
            .Where(settings.IsShown)
            .OrderByDescending(vulnerability => vulnerability.Severity)
            .ThenBy(vulnerability => vulnerability.Package, StringComparer.OrdinalIgnoreCase)];
        Reported = [.. Shown.Where(settings.IsReported)];
        CriticalCount = Shown.Count(vulnerability => vulnerability.Severity == VulnerabilitySeverity.Critical);
        HighCount = Shown.Count(vulnerability => vulnerability.Severity == VulnerabilitySeverity.High);
        Subtitle = DescribeOrigin();
        UpdateTimes(now);
    }

    public void UpdateTimes(DateTimeOffset now) =>
        ScanText = IsScanning ? "running"
            : ScanError is not null ? "failed"
            : _scan is null ? NoValue
            : AgeFormatter.Format(now - _scan.ScannedAt);

    /// <summary>Says where a fix has to come from: from a rebuild of an own image, or from whoever publishes the image.</summary>
    private string? DescribeOrigin()
    {
        if (Details.IsOwnBuild)
        {
            return Details.BaseImage is { } baseImage ? $"own image · base {baseImage}" : "own image";
        }

        if (Details.BaseImage is { } builtOn)
        {
            return $"base {builtOn}";
        }

        return Shown.Any(vulnerability => vulnerability.HasFix && vulnerability.Severity >= VulnerabilitySeverity.High)
            ? "a newer image may carry the fixes · pull"
            : null;
    }

    partial void OnIsScanningChanged(bool value)
    {
        if (value)
        {
            ScanText = "running";
        }
    }
}
