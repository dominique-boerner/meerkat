using System.IO;

namespace Meerkat.App.Updates;

public interface IUpdateInstaller
{
    /// <summary>False for builds that cannot replace themselves, such as multi-file development builds.</summary>
    bool IsSupported { get; }

    /// <summary>Puts the staged executable in place of the installed one.</summary>
    void Install(string stagedExecutablePath);

    /// <summary>Removes what a previous installation left behind.</summary>
    void RemoveLeftovers();
}

/// <summary>
/// Updates a single-file application in place. Windows does not allow overwriting a running executable,
/// but it allows renaming it, so the old file is moved aside and deleted on the next start.
/// </summary>
public sealed class ExecutableSwapInstaller(string? executablePath, bool isSingleFile) : IUpdateInstaller
{
    private const string BackupSuffix = ".old";

    public bool IsSupported => isSingleFile && executablePath is not null;

    public static ExecutableSwapInstaller ForCurrentProcess()
    {
        // A published single-file build has no separate application assembly next to the executable.
        var isSingleFile = !File.Exists(Path.Combine(AppContext.BaseDirectory, "Meerkat.dll"));
        return new ExecutableSwapInstaller(Environment.ProcessPath, isSingleFile);
    }

    public void Install(string stagedExecutablePath)
    {
        if (!IsSupported || executablePath is null)
        {
            throw new InvalidOperationException("This build cannot update itself.");
        }

        var backupPath = executablePath + BackupSuffix;
        File.Delete(backupPath);
        File.Move(executablePath, backupPath);
        try
        {
            File.Copy(stagedExecutablePath, executablePath);
        }
        catch
        {
            // Without the rollback a failed copy would leave no executable behind.
            File.Move(backupPath, executablePath);
            throw;
        }
    }

    public void RemoveLeftovers()
    {
        if (executablePath is null)
        {
            return;
        }

        try
        {
            File.Delete(executablePath + BackupSuffix);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The previous version may still be shutting down; the next start tries again.
        }
    }
}
