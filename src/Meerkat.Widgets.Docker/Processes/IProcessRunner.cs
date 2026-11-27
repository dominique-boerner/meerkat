namespace Meerkat.Widgets.Docker.Processes;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public readonly record struct ProcessOutputLine(string Text, bool IsError);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken);

    /// <summary>Streams stdout and stderr line by line until the process exits or the token is cancelled.</summary>
    IAsyncEnumerable<ProcessOutputLine> StreamAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}
