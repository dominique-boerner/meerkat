using Meerkat.Widgets.Docker.Containers;

namespace Meerkat.Widgets.Docker.ViewModels;

public sealed record ContainerFailureViewModel(
    string Title,
    string Message,
    string Detail,
    bool IsWarning,
    bool CanChooseDistribution)
{
    public static ContainerFailureViewModel From(Exception exception, string distributionLabel)
    {
        if (exception is not ContainerProviderException failure)
        {
            return new ContainerFailureViewModel(
                "Docker query failed",
                "An unexpected error occurred while querying the containers.",
                exception.Message,
                IsWarning: false,
                CanChooseDistribution: false);
        }

        var exitCode = failure.ExitCode is { } code ? $"\n→ exit code {code}" : string.Empty;
        return failure.Kind switch
        {
            ContainerFailureKind.WslUnavailable => new ContainerFailureViewModel(
                "WSL not reachable",
                $"The distribution \"{distributionLabel}\" was not found or does not start.",
                failure.Command + exitCode,
                IsWarning: true,
                CanChooseDistribution: true),
            ContainerFailureKind.DaemonUnavailable => new ContainerFailureViewModel(
                "Docker daemon is not running",
                "WSL responds, but the Docker socket is not reachable.",
                failure.Message,
                IsWarning: false,
                CanChooseDistribution: false),
            ContainerFailureKind.DockerNotInstalled => new ContainerFailureViewModel(
                "Docker not found",
                $"The distribution \"{distributionLabel}\" has no Docker CLI installed.",
                failure.Command + exitCode,
                IsWarning: true,
                CanChooseDistribution: true),
            _ => new ContainerFailureViewModel(
                "Docker query failed",
                "The Docker command exited with an error.",
                failure.Message,
                IsWarning: false,
                CanChooseDistribution: true),
        };
    }
}
