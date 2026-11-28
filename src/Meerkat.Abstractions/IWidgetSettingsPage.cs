using System.Windows;

namespace Meerkat.Abstractions;

public interface IWidgetSettingsPage
{
    /// <summary>
    /// Returns a fresh editor view that reflects the currently stored settings. There is no save button:
    /// the page stores every change as soon as it is complete and valid, and the widget follows at once.
    /// </summary>
    FrameworkElement CreateView();
}
