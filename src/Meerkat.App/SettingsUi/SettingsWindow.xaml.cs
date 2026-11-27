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
        Loaded += (_, _) => ShowSelectedSection();
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

    private void OnNavigationSelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelectedSection();

    /// <summary>Shows only the section selected in the navigation, like pages of a settings dialog.</summary>
    private void ShowSelectedSection()
    {
        // The selection is first set while the XAML is still loading, before the sections exist.
        if (!IsInitialized)
        {
            return;
        }

        FrameworkElement[] shellSections = [HotkeysSection, AppearanceSection, WidgetsSection];
        var selectedIndex = Math.Max(0, Navigation.SelectedIndex);
        for (var index = 0; index < shellSections.Length; index++)
        {
            shellSections[index].Visibility = ToVisibility(index == selectedIndex);
        }

        // The navigation lists the shell sections, then the widget pages, then the update page.
        var selectedWidgetIndex = selectedIndex - shellSections.Length;
        var isWidgetPage = selectedWidgetIndex >= 0 && selectedWidgetIndex < WidgetSections.Items.Count;
        WidgetSections.Visibility = ToVisibility(isWidgetPage);
        UpdatesSection.Visibility = ToVisibility(selectedWidgetIndex >= WidgetSections.Items.Count);

        // Item containers are only generated once the list has been laid out while visible.
        WidgetSections.UpdateLayout();
        for (var index = 0; index < WidgetSections.Items.Count; index++)
        {
            if (WidgetSections.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container)
            {
                container.Visibility = ToVisibility(index == selectedWidgetIndex);
            }
        }
    }

    private static Visibility ToVisibility(bool isVisible) => isVisible ? Visibility.Visible : Visibility.Collapsed;
}
