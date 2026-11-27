using Meerkat.Abstractions;

namespace Meerkat.App.Overlay;

/// <summary>Decouples widgets from the shell: widgets raise requests, the shell subscribes.</summary>
public sealed class OverlayCommands : IOverlayCommands
{
    public event EventHandler? SettingsRequested;

    public void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);
}
