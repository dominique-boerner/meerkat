using System.IO;
using Meerkat.Abstractions;

namespace Meerkat.Widgets.Notes.Settings;

public enum CompletedTaskHandling
{
    /// <summary>Completed tasks move to the archive note.</summary>
    Archive,

    Delete,
}

public sealed class NotesWidgetSettings
{
    /// <summary>Folder with the notes; null selects "Meerkat\Notes" in the user's documents.</summary>
    public string? Directory { get; set; }

    public CompletedTaskHandling CompletedTasks { get; set; } = CompletedTaskHandling.Archive;

    /// <summary>Whether the tasks of the scratchpad are shown below the status bar.</summary>
    public bool PinTasks { get; set; }

    /// <summary>Last day (yyyy-MM-dd) on which completed tasks were tidied up; they are removed once the day changes.</summary>
    public string? LastCleanupDate { get; set; }

    public string GetDirectory() => string.IsNullOrWhiteSpace(Directory)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Meerkat", "Notes")
        : Environment.ExpandEnvironmentVariables(Directory.Trim());
}

public sealed class NotesSettingsSource(IWidgetSettingsStore store)
{
    private NotesWidgetSettings? _current;

    public event EventHandler? Changed;

    public NotesWidgetSettings Current => _current ??= store.Load<NotesWidgetSettings>(NotesWidget.WidgetId);

    /// <summary>Persists a change made to <see cref="Current"/> in place, without announcing new settings.</summary>
    public void Save() => store.Save(NotesWidget.WidgetId, Current);

    public void Update(NotesWidgetSettings settings)
    {
        _current = settings;
        store.Save(NotesWidget.WidgetId, settings);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
