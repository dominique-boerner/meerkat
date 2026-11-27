using System.IO;
using System.Diagnostics;
using System.Text;
using Meerkat.Widgets.Notes.Markdown;
using Meerkat.Widgets.Notes.Settings;
using Microsoft.VisualBasic.FileIO;

namespace Meerkat.Widgets.Notes.Storage;

/// <summary>
/// Keeps every note as a Markdown file in one folder, so the notes can be read, edited, synchronized and
/// backed up with any other tool.
/// </summary>
public sealed class FileNoteStore : INoteStore, IDisposable
{
    private const string Extension = ".md";

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly NotesSettingsSource _settings;
    private FileSystemWatcher? _watcher;

    public FileNoteStore(NotesSettingsSource settings)
    {
        _settings = settings;
        _settings.Changed += (_, _) =>
        {
            // The folder may have changed; the next access watches the new one.
            StopWatching();
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? Changed;

    private string Folder => _settings.Current.GetDirectory();

    public IReadOnlyList<NoteInfo> List()
    {
        EnsureFolder();
        return [.. new DirectoryInfo(Folder)
            .EnumerateFiles("*" + Extension)
            .Select(file => new NoteInfo(Path.GetFileNameWithoutExtension(file.Name), file.LastWriteTime))];
    }

    public string? Read(string name)
    {
        var path = GetPath(name);
        return File.Exists(path) ? MarkdownDocument.Normalize(File.ReadAllText(path)) : null;
    }

    public void Write(string name, string text)
    {
        EnsureFolder();
        var path = GetPath(name);

        // Files keep the line breaks they came with, so a note edited elsewhere does not change as a whole.
        var usesUnixLineBreaks = File.Exists(path) && File.ReadAllText(path) is { Length: > 0 } existing && !existing.Contains("\r\n", StringComparison.Ordinal);
        var normalized = MarkdownDocument.Normalize(text);
        File.WriteAllText(path, usesUnixLineBreaks ? normalized : normalized.Replace("\n", "\r\n"), Utf8WithoutBom);
    }

    public bool Rename(string name, string newName)
    {
        if (newName.Length == 0 || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || newName.EndsWith('.'))
        {
            return false;
        }

        var target = GetPath(newName);

        // Changing only the letter case addresses the same file on Windows and must still be possible.
        if (File.Exists(target) && !string.Equals(name, newName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        File.Move(GetPath(name), target);
        return true;
    }

    public void Delete(string name)
    {
        var path = GetPath(name);
        if (File.Exists(path))
        {
            // The recycle bin makes a deletion by mistake recoverable without asking for confirmation.
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
    }

    public string GetDisplayPath(string name) => Path.Combine(Path.GetFileName(Path.TrimEndingDirectorySeparator(Folder)), name + Extension);

    public void OpenInEditor(string name)
    {
        var path = GetPath(name);
        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
    }

    public void OpenFolder()
    {
        EnsureFolder();
        Process.Start(new ProcessStartInfo(Folder) { UseShellExecute = true })?.Dispose();
    }

    public void Dispose() => StopWatching();

    private string GetPath(string name) => Path.Combine(Folder, name + Extension);

    private void EnsureFolder()
    {
        Directory.CreateDirectory(Folder);
        if (_watcher is not null)
        {
            return;
        }

        _watcher = new FileSystemWatcher(Folder, "*" + Extension)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.EnableRaisingEvents = true;
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}
