using Meerkat.Widgets.Server.Ssh;

namespace Meerkat.App;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        // For password sign-in, ssh starts this executable a second time to fetch the password. That instance
        // must answer and exit before anything else happens: no window, no tray icon, no single-instance check.
        if (OpenSshClient.TryAnswerPasswordPrompt())
        {
            return;
        }

        App.Main();
    }
}
