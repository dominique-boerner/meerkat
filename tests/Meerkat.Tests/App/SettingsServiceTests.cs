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
        service.Save("docker", new DockerWidgetSettings { Distribution = "Ubuntu-24.04", CustomStatsIntervalSeconds = 7, MaxLogLines = 800 });

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
        var settings = new DockerWidgetSettings { CustomListIntervalSeconds = 0, CustomStatsIntervalSeconds = double.NaN, MaxLogLines = 3 };

        Assert.Equal(TimeSpan.FromSeconds(1), settings.ListInterval);
        Assert.Equal(TimeSpan.FromSeconds(1), settings.StatsInterval);
        Assert.Equal(100, settings.EffectiveMaxLogLines);
    }

    [Fact]
    public void AFileChangedByHand_IsTakenOverAndAnnounced()
    {
        var service = new SettingsService(FilePath);
        service.Current.Opacity = 0.5;
        service.Save();
        var reloads = 0;
        service.Reloaded += (_, _) => reloads++;

        // What the service wrote itself is nothing new to it.
        Assert.False(service.ReloadIfChanged());

        File.WriteAllText(FilePath, File.ReadAllText(FilePath).Replace("0.5", "0.75"));
        Assert.True(service.ReloadIfChanged());
        Assert.Equal(0.75, service.Current.Opacity);
        Assert.Equal(1, reloads);

        // Halfway through an edit the file is no JSON; the settings in use stay until it is again.
        File.WriteAllText(FilePath, "{ \"Opacity\": ");
        Assert.False(service.ReloadIfChanged());
        Assert.Equal(0.75, service.Current.Opacity);
        Assert.Equal(1, reloads);
    }

    [Fact]
    public void SettingsViewModel_StoresEveryChangeAtOnce()
    {
        var service = new SettingsService(FilePath);
        var docker = new WidgetPageViewModel("docker", "Docker", isEnabled: true);
        var viewModel = CreateViewModel(service, docker);
        var applied = 0;
        var updateChanges = 0;
        viewModel.Applied += (_, _) => applied++;
        viewModel.UpdateSettingsApplied += (_, _) => updateChanges++;

        viewModel.TogglePanelHotkey = "Ctrl+Shift+F9";
        Assert.Equal("Ctrl+Shift+F9", new SettingsService(FilePath).Current.TogglePanelHotkey);

        viewModel.CheckForUpdatesAutomatically = false;
        viewModel.SelectedChannel = viewModel.Channels.Single(option => option.Channel == UpdateChannel.Prerelease);
        docker.IsEnabled = false;

        var reloaded = new SettingsService(FilePath);
        Assert.Equal(["docker"], reloaded.Current.DisabledWidgets);
        Assert.False(reloaded.Current.Updates.CheckAutomatically);
        Assert.True(reloaded.Current.Updates.DownloadInBackground);
        Assert.Equal(UpdateChannel.Prerelease, reloaded.Current.Updates.Channel);
        Assert.Equal(4, applied);
        Assert.Equal(2, updateChanges);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Theory]
    [InlineData("M", "Ctrl+Alt+H")]
    [InlineData("Ctrl+Alt+H", "Ctrl+Alt+H")]
    public void SettingsViewModel_DoesNotStoreInvalidOrConflictingHotkeys(string togglePanel, string toggleVisibility)
    {
        var service = new SettingsService(FilePath);
        var viewModel = CreateViewModel(service);
        var applied = 0;
        viewModel.Applied += (_, _) => applied++;

        viewModel.ToggleVisibilityHotkey = toggleVisibility;
        viewModel.TogglePanelHotkey = togglePanel;

        Assert.NotNull(viewModel.ErrorMessage);
        Assert.Equal("Ctrl+Alt+M", service.Current.TogglePanelHotkey);
        Assert.Equal(0, applied);

        // Once the combination is fine, everything is stored and the complaint is gone.
        viewModel.TogglePanelHotkey = "Ctrl+Alt+P";
        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal("Ctrl+Alt+P", new SettingsService(FilePath).Current.TogglePanelHotkey);
    }

    [Fact]
    public void SettingsViewModel_HasOnePageForTheShellAndOnePerWidget()
    {
        var docker = new WidgetPageViewModel("docker", "Docker", isEnabled: true);
        var notes = new WidgetPageViewModel("notes", "Notes", isEnabled: false);
        var viewModel = new SettingsViewModel(
            new SettingsService(FilePath),
            [new MonitorOption(null, "Primary monitor")],
            [docker, notes],
            _updates.ViewModel,
            widgetHotkeys: [new WidgetHotkeyViewModel("notes/quick-note", "Quick note", "Notes", "Ctrl+Alt+N", "notes")]);

        // A widget that is switched off keeps its entry: that is where it is switched on again.
        Assert.Equal(["General", "Docker", "Notes"], viewModel.NavigationItems.Select(item => item.Title));
        Assert.True(viewModel.IsGeneralSelected);
        Assert.Null(viewModel.SelectedWidget);
        Assert.False(viewModel.WidgetHotkeys[0].IsAvailable);

        viewModel.SelectWidget("notes");
        Assert.Same(notes, viewModel.SelectedWidget);
        Assert.Equal("Notes", viewModel.PageTitle);
        Assert.False(viewModel.IsGeneralSelected);
        Assert.Equal([false, false, true], viewModel.NavigationItems.Select(item => item.IsSelected));

        // The switch sits on the page of the widget and works at once.
        viewModel.SelectedWidget!.IsEnabled = true;
        Assert.True(viewModel.WidgetHotkeys[0].IsAvailable);
        Assert.Empty(new SettingsService(FilePath).Current.DisabledWidgets);
    }

    [Fact]
    public void SettingsViewModel_SwitchesAutostartOnlyWhenTheUserChangedIt()
    {
        var autostart = new FakeAutostart { IsEnabled = true };
        var viewModel = new SettingsViewModel(new SettingsService(FilePath), [new MonitorOption(null, "Primary monitor")], [], _updates.ViewModel, autostart: autostart);
        Assert.True(viewModel.CanStartWithWindows);
        Assert.True(viewModel.StartWithWindows);

        // The entry may point to another copy of Meerkat; storing other settings must leave it alone.
        viewModel.Opacity = 0.5;
        Assert.Equal(0, autostart.Changes);

        viewModel.StartWithWindows = false;
        Assert.False(autostart.IsEnabled);
        Assert.Equal(1, autostart.Changes);

        Assert.False(CreateViewModel(new SettingsService(FilePath)).CanStartWithWindows);
    }

    [Fact]
    public void SettingsViewModel_OpensTheSettingsFileOnRequest()
    {
        var opened = 0;
        var viewModel = new SettingsViewModel(new SettingsService(FilePath), [new MonitorOption(null, "Primary monitor")], [], _updates.ViewModel)
        {
            OpenSettingsFile = () => opened++,
        };

        viewModel.OpenFileCommand.Execute(null);

        Assert.Equal(1, opened);
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

    private SettingsViewModel CreateViewModel(SettingsService service, params WidgetPageViewModel[] widgets) =>
        new(service, [new MonitorOption(null, "Primary monitor")], widgets, _updates.ViewModel);

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
