using System.Windows;
using Meerkat.Abstractions;
using Meerkat.Widgets.Notes.Scratchpad;
using Meerkat.Widgets.Notes.Settings;
using Meerkat.Widgets.Notes.Storage;
using Meerkat.Widgets.Notes.ViewModels;
using Meerkat.Widgets.Notes.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Meerkat.Widgets.Notes;

/// <summary>
/// Notes as Markdown files, with a scratchpad that collects tasks and quick notes entered through global
/// hotkeys without leaving the current work.
/// </summary>
public sealed class NotesWidget(NotesWidgetViewModel viewModel, NotesSettingsPage settingsPage) : IOverlayWidget
{
    public const string WidgetId = "notes";

    private NotesWidgetView? _view;
    private NotesHeaderView? _headerView;
    private QuickCaptureWindow? _captureWindow;

    public event EventHandler? StatusChanged
    {
        add => viewModel.StatusChanged += value;
        remove => viewModel.StatusChanged -= value;
    }

    public string Id => WidgetId;

    public string Title => "Notes";

    public string Icon => "";

    public FrameworkElement View => _view ??= new NotesWidgetView { DataContext = viewModel };

    public FrameworkElement HeaderView => _headerView ??= new NotesHeaderView { DataContext = viewModel };

    public IWidgetSettingsPage SettingsPage => settingsPage;

    public WidgetStatusCard? StatusCard => viewModel.StatusCard;

    public IReadOnlyList<WidgetHotkey> Hotkeys =>
    [
        new("quick-note", "Quick note", "Ctrl+Alt+N", () => ShowCapture(CaptureKind.QuickNote)),
        new("new-task", "New task", "Ctrl+Alt+T", () => ShowCapture(CaptureKind.Task)),
    ];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        viewModel.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _captureWindow?.Close();
        viewModel.Stop();
        return Task.CompletedTask;
    }

    private void ShowCapture(CaptureKind kind)
    {
        if (_captureWindow is not null)
        {
            _captureWindow.Show(kind);
            return;
        }

        _captureWindow = new QuickCaptureWindow(kind, viewModel.Capture);
        _captureWindow.Closed += (_, _) => _captureWindow = null;
        _captureWindow.Show();
    }
}

public static class NotesWidgetServiceCollectionExtensions
{
    public static IServiceCollection AddNotesWidget(this IServiceCollection services)
    {
        services.AddSingleton<NotesSettingsSource>();
        services.AddSingleton<INoteStore, FileNoteStore>();
        services.AddSingleton(provider => new NotesWidgetViewModel(
            provider.GetRequiredService<INoteStore>(),
            provider.GetRequiredService<NotesSettingsSource>(),
            TimeProvider.System,
            provider.GetRequiredService<IOverlayCommands>()));
        services.AddSingleton<NotesSettingsPage>();
        services.AddSingleton<IOverlayWidget, NotesWidget>();
        return services;
    }
}
