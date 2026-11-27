using System.IO;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meerkat.Abstractions;
using Meerkat.Widgets.Notes.Markdown;
using Meerkat.Widgets.Notes.Scratchpad;
using Meerkat.Widgets.Notes.Settings;
using Meerkat.Widgets.Notes.Storage;

namespace Meerkat.Widgets.Notes.ViewModels;

public sealed record NoteListItemViewModel(string Name, string Title, string DateText, bool IsScratchpad, bool IsSelected);

public sealed partial class NotesWidgetViewModel : ObservableObject
{
    public const string ScratchpadName = "scratchpad";
    public const string ArchiveName = "archive";
    public const int MaxCardLines = 8;

    private const string ScratchpadTitle = "Scratchpad";
    private const string NewNoteName = "Untitled";
    private const string SavedText = "saved";

    // The day may change while Meerkat keeps running; this is how soon completed tasks are tidied up then.
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(10);

    private readonly INoteStore _store;
    private readonly NotesSettingsSource _settings;
    private readonly TimeProvider _time;

    private SynchronizationContext? _context;
    private ITimer? _timer;
    private string _selectedName = ScratchpadName;

    // The text of the open note as last read or written; a different text on disk means someone else changed it.
    private string _knownText = string.Empty;
    private bool _isWriting;
    private WidgetStatusCard? _card;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _title = ScratchpadTitle;

    [ObservableProperty]
    private bool _isScratchpad = true;

    [ObservableProperty]
    private string _saveState = SavedText;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _pathText = string.Empty;

    [ObservableProperty]
    private bool _isPinned;

    public NotesWidgetViewModel(INoteStore store, NotesSettingsSource settings, TimeProvider time)
    {
        _store = store;
        _settings = settings;
        _time = time;
    }

    public event EventHandler? StatusChanged;

    /// <summary>Raised after a note was created, so that the view lets the user name it right away.</summary>
    public event EventHandler? TitleEditRequested;

    public ObservableCollection<NoteListItemViewModel> Notes { get; } = [];

    public ObservableCollection<BlockViewModel> Blocks { get; } = [];

    /// <summary>The tasks of the scratchpad, if the user pinned them to the status bar.</summary>
    public WidgetStatusCard? StatusCard => _card;

    public void Start()
    {
        _context = SynchronizationContext.Current;
        _store.Changed += OnStoreChanged;
        _settings.Changed += OnSettingsChanged;
        _timer = _time.CreateTimer(_ => Post(Refresh), null, CleanupInterval, CleanupInterval);
        IsPinned = _settings.Current.PinTasks;
        Guard(LoadSelected);
        Refresh();
    }

