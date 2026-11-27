using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Meerkat.Widgets.Docker.Settings;
using Microsoft.Win32;

namespace Meerkat.Widgets.Docker.Views;

public partial class DockerSettingsView : UserControl
{
    public DockerSettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged previous)
            {
                previous.PropertyChanged -= OnViewModelPropertyChanged;
            }

            if (e.NewValue is INotifyPropertyChanged current)
            {
                current.PropertyChanged += OnViewModelPropertyChanged;
            }
        };
    }

    private DockerSettingsPageViewModel? ViewModel => DataContext as DockerSettingsPageViewModel;

    // A password box cannot be bound, on purpose: its content should not sit in a property longer than needed.
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.Password = PasswordInput.Password;
            UpdateStoredPasswordHint();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Opening the form for another host starts with an empty field.
        if (e.PropertyName == nameof(DockerSettingsPageViewModel.Password) && ViewModel is { Password.Length: 0 } && PasswordInput.Password.Length > 0)
        {
            PasswordInput.Clear();
        }

        if (e.PropertyName is nameof(DockerSettingsPageViewModel.Password) or nameof(DockerSettingsPageViewModel.HasStoredPassword))
        {
            UpdateStoredPasswordHint();
        }
    }

    private void UpdateStoredPasswordHint() =>
        StoredPasswordHint.Visibility = ViewModel is { HasStoredPassword: true, Password.Length: 0 } ? Visibility.Visible : Visibility.Collapsed;

    private void OnBrowseKeyFileClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var sshFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var dialog = new OpenFileDialog { Title = "Choose the private key", Filter = "All files|*.*" };
        if (Directory.Exists(sshFolder))
        {
            dialog.InitialDirectory = sshFolder;
        }

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            // The public half is easily picked by mistake; the private key lies next to it.
            var path = dialog.FileName;
            viewModel.KeyFile = path.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) && File.Exists(path[..^4]) ? path[..^4] : path;
        }
    }
}