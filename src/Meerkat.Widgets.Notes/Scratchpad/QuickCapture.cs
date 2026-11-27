using System.Text.RegularExpressions;

namespace Meerkat.Widgets.Notes.Scratchpad;

public enum CaptureKind
{
    QuickNote,
    Task,
}

/// <summary>What the user typed into the capture field, and where it belongs.</summary>
public sealed partial record QuickCapture(CaptureKind Kind, string Text)
{
    /// <summary>
    /// Interprets the input of the capture field. A leading "[]" or "- " turns a quick note into a task,
    /// so one hotkey is enough for both. Returns null when there is nothing to save.
    /// </summary>
    public static QuickCapture? Parse(string input, CaptureKind defaultKind)
    {
        var text = input.Trim();
        var kind = defaultKind;
        if (TaskPrefixPattern().Match(text) is { Success: true } prefix)
        {
            kind = CaptureKind.Task;
            text = text[prefix.Length..].Trim();
        }

        return text.Length > 0 ? new QuickCapture(kind, text) : null;
    }

    // "- [ ] text", "[] text", "[ ] text" or "- text". The dash needs a space so that "-v fails" stays a note.
    [GeneratedRegex(@"^(?:-\s+\[\s?\]|\[\s?\]|-\s)")]
    private static partial Regex TaskPrefixPattern();
}