    public void Stop()
    {
        CommitEditing();
        _store.Changed -= OnStoreChanged;
        _settings.Changed -= OnSettingsChanged;
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Brings list, open note and status card in line with the files, e.g. after another program changed them.</summary>
    public void Refresh() => Guard(() =>
    {
        TidyUpCompletedTasks();
        RefreshList();
        if (_selectedName != ScratchpadName && _store.Read(_selectedName) is null)
        {
            // The open note was deleted or renamed elsewhere.
            _selectedName = ScratchpadName;
            LoadSelected();
        }
        else if (!Blocks.Any(block => block.IsEditing) && ReadSelected() is var text && text != _knownText)
        {
            Load(text);
        }

        UpdateCard();
    });

    /// <summary>Saves what was typed into the capture field. Returns false if there was nothing to save or saving failed.</summary>
    public bool Capture(string input, CaptureKind kind)
    {
        if (QuickCapture.Parse(input, kind) is not { } capture)
        {
            return false;
        }

        CommitEditing();
        return Guard(() =>
        {
            var text = _store.Read(ScratchpadName) ?? ScratchpadDocument.Empty;
            var updated = capture.Kind == CaptureKind.Task
                ? ScratchpadDocument.AddTask(text, capture.Text)
                : ScratchpadDocument.AddQuickNote(text, capture.Text, _time.GetLocalNow().DateTime);
            Write(ScratchpadName, updated);
            if (IsScratchpad)
            {
                Load(updated);
            }

            AfterWrite();
        });
    }

    public void BeginEdit(BlockViewModel block)
    {
        if (block.IsEditing)
        {
            return;
        }

        CommitEditing();

        // Committing may have replaced the block, e.g. when the note was changed elsewhere in the meantime.
        if (Blocks.Contains(block))
        {
            block.EditText = block.Source.Text;
            block.IsEditing = true;
        }
    }

    /// <summary>Ends editing and saves the block if its text changed. A block left empty is removed.</summary>
    public void CommitEditing()
    {
        if (Blocks.FirstOrDefault(candidate => candidate.IsEditing) is not { } block)
        {
            return;
        }

        block.IsEditing = false;
        var text = string.Join('\n', MarkdownDocument.Normalize(block.EditText).Split('\n').Select(line => line.TrimEnd())).Trim('\n');
        if (text != block.Source.Text)
        {
            Guard(() => ApplyChange(block, text));
        }
        else if (text.Length == 0)
        {
            Blocks.Remove(block);
        }
    }

    /// <summary>Renames the open note. Returns false, leaving everything as it was, if the title cannot be used.</summary>
    public bool Rename(string newTitle)
    {
        var name = newTitle.Trim();
        if (IsScratchpad || name == _selectedName)
        {
            Title = IsScratchpad ? ScratchpadTitle : _selectedName;
            return true;
        }

        var isReserved = string.Equals(name, ScratchpadName, StringComparison.OrdinalIgnoreCase) || string.Equals(name, ArchiveName, StringComparison.OrdinalIgnoreCase);
        var renamed = false;
        Guard(() =>
        {
            CommitEditing();
            renamed = !isReserved && _store.Rename(_selectedName, name);
            if (renamed)
            {
                _selectedName = name;
                PathText = _store.GetDisplayPath(name);
                RefreshList();
            }
            else
            {
                ShowError("This title is taken or cannot be used as a file name");
            }
        });

        Title = _selectedName;
        return renamed;
    }

    [RelayCommand]
    private void Select(NoteListItemViewModel note)
    {
        CommitEditing();
        _selectedName = note.Name;
        Guard(() =>
        {
            LoadSelected();
            RefreshList();
        });
    }

    [RelayCommand]
    private void NewNote()
    {
        CommitEditing();
        var created = Guard(() =>
        {
            var taken = _store.List().Select(note => note.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var name = NewNoteName;
            for (var number = 2; taken.Contains(name); number++)
            {
                name = $"{NewNoteName} {number}";
            }

            Write(name, string.Empty);
            _selectedName = name;

            // A filter would hide the new note from the list it was just added to.
            SearchText = string.Empty;
            LoadSelected();
            RefreshList();
        });

        if (created)
        {
            TitleEditRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void DeleteNote()
    {
        if (IsScratchpad)
        {
            return;
        }

        Guard(() =>
        {
            Blocks.Clear();
            _store.Delete(_selectedName);
            _selectedName = ScratchpadName;
            LoadSelected();
            RefreshList();
        });
    }

    [RelayCommand]
    private void AddBlock()
    {
        CommitEditing();
        var block = CreateBlock(new MarkdownBlock(string.Empty, Blocks.Count > 0 ? 1 : 0));
        block.IsEditing = true;
        Blocks.Add(block);
    }

    [RelayCommand]
    private void TogglePin()
    {
        IsPinned = !IsPinned;
        Guard(() =>
        {
            _settings.Current.PinTasks = IsPinned;
            _settings.Save();
            UpdateCard();
        });
    }

    [RelayCommand]
    private void OpenExternally()
    {
        CommitEditing();
        Guard(() =>
        {
            // The scratchpad exists as a file only once something was written to it.
            if (_store.Read(_selectedName) is null)
            {
                Write(_selectedName, _knownText);
            }

            _store.OpenInEditor(_selectedName);
        });
    }

    [RelayCommand]
    private void OpenFolder() => Guard(_store.OpenFolder);

    public static string FormatDate(DateTime modified, DateTime now)
    {
        var days = (now.Date - modified.Date).Days;
        return days switch
        {
            <= 0 => $"today, {modified.ToString("HH:mm", CultureInfo.InvariantCulture)}",
            1 => "yesterday",
            _ => modified.ToString(modified.Year == now.Year ? "d MMM" : "d MMM yyyy", CultureInfo.InvariantCulture),
        };
    }

    partial void OnSearchTextChanged(string value) => Guard(RefreshList);

    private void ToggleTask(BlockViewModel block, int lineIndex)
    {
        CommitEditing();
        if (!Blocks.Contains(block))
        {
            return;
        }

        var lines = block.Source.Text.Split('\n');
        lines[lineIndex] = MarkdownLine.ToggleTask(lines[lineIndex]);
        Guard(() => ApplyChange(block, string.Join('\n', lines)));
    }

    private void ApplyChange(BlockViewModel block, string newText)
    {
        var onDisk = ReadSelected();
        string updated;
        if (onDisk == _knownText)
        {
            var sources = Blocks
                .Select(candidate => candidate == block ? candidate.Source with { Text = newText } : candidate.Source)
                .Where(source => source.Text.Length > 0)
                .ToList();

            // If the first block was removed, the next one must not bring its spacing to the top of the note.
            if (newText.Length == 0 && Blocks[0] == block && sources.Count > 0)
            {
                sources[0] = sources[0] with { BlankLinesBefore = block.Source.BlankLinesBefore };
            }

            updated = MarkdownDocument.Join(sources);
        }
        else if (block.Source.Text.Length > 0 && CountOccurrences(onDisk, block.Source.Text) == 1)
        {
            // The note was changed elsewhere in the meantime: only the edited block is replaced in the new text.
            updated = onDisk.Replace(block.Source.Text, newText, StringComparison.Ordinal);
        }
        else
        {
            // The block cannot be located any more; appending it keeps both the other change and this one.
            updated = newText.Length > 0 ? $"{onDisk.TrimEnd('\n')}\n\n{newText}\n" : onDisk;
        }

        Write(_selectedName, updated);
        Load(updated);
        AfterWrite();
    }

    /// <summary>
    /// Completed tasks stay visible for the rest of the day and leave the scratchpad once the day has changed.
    /// </summary>
    private void TidyUpCompletedTasks()
    {
        var settings = _settings.Current;
        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        var todayText = today.ToString(ScratchpadDocument.DateFormat, CultureInfo.InvariantCulture);
        if (settings.LastCleanupDate == todayText)
        {
            return;
        }

        var isNewDay = DateOnly.TryParseExact(settings.LastCleanupDate, ScratchpadDocument.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var lastDay) && lastDay < today;
        if (isNewDay && _store.Read(ScratchpadName) is { } text && ScratchpadDocument.RemoveCompletedTasks(text) is { Removed.Count: > 0 } result)
        {
            if (settings.CompletedTasks == CompletedTaskHandling.Archive)
            {
                // The tasks were completed on the last day Meerkat ran at the latest.
                Write(ArchiveName, ScratchpadDocument.AddToArchive(_store.Read(ArchiveName) ?? string.Empty, lastDay, result.Removed));
            }

            Write(ScratchpadName, result.Text);
        }

        settings.LastCleanupDate = todayText;
        _settings.Save();

        // Which date line counts as "today" has changed, so the open scratchpad is rendered anew.
        if (isNewDay && IsScratchpad && !Blocks.Any(block => block.IsEditing))
        {
            Blocks.Clear();
            Load(ReadSelected());
        }
    }

    private void LoadSelected()
    {
        IsScratchpad = _selectedName == ScratchpadName;
        Title = IsScratchpad ? ScratchpadTitle : _selectedName;
        PathText = _store.GetDisplayPath(_selectedName);
        Blocks.Clear();
        Load(ReadSelected());
        ShowSaved();
    }

    private string ReadSelected() => _store.Read(_selectedName) ?? (_selectedName == ScratchpadName ? ScratchpadDocument.Empty : string.Empty);

    /// <summary>Shows a text, keeping the blocks that did not change so that the view does not rebuild them.</summary>
    private void Load(string text)
    {
        _knownText = text;
        var updated = MarkdownDocument.Split(text);

        var prefix = 0;
        while (prefix < Blocks.Count && prefix < updated.Count && Blocks[prefix].Source == updated[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (prefix + suffix < Math.Min(Blocks.Count, updated.Count) && Blocks[^(suffix + 1)].Source == updated[^(suffix + 1)])
        {
            suffix++;
        }

        for (var index = Blocks.Count - suffix - 1; index >= prefix; index--)
        {
            Blocks.RemoveAt(index);
        }

        for (var index = prefix; index < updated.Count - suffix; index++)
        {
            Blocks.Insert(index, CreateBlock(updated[index]));
        }
    }

    private BlockViewModel CreateBlock(MarkdownBlock source)
    {
        // Quick notes are grouped by day under a line with the date, which reads better as "Yesterday" or "Mon, 23 Nov".
        if (IsScratchpad
            && source is { Kind: BlockKind.Heading, HeadingLevel: 3 }
            && DateOnly.TryParseExact(source.HeadingText, ScratchpadDocument.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
            var label = today.DayNumber - day.DayNumber == 1
                ? "Yesterday"
                : day.ToString(day.Year == today.Year ? "ddd, d MMM" : "ddd, d MMM yyyy", CultureInfo.InvariantCulture);
            return new BlockViewModel(source, ToggleTask, label, isHidden: day == today);
        }

        return new BlockViewModel(source, ToggleTask);
    }

    private void RefreshList()
    {
        var now = _time.GetLocalNow().DateTime;
        var notes = _store.List();
        var scratchpad = notes.FirstOrDefault(note => IsName(note, ScratchpadName));
        var items = new List<NoteListItemViewModel>
        {
            new(ScratchpadName, ScratchpadTitle, scratchpad is null ? "empty" : FormatDate(scratchpad.LastModified, now), IsScratchpad: true, _selectedName == ScratchpadName),
        };
        items.AddRange(notes
            .Where(note => !IsName(note, ScratchpadName) && !IsName(note, ArchiveName) && MatchesSearch(note))
            .OrderByDescending(note => note.LastModified)
            .Select(note => new NoteListItemViewModel(note.Name, note.Name, FormatDate(note.LastModified, now), IsScratchpad: false, note.Name == _selectedName)));

        // Files change often while typing; an unchanged list is left alone to avoid needless redraws.
        if (!Notes.SequenceEqual(items))
        {
            Notes.Clear();
            foreach (var item in items)
            {
                Notes.Add(item);
            }
        }
    }

    private bool MatchesSearch(NoteInfo note)
    {
        var search = SearchText.Trim();
        return search.Length == 0
            || note.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (_store.Read(note.Name)?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void UpdateCard()
    {
        WidgetStatusCard? card = null;
        if (IsPinned && ScratchpadDocument.GetTasks(_store.Read(ScratchpadName) ?? string.Empty) is { Count: > 0 } tasks)
        {
            var lines = tasks.Take(MaxCardLines).Select(task => new WidgetStatusCardLine(task.Text, task.IsDone)).ToList();
            if (tasks.Count > MaxCardLines)
            {
                lines.Add(new WidgetStatusCardLine($"+{tasks.Count - MaxCardLines} more"));
            }

            card = new WidgetStatusCard($"Tasks · {tasks.Count(task => task.IsDone)} of {tasks.Count} done", lines);
        }

        var isUnchanged = card?.Title == _card?.Title && (card?.Lines ?? []).SequenceEqual(_card?.Lines ?? []);
        if (!isUnchanged)
        {
            _card = card;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Write(string name, string text)
    {
        // The store reports every change, including this one; it must not be mistaken for a change made elsewhere.
        _isWriting = true;
        try
        {
            _store.Write(name, text);
        }
        finally
        {
            _isWriting = false;
        }
    }

    private void AfterWrite()
    {
        ShowSaved();
        RefreshList();
        UpdateCard();
    }

    private void ShowSaved()
    {
        SaveState = SavedText;
        HasError = false;
    }

    private void ShowError(string message)
    {
        SaveState = message;
        HasError = true;
    }

    /// <summary>Runs a file operation; a failure is shown next to the title instead of taking the widget down.</summary>
    private bool Guard(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowError(exception.Message);
            return false;
        }
    }

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        if (!_isWriting)
        {
            Post(Refresh);
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Post(() =>
    {
        IsPinned = _settings.Current.PinTasks;
        Guard(LoadSelected);
        Refresh();
    });

    private void Post(Action action)
    {
        if (_context is { } context && context != SynchronizationContext.Current)
        {
            context.Post(_ => action(), null);
        }
        else
        {
            action();
        }
    }

    private static bool IsName(NoteInfo note, string name) => string.Equals(note.Name, name, StringComparison.OrdinalIgnoreCase);

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
