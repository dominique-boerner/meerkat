using Meerkat.Abstractions;
using Meerkat.Ssh;
using Meerkat.Widgets.Docker.Containers;
using Meerkat.Widgets.Docker.Processes;
using Meerkat.Widgets.Docker.Settings;
using Meerkat.Widgets.Docker.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        // Shared with other widgets that reach machines over SSH; whoever registers first provides it.
        services.TryAddSingleton<ISshClient, OpenSshClient>();
        services.AddSingleton(provider =>
        {
            var ssh = provider.GetRequiredService<ISshClient>();
            var secrets = provider.GetRequiredService<IWidgetSecretStore>();
            return new DockerWidgetViewModel(
                provider.GetRequiredService<IContainerProvider>(),
                provider.GetRequiredService<IOverlayState>(),
                provider.GetRequiredService<IOverlayCommands>(),
                provider.GetRequiredService<DockerSettingsSource>(),
                TimeProvider.System,

                // The password is read for every connection, so a change in the settings applies at once.
                host => new SshContainerProvider(ssh, () => host.ToTarget(secrets.Read(host.SecretKey))));
        });
        services.AddSingleton<DockerSettingsPage>();
        services.AddSingleton<IOverlayWidget, DockerWidget>();
        return services;
    }
}
