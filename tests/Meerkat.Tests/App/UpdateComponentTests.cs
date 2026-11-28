using System.IO;
using Meerkat.App.Updates;

namespace Meerkat.Tests.App;

public sealed class UpdateComponentTests : IDisposable
{
    private const string ReleasesJson = """
        [
          { "tag_name": "v2.0.0", "draft": true, "prerelease": false, "body": "draft", "html_url": "https://github.com/dominique-boerner/meerkat/releases/tag/v2.0.0",
            "assets": [ { "name": "Meerkat-2.0.0-win-x64.zip", "size": 1, "browser_download_url": "https://github.com/dominique-boerner/meerkat/releases/download/v2.0.0/Meerkat-2.0.0-win-x64.zip" },
                        { "name": "SHA256SUMS.txt", "size": 1, "browser_download_url": "https://github.com/dominique-boerner/meerkat/releases/download/v2.0.0/SHA256SUMS.txt" } ] },
          { "tag_name": "v1.2.0-beta.1", "draft": false, "prerelease": true, "body": "beta", "html_url": "https://github.com/dominique-boerner/meerkat/releases/tag/v1.2.0-beta.1",
            "assets": [ { "name": "Meerkat-1.2.0-beta.1-win-x64.zip", "size": 300, "browser_download_url": "https://github.com/dominique-boerner/meerkat/releases/download/v1.2.0-beta.1/Meerkat-1.2.0-beta.1-win-x64.zip" },
                        { "name": "SHA256SUMS.txt", "size": 1, "browser_download_url": "https://github.com/dominique-boerner/meerkat/releases/download/v1.2.0-beta.1/SHA256SUMS.txt" } ] },
          { "tag_name": "v1.0.0", "draft": false, "prerelease": false, "body": "first", "html_url": "https://github.com/dominique-boerner/meerkat/releases/tag/v1.0.0",
            "assets": [ { "name": "Meerkat-1.0.0-win-x64.zip", "size": 100, "browser_download_url": "https://github.com/dominique-boerner/meerkat/releases/download/v1.0.0/Meerkat-1.0.0-win-x64.zip" },
                        { "name": "SHA256SUMS.txt", "size": 1, "browser_download_url": "https://github.com/dominique-boerner/meerkat/releases/download/v1.0.0/SHA256SUMS.txt" } ] },
          { "tag_name": "v1.1.0", "draft": false, "prerelease": false, "body": "second", "html_url": "https://github.com/dominique-boerner/meerkat/releases/tag/v1.1.0",
            "assets": [ { "name": "Meerkat-1.1.0-win-x64.zip", "size": 200, "browser_download_url": "https://github.com/dominique-boerner/meerkat/releases/download/v1.1.0/Meerkat-1.1.0-win-x64.zip" },
                        { "name": "Meerkat-1.1.0-win-x64-self-contained.zip", "size": 9000, "browser_download_url": "https://github.com/dominique-boerner/meerkat/releases/download/v1.1.0/Meerkat-1.1.0-win-x64-self-contained.zip" },
                        { "name": "SHA256SUMS.txt", "size": 1, "browser_download_url": "https://github.com/dominique-boerner/meerkat/releases/download/v1.1.0/SHA256SUMS.txt" } ] },
          { "tag_name": "v1.5.0", "draft": false, "prerelease": false, "body": "foreign host", "html_url": "https://github.com/dominique-boerner/meerkat/releases/tag/v1.5.0",
            "assets": [ { "name": "Meerkat-1.5.0-win-x64.zip", "size": 1, "browser_download_url": "https://example.com/Meerkat-1.5.0-win-x64.zip" },
                        { "name": "SHA256SUMS.txt", "size": 1, "browser_download_url": "https://example.com/SHA256SUMS.txt" } ] },
          { "tag_name": "nightly", "draft": false, "prerelease": false, "body": "", "html_url": "", "assets": [] }
        ]
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Meerkat.Tests", Guid.NewGuid().ToString("N"));

    public UpdateComponentTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("1.0.0-beta.1", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-beta")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10")]
    [InlineData("1.0.0-beta", "1.0.0-beta.1")]
    [InlineData("1.0.0-1", "1.0.0-alpha")]
    public void SemanticVersion_OrdersByPrecedence(string lower, string higher)
    {
        Assert.True(FakeReleaseFeed.Parse(lower) < FakeReleaseFeed.Parse(higher));
        Assert.True(FakeReleaseFeed.Parse(higher) > FakeReleaseFeed.Parse(lower));
    }

    [Fact]
    public void SemanticVersion_IgnoresTagPrefixAndBuildMetadata()
    {
        Assert.True(SemanticVersion.TryParse("v1.2.3-rc.1+abc123", out var version));

        Assert.Equal(new SemanticVersion(1, 2, 3, "rc.1"), version);
        Assert.Equal("1.2.3-rc.1", version.ToString());
        Assert.Equal(FakeReleaseFeed.Parse("1.2.3"), FakeReleaseFeed.Parse("1.2.3+build"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3-")]
    [InlineData("nightly")]
    public void SemanticVersion_RejectsMalformedText(string? text)
    {
        Assert.False(SemanticVersion.TryParse(text, out _));
    }

    [Fact]
    public void Parser_SelectsHighestStableReleaseFromTrustedDownloads()
    {
        var release = GitHubReleaseParser.SelectLatest(ReleasesJson, includePrerelease: false, selfContained: false);

        Assert.NotNull(release);
        Assert.Equal("1.1.0", release.Version.ToString());
        Assert.Equal("second", release.Notes);
        Assert.Equal("Meerkat-1.1.0-win-x64.zip", release.AssetName);
        Assert.Equal(200, release.AssetSize);
        Assert.EndsWith("/v1.1.0/SHA256SUMS.txt", release.ChecksumsUrl);
    }

    [Fact]
    public void Parser_OffersPrereleasesOnlyOnThePrereleaseChannel()
    {
        var release = GitHubReleaseParser.SelectLatest(ReleasesJson, includePrerelease: true, selfContained: false);

        Assert.Equal("1.2.0-beta.1", release?.Version.ToString());
    }

    [Fact]
    public void Parser_PicksTheArchiveMatchingTheInstalledFlavor()
    {
        var release = GitHubReleaseParser.SelectLatest(ReleasesJson, includePrerelease: false, selfContained: true);

        Assert.Equal("Meerkat-1.1.0-win-x64-self-contained.zip", release?.AssetName);
        Assert.Equal(9000, release?.AssetSize);
    }

    [Fact]
    public void ChecksumFile_FindsAndComputesHashes()
    {
        const string content = "aaa111  Meerkat-1.1.0-win-x64.zip\nbbb222  Meerkat-1.1.0-win-x64-self-contained.zip\n";
        var file = Path.Combine(_directory, "data.bin");
        File.WriteAllText(file, "abc");

        Assert.Equal("bbb222", ChecksumFile.FindHash(content, "Meerkat-1.1.0-win-x64-self-contained.zip"));
        Assert.Null(ChecksumFile.FindHash(content, "missing.zip"));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ChecksumFile.ComputeHash(file));
    }

    [Fact]
    public void ReleaseNotes_AreReadableWithoutMarkdownRendering()
    {
        const string markdown = "## What's Changed\n* Add **update** check by @someone\n- Fix `tray` icon\n\n\n**Full Changelog**: https://github.com/x/y/compare/v1...v2\n";

        Assert.Equal(
            $"What's Changed{Environment.NewLine}•  Add update check by @someone{Environment.NewLine}•  Fix tray icon",
            ReleaseNotesFormatter.ToPlainText(markdown));
        Assert.Equal(["Add update check by @someone"], ReleaseNotesFormatter.GetHighlights(markdown, maximum: 1));
        Assert.Empty(ReleaseNotesFormatter.GetHighlights("**Full Changelog**: https://example.com", maximum: 3));
    }

    [Fact]
    public void Installer_ReplacesTheExecutableAndCleansUpOnTheNextStart()
    {
        var installed = Path.Combine(_directory, "Meerkat.exe");
        var staged = Path.Combine(_directory, "staged.exe");
        File.WriteAllText(installed, "old");
        File.WriteAllText(staged, "new");
        var installer = new ExecutableSwapInstaller(installed, isSingleFile: true);

        installer.Install(staged);

        Assert.Equal("new", File.ReadAllText(installed));
        Assert.Equal("old", File.ReadAllText(installed + ".old"));

        installer.RemoveLeftovers();
        Assert.False(File.Exists(installed + ".old"));
    }

    [Fact]
    public void Installer_RestoresTheExecutableWhenTheNewOneCannotBeCopied()
    {
        var installed = Path.Combine(_directory, "Meerkat.exe");
        File.WriteAllText(installed, "old");
        var installer = new ExecutableSwapInstaller(installed, isSingleFile: true);

        Assert.Throws<FileNotFoundException>(() => installer.Install(Path.Combine(_directory, "missing.exe")));

        Assert.Equal("old", File.ReadAllText(installed));
        Assert.False(File.Exists(installed + ".old"));
    }

    [Fact]
    public void Installer_AsksForAdministratorRightsWhereTheProgramFolderIsProtected()
    {
        var installed = Path.Combine(_directory, "Meerkat.exe");
        var staged = Path.Combine(_directory, "staged.exe");
        File.WriteAllText(installed, "old");
        File.WriteAllText(staged, "new");
        var elevatedCalls = new List<(string FileName, IReadOnlyList<string> Arguments)>();
        var installer = new ExecutableSwapInstaller(
            installed,
            isSingleFile: true,
            runElevated: (fileName, arguments) =>
            {
                // What Windows would start with administrator rights: the installed program, told to swap itself.
                elevatedCalls.Add((fileName, arguments));
                return ExecutableSwapInstaller.TryRunElevatedInstall(arguments, fileName) ?? -1;
            },
            canWrite: _ => false);
        Assert.True(installer.NeedsAdministrator);

        installer.Install(staged);

        var call = Assert.Single(elevatedCalls);
        Assert.Equal(installed, call.FileName);
        Assert.Equal([ExecutableSwapInstaller.InstallArgument, staged, ChecksumFile.ComputeHash(staged)], call.Arguments);
        Assert.Equal("new", File.ReadAllText(installed));
        Assert.Equal("old", File.ReadAllText(installed + ".old"));
        Assert.False(File.Exists(installed + ".new"));
    }

    [Fact]
    public void ElevatedInstall_RefusesADownloadThatChangedAfterItWasVerified()
    {
        var installed = Path.Combine(_directory, "Meerkat.exe");
        var staged = Path.Combine(_directory, "staged.exe");
        File.WriteAllText(installed, "old");
        File.WriteAllText(staged, "new");
        var verified = ChecksumFile.ComputeHash(staged);
        File.WriteAllText(staged, "tampered");

        Assert.Equal(1, ExecutableSwapInstaller.TryRunElevatedInstall([ExecutableSwapInstaller.InstallArgument, staged, verified], installed));

        Assert.Equal("old", File.ReadAllText(installed));
        Assert.False(File.Exists(installed + ".new"));
        Assert.False(File.Exists(installed + ".old"));

        // Started for anything else, the program is none of the installer's business.
        Assert.Null(ExecutableSwapInstaller.TryRunElevatedInstall([], installed));
        Assert.Null(ExecutableSwapInstaller.TryRunElevatedInstall(["--something-else", staged, verified], installed));
    }

    [Fact]
    public void Installer_ReportsDeclinedOrFailedElevationWithoutTouchingTheProgram()
    {
        var installed = Path.Combine(_directory, "Meerkat.exe");
        var staged = Path.Combine(_directory, "staged.exe");
        File.WriteAllText(installed, "old");
        File.WriteAllText(staged, "new");
        var declined = new ExecutableSwapInstaller(installed, isSingleFile: true, (_, _) => throw new System.ComponentModel.Win32Exception(1223), _ => false);
        var failed = new ExecutableSwapInstaller(installed, isSingleFile: true, (_, _) => 1, _ => false);

        Assert.Contains("were not granted", Assert.Throws<InvalidOperationException>(() => declined.Install(staged)).Message);
        Assert.Throws<InvalidOperationException>(() => failed.Install(staged));
        Assert.Equal("old", File.ReadAllText(installed));
        Assert.False(new ExecutableSwapInstaller(installed, isSingleFile: true).NeedsAdministrator);
    }

    [Fact]
    public void Installer_RefusesBuildsThatAreNotSingleFile()
    {
        var installer = new ExecutableSwapInstaller(Path.Combine(_directory, "Meerkat.exe"), isSingleFile: false);

        Assert.False(installer.IsSupported);
        Assert.Throws<InvalidOperationException>(() => installer.Install("anything"));
    }
}
