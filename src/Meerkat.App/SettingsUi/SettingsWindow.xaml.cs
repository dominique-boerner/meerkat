using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Meerkat.App.Hotkeys;

namespace Meerkat.App.SettingsUi;

public partial class SettingsWindow : Window
{
    private static readonly Key[] ModifierOnlyKeys =
    [
        Key.LeftCtrl, Key.RightCtrl, Key.LeftAlt, Key.RightAlt, Key.LeftShift, Key.RightShift, Key.LWin, Key.RWin,
    ];

    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel && viewModel.TrySave())
        {
            Close();
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnHotkeyBoxKeyDown(object sender, KeyEventArgs e)
    {
        // Alt combinations arrive as Key.System with the real key in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab || ModifierOnlyKeys.Contains(key))
        {
            return;
        }

        e.Handled = true;
        if (Keyboard.Modifiers != ModifierKeys.None && sender is TextBox textBox)
        {
            textBox.Text = new HotkeyGesture(Keyboard.Modifiers, key).ToString();
        }
    }

    private void OnNavigationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        FrameworkElement?[] shellSections = [HotkeysSection, AppearanceSection, WidgetsSection];
        var index = Navigation.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        var target = index < shellSections.Length
            ? shellSections[index]
            : WidgetSections.ItemContainerGenerator.ContainerFromIndex(index - shellSections.Length) as FrameworkElement;
        target?.BringIntoView();
    }
}
