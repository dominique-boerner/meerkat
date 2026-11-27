using System.Windows;
using System.Windows.Controls;
using Meerkat.Widgets.Git.Settings;

namespace Meerkat.Widgets.Git.Views;

public partial class GitSettingsView : UserControl
{
    public GitSettingsView()
    {
        InitializeComponent();
    }

    // PasswordBox deliberately offers no bindable property, so the token is handed over here.
    private void OnTokenChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is GitSettingsPageViewModel viewModel)
        {
            viewModel.Token = TokenBox.Password;
        }
    }
}
