using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meerkat.App.Hotkeys;
using Meerkat.App.Plugins;
using Meerkat.App.Settings;
using Meerkat.App.Startup;
using Meerkat.App.Updates;

namespace Meerkat.App.SettingsUi;

public sealed record MonitorOption(string? DeviceName, string DisplayName);

public sealed record ChannelOption(UpdateChannel Channel, string DisplayName);

/// <summary>A widget as the settings show it: whether it is switched on, and its own settings if it has any.</summary>
/// <param name="id">The id of the widget.</param>
/// <param name="title">The name of the widget, which is also what its entry is called.</param>
/// <param name="isEnabled">Whether the widget is switched on.</param>
/// <param name="view">The settings page of the widget; null for a widget that has nothing to configure.</param>
public sealed partial class WidgetPageViewModel(string id, string title, bool isEnabled, FrameworkElement? view = null) : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled = isEnabled;

    public string Id { get; } = id;

    public string Title { get; } = title;

    public FrameworkElement? View { get; } = view;

    public bool HasView => View is not null;
}

/// <summary>An entry of the settings navigation and the page it opens.</summary>
/// <param name="title">What the entry is called.</param>
/// <param name="widget">The widget the page belongs to; null for the pages of the shell.</param>
/// <param name="badge">Marks the entry while an update is pending.</param>
public sealed partial class NavigationItem(string title, WidgetPageViewModel? widget = null, UpdateViewModel? badge = null) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public string Title { get; } = title;

    public WidgetPageViewModel? Widget { get; } = widget;

    /// <summary>Marks the entry while an update is pending.</summary>
    public UpdateViewModel? Badge { get; } = badge;
}

/// <summary>A hotkey offered by a widget, as edited in the settings.</summary>
/// <param name="settingsKey">Key under which the chosen combination is stored.</param>
/// <param name="widgetId">The widget the hotkey belongs to; it is only offered while that widget is enabled.</param>
public sealed partial class WidgetHotkeyViewModel(string settingsKey, string title, string widgetTitle, string gesture, string? widgetId = null) : ObservableObject
{
    [ObservableProperty]
    private string _gesture = gesture;

    [ObservableProperty]
    private bool _isAvailable = true;

    public string? WidgetId { get; } = widgetId;

    public string SettingsKey { get; } = settingsKey;

    public string Title { get; } = title;

    public string WidgetTitle { get; } = widgetTitle;
}

/// <summary>A plugin as the settings list it: what it added, or why it does not work.</summary>
public sealed class PluginViewModel(PluginInfo plugin)
{
    public string Name { get; } = plugin.Name;

    public string? Version { get; } = plugin.Version;

    public bool HasProblem { get; } = plugin.Problems.Count > 0;

    /// <summary>The widgets the plugin added, and below them whatever went wrong.</summary>
    public string Detail { get; } = string.Join(
        Environment.NewLine,
        new[] { plugin.Widgets.Count > 0 ? $"Adds {string.Join(", ", plugin.Widgets)}" : null }.Concat(plugin.Problems).Where(line => line is not null));

    public string Directory { get; } = plugin.Directory;
}

