namespace Meerkat.Widgets.Notes.Storage;

/// <param name="Name">The file name without extension; it is also the title of the note.</param>
public sealed record NoteInfo(string Name, DateTime LastModified);

/// <summary>The notes as the widget sees them: named Markdown texts, with line breaks normalized to "\n".</summary>
public interface INoteStore
{
    /// <summary>Raised when notes were added, removed or changed, possibly by another program and on any thread.</summary>
    event EventHandler? Changed;

    IReadOnlyList<NoteInfo> List();

    /// <summary>Returns null if the note does not exist.</summary>
    string? Read(string name);

    void Write(string name, string text);

    /// <summary>Returns false if the new name is taken or not usable as a file name.</summary>
    bool Rename(string name, string newName);

    void Delete(string name);

    /// <summary>Path of the note as shown to the user, e.g. "Notes\scratchpad.md".</summary>
    string GetDisplayPath(string name);

    void OpenInEditor(string name);

    void OpenFolder();
}
