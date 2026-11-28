using System.Reflection;
using Meerkat.Abstractions;
using Meerkat.Ssh;

namespace Meerkat.App.Plugins;

/// <summary>
/// What a plugin is built against: the assemblies Meerkat shares with it, and the rule that decides
/// whether a plugin built against one version of them fits the version this Meerkat brings.
/// </summary>
public static class PluginContract
{
    /// <summary>The shared assemblies with the version this Meerkat brings of each.</summary>
    public static IReadOnlyDictionary<string, Version> Offered { get; } = new[] { typeof(IOverlayWidget).Assembly, typeof(ISshClient).Assembly }
        .Select(assembly => assembly.GetName())
        .ToDictionary(name => name.Name!, name => name.Version ?? new Version(0, 0), StringComparer.OrdinalIgnoreCase);

    /// <summary>The assembly every plugin needs, as it holds the widget interface.</summary>
    public static string WidgetAssemblyName { get; } = typeof(IOverlayWidget).Assembly.GetName().Name!;

    /// <summary>Tells why a plugin does not fit; null when it does.</summary>
    /// <param name="references">The assemblies the plugin was built against.</param>
    public static string? FindMismatch(IEnumerable<AssemblyName> references) => FindMismatch(references, Offered);

    public static string? FindMismatch(IEnumerable<AssemblyName> references, IReadOnlyDictionary<string, Version> offered)
    {
        var shared = references.Where(reference => reference.Name is not null && offered.ContainsKey(reference.Name)).ToList();
        if (!shared.Any(reference => string.Equals(reference.Name, WidgetAssemblyName, StringComparison.OrdinalIgnoreCase)))
        {
            return $"It is not built against {WidgetAssemblyName}, so it holds no widget.";
        }

        foreach (var reference in shared)
        {
            var built = reference.Version ?? new Version(0, 0);
            var host = offered[reference.Name!];

            // A new major version took something away or changed it; what was built before may call what is gone.
            if (built.Major != host.Major)
            {
                return $"It was built for {reference.Name} {Short(built)}, this Meerkat brings {Short(host)}. The plugin has to be rebuilt.";
            }

            // A new minor version only added; a plugin that uses the additions needs a Meerkat that has them.
            if (built.Minor > host.Minor)
            {
                return $"It needs {reference.Name} {Short(built)}, this Meerkat brings {Short(host)}. Update Meerkat.";
            }
        }

        return null;
    }

    private static string Short(Version version) => $"{version.Major}.{version.Minor}";
}
