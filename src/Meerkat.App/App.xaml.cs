using System.Windows;
using Meerkat.Abstractions;
using Meerkat.App.Diagnostics;
using Meerkat.App.Hotkeys;
using Meerkat.App.Overlay;
using Meerkat.App.Settings;
using Meerkat.App.Tray;
using Meerkat.Widgets.Docker;
using Microsoft.Extensions.DependencyInjection;

namespace Meerkat.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "Meerkat.SingleInstance";

    private Mutex? _singleInstanceMutex;
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        var errorLog = new ErrorLog(AppPaths.ErrorLogFile);

        // Widget code runs on the shell's dispatcher; a failing widget handler must not take the shell down.
        DispatcherUnhandledException += (_, args) =>
        {
            errorLog.Write("Unhandled dispatcher exception", args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            errorLog.Write("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        try
        {
            _services = ConfigureServices(errorLog);
            await _services.GetRequiredService<OverlayShell>().StartAsync();
        }
        catch (Exception exception)
        {
            errorLog.Write("Startup failed", exception);
            MessageBox.Show(
                $"Meerkat could not be started:\n{exception.Message}",
                "Meerkat",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static ServiceProvider ConfigureServices(ErrorLog errorLog)
    {
        var services = new ServiceCollection();

        services.AddSingleton(errorLog);
        services.AddSingleton(_ => new SettingsService(AppPaths.SettingsFile));
        services.AddSingleton<IWidgetSettingsStore>(provider => provider.GetRequiredService<SettingsService>());
        services.AddSingleton<LayoutService>();
        services.AddSingleton<OverlayState>();
        services.AddSingleton<IOverlayState>(provider => provider.GetRequiredService<OverlayState>());
        services.AddSingleton<OverlayViewModel>();
        services.AddSingleton<OverlayCommands>();
        services.AddSingleton<IOverlayCommands>(provider => provider.GetRequiredService<OverlayCommands>());
        services.AddSingleton<IOverlayPanelHost, OverlayPanelHost>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<OverlayShell>();

        services.AddDockerWidget();

        return services.BuildServiceProvider();
    }
}
