using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Meerkat.App.Updates;

public interface IUpdateInstaller
{
    /// <summary>False for builds that cannot replace themselves, such as multi-file development builds.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// True where the folder of the program may only be written to by an administrator, as under
    /// "Program Files". Installing then makes Windows ask for administrator rights.
    /// </summary>
    bool NeedsAdministrator => false;

    /// <summary>Puts the staged executable in place of the installed one.</summary>
    void Install(string stagedExecutablePath);

    /// <summary>Removes what a previous installation left behind.</summary>
    void RemoveLeftovers();
}

/// <summary>
/// Updates a single-file application in place. Windows does not allow overwriting a running executable,
/// but it allows renaming it, so the old file is moved aside and deleted on the next start.
/// </summary>
/// <param name="runElevated">
/// Starts the installed program with administrator rights and the given arguments, waits for it and
/// returns its exit code. Defaults to asking Windows for those rights.
/// </param>
/// <param name="canWrite">Tells whether a folder may be written to; defaults to trying it.</param>
public sealed class ExecutableSwapInstaller(
    string? executablePath,
    bool isSingleFile,
    Func<string, IReadOnlyList<string>, int>? runElevated = null,
    Func<string, bool>? canWrite = null) : IUpdateInstaller
{
    /// <summary>First argument of the program when it is started only to put an update in place.</summary>
    public const string InstallArgument = "--install-update";

    private const string BackupSuffix = ".old";
    private const string IncomingSuffix = ".new";

    // What Windows reports when the user answers its question for administrator rights with "No".
    private const int ElevationDeclined = 1223;

    public bool IsSupported => isSingleFile && executablePath is not null;

    public bool NeedsAdministrator =>
        IsSupported && Path.GetDirectoryName(executablePath) is { } directory && !(canWrite ?? CanWrite)(directory);

    public static ExecutableSwapInstaller ForCurrentProcess()
    {
        // A published single-file build has no separate application assembly next to the executable.
        var isSingleFile = !File.Exists(Path.Combine(AppContext.BaseDirectory, "Meerkat.dll"));
        return new ExecutableSwapInstaller(Environment.ProcessPath, isSingleFile);
    }

    /// <summary>
    /// Does the work of a program that was started with administrator rights only to put an update in
    /// place. Call this at the very start of the program.
    /// </summary>
    /// <param name="executablePath">The program to replace, which is the one that is running.</param>
    /// <returns>Null if the program was started for something else; otherwise the exit code to end with.</returns>
    public static int? TryRunElevatedInstall(IReadOnlyList<string> arguments, string? executablePath)
    {
        if (arguments.Count != 3 || arguments[0] != InstallArgument)
        {
            return null;
        }

        try
        {
            if (executablePath is null)
            {
                return 1;
            }

            Swap(arguments[1], executablePath, expectedHash: arguments[2]);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return 1;
        }
    }

    public void Install(string stagedExecutablePath)
    {
        if (!IsSupported || executablePath is null)
        {
            throw new InvalidOperationException("This build cannot update itself.");
        }

        if (!NeedsAdministrator)
        {
            Swap(stagedExecutablePath, executablePath, expectedHash: null);
            return;
        }

        // The installed program does the swap, not the download: what runs with administrator rights is a
        // file only administrators can change. It is told what the download must look like, see Swap.
        int exitCode;
        try
        {
            exitCode = (runElevated ?? RunElevated)(executablePath, [InstallArgument, stagedExecutablePath, ChecksumFile.ComputeHash(stagedExecutablePath)]);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ElevationDeclined)
        {
            throw new InvalidOperationException("Administrator rights are needed for the folder Meerkat is in, and they were not granted.");
        }

        if (exitCode != 0)
        {
            throw new InvalidOperationException("The new version could not be put in place, even with administrator rights.");
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
            // The previous version may still be shutting down, or the folder needs an administrator, who
            // clears the file away with the next update.
        }
    }

    /// <param name="expectedHash">
    /// SHA-256 the new file must have; null skips the check. With administrator rights the check matters:
    /// the download lies where any program of the user can change it, so it is copied next to the
    /// installed program first, where only administrators can, and judged there.
    /// </param>
    private static void Swap(string stagedExecutablePath, string executablePath, string? expectedHash)
    {
        var incomingPath = executablePath + IncomingSuffix;
        var backupPath = executablePath + BackupSuffix;
        File.Copy(stagedExecutablePath, incomingPath, overwrite: true);
        try
        {
            if (expectedHash is not null && !string.Equals(expectedHash, ChecksumFile.ComputeHash(incomingPath), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The downloaded file changed after it was verified.");
            }

            File.Delete(backupPath);
            File.Move(executablePath, backupPath);
            try
            {
                File.Move(incomingPath, executablePath);
            }
            catch
            {
                // Without the rollback a failed move would leave no executable behind.
                File.Move(backupPath, executablePath);
                throw;
            }
        }
        finally
        {
            File.Delete(incomingPath);
        }
    }

    private static int RunElevated(string fileName, IReadOnlyList<string> arguments)
    {
        // "runas" is what makes Windows ask for administrator rights; it needs the shell to start the program.
        var startInfo = new ProcessStartInfo(fileName) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The program could not be started with administrator rights.");
        process.WaitForExit();
        return process.ExitCode;
    }

    private static bool CanWrite(string directory)
    {
        var probe = Path.Combine(directory, $".meerkat-{Guid.NewGuid():N}.tmp");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