/// <summary>
/// The settings of the shell. There is nothing to save: every change is stored and applied the moment
/// it is made, and what cannot be stored, like two hotkeys with the same combination, is said so instead.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private const string GeneralTitle = "General";
    private const string MachinesTitle = "Machines";

    private readonly SettingsService _settings;
    private readonly IAutostart? _autostart;
    private readonly IReadOnlyList<WidgetHotkeyViewModel> _widgetHotkeys = [];

    // While the page fills itself in, nothing the user did has changed.
    private readonly bool _isLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneralSelected), nameof(IsMachinesSelected), nameof(SelectedWidget), nameof(PageTitle))]
    private NavigationItem _selectedItem;

    [ObservableProperty]
    private string _togglePanelHotkey;

    [ObservableProperty]
    private string _toggleVisibilityHotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpacityText))]
    private double _opacity;

    [ObservableProperty]
    private MonitorOption _selectedMonitor;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private bool _checkForUpdatesAutomatically;

    [ObservableProperty]
    private bool _downloadUpdatesInBackground;

    [ObservableProperty]
    private ChannelOption _selectedChannel;

    /// <summary>A hotkey that Windows refused, as another program holds it.</summary>
    [ObservableProperty]
    private string? _hotkeyWarning;

    /// <summary>Why the last change was not stored; null once everything is stored again.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <param name="machines">The page on which the machines for all widgets are entered; null leaves it out.</param>
    public SettingsViewModel(
        SettingsService settings,
        IReadOnlyList<MonitorOption> monitors,
        IReadOnlyList<WidgetPageViewModel> widgets,
        UpdateViewModel update,
        MachinesPageViewModel? machines = null,
        IReadOnlyList<WidgetHotkeyViewModel>? widgetHotkeys = null,
        IAutostart? autostart = null,
        string? hotkeyWarning = null,
        IReadOnlyList<PluginViewModel>? plugins = null)
    {
        Plugins = plugins ?? [];
        _settings = settings;
        _autostart = autostart;
        _widgetHotkeys = widgetHotkeys ?? [];
        Monitors = monitors;
        Widgets = widgets;
        Machines = machines;
        Update = update;

        // Half as many entries as there are things to set: the shell has one page, and each widget has its own.
        NavigationItems.Add(new NavigationItem(GeneralTitle, badge: update));
        if (machines is not null)
        {
            NavigationItems.Add(new NavigationItem(MachinesTitle));
        }

        foreach (var widget in widgets)
        {
            NavigationItems.Add(new NavigationItem(widget.Title, widget));
            widget.PropertyChanged += (_, _) => Apply();
        }

        foreach (var hotkey in _widgetHotkeys)
        {
            hotkey.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(WidgetHotkeyViewModel.Gesture))
                {
                    Apply();
                }
            };
        }

        var current = settings.Current;
        _togglePanelHotkey = HotkeyGesture.ToDisplayText(current.TogglePanelHotkey);
        _toggleVisibilityHotkey = HotkeyGesture.ToDisplayText(current.ToggleVisibilityHotkey);
        _opacity = current.Opacity;
        _selectedMonitor = monitors.FirstOrDefault(option => option.DeviceName == current.Monitor) ?? monitors[0];
        _startWithWindows = autostart?.IsEnabled ?? false;
        _checkForUpdatesAutomatically = current.Updates.CheckAutomatically;
        _downloadUpdatesInBackground = current.Updates.DownloadInBackground;
        _selectedChannel = Channels.First(option => option.Channel == current.Updates.Channel);
        _hotkeyWarning = hotkeyWarning;
        _selectedItem = NavigationItems[0];
        _selectedItem.IsSelected = true;
        UpdateHotkeyAvailability();
        _isLoading = false;
    }

    /// <summary>Raised after a change was stored, for the shell to act on it.</summary>
    public event EventHandler? Applied;

    /// <summary>Raised in addition when the change was about how updates are looked for.</summary>
    public event EventHandler? UpdateSettingsApplied;

    /// <summary>Releases the application's global hotkeys while the user records a new one.</summary>
    public Func<IDisposable>? SuspendHotkeys { get; init; }

    /// <summary>Opens the settings file in whatever the user edits such files with.</summary>
    public Action? OpenSettingsFile { get; init; }

    /// <summary>Opens the folder plugins are installed into.</summary>
    public Action? OpenPluginsFolder { get; init; }

    public ObservableCollection<NavigationItem> NavigationItems { get; } = [];

    public IReadOnlyList<PluginViewModel> Plugins { get; }

    public bool HasPlugins => Plugins.Count > 0;

    public string PluginInfo => "Plugins add widgets that are not part of Meerkat. A plugin is a folder that is copied into the plugins folder; Meerkat finds it at its next start. Plugins can do whatever you can do on this computer, so only install those you trust.";

    public IReadOnlyList<MonitorOption> Monitors { get; }

    public IReadOnlyList<ChannelOption> Channels { get; } =
    [
        new(UpdateChannel.Stable, "Stable"),
        new(UpdateChannel.Prerelease, "Pre-release"),
    ];

    public IReadOnlyList<WidgetPageViewModel> Widgets { get; }

    public IReadOnlyList<WidgetHotkeyViewModel> WidgetHotkeys => _widgetHotkeys;

    public MachinesPageViewModel? Machines { get; }

    public UpdateViewModel Update { get; }

    public bool CanStartWithWindows => _autostart is not null;

    public bool IsGeneralSelected => SelectedItem.Title == GeneralTitle && SelectedItem.Widget is null;

    public bool IsMachinesSelected => SelectedItem.Title == MachinesTitle && SelectedItem.Widget is null;

    /// <summary>The widget whose page is shown; null on the pages of the shell.</summary>
    public WidgetPageViewModel? SelectedWidget => SelectedItem.Widget;

    public string PageTitle => SelectedItem.Title;

    public string OpacityText => $"{Math.Round(Opacity * 100).ToString(CultureInfo.CurrentCulture)} %";

    public string HotkeyInfo => "Click a field and press the new key combination. Esc keeps the current one. While Meerkat is hidden with its hotkey, nothing is polled.";

    public string MonitorInfo => "Status bar and panel appear on this monitor. To move the status bar, drag it by the handle at its end; its place is remembered relative to the screen, so it fits every monitor.";

    public string AutostartInfo => "Adds Meerkat to your startup apps. If you move Meerkat.exe afterwards, switch this off and on again.";

    public string SettingsFileInfo => "Opens the file all settings are kept in. A few values are only found there, such as exact intervals. Meerkat takes over what you save in it right away.";

    /// <summary>Shows the page of a widget, as when the widget itself asks for its settings.</summary>
    public void SelectWidget(string widgetId)
    {
        if (NavigationItems.FirstOrDefault(item => item.Widget?.Id == widgetId) is { } item)
        {
            SelectedItem = item;
        }
    }

    [RelayCommand]
    private void OpenFile() => OpenSettingsFile?.Invoke();

    [RelayCommand]
    private void OpenPlugins() => OpenPluginsFolder?.Invoke();

    partial void OnSelectedItemChanged(NavigationItem? oldValue, NavigationItem newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        newValue.IsSelected = true;
    }

    partial void OnTogglePanelHotkeyChanged(string value) => Apply();

    partial void OnToggleVisibilityHotkeyChanged(string value) => Apply();

    partial void OnOpacityChanged(double value) => Apply();

    partial void OnSelectedMonitorChanged(MonitorOption value) => Apply();

    partial void OnStartWithWindowsChanged(bool value) => Apply();

    partial void OnCheckForUpdatesAutomaticallyChanged(bool value) => Apply(updates: true);

    partial void OnDownloadUpdatesInBackgroundChanged(bool value) => Apply(updates: true);

    partial void OnSelectedChannelChanged(ChannelOption value) => Apply(updates: true);

    /// <summary>A widget that is switched off answers no hotkeys, so there is none to set for it.</summary>
    private void UpdateHotkeyAvailability()
    {
        foreach (var hotkey in _widgetHotkeys)
        {
            hotkey.IsAvailable = hotkey.WidgetId is null || Widgets.FirstOrDefault(widget => widget.Id == hotkey.WidgetId) is not { IsEnabled: false };
        }
    }

    /// <summary>Stores everything as it stands now, unless something stands in the way.</summary>
    private void Apply(bool updates = false)
    {
        if (_isLoading)
        {
            return;
        }

        UpdateHotkeyAvailability();
        if (!HotkeyGesture.TryParse(TogglePanelHotkey, out var togglePanel)
            || !HotkeyGesture.TryParse(ToggleVisibilityHotkey, out var toggleVisibility))
        {
            ErrorMessage = "Not stored: hotkeys need at least one modifier and a key, e.g. Ctrl+Alt+O.";
            return;
        }

        var gestures = new List<HotkeyGesture> { togglePanel, toggleVisibility };
        foreach (var hotkey in _widgetHotkeys)
        {
            if (!HotkeyGesture.TryParse(hotkey.Gesture, out var gesture))
            {
                ErrorMessage = "Not stored: hotkeys need at least one modifier and a key, e.g. Ctrl+Alt+O.";
                return;
            }

            gestures.Add(gesture);
        }

        if (gestures.Distinct().Count() != gestures.Count)
        {
            ErrorMessage = "Not stored: every hotkey needs its own combination.";
            return;
        }

        try
        {
            var current = _settings.Current;
            current.TogglePanelHotkey = togglePanel.ToString();
            current.ToggleVisibilityHotkey = toggleVisibility.ToString();
            foreach (var hotkey in _widgetHotkeys)
            {
                current.WidgetHotkeys[hotkey.SettingsKey] = HotkeyGesture.ToDisplayText(hotkey.Gesture);
            }

            current.Opacity = Opacity;
            current.Monitor = SelectedMonitor.DeviceName;
            current.DisabledWidgets = [.. Widgets.Where(widget => !widget.IsEnabled).Select(widget => widget.Id)];
            current.Updates.CheckAutomatically = CheckForUpdatesAutomatically;
            current.Updates.DownloadInBackground = DownloadUpdatesInBackground;
            current.Updates.Channel = SelectedChannel.Channel;
            _settings.Save();

            // Only touched when changed: the entry may belong to another copy of Meerkat.
            if (_autostart is not null && _autostart.IsEnabled != StartWithWindows)
            {
                _autostart.SetEnabled(StartWithWindows);
            }
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Not stored: {exception.Message}";
            return;
        }

        ErrorMessage = null;
        Applied?.Invoke(this, EventArgs.Empty);
        if (updates)
        {
            UpdateSettingsApplied?.Invoke(this, EventArgs.Empty);
        }
    }
}
