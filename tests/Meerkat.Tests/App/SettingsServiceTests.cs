using Meerkat.App.Startup;
using System.IO;
using Meerkat.App.Settings;
using Meerkat.App.SettingsUi;
using Meerkat.Widgets.Docker.Settings;

namespace Meerkat.Tests.App;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Meerkat.Tests", Guid.NewGuid().ToString("N"));

    private readonly UpdateTestContext _updates = new();

    private string FilePath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        _updates.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void MissingFile_YieldsDefaults()
    {
        var settings = new SettingsService(FilePath).Current;

        Assert.Equal("Ctrl+Alt+M", settings.TogglePanelHotkey);
        Assert.Equal("Ctrl+Alt+H", settings.ToggleVisibilityHotkey);
        Assert.Null(settings.Monitor);
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
    public void FileFromAnOlderVersion_IsReadWithoutItsObsoleteEntries()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """
            { "ToggleModeHotkey": "Ctrl+Alt+O", "Opacity": 0.6, "Layouts": { "primary": { "docker": { "Left": 1, "Top": 2, "Width": 3, "Height": 4 } } } }
            """);

        var settings = new SettingsService(FilePath).Current;

        Assert.Equal(0.6, settings.Opacity);
        Assert.Equal("Ctrl+Alt+M", settings.TogglePanelHotkey);
    }

    [Theory]
    [InlineData(900, 900)]
    [InlineData(100, OverlaySettings.MinimumPanelWidth)]
    [InlineData(double.NaN, OverlaySettings.DefaultPanelWidth)]
    public void PanelWidth_IsKeptWideEnoughForTheWidgetPages(double stored, double expected)
    {
        var service = new SettingsService(FilePath);
        service.Current.PanelWidth = stored;

        Assert.Equal(expected, service.Current.EffectivePanelWidth);
        Assert.Equal(OverlaySettings.DefaultPanelWidth, new OverlaySettings().PanelWidth);
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
    public void SettingsViewModel_SavesHotkeysWidgetStateAndUpdatePreferences()
    {
        var service = new SettingsService(FilePath);
        var docker = new WidgetToggleViewModel("docker", "Docker", "Meerkat.Widgets.Docker", isEnabled: true);
        var viewModel = CreateViewModel(service, docker);

        viewModel.TogglePanelHotkey = "Ctrl+Shift+F9";
        viewModel.CheckForUpdatesAutomatically = false;
        viewModel.SelectedChannel = viewModel.Channels.Single(option => option.Channel == UpdateChannel.Prerelease);
        docker.IsEnabled = false;
        var saved = viewModel.TrySave();

        Assert.True(saved);
        var reloaded = new SettingsService(FilePath);
        Assert.Equal("Ctrl+Shift+F9", reloaded.Current.TogglePanelHotkey);
        Assert.Equal(["docker"], reloaded.Current.DisabledWidgets);
        Assert.False(reloaded.Current.Updates.CheckAutomatically);
        Assert.True(reloaded.Current.Updates.DownloadInBackground);
        Assert.Equal(UpdateChannel.Prerelease, reloaded.Current.Updates.Channel);
        Assert.Equal(["Hotkeys", "General", "Widgets", "Updates"], viewModel.NavigationItems.Select(item => item.Title));
    }

    [Theory]
    [InlineData("M", "Ctrl+Alt+H")]
    [InlineData("Ctrl+Alt+H", "Ctrl+Alt+H")]
    public void SettingsViewModel_RejectsInvalidOrConflictingHotkeys(string togglePanel, string toggleVisibility)
    {
        var service = new SettingsService(FilePath);
        var viewModel = CreateViewModel(service);
        viewModel.TogglePanelHotkey = togglePanel;
        viewModel.ToggleVisibilityHotkey = toggleVisibility;

        Assert.False(viewModel.TrySave());
        Assert.NotNull(viewModel.ErrorMessage);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void SettingsViewModel_SwitchesAutostartOnlyWhenTheUserChangedIt()
    {
        var autostart = new FakeAutostart { IsEnabled = true };
        var unchanged = new SettingsViewModel(new SettingsService(FilePath), [new MonitorOption(null, "Primary monitor")], [], [], _updates.ViewModel, hotkeyWarning: null, FilePath) { Autostart = autostart };
        Assert.True(unchanged.CanStartWithWindows);
        Assert.True(unchanged.StartWithWindows);

        // The entry may point to another copy of Meerkat; saving other settings must leave it alone.
        Assert.True(unchanged.TrySave());
        Assert.Equal(0, autostart.Changes);

        unchanged.StartWithWindows = false;
        Assert.True(unchanged.TrySave());
        Assert.False(autostart.IsEnabled);
        Assert.Equal(1, autostart.Changes);

        Assert.False(CreateViewModel(new SettingsService(FilePath)).CanStartWithWindows);
    }

    [Fact]
    public void RegistryAutostart_AddsAndRemovesTheQuotedProgramPath()
    {
        var keyPath = $@"Software\Meerkat.Tests\{Guid.NewGuid():N}";
        var autostart = new RegistryAutostart(@"C:\Program Files\Meerkat\Meerkat.exe", keyPath, "Meerkat");
        try
        {
            Assert.False(autostart.IsEnabled);

            autostart.SetEnabled(true);
            Assert.True(autostart.IsEnabled);
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath))
            {
                Assert.Equal("\"C:\\Program Files\\Meerkat\\Meerkat.exe\"", key?.GetValue("Meerkat"));
            }

            autostart.SetEnabled(false);
            autostart.SetEnabled(false);
            Assert.False(autostart.IsEnabled);
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Meerkat.Tests", throwOnMissingSubKey: false);
        }
    }

    private SettingsViewModel CreateViewModel(SettingsService service, params WidgetToggleViewModel[] widgets) =>
        new(service, [new MonitorOption(null, "Primary monitor")], widgets, [], _updates.ViewModel, hotkeyWarning: null, FilePath);

    private sealed class FakeAutostart : IAutostart
    {
        public bool IsEnabled { get; set; }

        public int Changes { get; private set; }

        public void SetEnabled(bool isEnabled)
        {
            IsEnabled = isEnabled;
            Changes++;
        }
    }
}
