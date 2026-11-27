using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace Meerkat.Ssh;

/// <summary>
/// Uses the OpenSSH client that ships with Windows. That way the user's ~/.ssh/config, the SSH agent and
/// known_hosts apply exactly as they do in a terminal, and host key checking is done by OpenSSH itself.
/// </summary>
/// <param name="passwordHelperPath">
/// Program that ssh starts to ask for a password; it must call <see cref="TryAnswerPasswordPrompt"/> first thing.
/// Defaults to the running executable.
/// </param>
public sealed class OpenSshClient(string? passwordHelperPath = null) : ISshClient
{
    /// <summary>Carries the password from this process to the helper that ssh starts.</summary>
    public const string PasswordEnvironmentVariable = "MEERKAT_SSH_PASSWORD";

    private const string SshExecutable = "ssh.exe";
    private const string KeygenExecutable = "ssh-keygen.exe";
    private const string TerminalExecutable = "wt.exe";
    private const int DefaultPort = 22;
    private const int ConnectTimeoutSeconds = 8;
    private const int StreamBufferedLines = 1000;

    // Upper bound for a whole call, in case the connection stalls after it was established.
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// ssh has no option to pass a password. Instead it starts a helper program and reads the password from
    /// its output; Meerkat uses its own executable as that helper. Call this at the very start of the program:
    /// it returns true if the process was started as helper and has done its job, so it should exit right away.
    /// </summary>
    public static bool TryAnswerPasswordPrompt()
    {
        if (Environment.GetEnvironmentVariable(PasswordEnvironmentVariable) is not { } password)
        {
            return false;
        }

        // Written as UTF-8 bytes: Console.Out would use the console code page and garble anything beyond ASCII,
        // while the host compares the password byte by byte.
        using var output = Console.OpenStandardOutput();
        output.Write(Encoding.UTF8.GetBytes(password + "\n"));
        output.Flush();
        return true;
    }

    public static IReadOnlyList<string> BuildArguments(SshTarget target, HostKeyPolicy policy, string? command)
    {
        target.Validate();
        var arguments = new List<string>();
        var usesPassword = target.Authentication == SshAuthentication.Password;

        if (command is not null)
        {
            if (usesPassword)
            {
                // One attempt only: repeating a wrong password could lock the account on the host.
                arguments.AddRange(["-o", "PreferredAuthentications=password,keyboard-interactive"]);
                arguments.AddRange(["-o", "PubkeyAuthentication=no", "-o", "NumberOfPasswordPrompts=1"]);
            }
            else
            {
                // Never wait for a password or a confirmation nobody can answer.
                arguments.AddRange(["-o", "BatchMode=yes"]);
            }

            arguments.AddRange(["-o", $"ConnectTimeout={ConnectTimeoutSeconds}"]);
            arguments.AddRange(["-o", "ServerAliveInterval=5", "-o", "ServerAliveCountMax=2"]);
            arguments.AddRange(["-o", $"StrictHostKeyChecking={(policy == HostKeyPolicy.AcceptNew ? "accept-new" : "yes")}"]);
        }

        if (target.Port is { } port)
        {
            arguments.AddRange(["-p", port.ToString()]);
        }

        if (target.User is { Length: > 0 } user)
        {
            arguments.AddRange(["-l", user]);
        }

        if (target.Authentication == SshAuthentication.KeyFile && target.KeyFile is { Length: > 0 } keyFile)
        {
            // Without IdentitiesOnly the agent's keys would be tried first and could exhaust the server's attempts.
            arguments.AddRange(["-i", keyFile, "-o", "IdentitiesOnly=yes"]);
        }

        arguments.Add(target.Address);
        if (command is not null)
        {
            arguments.Add(command);
        }

        return arguments;
    }

