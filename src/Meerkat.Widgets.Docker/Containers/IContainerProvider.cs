namespace Meerkat.Widgets.Docker.Containers;

public interface IContainerProvider
{
    Task<IReadOnlyList<ContainerInfo>> ListAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ContainerStats>> GetStatsAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<LogLine> StreamLogsAsync(string containerId, CancellationToken cancellationToken);

    Task StartAsync(string containerId, CancellationToken cancellationToken);

    Task StopAsync(string containerId, CancellationToken cancellationToken);

    Task RestartAsync(string containerId, CancellationToken cancellationToken);
}
