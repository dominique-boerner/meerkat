using Meerkat.Widgets.Docker.Processes;

namespace Meerkat.Widgets.Docker.Containers;

public interface IWslDistributionCatalog
{
    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken);
}

public sealed class WslDistributionCatalog(IProcessRunner processRunner) : IWslDistributionCatalog
{
    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken)
    {
        var result = await processRunner
            .RunAsync("wsl.exe", ["--list", "--quiet"], cancellationToken)
            .ConfigureAwait(false);

        return result.ExitCode == 0
            ? result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
    }
}
