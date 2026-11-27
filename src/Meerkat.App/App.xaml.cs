using System.Diagnostics;
using System.Windows;
using Meerkat.Abstractions;
using Meerkat.App.Diagnostics;
using Meerkat.App.Hotkeys;
using Meerkat.App.Overlay;
using Meerkat.App.Settings;
using Meerkat.App.Tray;
using Meerkat.App.Updates;
using Meerkat.App.Security;
using Meerkat.Widgets.Docker;
using Meerkat.Widgets.Git;
using Meerkat.Widgets.Server;
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
        var restartRequested = _services?.GetService<ApplicationRestart>()?.IsRequested == true;
        _services?.Dispose();
        _singleInstanceMutex?.Dispose();

        // Only after the lock is gone can the next instance start without mistaking itself for a duplicate.
        if (restartRequested && Environment.ProcessPath is { } executable)
        {
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false });
        }

        base.OnExit(e);
    }

    private static ServiceProvider ConfigureServices(ErrorLog errorLog)
    {
        var services = new ServiceCollection();

        services.AddSingleton(errorLog);
        services.AddSingleton(_ => new SettingsService(AppPaths.SettingsFile));
        services.AddSingleton<IWidgetSettingsStore>(provider => provider.GetRequiredService<SettingsService>());
        services.AddSingleton<OverlayState>();
        services.AddSingleton<IOverlayState>(provider => provider.GetRequiredService<OverlayState>());
        services.AddSingleton<ApplicationRestart>();
        services.AddSingleton<IReleaseFeed, GitHubReleaseFeed>();
        services.AddSingleton<IUpdateInstaller>(_ => ExecutableSwapInstaller.ForCurrentProcess());
        services.AddSingleton(_ => UpdateEnvironment.ForCurrentProcess());
        services.AddSingleton<UpdateService>();
        services.AddSingleton<UpdateViewModel>();
        services.AddSingleton<StatusBarViewModel>();
        services.AddSingleton(provider => new PanelViewModel { Update = provider.GetRequiredService<UpdateViewModel>() });
        services.AddSingleton<OverlayCommands>();
        services.AddSingleton<IOverlayCommands>(provider => provider.GetRequiredService<OverlayCommands>());
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<OverlayShell>();

        services.AddSingleton<IWidgetSecretStore>(_ => new CredentialManagerSecretStore());

        services.AddDockerWidget();
        services.AddGitWidget();
        services.AddServerWidget();

        return services.BuildServiceProvider();
    }
}
