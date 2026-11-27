using System.Text.RegularExpressions;

namespace Meerkat.Ssh;

public enum SshFailureKind
{
    Unreachable,

    /// <summary>The host presents a different key than the one in known_hosts.</summary>
    HostKeyChanged,

    /// <summary>The host is not in known_hosts yet.</summary>
    HostKeyUnknown,
    AuthenticationFailed,
}

/// <summary>Why a connection did not come about, read from what ssh printed.</summary>
/// <param name="Fingerprint">For a changed host key: the fingerprint of the key the host presents now.</param>
public sealed partial record SshFailure(SshFailureKind Kind, string Message, string? Fingerprint = null)
{
    /// <summary>The exit code ssh uses for its own failures, as opposed to those of the remote command.</summary>
    public const int ConnectionFailedExitCode = 255;

    public static SshFailure Classify(string error)
    {
        if (error.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.Ordinal))
        {
            var fingerprint = FingerprintPattern().Match(error);
            return new SshFailure(SshFailureKind.HostKeyChanged, "The host key has changed.", fingerprint.Success ? fingerprint.Value : null);
        }

        if (error.Contains("Host key verification failed", StringComparison.Ordinal)
            || error.Contains("host key is known", StringComparison.Ordinal))
        {
            return new SshFailure(SshFailureKind.HostKeyUnknown, "The host key is not trusted yet.");
        }

        if (error.Contains("Permission denied", StringComparison.Ordinal))
        {
            return new SshFailure(SshFailureKind.AuthenticationFailed, "Authentication failed");
        }

        return new SshFailure(SshFailureKind.Unreachable, DescribeConnectionError(error));
    }

    private static string DescribeConnectionError(string error)
    {
        if (error.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "SSH timeout";
        }

        if (error.Contains("Could not resolve hostname", StringComparison.Ordinal))
        {
            return "Host name not found";
        }

        if (error.Contains("Connection refused", StringComparison.Ordinal))
        {
            return "Connection refused";
        }

        // Fall back to what ssh said, without its "ssh: " prefix and without warnings that precede the error.
        var line = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(candidate => !candidate.StartsWith("Warning:", StringComparison.Ordinal));
        return line is null ? "SSH connection failed" : line.Replace("ssh: ", string.Empty, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"SHA256:[A-Za-z0-9+/=]+")]
    private static partial Regex FingerprintPattern();
}
