using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace Meerkat.Widgets.Docker.Processes;

public sealed class SystemProcessRunner : IProcessRunner
{
    private const int StreamBufferedLines = 10_000;

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = Start(fileName, arguments);
        try
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new ProcessResult(
                process.ExitCode,
                await standardOutput.ConfigureAwait(false),
                await standardError.ConfigureAwait(false));
        }
        finally
        {
            KillIfRunning(process);
        }
    }

    public async IAsyncEnumerable<ProcessOutputLine> StreamAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var process = Start(fileName, arguments);

        // Bounded so a very chatty process is slowed down instead of growing memory without limit.
        var channel = Channel.CreateBounded<ProcessOutputLine>(StreamBufferedLines);
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
            KillIfRunning(process);
        }
    }

    private static async Task PumpAsync(
        StreamReader reader,
        bool isError,
        ChannelWriter<ProcessOutputLine> writer,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            await writer.WriteAsync(new ProcessOutputLine(line, isError), cancellationToken).ConfigureAwait(false);
        }
    }

    private static Process Start(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // wsl.exe prints its own messages as UTF-16 unless told otherwise, which would garble error texts.
        startInfo.Environment["WSL_UTF8"] = "1";

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Process '{fileName}' could not be started.");
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
