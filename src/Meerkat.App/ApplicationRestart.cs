namespace Meerkat.App;

/// <summary>
/// Carries a restart request from the shell to the application exit, where the new process can be
/// started after the single-instance lock has been released.
/// </summary>
public sealed class ApplicationRestart
{
    public bool IsRequested { get; private set; }

    public void Request() => IsRequested = true;
}
