using System.IO;
using Meerkat.Widgets.Notes.Storage;

namespace Meerkat.Tests.Notes;

internal sealed class InMemoryNoteStore : INoteStore
{
    private readonly Dictionary<string, (string Text, DateTime Modified)> _notes = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? Changed;

    /// <summary>Used as modification time of whatever is written next.</summary>
    public DateTime Now { get; set; } = new(2026, 11, 27, 15, 42, 0);

    public string? OpenedInEditor { get; private set; }

    public bool FailWrites { get; set; }

    public IReadOnlyList<NoteInfo> List() => [.. _notes.Select(note => new NoteInfo(note.Key, note.Value.Modified))];

    public string? Read(string name) => _notes.TryGetValue(name, out var note) ? note.Text : null;

    public void Write(string name, string text)
    {
        if (FailWrites)
        {
            throw new IOException("The disk is full.");
        }

        _notes[name] = (text, Now);
    }

    /// <summary>Changes a note the way another program would: behind the widget's back, then announced.</summary>
    public void ChangeExternally(string name, string text)
    {
        _notes[name] = (text, Now);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Rename(string name, string newName)
    {
        if (_notes.ContainsKey(newName) || newName.Contains('/'))
        {
            return false;
        }

        _notes[newName] = _notes[name];
        _notes.Remove(name);
        return true;
    }

    public void Delete(string name) => _notes.Remove(name);

    public string GetDisplayPath(string name) => $@"Notes\{name}.md";

    public void OpenInEditor(string name) => OpenedInEditor = name;

    public void OpenFolder()
    {
    }
}

/// <summary>A clock that stands still until a test moves it, in the local time zone of the tests: UTC.</summary>
internal sealed class ManualTimeProvider(DateTime now) : TimeProvider
{
    public DateTime Now { get; set; } = now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new IdleTimer();

    private sealed class IdleTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
