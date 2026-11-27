using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Meerkat.Abstractions;
using Meerkat.App.Hotkeys;
using Meerkat.App.Settings;
using Meerkat.App.Updates;

namespace Meerkat.App.SettingsUi;

public sealed record MonitorOption(string? DeviceName, string DisplayName);

public sealed record ChannelOption(UpdateChannel Channel, string DisplayName);

public sealed record WidgetSettingsSection(string Title, IWidgetSettingsPage Page, FrameworkElement View);

/// <summary>An entry of the settings navigation; <see cref="Badge"/> marks the entry while an update is pending.</summary>
public sealed record NavigationItem(string Title, UpdateViewModel? Badge = null);

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
public sealed partial class WidgetHotkeyViewModel(string settingsKey, string title, string widgetTitle, string gesture) : ObservableObject
{
    [ObservableProperty]
    private string _gesture = gesture;

    public string SettingsKey { get; } = settingsKey;

    public string Title { get; } = title;

    public string WidgetTitle { get; } = widgetTitle;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly string[] ShellSectionTitles = ["Hotkeys", "Appearance", "Widgets"];

    private readonly SettingsService _settings;

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
        NavigationItems =
        [
            .. ShellSectionTitles.Select(title => new NavigationItem(title)),
            .. widgetSections.Select(section => new NavigationItem(section.Title)),
            new NavigationItem("Updates", update),
        ];

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
    public IReadOnlyList<NavigationItem> NavigationItems { get; }

    public IReadOnlyList<MonitorOption> Monitors { get; }

    public IReadOnlyList<ChannelOption> Channels { get; } =
    [
        new(UpdateChannel.Stable, "Stable"),
        new(UpdateChannel.Prerelease, "Pre-release"),
    ];

    public IReadOnlyList<WidgetToggleViewModel> Widgets { get; }

    public IReadOnlyList<WidgetHotkeyViewModel> WidgetHotkeys { get; init; } = [];

    public IReadOnlyList<WidgetSettingsSection> WidgetSections { get; }

    public UpdateViewModel Update { get; }

    public string SettingsFilePath { get; }

    public string OpacityText => $"{Math.Round(Opacity * 100).ToString(CultureInfo.CurrentCulture)} %";

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
