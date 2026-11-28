using System.Text.Json.Serialization;

namespace Meerkat.Widgets.Docker.Security;

/// <summary>Ordered from harmless to grave, so that severities can be compared.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VulnerabilitySeverity>))]
public enum VulnerabilitySeverity
{
    Unknown,
    Low,
    Medium,
    High,
    Critical,
}

/// <param name="FixedVersion">The version of the package that closes the hole; null while there is none.</param>
/// <param name="LayerId">The layer of the image that brought the package in; tells whether a base image is the cause.</param>
public sealed record Vulnerability(
    string Id,
    VulnerabilitySeverity Severity,
    string Package,
    string InstalledVersion,
    string? FixedVersion,
    string? Title,
    string? Url,
    string? LayerId)
{
    [JsonIgnore]
    public bool HasFix => !string.IsNullOrWhiteSpace(FixedVersion);
}

/// <summary>An image that at least one container of a Docker system was created from.</summary>
/// <param name="Id">The full image id, "sha256:" followed by 64 hex digits.</param>
/// <param name="Name">Repository and tag, or a short form of the id for an image without a tag.</param>
/// <param name="IsOwnBuild">True for an image known to be built on the system; false where Docker does not tell.</param>
/// <param name="BaseImage">The image this one was built on, as far as it can be told.</param>
/// <param name="BaseLayers">The layers that came with <paramref name="BaseImage"/>.</param>
public sealed record ImageDetails(
    string Id,
    string Name,
    bool IsOwnBuild,
    string? BaseImage,
    IReadOnlySet<string> BaseLayers);

/// <param name="DatabaseUpdatedAt">When the vulnerability database was built; null before the first download.</param>
public sealed record ScannerInfo(string Name, string Version, DateTimeOffset? DatabaseUpdatedAt);

/// <summary>The scanner could not do what was asked; <see cref="Detail"/> carries what it printed.</summary>
public sealed class ScannerException(string message, string detail) : Exception(message)
{
    public string Detail { get; } = detail;
}

/// <summary>The machine Docker runs on could not be reached at all.</summary>
public sealed class DockerShellException(string message) : Exception(message);

public interface IImageScanner
{
    /// <summary>Returns null when no scanner is installed on the system.</summary>
    Task<ScannerInfo?> DetectAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Vulnerability>> ScanAsync(string imageId, CancellationToken cancellationToken);

    Task UpdateDatabaseAsync(CancellationToken cancellationToken);

    /// <summary>Installs the scanner on the system for the user Meerkat works as, without root rights.</summary>
    Task<ScannerInfo> InstallAsync(CancellationToken cancellationToken);
}

public interface IImageInventory
{
    /// <summary>The images the containers of the system were created from.</summary>
    Task<IReadOnlyList<ImageDetails>> ListAsync(CancellationToken cancellationToken);
}
