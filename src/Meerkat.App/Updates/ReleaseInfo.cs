namespace Meerkat.App.Updates;

public sealed record ReleaseInfo(
    SemanticVersion Version,
    string Notes,
    string PageUrl,
    string AssetName,
    string AssetUrl,
    long AssetSize,
    string ChecksumsUrl);

public interface IReleaseFeed
{
    /// <summary>Returns the newest published release that offers a matching download, or null if there is none.</summary>
    Task<ReleaseInfo?> GetLatestAsync(bool includePrerelease, bool selfContained, CancellationToken cancellationToken);

    Task DownloadAsync(string url, string destinationPath, IProgress<long> progress, CancellationToken cancellationToken);

    Task<string> GetTextAsync(string url, CancellationToken cancellationToken);
}
