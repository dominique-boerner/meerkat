using System.Text.Json;

namespace Meerkat.App.Updates;

public static class GitHubReleaseParser
{
    public const string Repository = "dominique-boerner/meerkat";
    public const string ChecksumsFileName = "SHA256SUMS.txt";

    // Downloads are only accepted from the project's own release storage.
    private const string TrustedDownloadPrefix = "https://github.com/" + Repository + "/releases/download/";

    /// <summary>Picks the highest version from the JSON returned by the GitHub "list releases" endpoint.</summary>
    public static ReleaseInfo? SelectLatest(string releasesJson, bool includePrerelease, bool selfContained)
    {
        using var document = JsonDocument.Parse(releasesJson);
        return document.RootElement.EnumerateArray()
            .Select(release => ToReleaseInfo(release, includePrerelease, selfContained))
            .OfType<ReleaseInfo>()
            .OrderByDescending(release => release.Version)
            .FirstOrDefault();
    }

    public static string GetAssetName(SemanticVersion version, bool selfContained) =>
        $"Meerkat-{version}-win-x64{(selfContained ? "-self-contained" : string.Empty)}.zip";

    private static ReleaseInfo? ToReleaseInfo(JsonElement release, bool includePrerelease, bool selfContained)
    {
        if (GetBoolean(release, "draft")
            || (GetBoolean(release, "prerelease") && !includePrerelease)
            || !SemanticVersion.TryParse(GetString(release, "tag_name"), out var version)
            || (version.IsPrerelease && !includePrerelease)
            || !release.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var assetName = GetAssetName(version, selfContained);
        var asset = FindAsset(assets, assetName);
        var checksums = FindAsset(assets, ChecksumsFileName);
        if (asset is not { } archive || checksums is not { } checksumFile)
        {
            return null;
        }

        return new ReleaseInfo(
            version,
            GetString(release, "body"),
            GetString(release, "html_url"),
            assetName,
            GetString(archive, "browser_download_url"),
            archive.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
            GetString(checksumFile, "browser_download_url"));
    }

    private static JsonElement? FindAsset(JsonElement assets, string name)
    {
        foreach (var asset in assets.EnumerateArray())
        {
            if (GetString(asset, "name") == name
                && GetString(asset, "browser_download_url").StartsWith(TrustedDownloadPrefix, StringComparison.Ordinal))
            {
                return asset;
            }
        }

        return null;
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool GetBoolean(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}
