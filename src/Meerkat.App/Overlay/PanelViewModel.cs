using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meerkat.Abstractions;
using Meerkat.App.Updates;

namespace Meerkat.App.Overlay;

public sealed record WidgetFailureViewModel(string Detail, ICommand RestartCommand, ICommand OpenLogCommand);

/// <summary>A widget's entry in the panel: its tab in the rail and the page shown when it is selected.</summary>
public sealed partial class WidgetEntryViewModel(string id, string title, string icon) : ObservableObject
{
    [ObservableProperty]
    private object? _content;

    [ObservableProperty]
    private object? _headerContent;

    /// <summary>Set while the widget is suspended after an error; shown instead of its content.</summary>
    [ObservableProperty]
    private WidgetFailureViewModel? _failure;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Highest severity among the widget's alerts; <see cref="WidgetSeverity.Ok"/> shows no badge.</summary>
    [ObservableProperty]
    private WidgetSeverity _badge;

    public string Id { get; } = id;

    public string Title { get; } = title;

    public string Icon { get; } = icon;
}

public sealed partial class PanelViewModel : ObservableObject
{
    [ObservableProperty]
    private WidgetEntryViewModel? _selectedWidget;

    [ObservableProperty]
    private string _attentionText = string.Empty;

    /// <summary>A pinned panel stays open when another window gets the focus.</summary>
    [ObservableProperty]
    private bool _isPinned;

    public event EventHandler? CloseRequested;

    public event EventHandler? SettingsRequested;

    /// <summary>Backs the update card; null where no update support is wired up.</summary>
    public UpdateViewModel? Update { get; init; }

    public ObservableCollection<WidgetEntryViewModel> Widgets { get; } = [];

    public void AddWidget(WidgetEntryViewModel widget)
    {
        Widgets.Add(widget);
        if (SelectedWidget is null)
        {
            Select(widget);
        }
    }

    public void RemoveWidget(WidgetEntryViewModel widget)
    {
        Widgets.Remove(widget);
        widget.IsSelected = false;
        if (SelectedWidget == widget)
        {
            SelectedWidget = null;
            if (Widgets.Count > 0)
            {
                Select(Widgets[0]);
            }
        }
    }

    public void SetAttentionCount(int count) => AttentionText = count switch
    {
        0 => "All quiet",
        1 => "1 needs attention",
        _ => $"{count} need attention",
    };

    [RelayCommand]
    private void Select(WidgetEntryViewModel widget)
    {
        foreach (var candidate in Widgets)
        {
            candidate.IsSelected = candidate == widget;
        }

        SelectedWidget = widget;
    }

    [RelayCommand]
    private void TogglePin() => IsPinned = !IsPinned;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);
}
