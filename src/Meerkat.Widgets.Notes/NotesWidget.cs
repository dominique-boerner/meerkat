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
public sealed class NotesWidget(NotesWidgetViewModel viewModel, NotesSettingsPage settingsPage, IOverlayCommands commands) : IOverlayWidget
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

    public IReadOnlyList<WidgetAction> Actions =>
    [
        new()
        {
            Id = "quick-note",
            Title = "Quick note",
            Run = (_, _) =>
            {
                ShowCapture(CaptureKind.QuickNote);
                return Task.FromResult(WidgetActionResult.Done);
            },

            // What was typed into the search and found no action is most likely a thought to keep.
            TextTitle = "Save as quick note",
            RunWithText = (text, _) => Task.FromResult(viewModel.Capture(text, CaptureKind.QuickNote)
                ? WidgetActionResult.Success("Saved to the quick notes")
                : WidgetActionResult.Failure("The note could not be saved", "The Notes page tells why.")),
        },
        new()
        {
            Id = "new-note",
            Title = "New note",
            Run = (_, _) =>
            {
                viewModel.NewNoteCommand.Execute(null);
                commands.ShowWidget(WidgetId);
                return Task.FromResult(WidgetActionResult.Done);
            },
        },
        new()
        {
            Id = "open-note",
            Title = "Open note",
            TargetPrompt = "Choose a note …",
            Targets = () => [new WidgetActionTargetGroup("Notes", [.. viewModel.Notes.Select(note => new WidgetActionTarget(note.Name, note.Title, note.DateText))])],
            Run = (target, _) =>
            {
                if (viewModel.Notes.FirstOrDefault(note => note.Name == target?.Id) is not { } note)
                {
                    return Task.FromResult(WidgetActionResult.Failure($"{target?.Title ?? "The note"} is no longer there"));
                }

                viewModel.SelectCommand.Execute(note);
                commands.ShowWidget(WidgetId);
                return Task.FromResult(WidgetActionResult.Done);
            },
        },
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
