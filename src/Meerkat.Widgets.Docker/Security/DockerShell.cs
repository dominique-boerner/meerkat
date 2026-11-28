using System.ComponentModel;
using System.Text;
using Meerkat.Ssh;
using Meerkat.Widgets.Docker.Processes;

namespace Meerkat.Widgets.Docker.Security;

public sealed record ShellResult(int ExitCode, string Output, string Error);

/// <summary>
/// Runs shell scripts on the machine a Docker instance lives on, be it the WSL distribution of this
/// computer or another machine. Whatever is built on it works the same for both.
/// </summary>
public interface IDockerShell
{
    /// <summary>Runs a script with the POSIX shell of the machine. May take minutes; there is no time limit.</summary>
    /// <exception cref="DockerShellException">The machine could not be reached.</exception>
    Task<ShellResult> RunAsync(string script, CancellationToken cancellationToken);
}

public static class ShellScript
{
    /// <summary>
    /// Wraps a script so that it survives every hop on its way: wsl.exe and ssh each re-read their command
    /// line, and the login shell of a host need not be a POSIX shell. Base64 has no character any of them touches.
    /// </summary>
    public static string Encode(string script)
    {
        var normalized = script.Replace("\r\n", "\n", StringComparison.Ordinal);
        return $"echo {Convert.ToBase64String(Encoding.UTF8.GetBytes(normalized))} | base64 -d | sh";
    }
}

/// <summary>The WSL distribution of this computer.</summary>
public sealed class WslDockerShell(IProcessRunner processRunner, Func<string?> distributionAccessor) : IDockerShell
{
    public async Task<ShellResult> RunAsync(string script, CancellationToken cancellationToken)
    {
        var arguments = new List<string>();
        if (distributionAccessor() is { } distribution && !string.IsNullOrWhiteSpace(distribution))
        {
            arguments.AddRange(["-d", distribution.Trim()]);
        }

        arguments.AddRange(["--exec", "sh", "-c", ShellScript.Encode(script)]);
        try
        {
            var result = await processRunner.RunAsync("wsl.exe", arguments, cancellationToken).ConfigureAwait(false);
            return new ShellResult(result.ExitCode, result.StandardOutput, result.StandardError);
        }
        catch (Win32Exception exception)
        {
            throw new DockerShellException($"WSL could not be started: {exception.Message}");
        }
    }
}

/// <summary>Another machine, reached over SSH.</summary>
public sealed class SshDockerShell(ISshClient ssh, Func<SshTarget> targetAccessor) : IDockerShell
{
    private const string ExitMarker = "___MEERKAT_EXIT___:";

    public async Task<ShellResult> RunAsync(string script, CancellationToken cancellationToken)
    {
        // Read as a stream, which has no time limit, unlike a plain call. A stream does not tell the exit
        // code of the script, so the script reports it on a last line of its own.
        var command = $"{ShellScript.Encode(script)}; echo {ExitMarker}$?";
        var output = new StringBuilder();
        var error = new StringBuilder();
        int? exitCode = null;

        try
        {
            await foreach (var line in ssh.StreamAsync(targetAccessor(), command, HostKeyPolicy.Strict, cancellationToken).ConfigureAwait(false))
            {
                if (line.IsError)
                {
                    error.AppendLine(line.Text);
                }
                else if (line.Text.StartsWith(ExitMarker, StringComparison.Ordinal) && int.TryParse(line.Text[ExitMarker.Length..], out var code))
                {
                    exitCode = code;
                }
                else
                {
                    output.AppendLine(line.Text);
                }
            }
        }
        catch (Exception exception) when (exception is Win32Exception or ArgumentException or InvalidOperationException)
        {
            throw new DockerShellException(exception.Message);
        }

        // Without the last line the script never ran to its end: the connection failed or broke.
        return exitCode is { } result
            ? new ShellResult(result, output.ToString(), error.ToString())
            : throw new DockerShellException(SshFailure.Classify(error.ToString()).Message);
    }
}
