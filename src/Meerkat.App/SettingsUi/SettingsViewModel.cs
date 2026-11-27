using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Meerkat.Abstractions;
using Meerkat.App.Hotkeys;
using Meerkat.App.Settings;
using Meerkat.App.Startup;
using Meerkat.App.Updates;

namespace Meerkat.App.SettingsUi;

public sealed record MonitorOption(string? DeviceName, string DisplayName);

public sealed record ChannelOption(UpdateChannel Channel, string DisplayName);

/// <param name="WidgetId">The widget the page belongs to; the page is only offered while that widget is enabled.</param>
public sealed record WidgetSettingsSection(string Title, IWidgetSettingsPage Page, FrameworkElement View, string? WidgetId = null);

/// <summary>An entry of the settings navigation; <see cref="Badge"/> marks the entry while an update is pending.</summary>
/// <param name="Section">The settings page of a widget this entry opens; null for the pages of the shell.</param>
public sealed record NavigationItem(string Title, UpdateViewModel? Badge = null, WidgetSettingsSection? Section = null);

public sealed partial class WidgetToggleViewModel(string id, string title, string typeName, bool isEnabled) : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled = isEnabled;

    public string Id { get; } = id;

    public string Title { get; } = title;

    public string TypeName { get; } = typeName;
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

public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly string[] ShellSectionTitles = ["Hotkeys", "General", "Widgets"];

    private readonly SettingsService _settings;
    private readonly IAutostart? _autostart;
    private readonly IReadOnlyList<NavigationItem> _widgetItems;
    private IReadOnlyList<WidgetHotkeyViewModel> _widgetHotkeys = [];

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

    [ObservableProperty]
    private string? _hotkeyWarning;

    [ObservableProperty]
    private string? _errorMessage;

    public SettingsViewModel(
        SettingsService settings,
        IReadOnlyList<MonitorOption> monitors,
        IReadOnlyList<WidgetToggleViewModel> widgets,
        IReadOnlyList<WidgetSettingsSection> widgetSections,
        UpdateViewModel update,
        string? hotkeyWarning,
        string settingsFilePath)
    {
        _settings = settings;
        Monitors = monitors;
        Widgets = widgets;
        WidgetSections = widgetSections;
        Update = update;
        SettingsFilePath = settingsFilePath;
        _widgetItems = [.. widgetSections.Select(section => new NavigationItem(section.Title, Section: section))];
        NavigationItems =
        [
            .. ShellSectionTitles.Select(title => new NavigationItem(title)),
            .. _widgetItems.Where(item => IsEnabled(item.Section!.WidgetId)),
            new NavigationItem("Updates", update),
        ];

        // Switching a widget off takes its settings page away at once, switching it on brings the page back.
        foreach (var widget in widgets)
        {
            widget.PropertyChanged += (_, _) => UpdateAvailability();
        }

        var current = settings.Current;
        _togglePanelHotkey = HotkeyGesture.ToDisplayText(current.TogglePanelHotkey);
        _toggleVisibilityHotkey = HotkeyGesture.ToDisplayText(current.ToggleVisibilityHotkey);
        _opacity = current.Opacity;
        _selectedMonitor = monitors.FirstOrDefault(option => option.DeviceName == current.Monitor) ?? monitors[0];
        _checkForUpdatesAutomatically = current.Updates.CheckAutomatically;
        _downloadUpdatesInBackground = current.Updates.DownloadInBackground;
        _selectedChannel = Channels.First(option => option.Channel == current.Updates.Channel);
        _hotkeyWarning = hotkeyWarning;
    }

    public event EventHandler? Saved;

    /// <summary>Releases the application's global hotkeys while the user records a new one.</summary>
    public Func<IDisposable>? SuspendHotkeys { get; init; }

    /// <summary>Shell sections first, then one entry per widget settings page, then the update page.</summary>
    public ObservableCollection<NavigationItem> NavigationItems { get; }

    public IReadOnlyList<MonitorOption> Monitors { get; }

    public IReadOnlyList<ChannelOption> Channels { get; } =
    [
        new(UpdateChannel.Stable, "Stable"),
        new(UpdateChannel.Prerelease, "Pre-release"),
    ];

    public IReadOnlyList<WidgetToggleViewModel> Widgets { get; }

    public IReadOnlyList<WidgetHotkeyViewModel> WidgetHotkeys
    {
        get => _widgetHotkeys;
        init
        {
            _widgetHotkeys = value;
            UpdateAvailability();
        }
    }

    /// <summary>Switches the start with Windows on and off; without it the option is not offered.</summary>
    public IAutostart? Autostart
    {
        get => _autostart;
        init
        {
            _autostart = value;
            StartWithWindows = value?.IsEnabled ?? false;
        }
    }

    public bool CanStartWithWindows => _autostart is not null;

    public IReadOnlyList<WidgetSettingsSection> WidgetSections { get; }

    public UpdateViewModel Update { get; }

    public string SettingsFilePath { get; }

    public string OpacityText => $"{Math.Round(Opacity * 100).ToString(CultureInfo.CurrentCulture)} %";

    private bool IsEnabled(string? widgetId) => widgetId is null || Widgets.FirstOrDefault(widget => widget.Id == widgetId) is not { IsEnabled: false };

    /// <summary>
    /// A widget that is switched off offers nothing to configure: neither its settings page nor its hotkeys.
    /// What was set for it is kept, so it is all there again when the widget is switched back on.
    /// </summary>
    private void UpdateAvailability()
    {
        foreach (var hotkey in _widgetHotkeys)
        {
            hotkey.IsAvailable = IsEnabled(hotkey.WidgetId);
        }

        var position = ShellSectionTitles.Length;
        foreach (var item in _widgetItems)
        {
            var isListed = NavigationItems.Contains(item);
            if (IsEnabled(item.Section!.WidgetId))
            {
                if (!isListed)
                {
                    NavigationItems.Insert(position, item);
                }

                position++;
            }
            else if (isListed)
            {
                NavigationItems.Remove(item);
            }
        }
    }

    public bool TrySave()
    {
        if (!HotkeyGesture.TryParse(TogglePanelHotkey, out var togglePanel)
            || !HotkeyGesture.TryParse(ToggleVisibilityHotkey, out var toggleVisibility))
        {
            ErrorMessage = "Hotkeys need at least one modifier and a key, e.g. Ctrl+Alt+O.";
            return false;
        }

        var gestures = new List<HotkeyGesture> { togglePanel, toggleVisibility };
        foreach (var hotkey in WidgetHotkeys)
        {
            if (!HotkeyGesture.TryParse(hotkey.Gesture, out var gesture))
            {
                ErrorMessage = "Hotkeys need at least one modifier and a key, e.g. Ctrl+Alt+O.";
                return false;
            }

            gestures.Add(gesture);
        }

        if (gestures.Distinct().Count() != gestures.Count)
        {
            ErrorMessage = "Every hotkey needs its own combination.";
            return false;
        }

        try
        {
            foreach (var section in WidgetSections)
            {
                section.Page.Apply();
            }

            var current = _settings.Current;
            current.TogglePanelHotkey = togglePanel.ToString();
            current.ToggleVisibilityHotkey = toggleVisibility.ToString();
            foreach (var hotkey in WidgetHotkeys)
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
            ErrorMessage = $"Saving failed: {exception.Message}";
            return false;
        }

        ErrorMessage = null;
        Saved?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
