using System.Windows;

namespace Meerkat.Abstractions;

public interface IWidgetSettingsPage
{
    /// <summary>Returns a fresh editor view that reflects the currently stored settings.</summary>
    FrameworkElement CreateView();

    /// <summary>Persists the values edited in the most recently created view.</summary>
    void Apply();
}
