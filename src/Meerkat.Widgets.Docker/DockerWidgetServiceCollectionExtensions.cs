using Meerkat.Abstractions;
using Meerkat.Ssh;
using Meerkat.Widgets.Docker.Containers;
using Meerkat.Widgets.Docker.Processes;
using Meerkat.Widgets.Docker.Security;
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
        services.AddSingleton<IDockerShellFactory, DockerShellFactory>();
        services.AddSingleton<IScanStore>(_ => new FileScanStore(FileScanStore.DefaultPath));
        services.AddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<DockerSettingsSource>();
            var shells = provider.GetRequiredService<IDockerShellFactory>();
            var store = provider.GetRequiredService<IScanStore>();
            var overlayState = provider.GetRequiredService<IOverlayState>();

            // The same code scans this computer and every other machine; only the way to reach them differs.
            SecuritySourceViewModel CreateSource(DockerHost? host)
            {
                var shell = host is null ? shells.ForLocal(() => settings.Current.Distribution) : shells.ForHost(host);
                var options = host is null
                    ? new SecuritySourceOptions(ContainerSourceOptions.LocalId, ContainerSourceOptions.ForLocal(settings).Label, IsRemote: false)
                    : new SecuritySourceOptions(host.Id, () => host.DisplayName, IsRemote: true);
                return new SecuritySourceViewModel(
                    new TrivyScanner(shell),
                    new DockerImageInventory(shell),
                    store,
                    settings,
                    options,
                    overlayState,
                    TimeProvider.System,
                    () => System.Windows.SystemParameters.PowerLineStatus == System.Windows.PowerLineStatus.Offline);
            }

            return new SecurityViewModel(settings, CreateSource, provider.GetRequiredService<IOverlayCommands>(), TimeProvider.System);
        });
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
                host => new SshContainerProvider(ssh, () => host.ToTarget(secrets.Read(host.SecretKey))),
                provider.GetRequiredService<SecurityViewModel>());
        });
        services.AddSingleton<DockerSettingsPage>();
        services.AddSingleton<IOverlayWidget, DockerWidget>();
        return services;
    }
}
