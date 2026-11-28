using System.IO;
using System.Text.Json;

namespace Meerkat.Widgets.Docker.Security;

public sealed class StoredImageScan
{
    public DateTimeOffset ScannedAt { get; set; }

    public List<Vulnerability> Vulnerabilities { get; set; } = [];
}

/// <summary>What is known about the images of one Docker system from earlier scans.</summary>
public sealed class StoredScans
{
    /// <summary>When all images were last scanned in one go; the daily scan is measured against it.</summary>
    public DateTimeOffset? LastFullScan { get; set; }

    /// <summary>Scan results by image id.</summary>
    public Dictionary<string, StoredImageScan> Images { get; set; } = [];

    /// <summary>When each vulnerability was first found on this system, by its id.</summary>
    public Dictionary<string, DateTimeOffset> FirstSeen { get; set; } = [];
}

public interface IScanStore
{
    StoredScans Load(string sourceId);

    void Save(string sourceId, StoredScans scans);
}

/// <summary>
/// Keeps scan results in a file of their own. They are no settings: they can be thrown away at any time
/// and grow far larger than anything a user would want in the settings file.
/// </summary>
public sealed class FileScanStore(string filePath) : IScanStore
{
    private readonly Lock _gate = new();
    private Dictionary<string, StoredScans>? _sources;

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Meerkat",
        "docker-image-scans.json");

    public StoredScans Load(string sourceId)
    {
        lock (_gate)
        {
            return ReadAll().TryGetValue(sourceId, out var scans) ? scans : new StoredScans();
        }
    }

    public void Save(string sourceId, StoredScans scans)
    {
        lock (_gate)
        {
            var sources = ReadAll();
            sources[sourceId] = scans;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                File.WriteAllText(filePath, JsonSerializer.Serialize(sources));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The results stay in memory; losing them only costs another scan after a restart.
            }
        }
    }

    private Dictionary<string, StoredScans> ReadAll()
    {
        if (_sources is not null)
        {
            return _sources;
        }

        try
        {
            _sources = File.Exists(filePath)
                ? JsonSerializer.Deserialize<Dictionary<string, StoredScans>>(File.ReadAllText(filePath))
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A file that cannot be read is as good as none: everything in it can be found out again.
        }

        return _sources ??= [];
    }
}
