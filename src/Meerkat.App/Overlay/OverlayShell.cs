using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Meerkat.Abstractions;
using Meerkat.App.Diagnostics;
using Meerkat.App.Hotkeys;
using Meerkat.App.Settings;
using Meerkat.App.SettingsUi;
using Meerkat.App.Tray;
using Meerkat.App.Updates;
using Microsoft.Win32;

namespace Meerkat.App.Overlay;

public sealed class OverlayShell(
    SettingsService settings,
    OverlayState state,
    StatusBarViewModel statusBar,
    PanelViewModel panel,
    OverlayCommands commands,
    IEnumerable<IOverlayWidget> widgets,
    HotkeyService hotkeys,
    TrayIconService tray,
    UpdateService updates,
    UpdateViewModel updateViewModel,
    ApplicationRestart applicationRestart,
    ErrorLog errorLog)
{
    private const int TogglePanelHotkeyId = 1;
    private const int ToggleVisibilityHotkeyId = 2;
    private const double MinimumOpacity = 0.2;
    private const double PanelWidth = 680;

    private static readonly TimeSpan WidgetStopTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AgeRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly List<HostedWidget> _hostedWidgets = [];
    private readonly CancellationTokenSource _lifetime = new();

    private StatusBarWindow? _statusBarWindow;
    private PanelWindow? _panelWindow;
    private SettingsWindow? _settingsWindow;
    private DispatcherTimer? _ageTimer;
    private string? _hotkeyWarning;
    private bool _isExiting;

    public async Task StartAsync()
    {
        _statusBarWindow = new StatusBarWindow { DataContext = statusBar };
        _panelWindow = new PanelWindow { DataContext = panel };
        _panelWindow.Dismissed += (_, _) => SetPanelOpen(false);

        panel.CloseRequested += (_, _) => SetPanelOpen(false);
        panel.SettingsRequested += (_, _) => OpenSettings();
        tray.ToggleVisibilityRequested += (_, _) => ToggleVisibility();
        tray.TogglePanelRequested += (_, _) => TogglePanel();
        tray.SettingsRequested += (_, _) => OpenSettings();
        tray.ExitRequested += async (_, _) => await ExitAsync(restart: false);
        commands.SettingsRequested += (_, _) => OpenSettings();
        updates.RestartRequested += async (_, _) => await ExitAsync(restart: true);
        updateViewModel.PropertyChanged += (_, _) => RefreshStatus();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        foreach (var widget in widgets)
        {
            RegisterWidget(widget);
        }

        ApplySettings();
        _statusBarWindow.Show();

        // Showing the window lets WPF apply its own initial placement, so the monitor bounds are enforced again.
        _statusBarWindow.MoveToBounds(MonitorLocator.ResolveWorkingArea(settings.Current.Monitor));
        state.SetVisible(true);

        // Ages such as "4 min ago" move on even when no widget reports anything new.
        _ageTimer = new DispatcherTimer(AgeRefreshInterval, DispatcherPriority.Background, (_, _) => RefreshStatus(), _statusBarWindow.Dispatcher);

        _ = updates.RunAsync(_lifetime.Token);
        await SyncWidgetsAsync();
        RefreshStatus();
    }

    private void RegisterWidget(IOverlayWidget widget)
    {
        try
        {
            var hosted = new HostedWidget(widget, new WidgetEntryViewModel(widget.Id, widget.Title, widget.Icon));

            // Widgets may report from a background thread; everything the shell shows is updated on the UI thread.
            widget.StatusChanged += (_, _) => _statusBarWindow?.Dispatcher.BeginInvoke(RefreshStatus);
            _hostedWidgets.Add(hosted);
        }
        catch (Exception exception)
        {
            // Without id and title the widget can neither be shown nor configured, so it is skipped entirely.
            errorLog.Write($"Widget {widget.GetType().FullName} could not be registered", exception);
        }
    }

    /// <summary>Starts widgets that are enabled and stops those that were disabled in the settings.</summary>
    private async Task SyncWidgetsAsync()
    {
        foreach (var hosted in _hostedWidgets)
        {
            var isEnabled = !settings.Current.DisabledWidgets.Contains(hosted.Entry.Id);
            if (isEnabled && !hosted.IsActive)
            {
                hosted.IsActive = true;
                panel.AddWidget(hosted.Entry);
                await StartWidgetAsync(hosted);
            }
            else if (!isEnabled && hosted.IsActive)
            {
                hosted.IsActive = false;
                await StopWidgetAsync(hosted);
                panel.RemoveWidget(hosted.Entry);
            }
        }

        RefreshStatus();
    }

    private async Task StartWidgetAsync(HostedWidget hosted)
    {
        try
        {
            hosted.Entry.Failure = null;
            hosted.Entry.Content = hosted.Widget.View;
            hosted.Entry.HeaderContent = hosted.Widget.HeaderView;
            await hosted.Widget.StartAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            errorLog.Write($"Widget '{hosted.Entry.Id}' failed to start", exception);
            hosted.FailedSince = DateTimeOffset.Now;
            hosted.Entry.Content = null;
            hosted.Entry.HeaderContent = null;
            hosted.Entry.Failure = new WidgetFailureViewModel(
                $"{exception.GetType().Name}: {exception.Message}",
                new AsyncRelayCommand(() => RestartWidgetAsync(hosted)),
                new RelayCommand(OpenErrorLog));
        }
    }

    private async Task StopWidgetAsync(HostedWidget hosted)
    {
        try
        {
            // A widget that hangs while stopping must not block the shell.
            await hosted.Widget.StopAsync(CancellationToken.None).WaitAsync(WidgetStopTimeout);
        }
        catch (Exception exception)
        {
            errorLog.Write($"Widget '{hosted.Entry.Id}' failed to stop", exception);
        }
    }

    private async Task RestartWidgetAsync(HostedWidget hosted)
    {
        await StopWidgetAsync(hosted);
        await StartWidgetAsync(hosted);
        RefreshStatus();
    }

    /// <summary>Collects what every widget reports and distributes it to the status bar and the panel.</summary>
    private void RefreshStatus()
    {
        var reports = new List<WidgetReport>();
        foreach (var hosted in _hostedWidgets.Where(hosted => hosted.IsActive))
        {
            var report = GetReport(hosted);
            hosted.Entry.Badge = report.Alerts.Count == 0 ? WidgetSeverity.Ok : report.Alerts.Max(alert => alert.Severity);
            reports.Add(report);
        }

        statusBar.Update(reports, DateTimeOffset.Now, state.PanelHotkeyText, updateViewModel.Notice);
        panel.SetAttentionCount(reports.Sum(report => report.Alerts.Count));
    }

    private WidgetReport GetReport(HostedWidget hosted)
    {
        if (hosted.Entry.Failure is { } failure)
        {
            return new WidgetReport(
                new WidgetStatus(hosted.Entry.Title, "–", WidgetSeverity.Error),
                [new WidgetAlert(WidgetSeverity.Error, $"{hosted.Entry.Title} widget was suspended", failure.Detail, hosted.FailedSince)]);
        }

        try
        {
            return new WidgetReport(hosted.Widget.Status, hosted.Widget.Alerts);
        }
        catch (Exception exception)
        {
            // A widget that cannot even report its status must not take the status bar down with it.
            errorLog.Write($"Widget '{hosted.Entry.Id}' failed to report its status", exception);
            return new WidgetReport(null, []);
        }
    }

    private void OpenErrorLog()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.ErrorLogFile) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            errorLog.Write("Opening the error log failed", exception);
        }
    }

    private void TogglePanel() => SetPanelOpen(!state.IsPanelOpen);

    private void SetPanelOpen(bool isOpen, bool animated = true)
    {
        if (_panelWindow is null || state.IsPanelOpen == isOpen)
        {
            return;
        }

        // The state is set first: hiding the window deactivates it, which would otherwise ask to close it again.
        state.SetPanelOpen(isOpen);
        if (isOpen)
        {
            _panelWindow.MoveToBounds(MonitorLocator.ResolvePanelBounds(settings.Current.Monitor, PanelWidth));
            _panelWindow.Open();
        }
        else
        {
            _panelWindow.Dismiss(animated);
        }
    }

    private void ToggleVisibility()
    {
        if (_statusBarWindow is null)
        {
            return;
        }

        if (state.IsVisible)
        {
            // Everything disappears at once; a panel still sliding out would look like a glitch.
            SetPanelOpen(false, animated: false);
            _statusBarWindow.Hide();
        }
        else
        {
            _statusBarWindow.Show();
        }

        state.SetVisible(!state.IsVisible);
    }

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var settingsViewModel = new SettingsViewModel(
            settings,
            MonitorLocator.GetOptions(),
            [.. _hostedWidgets.Select(hosted => new WidgetToggleViewModel(
                hosted.Entry.Id,
                hosted.Entry.Title,
                hosted.Widget.GetType().Namespace ?? string.Empty,
                !settings.Current.DisabledWidgets.Contains(hosted.Entry.Id)))],
            CreateSettingsSections(),
            updateViewModel,
            _hotkeyWarning,
            AppPaths.SettingsFileDisplayPath)
        {
            SuspendHotkeys = hotkeys.Suspend,
        };
        settingsViewModel.Saved += async (_, _) =>
        {
            ApplySettings();
            await SyncWidgetsAsync();

            // The channel or the automatic check may have changed.
            updateViewModel.Refresh();
            if (settings.Current.Updates.CheckAutomatically)
            {
                await updates.CheckAsync(userInitiated: false);
            }
        };

        _settingsWindow = new SettingsWindow { DataContext = settingsViewModel };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private List<WidgetSettingsSection> CreateSettingsSections()
    {
        var sections = new List<WidgetSettingsSection>();
        foreach (var hosted in _hostedWidgets)
        {
            try
            {
                if (hosted.Widget.SettingsPage is { } page)
                {
                    sections.Add(new WidgetSettingsSection(hosted.Entry.Title, page, page.CreateView()));
                }
            }
            catch (Exception exception)
            {
                errorLog.Write($"Widget '{hosted.Entry.Id}' failed to create its settings page", exception);
            }
        }

        return sections;
    }

    private void ApplySettings()
    {
        if (_statusBarWindow is null)
        {
            return;
        }

        var current = settings.Current;
        _statusBarWindow.Opacity = Math.Clamp(current.Opacity, MinimumOpacity, 1.0);
        ApplyBounds();

        var failedHotkeys = new List<string>();
        RegisterHotkey(TogglePanelHotkeyId, current.TogglePanelHotkey, TogglePanel, failedHotkeys);
        RegisterHotkey(ToggleVisibilityHotkeyId, current.ToggleVisibilityHotkey, ToggleVisibility, failedHotkeys);
        state.SetHotkeyTexts(
            HotkeyGesture.ToDisplayText(current.TogglePanelHotkey),
            HotkeyGesture.ToDisplayText(current.ToggleVisibilityHotkey));

        _hotkeyWarning = failedHotkeys.Count > 0
            ? $"{string.Join(" and ", failedHotkeys)} could not be registered; another application may already be using it."
            : null;
        if (_hotkeyWarning is not null)
        {
            tray.ShowWarning(_hotkeyWarning);
        }

        RefreshStatus();
    }

    private void ApplyBounds()
    {
        var monitor = settings.Current.Monitor;
        _statusBarWindow?.MoveToBounds(MonitorLocator.ResolveWorkingArea(monitor));
        if (state.IsPanelOpen)
        {
            _panelWindow?.MoveToBounds(MonitorLocator.ResolvePanelBounds(monitor, PanelWidth));
        }
    }

    private void RegisterHotkey(int id, string text, Action handler, List<string> failures)
    {
        if (!HotkeyGesture.TryParse(text, out var gesture) || !hotkeys.Register(id, gesture, handler))
        {
            failures.Add(HotkeyGesture.ToDisplayText(text));
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        _statusBarWindow?.Dispatcher.BeginInvoke(ApplyBounds);

    private async Task ExitAsync(bool restart)
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _ageTimer?.Stop();
        await _lifetime.CancelAsync();
        if (restart)
        {
            applicationRestart.Request();
        }
        else
        {
            updates.InstallPendingOnExit();
        }

        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        foreach (var hosted in _hostedWidgets.Where(hosted => hosted.IsActive))
        {
            await StopWidgetAsync(hosted);
        }

        _settingsWindow?.Close();
        _panelWindow?.Close();
        _statusBarWindow?.Close();
        Application.Current.Shutdown();
    }

    private sealed class HostedWidget(IOverlayWidget widget, WidgetEntryViewModel entry)
    {
        public IOverlayWidget Widget { get; } = widget;

        public WidgetEntryViewModel Entry { get; } = entry;

        public bool IsActive { get; set; }

        /// <summary>When the widget was suspended, for the age shown with the resulting alert.</summary>
        public DateTimeOffset FailedSince { get; set; }
    }
}
