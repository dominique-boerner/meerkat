namespace Meerkat.Abstractions;

/// <summary>Shell actions a widget may trigger.</summary>
public interface IOverlayCommands
{
    void OpenSettings();

    /// <summary>Opens a web address in the user's browser. Anything but http and https is ignored.</summary>
    void OpenUrl(Uri url);
}
