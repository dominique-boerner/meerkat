namespace Meerkat.Abstractions;

/// <summary>Shell actions a widget may trigger.</summary>
public interface IOverlayCommands
{
    void OpenSettings();

    /// <summary>Opens a web address in the user's browser. Anything but http and https is ignored.</summary>
    void OpenUrl(Uri url);

    /// <summary>Opens the panel on the page of a widget, for an action whose result is to be seen there.</summary>
    void ShowWidget(string widgetId)
    {
    }
}
