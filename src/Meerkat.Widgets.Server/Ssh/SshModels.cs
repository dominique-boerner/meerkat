using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Meerkat.Widgets.Server.Ssh;

[JsonConverter(typeof(JsonStringEnumConverter<SshAuthentication>))]
public enum SshAuthentication
{
    /// <summary>Keys offered by the SSH agent or configured in ~/.ssh/config.</summary>
    Agent,
    KeyFile,

    /// <summary>User name and password. Weaker than a key, but some machines offer nothing else.</summary>
    Password,
}

public enum HostKeyPolicy
{
    /// <summary>Connect only to hosts whose key is already in known_hosts.</summary>
    Strict,

    /// <summary>Additionally accept the key of a host seen for the first time. Never accepts a changed key.</summary>
    AcceptNew,
}

/// <param name="Address">A host name, an IP address or an alias from ~/.ssh/config.</param>
/// <param name="User">Null leaves the user to the SSH configuration.</param>
/// <param name="Port">Null leaves the port to the SSH configuration.</param>
/// <param name="Password">Only used with <see cref="SshAuthentication.Password"/>; never persisted with the target.</param>
public sealed partial record SshTarget(
    string Address,
    string? User,
    int? Port,
    SshAuthentication Authentication,
    string? KeyFile,
    string? Password = null)
{
    /// <summary>
    /// Rejects values that the ssh command line could misread. Address and user end up as program arguments,
    /// where a leading dash would turn them into options.
    /// </summary>
    public void Validate()
    {
        if (!AddressPattern().IsMatch(Address))
        {
            throw new ArgumentException($"'{Address}' is not a valid host name.");
        }

        if (User is not null && !UserPattern().IsMatch(User))
        {
            throw new ArgumentException($"'{User}' is not a valid user name.");
        }

        if (Port is < 1 or > 65535)
        {
            throw new ArgumentException($"{Port} is not a valid port.");
        }

        if (Authentication == SshAuthentication.KeyFile && string.IsNullOrWhiteSpace(KeyFile))
        {
            throw new ArgumentException("Select a key file or use the SSH agent.");
        }

        if (Authentication == SshAuthentication.Password && string.IsNullOrEmpty(User))
        {
            throw new ArgumentException("Enter the user name that belongs to the password.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:\-]*$")]
    private static partial Regex AddressPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]*\$?$")]
    private static partial Regex UserPattern();
}

public sealed record SshResult(int ExitCode, string Output, string Error, TimeSpan Duration);

public interface ISshClient
{
    Task<SshResult> RunAsync(SshTarget target, string command, HostKeyPolicy policy, CancellationToken cancellationToken);

    /// <summary>Removes the host's key from known_hosts, so that a new key can be accepted.</summary>
    Task ForgetHostKeyAsync(SshTarget target, CancellationToken cancellationToken);

    /// <summary>Opens an interactive session in a terminal window.</summary>
    void OpenTerminal(SshTarget target);
}
