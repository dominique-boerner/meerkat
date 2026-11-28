using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Meerkat.App.Diagnostics;
using Meerkat.App.Settings;
using Meerkat.App.Updates;

namespace Meerkat.Tests.App;

internal sealed class FakeReleaseFeed : IReleaseFeed
{
    private readonly byte[] _archive = CreateArchive("Meerkat.exe", "new version");

    public ReleaseInfo? Latest { get; set; }

    public Exception? CheckFailure { get; set; }

    /// <summary>Overrides the published checksum, e.g. to simulate a corrupted download.</summary>
    public string? PublishedHash { get; set; }

    public int Downloads { get; private set; }

    public static ReleaseInfo CreateRelease(string version, string notes = "") => new(
        Parse(version),
        notes,
        $"https://github.com/dominique-boerner/meerkat/releases/tag/v{version}",
        $"Meerkat-{version}-win-x64.zip",
        "https://example.invalid/archive",
        AssetSize: 1000,
        "https://example.invalid/checksums");

    public static SemanticVersion Parse(string version) =>
        SemanticVersion.TryParse(version, out var parsed) ? parsed : throw new FormatException(version);

    public Task<ReleaseInfo?> GetLatestAsync(bool includePrerelease, bool selfContained, CancellationToken cancellationToken) =>
        CheckFailure is null ? Task.FromResult(Latest) : Task.FromException<ReleaseInfo?>(CheckFailure);

    public async Task DownloadAsync(string url, string destinationPath, IProgress<long> progress, CancellationToken cancellationToken)
    {
        Downloads++;
        await File.WriteAllBytesAsync(destinationPath, _archive, cancellationToken);
        progress.Report(_archive.Length);
    }

    public Task<string> GetTextAsync(string url, CancellationToken cancellationToken)
    {
        var hash = PublishedHash ?? Convert.ToHexStringLower(SHA256.HashData(_archive));
        return Task.FromResult($"{hash}  {Latest?.AssetName}\n");
    }

    private static byte[] CreateArchive(string entryName, string content)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry(entryName).Open()))
        {
            writer.Write(content);
        }

        return stream.ToArray();
    }
}

internal sealed class FakeUpdateInstaller : IUpdateInstaller
{
    public bool IsSupported { get; set; } = true;

    public bool NeedsAdministrator { get; set; }

    public List<string> Installed { get; } = [];

    public Exception? InstallFailure { get; set; }

    public void Install(string stagedExecutablePath)
    {
        if (InstallFailure is not null)
        {
            throw InstallFailure;
        }

        Installed.Add(stagedExecutablePath);
    }

    public void RemoveLeftovers()
    {
    }
}

/// <summary>Wires an <see cref="UpdateService"/> to fakes and a throwaway directory.</summary>
internal sealed class UpdateTestContext : IDisposable
{
    private readonly string _directory;
    private readonly bool _ownsDirectory;

    /// <param name="previousSession">Shares its files, standing in for an earlier run of the application.</param>
    public UpdateTestContext(string installedVersion = "1.0.0", bool isSupported = true, UpdateTestContext? previousSession = null)
    {
        _ownsDirectory = previousSession is null;
        _directory = previousSession?._directory
            ?? Path.Combine(Path.GetTempPath(), "Meerkat.Tests", Guid.NewGuid().ToString("N"));
        Installer.IsSupported = isSupported;
        Settings = new SettingsService(Path.Combine(_directory, "settings.json"));
        Service = new UpdateService(
            Feed,
            Installer,
            Settings,
            new UpdateEnvironment(FakeReleaseFeed.Parse(installedVersion), IsSelfContained: false, Path.Combine(_directory, "updates")),
            new ErrorLog(Path.Combine(_directory, "error.log")));
        ViewModel = new UpdateViewModel(Service, Settings);
    }

    public FakeReleaseFeed Feed { get; } = new();

    public FakeUpdateInstaller Installer { get; } = new();

    public SettingsService Settings { get; }

    public UpdateService Service { get; }

    public UpdateViewModel ViewModel { get; }

    public void Dispose()
    {
        if (_ownsDirectory && Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
