using Meerkat.Widgets.Docker.Containers;

namespace Meerkat.Widgets.Docker.ViewModels;

/// <param name="SettingsAction">Label of a button that opens the settings, where the problem can be fixed; null hides it.</param>
public sealed record ContainerFailureViewModel(
    string Title,
    string Message,
    string Detail,
    bool IsWarning,
    string? SettingsAction)
{
    private const string ChooseDistribution = "Choose distribution";
    private const string OpenSettings = "Open settings";

    /// <param name="sourceLabel">The WSL distribution or the host the containers were asked from.</param>
    public static ContainerFailureViewModel From(Exception exception, string sourceLabel)
    {
        if (exception is not ContainerProviderException failure)
        {
            return new ContainerFailureViewModel(
                "Docker query failed",
                "An unexpected error occurred while querying the containers.",
                exception.Message,
                IsWarning: false,
                SettingsAction: null);
        }

        var exitCode = failure.ExitCode is { } code ? $"\n→ exit code {code}" : string.Empty;
        return failure.Kind switch
        {
            ContainerFailureKind.WslUnavailable => new ContainerFailureViewModel(
                "WSL not reachable",
                $"The distribution \"{sourceLabel}\" was not found or does not start.",
                failure.Command + exitCode,
                IsWarning: true,
                ChooseDistribution),
            ContainerFailureKind.DaemonUnavailable => new ContainerFailureViewModel(
                "Docker daemon is not running",
                $"\"{sourceLabel}\" responds, but the Docker socket is not reachable.",
                failure.Message,
                IsWarning: false,
                SettingsAction: null),
            ContainerFailureKind.DockerNotInstalled => new ContainerFailureViewModel(
                "Docker not found",
                $"\"{sourceLabel}\" has no Docker CLI installed.",
                failure.Command + exitCode,
                IsWarning: true,
                ChooseDistribution),
            ContainerFailureKind.PermissionDenied => new ContainerFailureViewModel(
                "Not allowed to use Docker",
                $"The user Meerkat signs in with on \"{sourceLabel}\" may not talk to the Docker daemon. Add it to the docker group on the host.",
                failure.Message,
                IsWarning: true,
                SettingsAction: null),
            ContainerFailureKind.SshUnavailable => new ContainerFailureViewModel(
                "SSH client not found",
                "The OpenSSH client of Windows (ssh.exe) could not be started.",
                failure.Message,
                IsWarning: true,
                SettingsAction: null),
            ContainerFailureKind.HostUnreachable => new ContainerFailureViewModel(
                $"{sourceLabel} not reachable",
                failure.Message,
                failure.Command + exitCode,
                IsWarning: false,
                SettingsAction: null),
            ContainerFailureKind.AuthenticationFailed => new ContainerFailureViewModel(
                $"Sign-in to {sourceLabel} failed",
                "The host refused the key or password. Check the sign-in method in the settings.",
                failure.Command + exitCode,
                IsWarning: true,
                OpenSettings),
            ContainerFailureKind.HostKeyUnknown => new ContainerFailureViewModel(
                $"{sourceLabel} is not trusted yet",
                "The host is not known to SSH. Open the host in the settings and choose \"Connect and check\" to confirm it.",
                failure.Command + exitCode,
                IsWarning: true,
                OpenSettings),
            ContainerFailureKind.HostKeyChanged => new ContainerFailureViewModel(
                $"The host key of {sourceLabel} has changed",
                "Meerkat does not connect until this is cleared up: the host may have been reinstalled, or someone may be intercepting the connection. If the change is expected, remove the old key with \"ssh-keygen -R\" and confirm the host again in the settings.",
                failure.Message,
                IsWarning: false,
                OpenSettings),
            _ => new ContainerFailureViewModel(
                "Docker query failed",
                "The Docker command exited with an error.",
                failure.Message,
                IsWarning: false,
                SettingsAction: null),
        };
    }
}
