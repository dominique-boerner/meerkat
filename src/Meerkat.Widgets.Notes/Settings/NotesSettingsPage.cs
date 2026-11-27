using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Meerkat.Abstractions;
using Meerkat.Widgets.Notes.Views;

namespace Meerkat.Widgets.Notes.Settings;

public sealed class NotesSettingsPage(NotesSettingsSource settingsSource) : IWidgetSettingsPage
{
    private NotesSettingsPageViewModel? _viewModel;

    public FrameworkElement CreateView()
    {
        _viewModel = new NotesSettingsPageViewModel(settingsSource.Current);
        return new NotesSettingsView { DataContext = _viewModel };
    }

    public void Apply()
    {
        if (_viewModel is not null)
        {
            settingsSource.Update(_viewModel.ToSettings());
        }
    }
}

public sealed record CompletedTaskOption(CompletedTaskHandling Handling, string DisplayName);

public sealed partial class NotesSettingsPageViewModel : ObservableObject
{
    private readonly NotesWidgetSettings _settings;

    [ObservableProperty]
    private string _directory;

    [ObservableProperty]
    private CompletedTaskOption _selectedCompletedTasks;

    [ObservableProperty]
    private bool _pinTasks;

    public NotesSettingsPageViewModel(NotesWidgetSettings settings)
    {
        _settings = settings;
        _directory = settings.GetDirectory();
        _selectedCompletedTasks = CompletedTaskOptions.First(option => option.Handling == settings.CompletedTasks);
        _pinTasks = settings.PinTasks;
    }

    public IReadOnlyList<CompletedTaskOption> CompletedTaskOptions { get; } =
    [
        new(CompletedTaskHandling.Archive, "Move to the archive note"),
        new(CompletedTaskHandling.Delete, "Delete"),
    ];

    public NotesWidgetSettings ToSettings()
    {
        // The default folder is stored as "not set", so it keeps following the user's documents folder.
        var isDefault = string.Equals(Directory.Trim(), new NotesWidgetSettings().GetDirectory(), StringComparison.OrdinalIgnoreCase);
        return new NotesWidgetSettings
        {
            Directory = isDefault || string.IsNullOrWhiteSpace(Directory) ? null : Directory.Trim(),
            CompletedTasks = SelectedCompletedTasks.Handling,
            PinTasks = PinTasks,
            LastCleanupDate = _settings.LastCleanupDate,
        };
    }
}