    /// <summary>Environment that lets ssh obtain the password without a terminal; empty for other sign-in methods.</summary>
    public static IReadOnlyDictionary<string, string> BuildEnvironment(SshTarget target, string? helperPath)
    {
        if (target.Authentication != SshAuthentication.Password)
        {
            return new Dictionary<string, string>();
        }

        if (string.IsNullOrEmpty(target.Password))
        {
            throw new ArgumentException("No password is stored for this host. Enter it in the settings.");
        }

        return new Dictionary<string, string>
        {
            ["SSH_ASKPASS"] = helperPath ?? throw new InvalidOperationException("The password helper program is unknown."),

            // Without "force" ssh only uses the helper when it has no terminal, which is not guaranteed here.
            ["SSH_ASKPASS_REQUIRE"] = "force",
            [PasswordEnvironmentVariable] = target.Password,
        };
    }

    public Task<SshResult> RunAsync(SshTarget target, string command, HostKeyPolicy policy, CancellationToken cancellationToken) =>
        RunProcessAsync(
            SshExecutable,
            BuildArguments(target, policy, command),
            BuildEnvironment(target, passwordHelperPath ?? Environment.ProcessPath),
            cancellationToken);

    public async IAsyncEnumerable<SshOutputLine> StreamAsync(
        SshTarget target,
        string command,
        HostKeyPolicy policy,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var process = Start(
            SshExecutable,
            BuildArguments(target, policy, command),
            BuildEnvironment(target, passwordHelperPath ?? Environment.ProcessPath),
            createWindow: false);

        // Bounded so a very chatty command is slowed down instead of growing memory without limit.
        var channel = Channel.CreateBounded<SshOutputLine>(StreamBufferedLines);
        var pumps = Task.WhenAll(
            PumpAsync(process.StandardOutput, isError: false, channel.Writer, cancellationToken),
            PumpAsync(process.StandardError, isError: true, channel.Writer, cancellationToken));
        _ = pumps.ContinueWith(
            task => channel.Writer.TryComplete(task.Exception?.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            await foreach (var line in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return line;
            }
        }
        finally
        {
            // Ending the local process closes the connection, which ends the command on the host.
            KillIfRunning(process);
        }
    }

    public async Task ForgetHostKeyAsync(SshTarget target, CancellationToken cancellationToken)
    {
        target.Validate();

        // known_hosts lists a host on a non-standard port as "[host]:port".
        var entry = target.Port is { } port && port != DefaultPort ? $"[{target.Address}]:{port}" : target.Address;
        await RunProcessAsync(KeygenExecutable, ["-R", entry], new Dictionary<string, string>(), cancellationToken).ConfigureAwait(false);
    }

    public void OpenTerminal(SshTarget target)
    {
        // An interactive session asks for the password itself, so none is handed over.
        var sshArguments = BuildArguments(target, HostKeyPolicy.Strict, command: null);
        try
        {
            Start(TerminalExecutable, [SshExecutable, .. sshArguments], new Dictionary<string, string>(), createWindow: true);
        }
        catch (Win32Exception)
        {
            // Windows Terminal is not installed everywhere; a plain console window does the job too.
            Start(SshExecutable, sshArguments, new Dictionary<string, string>(), createWindow: true);
        }
    }

    private static async Task<SshResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CallTimeout);

        var stopwatch = Stopwatch.StartNew();
        using var process = Start(fileName, arguments, environment, createWindow: false);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return new SshResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false), stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SshResult(-1, string.Empty, "The connection timed out.", stopwatch.Elapsed);
        }
        finally
        {
            KillIfRunning(process);
        }
    }

    private static async Task PumpAsync(StreamReader reader, bool isError, ChannelWriter<SshOutputLine> writer, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            await writer.WriteAsync(new SshOutputLine(line, isError), cancellationToken).ConfigureAwait(false);
        }
    }

    private static Process Start(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        bool createWindow)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = !createWindow,
            RedirectStandardOutput = !createWindow,
            RedirectStandardError = !createWindow,
            RedirectStandardInput = !createWindow,
        };
        if (!createWindow)
        {
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Process '{fileName}' could not be started.");
        if (!createWindow)
        {
            // Closed input makes ssh fail instead of hang should it ever try to ask something.
            process.StandardInput.Close();
        }

        return process;
    }

    private static void KillIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The process exited between the check and the kill; nothing left to clean up.
        }
    }
}
