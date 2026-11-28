using Meerkat.Abstractions;
using Meerkat.Ssh;
using Meerkat.Widgets.Docker.Processes;
using Meerkat.Widgets.Docker.Settings;

namespace Meerkat.Widgets.Docker.Security;

/// <summary>Gives access to the machines Docker runs on, wherever they are.</summary>
public interface IDockerShellFactory
{
    /// <param name="distribution">The WSL distribution; read for every call, because the user may choose another one.</param>
    IDockerShell ForLocal(Func<string?> distribution);

    /// <param name="password">A password not stored yet, as typed into the settings; null uses the stored one.</param>
    IDockerShell ForHost(DockerHost host, string? password = null);
}

public sealed class DockerShellFactory(IProcessRunner processRunner, ISshClient ssh, IWidgetSecretStore secrets) : IDockerShellFactory
{
    public IDockerShell ForLocal(Func<string?> distribution) => new WslDockerShell(processRunner, distribution);

    // The password is read for every connection, so a change in the settings applies at once.
    public IDockerShell ForHost(DockerHost host, string? password = null) =>
        new SshDockerShell(ssh, () => host.ToTarget(password ?? secrets.Read(host.SecretKey)));
}
