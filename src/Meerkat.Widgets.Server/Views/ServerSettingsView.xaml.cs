using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Meerkat.Widgets.Server.Settings;
using Microsoft.Win32;

namespace Meerkat.Widgets.Server.Views;

public partial class ServerSettingsView : UserControl
{
    public ServerSettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged previous)
        {
            previous.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is INotifyPropertyChanged current)
        {
            current.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The view model clears the password when the form is opened or a host was saved;
        // PasswordBox has no bindable property, so the box is emptied here.
        if (e.PropertyName == nameof(ServerSettingsPageViewModel.Password)
            && sender is ServerSettingsPageViewModel { Password.Length: 0 }
            && PasswordInput.Password.Length > 0)
        {
            PasswordInput.Clear();
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ServerSettingsPageViewModel viewModel)
        {
            viewModel.Password = PasswordInput.Password;
        }
    }

    private void OnBrowseKeyFile(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ServerSettingsPageViewModel viewModel)
        {
            return;
        }

        // Keys normally live in ~/.ssh and have no file extension, so all files are listed.
        var sshDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var dialog = new OpenFileDialog
        {
            Title = "Select the private key",
            Filter = "All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(sshDirectory) ? sshDirectory : string.Empty,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            // Picking the public half is an easy mistake; the private key has the same name without ".pub".
            var selected = dialog.FileName;
            viewModel.KeyFile = selected.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) && File.Exists(selected[..^4])
                ? selected[..^4]
                : selected;
        }
    }
}
