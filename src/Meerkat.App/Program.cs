using Meerkat.App.Updates;
using Meerkat.Ssh;

namespace Meerkat.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // For password sign-in, ssh starts this executable a second time to fetch the password. That instance
        // must answer and exit before anything else happens: no window, no tray icon, no single-instance check.
        if (OpenSshClient.TryAnswerPasswordPrompt())
        {
            return 0;
        }

        // Where the program folder belongs to administrators, an update starts this executable once more with
        // their rights, only to put the new version in place. That instance, too, does its job and exits.
        if (ExecutableSwapInstaller.TryRunElevatedInstall(args, Environment.ProcessPath) is { } exitCode)
        {
            return exitCode;
        }

        App.Main();
        return 0;
    }
}
