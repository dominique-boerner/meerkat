using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meerkat.Abstractions;
using Meerkat.App.Hotkeys;
using Meerkat.App.Settings;

namespace Meerkat.App.SettingsUi;

public sealed record MonitorOption(string? DeviceName, string DisplayName);

public sealed record WidgetSettingsSection(string Title, IWidgetSettingsPage Page, FrameworkElement View);

public sealed partial class WidgetToggleViewModel(string id, string title, string typeName, bool isEnabled) : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled = isEnabled;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetPositionCommand))]
    private bool _isResetPending;

    public string Id { get; } = id;

    public string Title { get; } = title;

    public string TypeName { get; } = typeName;

    [RelayCommand(CanExecute = nameof(CanResetPosition))]
    private void ResetPosition() => IsResetPending = true;

    private bool CanResetPosition() => !IsResetPending;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly string[] ShellSectionTitles = ["Hotkeys", "Appearance", "Widgets"];

    private readonly SettingsService _settings;

    [ObservableProperty]
    private string _toggleModeHotkey;

    [ObservableProperty]
    private string _toggleVisibilityHotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpacityText))]
    private double _opacity;

    [ObservableProperty]
    private MonitorOption _selectedMonitor;

    [ObservableProperty]
    private string? _hotkeyWarning;

    [ObservableProperty]
    private string? _errorMessage;

    public SettingsViewModel(
        SettingsService settings,
        IReadOnlyList<MonitorOption> monitors,
        IReadOnlyList<WidgetToggleViewModel> widgets,
        IReadOnlyList<WidgetSettingsSection> widgetSections,
        string? hotkeyWarning,
        string settingsFilePath)
    {
        _settings = settings;
        Monitors = monitors;
        Widgets = widgets;
        WidgetSections = widgetSections;
        SettingsFilePath = settingsFilePath;
        NavigationItems = [.. ShellSectionTitles, .. widgetSections.Select(section => section.Title)];

        var current = settings.Current;
        _toggleModeHotkey = HotkeyGesture.ToDisplayText(current.ToggleModeHotkey);
        _toggleVisibilityHotkey = HotkeyGesture.ToDisplayText(current.ToggleVisibilityHotkey);
        _opacity = current.Opacity;
        _selectedMonitor = monitors.FirstOrDefault(option => option.DeviceName == current.Monitor) ?? monitors[0];
        _hotkeyWarning = hotkeyWarning;
    }

    public event EventHandler? Saved;

    public IReadOnlyList<string> NavigationItems { get; }

    public IReadOnlyList<MonitorOption> Monitors { get; }

    public IReadOnlyList<WidgetToggleViewModel> Widgets { get; }

    public IReadOnlyList<WidgetSettingsSection> WidgetSections { get; }

    public string SettingsFilePath { get; }

    public string OpacityText => $"{Math.Round(Opacity * 100).ToString(CultureInfo.CurrentCulture)} %";

    public bool TrySave()
    {
        if (!HotkeyGesture.TryParse(ToggleModeHotkey, out var toggleMode)
            || !HotkeyGesture.TryParse(ToggleVisibilityHotkey, out var toggleVisibility))
        {
            ErrorMessage = "Hotkeys need at least one modifier and a key, e.g. Ctrl+Alt+O.";
            return false;
        }

        if (toggleMode == toggleVisibility)
        {
            ErrorMessage = "The two hotkeys must differ.";
            return false;
        }

        try
        {
            foreach (var section in WidgetSections)
            {
                section.Page.Apply();
            }

            var current = _settings.Current;
            current.ToggleModeHotkey = toggleMode.ToString();
            current.ToggleVisibilityHotkey = toggleVisibility.ToString();
            current.Opacity = Opacity;
            current.Monitor = SelectedMonitor.DeviceName;
            current.DisabledWidgets = [.. Widgets.Where(widget => !widget.IsEnabled).Select(widget => widget.Id)];
            foreach (var widget in Widgets.Where(widget => widget.IsResetPending))
            {
                LayoutService.Reset(current, widget.Id);
            }

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
