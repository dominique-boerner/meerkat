using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Meerkat.App.Plugins;

/// <summary>
/// Loads one plugin. Whatever Meerkat brings itself is shared with the plugin, so that a widget is the
/// same type on both sides; everything else comes from the plugin's own folder and stays its own, so
/// that two plugins may bring different versions of the same library.
/// </summary>
internal sealed class PluginLoadContext(string directory) : AssemblyLoadContext(Path.GetFileName(directory))
{
    private const string NativeDirectory = @"runtimes\win-x64\native";

    public string Directory { get; } = directory;

    /// <summary>Whether the plugin brings an assembly of that name itself.</summary>
    public bool Brings(AssemblyName name) => FindManaged(name) is not null;

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        try
        {
            // Meerkat first: a copy of the contracts or of WPF in the plugin's folder would be a different type.
            t_isAskingHost = true;
            return Default.LoadFromAssemblyName(assemblyName);
        }
        catch (Exception exception) when (exception is FileNotFoundException or FileLoadException)
        {
            return FindManaged(assemblyName) is { } path ? LoadFromAssemblyPath(path) : null;
        }
        finally
        {
            t_isAskingHost = false;
        }
    }

    /// <summary>
    /// Whether a plugin is asking on this thread what Meerkat brings. Meerkat must not answer that by
    /// asking the plugins in turn, or the two would ask each other forever.
    /// </summary>
    public static bool IsAskingHost => t_isAskingHost;

    [ThreadStatic]
    private static bool t_isAskingHost;

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var fileName = unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? unmanagedDllName : unmanagedDllName + ".dll";
        foreach (var candidate in new[] { Path.Combine(Directory, fileName), Path.Combine(Directory, NativeDirectory, fileName) })
        {
            if (File.Exists(candidate))
            {
                return LoadUnmanagedDllFromPath(candidate);
            }
        }

        return IntPtr.Zero;
    }

    private string? FindManaged(AssemblyName name)
    {
        if (name.Name is null)
        {
            return null;
        }

        // Translations of a library lie in a folder named after their language.
        var folder = string.IsNullOrEmpty(name.CultureName) ? Directory : Path.Combine(Directory, name.CultureName);
        var path = Path.Combine(folder, name.Name + ".dll");
        return File.Exists(path) ? path : null;
    }
}
