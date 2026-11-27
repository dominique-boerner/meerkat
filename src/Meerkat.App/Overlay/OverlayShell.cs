using System.Diagnostics;
using System.Windows;
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
    LayoutService layouts,
    OverlayState state,
    OverlayViewModel viewModel,
    OverlayCommands commands,
    IEnumerable<IOverlayWidget> widgets,
    HotkeyService hotkeys,
    TrayIconService tray,
    UpdateService updates,
    UpdateViewModel updateViewModel,
    ApplicationRestart applicationRestart,
    ErrorLog errorLog)
{
    private const int ToggleModeHotkeyId = 1;
    private const int ToggleVisibilityHotkeyId = 2;
    private const double MinimumOpacity = 0.2;
    private const double DefaultWidgetOffset = 40;

    private static readonly TimeSpan WidgetStopTimeout = TimeSpan.FromSeconds(3);

    private readonly List<HostedWidget> _hostedWidgets = [];
    private readonly CancellationTokenSource _lifetime = new();

    private OverlayWindow? _window;
    private SettingsWindow? _settingsWindow;
    private string? _hotkeyWarning;
    private bool _isExiting;

    public async Task StartAsync()
    {
        _window = new OverlayWindow { DataContext = viewModel };
        _window.LayoutCommitted += (_, _) => SaveLayout();
        _window.SizeChanged += (_, args) => viewModel.SetBounds(args.NewSize);

        tray.ToggleVisibilityRequested += (_, _) => ToggleVisibility();
        tray.ToggleModeRequested += (_, _) => ToggleMode();
        tray.SettingsRequested += (_, _) => OpenSettings();
        tray.ExitRequested += async (_, _) => await ExitAsync(restart: false);
        commands.SettingsRequested += (_, _) => OpenSettings();
        updates.RestartRequested += async (_, _) => await ExitAsync(restart: true);
        updateViewModel.PropertyChanged += (_, _) => state.SetNotice(updateViewModel.Notice);
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        foreach (var widget in widgets)
        {
            RegisterWidget(widget);
        }

        ApplySettings();
        ApplyMode(OverlayMode.Display);
        _window.Show();

        // Showing the window lets WPF apply its own initial placement, so the monitor bounds are enforced again.
        _window.MoveToBounds(MonitorLocator.ResolveWorkingArea(settings.Current.Monitor));
        state.SetVisible(true);

        _ = updates.RunAsync(_lifetime.Token);
        await SyncWidgetsAsync();
    }

    private void RegisterWidget(IOverlayWidget widget)
    {
        try
        {
            var offset = DefaultWidgetOffset * (_hostedWidgets.Count + 1);
            var size = widget.DefaultSize;
            var defaultLayout = new WidgetLayout { Left = offset, Top = offset, Width = size.Width, Height = size.Height };
            _hostedWidgets.Add(new HostedWidget(widget, new WidgetHostViewModel(widget.Id, widget.Title, defaultLayout)));
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
            var isEnabled = !settings.Current.DisabledWidgets.Contains(hosted.Host.Id);
            if (isEnabled && !hosted.IsActive)
            {
                hosted.IsActive = true;
                hosted.Host.ApplyLayout(layouts.Find(hosted.Host.Id) ?? hosted.Host.DefaultLayout);
                viewModel.AddHost(hosted.Host);
                await StartWidgetAsync(hosted);
            }
            else if (!isEnabled && hosted.IsActive)
            {
                hosted.IsActive = false;
                await StopWidgetAsync(hosted);
                viewModel.RemoveHost(hosted.Host);
            }
        }
    }

    private async Task StartWidgetAsync(HostedWidget hosted)
    {
        try
        {
            hosted.Host.Failure = null;
            hosted.Host.Content = hosted.Widget.View;
            hosted.Host.HeaderContent = hosted.Widget.HeaderView;
            await hosted.Widget.StartAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            errorLog.Write($"Widget '{hosted.Host.Id}' failed to start", exception);
            hosted.Host.Content = null;
            hosted.Host.HeaderContent = null;
            hosted.Host.Failure = new WidgetFailureViewModel(
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
            errorLog.Write($"Widget '{hosted.Host.Id}' failed to stop", exception);
        }
    }

    private async Task RestartWidgetAsync(HostedWidget hosted)
    {
        await StopWidgetAsync(hosted);
        await StartWidgetAsync(hosted);
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

    private void ToggleMode() =>
        ApplyMode(state.Mode == OverlayMode.Display ? OverlayMode.Interactive : OverlayMode.Display);

    private void ApplyMode(OverlayMode mode)
    {
        var isInteractive = mode == OverlayMode.Interactive;
        _window?.SetClickThrough(!isInteractive);
        viewModel.IsInteractive = isInteractive;
        state.SetMode(mode);
    }

    private void ToggleVisibility()
    {
        if (_window is null)
        {
            return;
        }

        if (state.IsVisible)
        {
            _window.Hide();
        }
        else
        {
            _window.Show();
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
                hosted.Host.Id,
                hosted.Host.Title,
                hosted.Widget.GetType().Namespace ?? string.Empty,
                !settings.Current.DisabledWidgets.Contains(hosted.Host.Id)))],
            CreateSettingsSections(),
            updateViewModel,
            _hotkeyWarning,
            AppPaths.SettingsFileDisplayPath);
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
                    sections.Add(new WidgetSettingsSection(hosted.Host.Title, page, page.CreateView()));
                }
            }
            catch (Exception exception)
            {
                errorLog.Write($"Widget '{hosted.Host.Id}' failed to create its settings page", exception);
            }
        }

        return sections;
    }

    private void ApplySettings()
    {
        if (_window is null)
        {
            return;
        }

        var current = settings.Current;
        _window.Opacity = Math.Clamp(current.Opacity, MinimumOpacity, 1.0);
        _window.MoveToBounds(MonitorLocator.ResolveWorkingArea(current.Monitor));

        // The monitor or a position reset may have changed which stored layout applies.
        foreach (var host in viewModel.Hosts)
        {
            host.ApplyLayout(layouts.Find(host.Id) ?? host.DefaultLayout);
        }

        viewModel.ClampAll();

        var failedHotkeys = new List<string>();
        RegisterHotkey(ToggleModeHotkeyId, current.ToggleModeHotkey, ToggleMode, failedHotkeys);
        RegisterHotkey(ToggleVisibilityHotkeyId, current.ToggleVisibilityHotkey, ToggleVisibility, failedHotkeys);

        var modeHotkeyText = HotkeyGesture.ToDisplayText(current.ToggleModeHotkey);
        viewModel.ModeHotkeyText = modeHotkeyText;
        state.SetHotkeyTexts(modeHotkeyText, HotkeyGesture.ToDisplayText(current.ToggleVisibilityHotkey));

        _hotkeyWarning = failedHotkeys.Count > 0
            ? $"{string.Join(" and ", failedHotkeys)} could not be registered; another application may already be using it."
            : null;
        if (_hotkeyWarning is not null)
        {
            tray.ShowWarning(_hotkeyWarning);
        }
    }

    private void RegisterHotkey(int id, string text, Action handler, List<string> failures)
    {
        if (!HotkeyGesture.TryParse(text, out var gesture) || !hotkeys.Register(id, gesture, handler))
        {
            failures.Add(HotkeyGesture.ToDisplayText(text));
        }
    }

    private void SaveLayout()
    {
        try
        {
            layouts.Store(viewModel.Hosts.Select(host => (host.Id, host.ToLayout())));
        }
        catch (Exception exception)
        {
            errorLog.Write("Saving the widget layout failed", exception);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        _window?.Dispatcher.BeginInvoke(
            () => _window.MoveToBounds(MonitorLocator.ResolveWorkingArea(settings.Current.Monitor)));

    private async Task ExitAsync(bool restart)
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
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
        SaveLayout();

        foreach (var hosted in _hostedWidgets.Where(hosted => hosted.IsActive))
        {
            await StopWidgetAsync(hosted);
        }

        _settingsWindow?.Close();
        _window?.Close();
        Application.Current.Shutdown();
    }

    private sealed class HostedWidget(IOverlayWidget widget, WidgetHostViewModel host)
    {
        public IOverlayWidget Widget { get; } = widget;

        public WidgetHostViewModel Host { get; } = host;

        public bool IsActive { get; set; }
    }
}
