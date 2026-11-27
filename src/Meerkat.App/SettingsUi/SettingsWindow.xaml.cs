using System.Windows;
using System.Windows.Controls;

namespace Meerkat.App.SettingsUi;

public partial class SettingsWindow : Window
{
    private IDisposable? _hotkeySuspension;

    public SettingsWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ShowSelectedSection();
        Closed += (_, _) => ResumeHotkeys();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel && viewModel.TrySave())
        {
            Close();
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

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

    private void OnNavigationSelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelectedSection();

    /// <summary>Shows only the section selected in the navigation, like pages of a settings dialog.</summary>
    private void ShowSelectedSection()
    {
        // The selection is first set while the XAML is still loading, before the sections exist.
        if (!IsInitialized)
        {
            return;
        }

        FrameworkElement[] shellSections = [HotkeysSection, GeneralSection, WidgetsSection];
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
