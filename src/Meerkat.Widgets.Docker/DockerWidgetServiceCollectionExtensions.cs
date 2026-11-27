using Meerkat.Abstractions;
using Meerkat.Widgets.Docker.Containers;
using Meerkat.Widgets.Docker.Processes;
using Meerkat.Widgets.Docker.Settings;
using Meerkat.Widgets.Docker.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Meerkat.Widgets.Docker;

public static class DockerWidgetServiceCollectionExtensions
{
    public static IServiceCollection AddDockerWidget(this IServiceCollection services)
    {
        services.AddSingleton<IProcessRunner, SystemProcessRunner>();
        services.AddSingleton<IWslDistributionCatalog, WslDistributionCatalog>();
        services.AddSingleton<DockerSettingsSource>();
        services.AddSingleton<IContainerProvider>(provider =>
        {
            var settings = provider.GetRequiredService<DockerSettingsSource>();
            return new WslCliContainerProvider(
                provider.GetRequiredService<IProcessRunner>(),
                () => settings.Current.Distribution);
        });
        services.AddSingleton(provider => new DockerWidgetViewModel(
            provider.GetRequiredService<IContainerProvider>(),
            provider.GetRequiredService<IOverlayState>(),
            provider.GetRequiredService<IOverlayCommands>(),
            provider.GetRequiredService<DockerSettingsSource>(),
            TimeProvider.System));
        services.AddSingleton<DockerSettingsPage>();
        services.AddSingleton<IOverlayWidget, DockerWidget>();
        return services;
    }
}
