using System.Text.Json;
using System.Text.RegularExpressions;

namespace Meerkat.Widgets.Docker.Security;

/// <summary>
/// Scans images with Trivy on the machine the images are on, so nothing has to be copied anywhere.
/// A Trivy the system already has is used as it is; otherwise Meerkat can put one into the home
/// directory of the user it works as.
/// </summary>
public sealed partial class TrivyScanner(IDockerShell shell) : IImageScanner
{
    public const string ScannerName = "Trivy";

    /// <summary>
    /// The release Meerkat installs. The checksums are part of Meerkat on purpose: a download that was
    /// tampered with, at the source or on the way, is refused instead of run.
    /// </summary>
    public const string InstalledVersion = "0.76.2";

    private const string ChecksumAmd64 = "bc0fc726e017b8927a62b2d3531d7a84ee01769daf5c99ed866dced54089fdbf";
    private const string ChecksumArm64 = "ab9d51f86e6a5fb936f605faff6b593ac51bc378fff4d3e8b9d58358b9c367c3";

    private const string InstallDirectory = "$HOME/.local/share/meerkat/trivy";
    private const int NotInstalledExitCode = 127;

    // A Trivy the user maintains wins over the one Meerkat installed.
    private static readonly string Locate = $"""
        T=$(command -v trivy) || T="{InstallDirectory}/trivy"
        [ -x "$T" ] || exit {NotInstalledExitCode}

        """;

    /// <summary>What the installation does, for showing it to the user before they agree to it.</summary>
    public static string InstallDescription =>
        $"Downloads Trivy {InstalledVersion} from its releases on GitHub, checks it against a known checksum and puts it into ~/.local/share/meerkat/trivy of the user Meerkat works as. Needs no root rights and changes nothing else on the machine.";

    public async Task<ScannerInfo?> DetectAsync(CancellationToken cancellationToken)
    {
        var result = await shell.RunAsync(Locate + "\"$T\" version --format json", cancellationToken).ConfigureAwait(false);
        return result.ExitCode switch
        {
            0 => TrivyOutputParser.ParseVersion(result.Output),
            NotInstalledExitCode => null,
            _ => throw Failure("The system could not be asked for its scanner.", result),
        };
    }

    public async Task<IReadOnlyList<Vulnerability>> ScanAsync(string imageId, CancellationToken cancellationToken)
    {
        if (!ImageId().IsMatch(imageId))
        {
            throw new ArgumentException($"Invalid image id '{imageId}'.", nameof(imageId));
        }

        // "nice" leaves the processor to whatever else the machine is there for. Without a connection to
        // the database server the scan is repeated with the database at hand, which beats no result.
        const string Scan = "nice -n 10 \"$T\" image --quiet --format json --scanners vuln --timeout 20m";
        var script = $"{Locate}{Scan} {imageId} || {Scan} --skip-db-update {imageId}";

        var result = await shell.RunAsync(script, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw Failure(result.ExitCode == NotInstalledExitCode ? "Trivy is not installed." : "The scan failed.", result);
        }

        try
        {
            return TrivyOutputParser.ParseScan(result.Output);
        }
        catch (JsonException exception)
        {
            throw new ScannerException("Trivy returned a result that could not be read.", exception.Message);
        }
    }

    public async Task UpdateDatabaseAsync(CancellationToken cancellationToken)
    {
        var result = await shell.RunAsync(Locate + "\"$T\" image --quiet --download-db-only", cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw Failure("The vulnerability database could not be updated.", result);
        }
    }

    public async Task<ScannerInfo> InstallAsync(CancellationToken cancellationToken)
    {
        var result = await shell.RunAsync(InstallScript, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw Failure("Trivy could not be installed.", result);
        }

        return TrivyOutputParser.ParseVersion(result.Output);
    }

    private static string InstallScript => $$"""
        set -e
        case "$(uname -m)" in
          x86_64|amd64) asset=Linux-64bit; sum={{ChecksumAmd64}} ;;
          aarch64|arm64) asset=Linux-ARM64; sum={{ChecksumArm64}} ;;
          *) echo "Unsupported processor: $(uname -m)" >&2; exit 3 ;;
        esac
        dir="{{InstallDirectory}}"
        tmp=$(mktemp -d)
        trap 'rm -rf "$tmp"' EXIT
        url="https://github.com/aquasecurity/trivy/releases/download/v{{InstalledVersion}}/trivy_{{InstalledVersion}}_${asset}.tar.gz"
        if command -v curl >/dev/null 2>&1; then curl -fsSL -o "$tmp/trivy.tar.gz" "$url"
        elif command -v wget >/dev/null 2>&1; then wget -q -O "$tmp/trivy.tar.gz" "$url"
        else echo "Neither curl nor wget is installed." >&2; exit 4; fi
        echo "$sum  $tmp/trivy.tar.gz" | sha256sum -c - >/dev/null 2>&1 || { echo "The download does not match the expected checksum." >&2; exit 5; }
        mkdir -p "$dir"
        tar -xzf "$tmp/trivy.tar.gz" -C "$dir" trivy
        chmod 755 "$dir/trivy"
        "$dir/trivy" version --format json
        """;

    private static ScannerException Failure(string message, ShellResult result)
    {
        var detail = new[] { result.Error, result.Output }
            .Select(text => text.Trim())
            .FirstOrDefault(text => text.Length > 0) ?? $"exit code {result.ExitCode}";
        return new ScannerException(message, detail);
    }

    // The id becomes part of a script, so nothing but the form Docker gives ids may pass.
    [GeneratedRegex("^sha256:[a-f0-9]{64}$")]
    private static partial Regex ImageId();
}

public static class TrivyOutputParser
{
    public static ScannerInfo ParseVersion(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var version = root.TryGetProperty("Version", out var value) ? value.GetString() ?? string.Empty : string.Empty;
            DateTimeOffset? updatedAt = root.TryGetProperty("VulnerabilityDB", out var database)
                && database.TryGetProperty("UpdatedAt", out var updated)
                && updated.TryGetDateTimeOffset(out var parsed)
                    ? parsed
                    : null;
            return new ScannerInfo(TrivyScanner.ScannerName, version, updatedAt);
        }
        catch (JsonException exception)
        {
            throw new ScannerException("Trivy returned a version that could not be read.", exception.Message);
        }
    }

    public static IReadOnlyList<Vulnerability> ParseScan(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var vulnerabilities = new List<Vulnerability>();
        foreach (var result in results.EnumerateArray())
        {
            if (!result.TryGetProperty("Vulnerabilities", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                vulnerabilities.Add(new Vulnerability(
                    Text(entry, "VulnerabilityID") ?? string.Empty,
                    ParseSeverity(Text(entry, "Severity")),
                    Text(entry, "PkgName") ?? string.Empty,
                    Text(entry, "InstalledVersion") ?? string.Empty,
                    Text(entry, "FixedVersion") is { Length: > 0 } fixedVersion ? fixedVersion : null,
                    Text(entry, "Title"),
                    Text(entry, "PrimaryURL"),
                    entry.TryGetProperty("Layer", out var layer) ? Text(layer, "DiffID") : null));
            }
        }

        // A package found in two places of an image is still one thing to fix.
        return [.. vulnerabilities.DistinctBy(vulnerability => (vulnerability.Id, vulnerability.Package, vulnerability.InstalledVersion))];
    }

    private static VulnerabilitySeverity ParseSeverity(string? text) =>
        Enum.TryParse<VulnerabilitySeverity>(text, ignoreCase: true, out var severity) ? severity : VulnerabilitySeverity.Unknown;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
