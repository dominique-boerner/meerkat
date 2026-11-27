using System.IO;
using System.Windows;
using System.Windows.Controls;
using Meerkat.Widgets.Notes.Settings;
using Microsoft.Win32;

namespace Meerkat.Widgets.Notes.Views;

public partial class NotesSettingsView : UserControl
{
    public NotesSettingsView()
    {
        InitializeComponent();
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not NotesSettingsPageViewModel viewModel)
        {
            return;
        }

        var dialog = new OpenFolderDialog { Title = "Choose the folder for your notes" };
        if (Directory.Exists(viewModel.Directory))
        {
            dialog.InitialDirectory = viewModel.Directory;
        }

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            viewModel.Directory = dialog.FolderName;
        }
    }
}
