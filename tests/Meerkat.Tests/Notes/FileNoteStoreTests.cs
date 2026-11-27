using System.IO;
using Meerkat.Tests.Docker;
using Meerkat.Widgets.Notes.Settings;
using Meerkat.Widgets.Notes.Storage;

namespace Meerkat.Tests.Notes;

public sealed class FileNoteStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Meerkat.Tests", Guid.NewGuid().ToString("N"), "Notes");
    private readonly FileNoteStore _store;

    public FileNoteStoreTests()
    {
        var settings = new InMemoryWidgetSettingsStore();
        settings.Save("notes", new NotesWidgetSettings { Directory = _folder });
        _store = new FileNoteStore(new NotesSettingsSource(settings));
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(Path.GetDirectoryName(_folder)!, recursive: true);
    }

    [Fact]
    public void Notes_AreMarkdownFilesInTheFolder()
    {
        Assert.Empty(_store.List());
        Assert.Null(_store.Read("Ideas"));

        _store.Write("Ideas", "# Ideas\n\nGrüße – ß\n");

        Assert.Equal("# Ideas\n\nGrüße – ß\n", _store.Read("Ideas"));
        Assert.Equal(["Ideas"], _store.List().Select(note => note.Name));
        Assert.True(File.Exists(Path.Combine(_folder, "Ideas.md")));
        Assert.Equal(@"Notes\Ideas.md", _store.GetDisplayPath("Ideas"));
    }

    [Fact]
    public void Write_KeepsTheLineBreaksAFileCameWith()
    {
        var unixFile = Path.Combine(_folder, "unix.md");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(unixFile, "a\nb\n");

        _store.Write("unix", "a\nb\nc\n");
        _store.Write("windows", "a\nb\n");

        Assert.Equal("a\nb\nc\n", File.ReadAllText(unixFile));
        Assert.Equal("a\r\nb\r\n", File.ReadAllText(Path.Combine(_folder, "windows.md")));
        Assert.Equal("a\nb\n", _store.Read("windows"));
    }

    [Fact]
    public void Rename_RefusesTakenAndInvalidNames()
    {
        _store.Write("Ideas", "text");
        _store.Write("Other", "text");

        Assert.False(_store.Rename("Ideas", "Other"));
        Assert.False(_store.Rename("Ideas", @"sub\folder"));
        Assert.False(_store.Rename("Ideas", string.Empty));
        Assert.True(_store.Rename("Ideas", "ideas"));
        Assert.True(_store.Rename("ideas", "Plans"));

        Assert.Equal(["Other", "Plans"], _store.List().Select(note => note.Name).Order());
    }

    [Fact]
    public async Task Changed_IsRaisedWhenAnotherProgramWritesAFile()
    {
        _store.Write("Ideas", "text");
        var changed = new TaskCompletionSource();
        _store.Changed += (_, _) => changed.TrySetResult();

        await File.WriteAllTextAsync(Path.Combine(_folder, "Ideas.md"), "changed elsewhere");

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
