using System.IO;
using Meerkat.App.Settings;
using Meerkat.App.SettingsUi;
using Meerkat.Widgets.Docker.Settings;

namespace Meerkat.Tests.App;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Meerkat.Tests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void MissingFile_YieldsDefaults()
    {
        var settings = new SettingsService(FilePath).Current;

        Assert.Equal("Ctrl+Alt+O", settings.ToggleModeHotkey);
        Assert.Equal("Ctrl+Alt+H", settings.ToggleVisibilityHotkey);
        Assert.Null(settings.Monitor);
        Assert.Empty(settings.Layouts);
        Assert.Empty(settings.DisabledWidgets);
    }

    [Fact]
    public void Save_PersistsOverlayAndWidgetSettings()
    {
        var service = new SettingsService(FilePath);
        service.Current.Opacity = 0.5;
        service.Current.Monitor = @"\\.\DISPLAY2";
        service.Save("docker", new DockerWidgetSettings { Distribution = "Ubuntu-24.04", StatsIntervalSeconds = 7, MaxLogLines = 800 });

        var reloaded = new SettingsService(FilePath);

        Assert.Equal(0.5, reloaded.Current.Opacity);
        Assert.Equal(@"\\.\DISPLAY2", reloaded.Current.Monitor);
        var dockerSettings = reloaded.Load<DockerWidgetSettings>("docker");
        Assert.Equal("Ubuntu-24.04", dockerSettings.Distribution);
        Assert.Equal(TimeSpan.FromSeconds(7), dockerSettings.StatsInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), dockerSettings.ListInterval);
        Assert.Equal(800, dockerSettings.EffectiveMaxLogLines);
    }

    [Fact]
    public void Layouts_AreKeptSeparatelyPerMonitor()
    {
        var service = new SettingsService(FilePath);
        var layouts = new LayoutService(service);

        layouts.Store([("docker", new WidgetLayout { Left = 10, Top = 20, Width = 300, Height = 200 })]);
        service.Current.Monitor = @"\\.\DISPLAY2";
        Assert.Null(layouts.Find("docker"));
        layouts.Store([("docker", new WidgetLayout { Left = 500, Top = 20, Width = 300, Height = 200 })]);

        var reloaded = new LayoutService(new SettingsService(FilePath));
        Assert.Equal(500, reloaded.Find("docker")?.Left);
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ not json");

        var service = new SettingsService(FilePath);

        Assert.Equal(0.9, service.Current.Opacity);
        Assert.Equal(TimeSpan.FromSeconds(3), service.Load<DockerWidgetSettings>("docker").StatsInterval);
    }

    [Fact]
    public void OutOfRangeWidgetValues_AreClamped()
    {
        var settings = new DockerWidgetSettings { ListIntervalSeconds = 0, StatsIntervalSeconds = double.NaN, MaxLogLines = 3 };

        Assert.Equal(TimeSpan.FromSeconds(1), settings.ListInterval);
        Assert.Equal(TimeSpan.FromSeconds(1), settings.StatsInterval);
        Assert.Equal(100, settings.EffectiveMaxLogLines);
    }

    [Fact]
    public void SettingsViewModel_SavesHotkeysWidgetStateAndPositionResets()
    {
        var service = new SettingsService(FilePath);
        new LayoutService(service).Store(
        [
            ("docker", new WidgetLayout { Left = 10, Top = 20, Width = 300, Height = 200 }),
            ("docker.logs", new WidgetLayout { Left = 10, Top = 300, Width = 600, Height = 200 }),
            ("other", new WidgetLayout { Left = 1, Top = 1, Width = 300, Height = 200 }),
        ]);
        var docker = new WidgetToggleViewModel("docker", "Docker", "Meerkat.Widgets.Docker", isEnabled: true);
        var viewModel = CreateViewModel(service, docker);

        viewModel.ToggleModeHotkey = "Ctrl+Shift+F9";
        docker.IsEnabled = false;
        docker.ResetPositionCommand.Execute(null);
        var saved = viewModel.TrySave();

        Assert.True(saved);
        var reloaded = new SettingsService(FilePath);
        Assert.Equal("Ctrl+Shift+F9", reloaded.Current.ToggleModeHotkey);
        Assert.Equal(["docker"], reloaded.Current.DisabledWidgets);
        Assert.Equal(["other"], reloaded.Current.Layouts["primary"].Keys);
    }

    [Theory]
    [InlineData("O", "Ctrl+Alt+H")]
    [InlineData("Ctrl+Alt+H", "Ctrl+Alt+H")]
    public void SettingsViewModel_RejectsInvalidOrConflictingHotkeys(string toggleMode, string toggleVisibility)
    {
        var service = new SettingsService(FilePath);
        var viewModel = CreateViewModel(service);
        viewModel.ToggleModeHotkey = toggleMode;
        viewModel.ToggleVisibilityHotkey = toggleVisibility;

        Assert.False(viewModel.TrySave());
        Assert.NotNull(viewModel.ErrorMessage);
        Assert.False(File.Exists(FilePath));
    }

    private SettingsViewModel CreateViewModel(SettingsService service, params WidgetToggleViewModel[] widgets) =>
        new(service, [new MonitorOption(null, "Primary monitor")], widgets, [], hotkeyWarning: null, FilePath);
}
