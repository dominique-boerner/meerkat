using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meerkat.Widgets.Notes.Markdown;

namespace Meerkat.Widgets.Notes.ViewModels;

public sealed partial class LineViewModel(int index, MarkdownLine line, Action<int> toggle)
{
    public LineKind Kind => line.Kind;

    public string Text => line.Text;

    public bool IsDone => line.IsDone;

    public string? Time => line.Time;

    [RelayCommand]
    private void Toggle() => toggle(index);
}

/// <summary>One block of the open note: shown rendered, and as Markdown source while it is being edited.</summary>
public sealed partial class BlockViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editText = string.Empty;

    /// <param name="headingOverride">Text shown instead of the heading as written, e.g. "Yesterday" for a date.</param>
    public BlockViewModel(MarkdownBlock source, Action<BlockViewModel, int> toggleTask, string? headingOverride = null, bool isHidden = false)
    {
        Source = source;
        HeadingText = headingOverride ?? source.HeadingText;
        IsDateLine = headingOverride is not null || isHidden;
        IsHidden = isHidden;
        Lines = source.Kind == BlockKind.Text
            ? [.. source.Text.Split('\n').Select((line, index) => new LineViewModel(index, MarkdownLine.Parse(line), lineIndex => toggleTask(this, lineIndex)))]
            : [];
    }

    public MarkdownBlock Source { get; }

    public BlockKind Kind => Source.Kind;

    public int HeadingLevel => Source.HeadingLevel;

    public string HeadingText { get; }

    /// <summary>True for the lines that separate the quick notes of different days.</summary>
    public bool IsDateLine { get; }

    /// <summary>The date line of today is not shown: notes without a date line are the ones from today.</summary>
    public bool IsHidden { get; }

    public string CodeText => Source.CodeText;

    public IReadOnlyList<LineViewModel> Lines { get; }
}
