using System.Windows;
using System.Windows.Input;

namespace Meerkat.App.SettingsUi;

public partial class SettingsWindow : Window
{
    private IDisposable? _hotkeySuspension;

    public SettingsWindow()
    {
        InitializeComponent();
        Closed += (_, _) => ResumeHotkeys();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // A field that records a hotkey keeps Esc for itself; anywhere else it closes the window.
        if (e.Key == Key.Escape && !e.Handled && _hotkeySuspension is null)
        {
            Close();
        }
    }

    // Windows swallows a registered hotkey, so Meerkat's own hotkeys are released while one is being
    // recorded. Otherwise pressing the current combination would trigger it instead of reaching the field.
    private void OnHotkeyRecordingStarted(object? sender, EventArgs e) =>
        _hotkeySuspension ??= (DataContext as SettingsViewModel)?.SuspendHotkeys?.Invoke();

    private void OnHotkeyRecordingStopped(object? sender, EventArgs e) => ResumeHotkeys();

    private void ResumeHotkeys()
    {
        _hotkeySuspension?.Dispose();
        _hotkeySuspension = null;
    }
}
