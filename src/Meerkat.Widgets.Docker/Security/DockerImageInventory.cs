using System.Text.Json;

namespace Meerkat.Widgets.Docker.Security;

public sealed class DockerImageInventory(IDockerShell shell) : IImageInventory
{
    private const string Marker = "___MEERKAT_IMAGES___";

    // One script, because every call to another machine opens a connection: first the ids of the images
    // in use, then everything Docker knows about all images, which is what base images are found in.
    private const string Script = $"""
        set -e
        ids=$(docker ps -aq)
        if [ -n "$ids" ]; then docker inspect --format '{"{{.Image}}"}' $ids | sort -u; fi
        echo {Marker}
        all=$(docker image ls -q --no-trunc | sort -u)
        if [ -n "$all" ]; then docker image inspect $all; else echo '[]'; fi
        """;

    public async Task<IReadOnlyList<ImageDetails>> ListAsync(CancellationToken cancellationToken)
    {
        var result = await shell.RunAsync(Script, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var detail = result.Error.Trim() is { Length: > 0 } error ? error : $"exit code {result.ExitCode}";
            throw new ScannerException("The images could not be listed.", detail);
        }

        try
        {
            return DockerImageParser.Parse(result.Output, Marker);
        }
        catch (JsonException exception)
        {
            throw new ScannerException("Docker returned an image list that could not be read.", exception.Message);
        }
    }
}

public static class DockerImageParser
{
    private const string BaseNameLabel = "org.opencontainers.image.base.name";
    private const int ShortIdLength = 12;

    /// <param name="IsOwnBuild">
    /// True where Docker leaves no doubt that the image was built here: it has no digest of a registry, or it
    /// names the image it was built from. Docker setups differ in what they record, so false means "unknown".
    /// </param>
    private sealed record InspectedImage(string Id, string? Tag, bool IsOwnBuild, IReadOnlyList<string> Layers, string? BaseLabel);

    /// <param name="output">The ids of the images in use, a line with the marker, and the JSON of "docker image inspect".</param>
    public static IReadOnlyList<ImageDetails> Parse(string output, string marker)
    {
        var parts = output.Split(marker, 2);
        if (parts.Length < 2)
        {
            return [];
        }

        var inUse = parts[0].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var all = ParseInspect(parts[1]);

        return [.. all
            .Where(image => inUse.Contains(image.Id))
            .DistinctBy(image => image.Id)
            .Select(image => Describe(image, all))
            .OrderBy(image => image.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static ImageDetails Describe(InspectedImage image, IReadOnlyList<InspectedImage> all)
    {
        // Docker does not record what an image was built on. A tagged image whose layers this one starts
        // with is its base all the same; of several, the one that shares the most layers is the closest.
        var layered = all
            .Where(candidate => candidate.Tag is not null && candidate.Id != image.Id
                && candidate.Layers.Count > 0 && candidate.Layers.Count < image.Layers.Count
                && image.Layers.Take(candidate.Layers.Count).SequenceEqual(candidate.Layers))
            .OrderByDescending(candidate => candidate.Layers.Count)
            .FirstOrDefault();

        // An image may also name its base itself; then that name is the one its author meant.
        var labelled = image.BaseLabel is null ? null : all.FirstOrDefault(candidate => candidate.Tag == image.BaseLabel && candidate.Id != image.Id);
        var baseImage = labelled ?? layered;
        var baseLayers = baseImage is not null && image.Layers.Take(baseImage.Layers.Count).SequenceEqual(baseImage.Layers)
            ? image.Layers.Take(baseImage.Layers.Count).ToHashSet(StringComparer.Ordinal)
            : [];

        return new ImageDetails(
            image.Id,
            image.Tag ?? ShortId(image.Id),
            image.IsOwnBuild,
            image.BaseLabel ?? baseImage?.Tag,
            baseLayers);
    }

    private static List<InspectedImage> ParseInspect(string json)
    {
        using var document = JsonDocument.Parse(json);
        var images = new List<InspectedImage>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (Text(element, "Id") is not { } id)
            {
                continue;
            }

            var tag = Strings(element, "RepoTags").FirstOrDefault(candidate => !candidate.StartsWith("<none>", StringComparison.Ordinal));
            var layers = element.TryGetProperty("RootFS", out var rootFs) ? Strings(rootFs, "Layers") : [];
            var baseLabel = element.TryGetProperty("Config", out var config)
                && config.ValueKind == JsonValueKind.Object
                && config.TryGetProperty("Labels", out var labels)
                && labels.ValueKind == JsonValueKind.Object
                    ? Text(labels, BaseNameLabel)
                    : null;
            var isOwnBuild = Strings(element, "RepoDigests").Count == 0 || Text(element, "Parent") is { Length: > 0 };
            images.Add(new InspectedImage(id, tag, isOwnBuild, layers, string.IsNullOrWhiteSpace(baseLabel) ? null : baseLabel));
        }

        return images;
    }

    private static string ShortId(string id)
    {
        var hex = id.StartsWith("sha256:", StringComparison.Ordinal) ? id["sha256:".Length..] : id;
        return hex.Length > ShortIdLength ? hex[..ShortIdLength] : hex;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static List<string> Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)]
            : [];
}
