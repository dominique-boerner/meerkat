using System.Windows;
using Meerkat.Abstractions;
using Meerkat.Widgets.Server.Monitoring;
using Meerkat.Widgets.Server.Settings;
using Meerkat.Ssh;
using Meerkat.Widgets.Server.ViewModels;
using Meerkat.Widgets.Server.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meerkat.Widgets.Server;

/// <summary>Monitors Linux machines over SSH: reachability, load, memory, disk and systemd services.</summary>
public sealed class ServerWidget(ServerWidgetViewModel viewModel, ServerSettingsPage settingsPage) : IOverlayWidget
{
    public const string WidgetId = "server";

    private ServerWidgetView? _view;
    private ServerHeaderView? _headerView;

    public event EventHandler? StatusChanged
    {
        add => viewModel.StatusChanged += value;
        remove => viewModel.StatusChanged -= value;
    }

    public string Id => WidgetId;

    public string Title => "Server";

    public string Icon => "";

    public FrameworkElement View => _view ??= new ServerWidgetView { DataContext = viewModel };

    public FrameworkElement HeaderView => _headerView ??= new ServerHeaderView { DataContext = viewModel };

    public IWidgetSettingsPage SettingsPage => settingsPage;

    public WidgetStatus? Status => viewModel.Status;

    public IReadOnlyList<WidgetAlert> Alerts => viewModel.Alerts;

    public IReadOnlyList<WidgetAction> Actions => viewModel.Actions;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        viewModel.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => viewModel.StopAsync();
}

public static class ServerWidgetServiceCollectionExtensions
{
    public static IServiceCollection AddServerWidget(this IServiceCollection services)
    {
        services.TryAddSingleton<ISshClient, OpenSshClient>();

        // The machines are shared with other widgets that reach machines over SSH; whoever registers first provides them.
        services.TryAddSingleton<MachineRegistry>();
        services.AddSingleton<IServerProbe, ServerProbe>();
        services.AddSingleton<ServerSettingsSource>();
        services.AddSingleton(provider => new ServerWidgetViewModel(
            provider.GetRequiredService<IServerProbe>(),
            provider.GetRequiredService<ServerSettingsSource>(),
            provider.GetRequiredService<IOverlayState>(),
            provider.GetRequiredService<IOverlayCommands>(),
            TimeProvider.System));
        services.AddSingleton<ServerSettingsPage>();
        services.AddSingleton<IOverlayWidget, ServerWidget>();
        return services;
    }
}
