using System.Diagnostics;
using Meerkat.Abstractions;
using Meerkat.App.Diagnostics;

namespace Meerkat.App.Overlay;

/// <summary>Decouples widgets from the shell: widgets raise requests, the shell subscribes.</summary>
public sealed class OverlayCommands(ErrorLog errorLog) : IOverlayCommands
{
    public event EventHandler? SettingsRequested;

    /// <summary>Raised with the id of the widget whose page is asked for.</summary>
    public event EventHandler<string>? WidgetRequested;

    public void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    public void ShowWidget(string widgetId) => WidgetRequested?.Invoke(this, widgetId);

    public void OpenUrl(Uri url)
    {
        // Widgets pass on addresses they received from servers; only web links may reach the shell.
        if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            errorLog.Write($"Opening {url.Host} failed", exception);
        }
    }
}
