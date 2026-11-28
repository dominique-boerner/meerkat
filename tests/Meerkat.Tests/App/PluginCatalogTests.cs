using System.Diagnostics;
using System.IO;
using System.Reflection;
using Meerkat.Abstractions;
using Meerkat.App.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Meerkat.Tests.App;

public sealed class PluginCatalogTests : IDisposable
{
    private const string SampleName = "Meerkat.Plugins.Sample";

    // The build puts the sample plugin there, the way a user would install it.
    private static readonly string InstalledPlugins = Path.Combine(AppContext.BaseDirectory, "plugins");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "meerkat-plugins-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An assembly that was loaded stays open until the test run ends; the temp folder is left to Windows.
        }
    }

    [Fact]
    public void Plugin_AddsItsWidget()
    {
        var catalog = new PluginCatalog([InstalledPlugins]);

        var widgets = catalog.CreateWidgets(Services(), []);

        var plugin = Assert.Single(catalog.Plugins);
        Assert.Equal(SampleName, plugin.Name);
        // What the assembly states about itself, whatever version this build was given.
        var stated = FileVersionInfo.GetVersionInfo(Path.Combine(plugin.Directory, SampleName + ".dll")).ProductVersion;
        Assert.Equal(stated?.Split('+')[0], plugin.Version);
        Assert.NotNull(plugin.Version);
        Assert.Empty(plugin.Problems);
        Assert.Equal(["Disk"], plugin.Widgets);
        Assert.Equal("sample-disk", Assert.Single(widgets).Id);
    }

    [Fact]
    public void Widget_IsTheTypeMeerkatKnows()
    {
        var catalog = new PluginCatalog([InstalledPlugins]);

        var widget = Assert.Single(catalog.CreateWidgets(Services(), []));

        // Loaded apart from Meerkat, the plugin still has to share the contracts with it.
        Assert.NotSame(typeof(IOverlayWidget).Assembly, widget.GetType().Assembly);
        Assert.IsAssignableFrom<IOverlayWidget>(widget);
    }

    [Fact]
    public void Widget_CannotTakeTheIdOfAnother()
    {
        var catalog = new PluginCatalog([InstalledPlugins]);

        var widgets = catalog.CreateWidgets(Services(), ["sample-disk"]);

        Assert.Empty(widgets);
        var plugin = Assert.Single(catalog.Plugins);
        Assert.Empty(plugin.Widgets);
        Assert.Contains("'sample-disk'", Assert.Single(plugin.Problems));
    }

    [Fact]
    public void Widget_ThatCannotBeCreated_IsReportedInsteadOfThrown()
    {
        var catalog = new PluginCatalog([InstalledPlugins]);

        // Without the service the widget asks for in its constructor.
        var widgets = catalog.CreateWidgets(new ServiceCollection().BuildServiceProvider(), []);

        Assert.Empty(widgets);
        Assert.StartsWith("DiskWidget could not be created", Assert.Single(Assert.Single(catalog.Plugins).Problems));
    }

    [Fact]
    public void FolderWithoutAssemblyOfItsName_IsReported()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Acme.Deploys"));

        var plugin = Assert.Single(new PluginCatalog([_root]).Plugins);

        Assert.Contains("Acme.Deploys.dll", Assert.Single(plugin.Problems));
    }

    [Fact]
    public void FileThatIsNoAssembly_IsReported()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "Broken")).FullName;
        File.WriteAllText(Path.Combine(directory, "Broken.dll"), "not an assembly");

        var plugin = Assert.Single(new PluginCatalog([_root]).Plugins);

        Assert.Equal("Broken.dll is not a .NET assembly.", Assert.Single(plugin.Problems));
    }

    [Fact]
    public void AssemblyWithoutWidget_IsReported()
    {
        // An assembly that is built against the contracts but implements none of them publicly.
        var directory = Directory.CreateDirectory(Path.Combine(_root, "Meerkat.Ssh")).FullName;
        File.Copy(typeof(Meerkat.Ssh.ISshClient).Assembly.Location, Path.Combine(directory, "Meerkat.Ssh.dll"));

        var plugin = Assert.Single(new PluginCatalog([_root]).Plugins);

        Assert.Contains(nameof(IOverlayWidget), Assert.Single(plugin.Problems));
    }

    [Fact]
    public void SameNameInTwoFolders_UsesTheFirst()
    {
        var second = Directory.CreateDirectory(Path.Combine(_root, SampleName)).FullName;
        File.Copy(Path.Combine(InstalledPlugins, SampleName, SampleName + ".dll"), Path.Combine(second, SampleName + ".dll"));

        var catalog = new PluginCatalog([InstalledPlugins, _root]);

        Assert.Equal(2, catalog.Plugins.Count);
        Assert.Empty(catalog.Plugins[0].Problems);
        Assert.Contains("same name", Assert.Single(catalog.Plugins[1].Problems));
        Assert.Single(catalog.CreateWidgets(Services(), []));
    }

    [Fact]
    public void MissingFolder_MeansNoPlugins() =>
        Assert.Empty(new PluginCatalog([Path.Combine(_root, "nowhere")]).Plugins);

    [Theory]
    [InlineData("1.0", "1.0", null)]
    [InlineData("1.0", "1.3", null)]
    [InlineData("1.4", "1.3", "Update Meerkat")]
    [InlineData("1.0", "2.0", "has to be rebuilt")]
    [InlineData("2.0", "1.3", "has to be rebuilt")]
    public void Contract_FitsWithinAMajorVersionUpToWhatMeerkatBrings(string built, string offered, string? expected)
    {
        var mismatch = PluginContract.FindMismatch(
            [new AssemblyName(PluginContract.WidgetAssemblyName) { Version = Version.Parse(built) }],
            new Dictionary<string, Version> { [PluginContract.WidgetAssemblyName] = Version.Parse(offered) });

        if (expected is null)
        {
            Assert.Null(mismatch);
        }
        else
        {
            Assert.Contains(expected, mismatch);
        }
    }

    [Fact]
    public void Contract_NeedsTheWidgetAssembly()
    {
        var mismatch = PluginContract.FindMismatch([new AssemblyName("System.Runtime")]);

        Assert.Contains("holds no widget", mismatch);
    }

    [Fact]
    public void Contract_HasAVersionOfItsOwn()
    {
        // The version of the application changes with every release; that of the contract must not follow it.
        Assert.All(PluginContract.Offered.Values, version => Assert.Equal(1, version.Major));
        Assert.NotEqual(typeof(PluginCatalog).Assembly.GetName().Version, PluginContract.Offered[PluginContract.WidgetAssemblyName]);
    }

    private static ServiceProvider Services() =>
        new ServiceCollection().AddSingleton<IOverlayState, VisibleOverlay>().BuildServiceProvider();

    private sealed class VisibleOverlay : IOverlayState
    {
        public bool IsVisible => true;

        public bool IsPanelOpen => false;

        public string PanelHotkeyText => "Ctrl+Alt+M";

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }
}
