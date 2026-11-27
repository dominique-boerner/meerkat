using System.IO;
using System.Reflection;

namespace Meerkat.App.Updates;

public sealed record UpdateEnvironment(SemanticVersion InstalledVersion, bool IsSelfContained, string StagingDirectory)
{
    private const string SelfContainedMetadataKey = "SelfContained";

    public static UpdateEnvironment ForCurrentProcess()
    {
        var assembly = typeof(UpdateEnvironment).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        SemanticVersion.TryParse(informationalVersion, out var installedVersion);

        // Written at build time, because the two download flavors cannot be told apart reliably at run time.
        var isSelfContained = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(attribute => attribute.Key == SelfContainedMetadataKey
                              && string.Equals(attribute.Value, bool.TrueString, StringComparison.OrdinalIgnoreCase));

        var stagingDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Meerkat",
            "updates");
        return new UpdateEnvironment(installedVersion, isSelfContained, stagingDirectory);
    }
}
