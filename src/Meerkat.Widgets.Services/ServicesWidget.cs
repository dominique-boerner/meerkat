using System.Windows;
using Meerkat.Abstractions;
using Meerkat.Widgets.Services.Checks;
using Meerkat.Widgets.Services.Settings;
using Meerkat.Widgets.Services.ViewModels;
using Meerkat.Widgets.Services.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Meerkat.Widgets.Services;

/// <summary>
/// Watches the services of others that one's own work depends on, by reading their status pages or any
/// other address that tells how they are doing.
/// </summary>
public sealed class ServicesWidget(ServicesWidgetViewModel viewModel, ServicesSettingsPage settingsPage) : IOverlayWidget
{
    public const string WidgetId = "services";

    private ServicesWidgetView? _view;
    private ServicesHeaderView? _headerView;

    public event EventHandler? StatusChanged
    {
        add => viewModel.StatusChanged += value;
        remove => viewModel.StatusChanged -= value;
    }

    public string Id => WidgetId;

    public string Title => "Services";

    public string Icon => "";

    public FrameworkElement View => _view ??= new ServicesWidgetView { DataContext = viewModel };

    public FrameworkElement HeaderView => _headerView ??= new ServicesHeaderView { DataContext = viewModel };

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

public static class ServicesWidgetServiceCollectionExtensions
{
    public static IServiceCollection AddServicesWidget(this IServiceCollection services)
    {
        services.AddSingleton<IServiceChecker>(_ => new HttpServiceChecker(HttpServiceChecker.CreateClient()));
        services.AddSingleton<ServicesSettingsSource>();
        services.AddSingleton(provider => new ServicesWidgetViewModel(
            provider.GetRequiredService<IServiceChecker>(),
            provider.GetRequiredService<ServicesSettingsSource>(),
            provider.GetRequiredService<IOverlayState>(),
            provider.GetRequiredService<IOverlayCommands>(),
            TimeProvider.System));
        services.AddSingleton<ServicesSettingsPage>();
        services.AddSingleton<IOverlayWidget, ServicesWidget>();
        return services;
    }
}
