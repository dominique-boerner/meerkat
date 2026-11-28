using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Meerkat.Abstractions;
using Meerkat.App.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Meerkat.App.Plugins;

/// <summary>A plugin as it was found: what it brought, or why it could not be used.</summary>
public sealed class PluginInfo(string name, string directory)
{
    private readonly List<string> _problems = [];

    /// <summary>The name of the plugin's folder, which is also the name of its assembly.</summary>
    public string Name { get; } = name;

    public string Directory { get; } = directory;

    /// <summary>The version the plugin states about itself; null when it could not be read.</summary>
    public string? Version { get; internal set; }

    /// <summary>The titles of the widgets the plugin added.</summary>
    public IReadOnlyList<string> Widgets { get; internal set; } = [];

    /// <summary>What went wrong with the plugin; empty when all of it works.</summary>
    public IReadOnlyList<string> Problems => _problems;

    internal IReadOnlyList<Type> WidgetTypes { get; set; } = [];

    internal void Report(string problem) => _problems.Add(problem);
}

/// <summary>
/// Finds the plugins the user installed and loads their widgets. A plugin is a folder that holds an
/// assembly of the same name; every public class in it that implements <see cref="IOverlayWidget"/>
/// becomes a widget. Plugins run with all rights of the user, so only folders the user or an
/// administrator filled are searched, and nothing is ever fetched from elsewhere.
/// </summary>
public sealed class PluginCatalog
{
    private readonly List<PluginLoadContext> _contexts = [];
    private readonly ErrorLog? _errorLog;

    /// <param name="roots">The folders plugins are installed into, the one that wins a name twice first.</param>
    public PluginCatalog(IEnumerable<string> roots, ErrorLog? errorLog = null)
    {
        _errorLog = errorLog;
        var plugins = new List<PluginInfo>();
        foreach (var directory in roots.Where(System.IO.Directory.Exists).SelectMany(root => System.IO.Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase)))
        {
            var plugin = new PluginInfo(Path.GetFileName(directory), directory);
            if (plugins.Any(known => string.Equals(known.Name, plugin.Name, StringComparison.OrdinalIgnoreCase)))
            {
                plugin.Report("Another plugin of the same name was found first; this one is not used.");
            }
            else
            {
                Load(plugin);
            }

            plugins.Add(plugin);
        }

        Plugins = plugins;
        if (_contexts.Count > 0)
        {
            // WPF looks up the assemblies a view names where Meerkat itself was loaded, which knows nothing of a plugin's own libraries.
            AssemblyLoadContext.Default.Resolving += ResolveForPlugins;
        }
    }

    public IReadOnlyList<PluginInfo> Plugins { get; }

    /// <summary>Creates the widgets of all plugins. A widget may ask for the same services in its constructor as a built-in one.</summary>
    /// <param name="services">Where the services for the widgets come from.</param>
    /// <param name="takenIds">Ids of the widgets that exist already; a plugin cannot replace one of them.</param>
    public IReadOnlyList<IOverlayWidget> CreateWidgets(IServiceProvider services, IEnumerable<string> takenIds)
    {
        var taken = new HashSet<string>(takenIds, StringComparer.Ordinal);
        var created = new List<IOverlayWidget>();
        foreach (var plugin in Plugins)
        {
            var titles = new List<string>();
            foreach (var type in plugin.WidgetTypes)
            {
                try
                {
                    var widget = (IOverlayWidget)ActivatorUtilities.CreateInstance(services, type);
                    if (!taken.Add(widget.Id))
                    {
                        plugin.Report($"{type.Name} uses the id '{widget.Id}', which another widget has already; it is not used.");
                        continue;
                    }

                    titles.Add(widget.Title);
                    created.Add(widget);
                }
                catch (Exception exception)
                {
                    // One widget that cannot be created takes neither the others nor Meerkat down with it.
                    var cause = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
                    plugin.Report($"{type.Name} could not be created: {cause.Message}");
                    _errorLog?.Write($"Widget {type.FullName} of plugin '{plugin.Name}' could not be created", exception);
                }
            }

            plugin.Widgets = titles;
        }

        return created;
    }

    private void Load(PluginInfo plugin)
    {
        var path = Path.Combine(plugin.Directory, plugin.Name + ".dll");
        if (!File.Exists(path))
        {
            plugin.Report($"The folder holds no {plugin.Name}.dll. A plugin's assembly is named like its folder.");
            return;
        }

        try
        {
            var context = new PluginLoadContext(plugin.Directory);
            var assembly = context.LoadFromAssemblyPath(path);
            plugin.Version = ReadVersion(assembly);
            if (PluginContract.FindMismatch(assembly.GetReferencedAssemblies()) is { } mismatch)
            {
                plugin.Report(mismatch);
                return;
            }

            plugin.WidgetTypes = [.. assembly.GetExportedTypes().Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IOverlayWidget).IsAssignableFrom(type))];
            if (plugin.WidgetTypes.Count == 0)
            {
                plugin.Report($"It holds no public class that implements {nameof(IOverlayWidget)}.");
                return;
            }

            _contexts.Add(context);
        }
        catch (Exception exception)
        {
            plugin.Report(exception is BadImageFormatException ? $"{plugin.Name}.dll is not a .NET assembly." : $"It could not be loaded: {exception.Message}");
            _errorLog?.Write($"Plugin '{plugin.Name}' could not be loaded", exception);
        }
    }

    private static string? ReadVersion(Assembly assembly)
    {
        var stated = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // Builds append the commit they were made from, which nobody wants to read in a list.
        return stated?.Split('+')[0] ?? assembly.GetName().Version?.ToString(3);
    }

    private Assembly? ResolveForPlugins(AssemblyLoadContext context, AssemblyName name) =>
        PluginLoadContext.IsAskingHost ? null : _contexts.FirstOrDefault(plugin => plugin.Brings(name))?.LoadFromAssemblyName(name);
}
