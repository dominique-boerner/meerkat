using Meerkat.Abstractions;
using Meerkat.Tests.Docker;
using Meerkat.Widgets.Notes.Scratchpad;
using Meerkat.Widgets.Notes.Settings;
using Meerkat.Widgets.Notes.ViewModels;

namespace Meerkat.Tests.Notes;

public class NotesWidgetViewModelTests
{
    private const string Scratchpad = NotesWidgetViewModel.ScratchpadName;

    private readonly InMemoryNoteStore _store = new();
    private readonly InMemoryWidgetSettingsStore _settingsStore = new();
    private readonly ManualTimeProvider _time = new(new DateTime(2026, 11, 27, 15, 42, 0));

    [Fact]
    public void Scratchpad_IsAlwaysThereAndComesFirst()
    {
        _store.Now = new DateTime(2026, 11, 26, 9, 0, 0);
        _store.Write("Sprint review", "notes");
        _store.Now = new DateTime(2026, 11, 20, 9, 0, 0);
        _store.Write("Ideas", "more notes");
        _store.Write("archive", "# Archive\n");

        var viewModel = Start();

        Assert.Equal(
            [
                new NoteListItemViewModel(Scratchpad, "Scratchpad", "empty", IsScratchpad: true, IsSelected: true),
                new NoteListItemViewModel("Sprint review", "Sprint review", "yesterday", IsScratchpad: false, IsSelected: false),
                new NoteListItemViewModel("Ideas", "Ideas", "20 Nov", IsScratchpad: false, IsSelected: false),
            ],
            viewModel.Notes);
        Assert.True(viewModel.IsScratchpad);
        Assert.Equal(["Tasks", "Quick notes"], viewModel.Blocks.Select(block => block.HeadingText));
        Assert.Equal(@"Notes\scratchpad.md", viewModel.PathText);

        // Nothing is written before the user enters something.
        Assert.Null(_store.Read(Scratchpad));
    }

    [Fact]
    public void Capture_AddsQuickNotesAndTasksToTheScratchpad()
    {
        var viewModel = Start();

        Assert.True(viewModel.Capture("Test token expires on Friday", CaptureKind.QuickNote));
        Assert.True(viewModel.Capture("[] Review !1287", CaptureKind.QuickNote));
        Assert.True(viewModel.Capture("Check mapping", CaptureKind.Task));
        Assert.False(viewModel.Capture("   ", CaptureKind.QuickNote));

        Assert.Equal(
            "## Tasks\n\n- [ ] Review !1287\n- [ ] Check mapping\n\n## Quick notes\n\n### 2026-11-27\n\n- 15:42 Test token expires on Friday\n",
            _store.Read(Scratchpad));

        // The open scratchpad shows the entries right away; today's date line stays out of sight.
        Assert.Equal(["Review !1287", "Check mapping"], viewModel.Blocks[1].Lines.Select(line => line.Text));
        Assert.True(viewModel.Blocks.Single(block => block.IsDateLine).IsHidden);
        Assert.Equal("today, 15:42", viewModel.Notes[0].DateText);
    }

    [Fact]
    public void DateLines_OfEarlierDaysAreShownInWords()
    {
        _store.Write(Scratchpad, "## Tasks\n\n## Quick notes\n\n### 2026-11-27\n\n- 09:00 A\n\n### 2026-11-26\n\n- 16:00 B\n\n### 2026-11-20\n\n- 12:00 C\n");

        var viewModel = Start();

        var dateLines = viewModel.Blocks.Where(block => block.IsDateLine).ToList();
        Assert.Equal([true, false, false], dateLines.Select(block => block.IsHidden));
        Assert.Equal(["Yesterday", "Fri, 20 Nov"], dateLines.Skip(1).Select(block => block.HeadingText));
    }

    [Fact]
    public void EditingABlock_SavesTheNoteAndKeepsTheOtherBlocks()
    {
        _store.Write(Scratchpad, "## Tasks\n\n- [ ] A\n\n## Quick notes\n\n## Commands\n\nold text\n");
        var viewModel = Start();
        var heading = viewModel.Blocks[0];
        var paragraph = viewModel.Blocks[^1];

        viewModel.BeginEdit(paragraph);
        Assert.Equal("old text", paragraph.EditText);
        paragraph.EditText = "new text\r\n\r\nand a second paragraph  ";
        viewModel.CommitEditing();

        Assert.Equal("## Tasks\n\n- [ ] A\n\n## Quick notes\n\n## Commands\n\nnew text\n\nand a second paragraph\n", _store.Read(Scratchpad));
        Assert.Equal(["new text", "and a second paragraph"], viewModel.Blocks.TakeLast(2).Select(block => block.Source.Text));
        Assert.DoesNotContain(viewModel.Blocks, block => block.IsEditing);

        // Untouched blocks keep their view models, so the view does not rebuild them.
        Assert.Same(heading, viewModel.Blocks[0]);
        Assert.Equal("saved", viewModel.SaveState);
    }

    [Fact]
    public void EditingAnotherBlock_CommitsTheOneBeingEdited()
    {
        _store.Write("Ideas", "first\n\nsecond\n");
        var viewModel = Start();
        viewModel.SelectCommand.Execute(viewModel.Notes[1]);

        viewModel.BeginEdit(viewModel.Blocks[0]);
        viewModel.Blocks[0].EditText = "first, edited";
        var second = viewModel.Blocks[1];
        viewModel.BeginEdit(second);

        Assert.Equal("first, edited\n\nsecond\n", _store.Read("Ideas"));
        Assert.True(second.IsEditing);
        Assert.Single(viewModel.Blocks, block => block.IsEditing);
    }

    [Fact]
    public void ClearingABlock_RemovesIt()
    {
        _store.Write("Ideas", "first\n\nsecond\n");
        var viewModel = Start();
        viewModel.SelectCommand.Execute(viewModel.Notes[1]);

        viewModel.BeginEdit(viewModel.Blocks[0]);
        viewModel.Blocks[0].EditText = "  ";
        viewModel.CommitEditing();

        Assert.Equal("second\n", _store.Read("Ideas"));
    }

    [Fact]
    public void AddBlock_AppendsWhatWasWrittenAndDropsAnEmptyOne()
    {
        _store.Write("Ideas", "first\n");
        var viewModel = Start();
        viewModel.SelectCommand.Execute(viewModel.Notes[1]);

        viewModel.AddBlockCommand.Execute(null);
        viewModel.CommitEditing();
        Assert.Single(viewModel.Blocks);
        Assert.Equal("first\n", _store.Read("Ideas"));

        viewModel.AddBlockCommand.Execute(null);
        viewModel.Blocks[^1].EditText = "- [ ] second";
        viewModel.CommitEditing();
        Assert.Equal("first\n\n- [ ] second\n", _store.Read("Ideas"));
    }

    [Fact]
    public void CommitAndInsertBelow_ContinuesInANewBlockRightBelow()
    {
        _store.Write("Ideas", "first\n\nlast\n");
        var viewModel = Start();
        viewModel.SelectCommand.Execute(viewModel.Notes[1]);

        viewModel.BeginEdit(viewModel.Blocks[0]);
        viewModel.Blocks[0].EditText = "first, edited\n\nand split";
        viewModel.CommitAndInsertBelow();

        // The new block follows everything the edited one turned into, not just its old position.
        Assert.Equal(["first, edited", "and split", string.Empty, "last"], viewModel.Blocks.Select(block => block.Source.Text));
        Assert.True(viewModel.Blocks[2].IsEditing);

        viewModel.Blocks[2].EditText = "second";
        viewModel.CommitAndInsertBelow();
        Assert.Equal("first, edited\n\nand split\n\nsecond\n\nlast\n", _store.Read("Ideas"));
        Assert.True(viewModel.Blocks[3].IsEditing);

        // Leaving the new block empty ends the series without a trace.
        viewModel.CommitEditing();
        Assert.Equal(["first, edited", "and split", "second", "last"], viewModel.Blocks.Select(block => block.Source.Text));
        Assert.Equal("first, edited\n\nand split\n\nsecond\n\nlast\n", _store.Read("Ideas"));
    }

    [Fact]
    public void EditAdjacent_MovesThroughTheBlocksAndStartsANewOneAtTheEnd()
    {
        _store.Write("Ideas", "first\n\nsecond\n");
        var viewModel = Start();
        viewModel.SelectCommand.Execute(viewModel.Notes[1]);

        viewModel.BeginEdit(viewModel.Blocks[0]);
        viewModel.Blocks[0].EditText = "first, edited";
        viewModel.EditAdjacent(1);
        Assert.Equal("first, edited\n\nsecond\n", _store.Read("Ideas"));
        Assert.Equal("second", viewModel.Blocks.Single(block => block.IsEditing).Source.Text);

        viewModel.EditAdjacent(-1);
        Assert.Equal("first, edited", viewModel.Blocks.Single(block => block.IsEditing).Source.Text);

        // Above the first block there is nothing, so the editing stays where it is.
        viewModel.EditAdjacent(-1);
        Assert.Equal("first, edited", viewModel.Blocks.Single(block => block.IsEditing).Source.Text);

        viewModel.EditAdjacent(1);
        viewModel.EditAdjacent(1);
        Assert.Equal(3, viewModel.Blocks.Count);
        Assert.True(viewModel.Blocks[2].IsEditing);

        // An empty block at the end is not followed by yet another one.
        viewModel.EditAdjacent(1);
        Assert.Equal(2, viewModel.Blocks.Count);
        Assert.DoesNotContain(viewModel.Blocks, block => block.IsEditing);
    }

    [Fact]
    public void EditAdjacent_SkipsTheHiddenDateLineOfToday()
    {
        _store.Write(Scratchpad, "## Tasks\n\n## Quick notes\n\n### 2026-11-27\n\n- 09:00 A\n");
        var viewModel = Start();

        viewModel.BeginEdit(viewModel.Blocks[1]);
        viewModel.EditAdjacent(1);

        Assert.Equal("- 09:00 A", viewModel.Blocks.Single(block => block.IsEditing).Source.Text);
    }

    [Fact]
    public void TogglingATask_ChecksItInTheFile()
    {
        _store.Write(Scratchpad, "## Tasks\n\n- [ ] A\n- [ ] B\n\n## Quick notes\n");
        var viewModel = Start();

        viewModel.Blocks[1].Lines[1].ToggleCommand.Execute(null);

        Assert.Equal("## Tasks\n\n- [ ] A\n- [x] B\n\n## Quick notes\n", _store.Read(Scratchpad));
        Assert.True(viewModel.Blocks[1].Lines[1].IsDone);
    }

    [Fact]
    public void ExternalChange_IsShownUnlessABlockIsBeingEdited()
    {
        _store.Write("Ideas", "first\n\nsecond\n");
        var viewModel = Start();
        viewModel.SelectCommand.Execute(viewModel.Notes[1]);

        _store.ChangeExternally("Ideas", "first\n\nsecond\n\nthird\n");
        Assert.Equal(["first", "second", "third"], viewModel.Blocks.Select(block => block.Source.Text));

        // While editing, the change made elsewhere must not pull the text away from under the cursor ...
        viewModel.BeginEdit(viewModel.Blocks[1]);
        viewModel.Blocks[1].EditText = "second, edited";
        _store.ChangeExternally("Ideas", "zero\n\nfirst\n\nsecond\n\nthird\n");
        Assert.True(viewModel.Blocks[1].IsEditing);

        // ... and committing keeps both changes.
        viewModel.CommitEditing();
        Assert.Equal("zero\n\nfirst\n\nsecond, edited\n\nthird\n", _store.Read("Ideas"));
        Assert.Equal(4, viewModel.Blocks.Count);
    }

    [Fact]
    public void ExternalChange_ThatRemovedTheEditedBlockKeepsTheEditAtTheEnd()
    {
        _store.Write("Ideas", "first\n\nsecond\n");
        var viewModel = Start();
        viewModel.SelectCommand.Execute(viewModel.Notes[1]);

        viewModel.BeginEdit(viewModel.Blocks[1]);
        viewModel.Blocks[1].EditText = "second, edited";
        _store.ChangeExternally("Ideas", "completely different\n");
        viewModel.CommitEditing();

        Assert.Equal("completely different\n\nsecond, edited\n", _store.Read("Ideas"));
    }

    [Fact]
    public void CompletedTasks_MoveToTheArchiveOnTheNextDay()
    {
        var viewModel = Start();
        viewModel.Capture("Done today", CaptureKind.Task);
        viewModel.Capture("Still open", CaptureKind.Task);
        viewModel.Blocks[1].Lines[0].ToggleCommand.Execute(null);

        // For the rest of the day the completed task stays where it is.
        viewModel.Refresh();
        Assert.Contains("- [x] Done today", _store.Read(Scratchpad));

        _time.Now = _time.Now.AddDays(1);
        viewModel.Refresh();

        Assert.Equal("## Tasks\n\n- [ ] Still open\n\n## Quick notes\n", _store.Read(Scratchpad));
        Assert.Equal("# Archive\n\n## 2026-11-27\n\n- [x] Done today\n", _store.Read(NotesWidgetViewModel.ArchiveName));
        Assert.Equal(["Still open"], viewModel.Blocks[1].Lines.Select(line => line.Text));
    }

    [Fact]
    public void CompletedTasks_AreDeletedIfTheUserPrefersThat()
    {
        _settingsStore.Save("notes", new NotesWidgetSettings { CompletedTasks = CompletedTaskHandling.Delete });
        var viewModel = Start();
        viewModel.Capture("Done", CaptureKind.Task);
        viewModel.Blocks[1].Lines[0].ToggleCommand.Execute(null);

        _time.Now = _time.Now.AddDays(3);
        viewModel.Refresh();

        Assert.Equal("## Tasks\n\n## Quick notes\n", _store.Read(Scratchpad));
        Assert.Null(_store.Read(NotesWidgetViewModel.ArchiveName));
    }

    [Fact]
    public void PinnedTasks_AreReportedAsStatusCard()
    {
        _store.Write(Scratchpad, "## Tasks\n\n- [x] Review\n- [ ] Check mapping\n- [ ] Align queue\n\n## Quick notes\n");
        var viewModel = Start();
        var changes = 0;
        viewModel.StatusChanged += (_, _) => changes++;
        Assert.Null(viewModel.StatusCard);

        viewModel.TogglePinCommand.Execute(null);

        Assert.Equal(1, changes);
        Assert.Equal("Tasks · 1 of 3 done", viewModel.StatusCard?.Title);
        Assert.Equal(
            [new WidgetStatusCardLine("Review", true), new WidgetStatusCardLine("Check mapping"), new WidgetStatusCardLine("Align queue")],
            viewModel.StatusCard?.Lines);
        Assert.True(_settingsStore.Load<NotesWidgetSettings>("notes").PinTasks);

        viewModel.Capture("One more", CaptureKind.Task);
        Assert.Equal(2, changes);
        Assert.Equal("Tasks · 1 of 4 done", viewModel.StatusCard?.Title);

        // Nothing changed, so the status bar is not asked to redraw.
        viewModel.Refresh();
        Assert.Equal(2, changes);

        viewModel.TogglePinCommand.Execute(null);
        Assert.Null(viewModel.StatusCard);
    }

    [Fact]
    public void StatusCard_ShortensALongTaskList()
    {
        _settingsStore.Save("notes", new NotesWidgetSettings { PinTasks = true });
        _store.Write(Scratchpad, "## Tasks\n\n" + string.Concat(Enumerable.Range(1, 11).Select(index => $"- [ ] Task {index}\n")));

        var viewModel = Start();

        Assert.Equal(NotesWidgetViewModel.MaxCardLines + 1, viewModel.StatusCard?.Lines.Count);
        Assert.Equal("+3 more", viewModel.StatusCard?.Lines[^1].Text);
    }

    [Fact]
    public void NewNote_GetsAFreeNameAndCanBeRenamed()
    {
        _store.Write("Untitled", "taken");
        var viewModel = Start();
        var titleEdits = 0;
        viewModel.TitleEditRequested += (_, _) => titleEdits++;

        viewModel.NewNoteCommand.Execute(null);

        Assert.Equal("Untitled 2", viewModel.Title);
        Assert.False(viewModel.IsScratchpad);
        Assert.Equal(1, titleEdits);
        Assert.Empty(viewModel.Blocks);

        Assert.True(viewModel.Rename(" Keycloak setup "));
        Assert.Equal("Keycloak setup", viewModel.Title);
        Assert.Equal(@"Notes\Keycloak setup.md", viewModel.PathText);
        Assert.NotNull(_store.Read("Keycloak setup"));
        Assert.Contains(viewModel.Notes, note => note is { Name: "Keycloak setup", IsSelected: true });
    }

    [Theory]
    [InlineData("Untitled")]
    [InlineData("scratchpad")]
    [InlineData("Archive")]
    [InlineData("a/b")]
    public void Rename_RefusesTitlesThatCannotBeUsed(string title)
    {
        _store.Write("Untitled", "taken");
        _store.Write("Ideas", "text");
        var viewModel = Start();
        viewModel.SelectCommand.Execute(viewModel.Notes.Single(note => note.Name == "Ideas"));

        Assert.False(viewModel.Rename(title));

        Assert.Equal("Ideas", viewModel.Title);
        Assert.True(viewModel.HasError);
        Assert.Equal("text", _store.Read("Ideas"));
    }

    [Fact]
    public void DeleteNote_ReturnsToTheScratchpadWhichCannotBeDeleted()
    {
        _store.Write("Ideas", "text");
        var viewModel = Start();
        viewModel.SelectCommand.Execute(viewModel.Notes[1]);

        viewModel.DeleteNoteCommand.Execute(null);

        Assert.Null(_store.Read("Ideas"));
        Assert.True(viewModel.IsScratchpad);
        Assert.Single(viewModel.Notes);

        viewModel.Capture("Note", CaptureKind.QuickNote);
        viewModel.DeleteNoteCommand.Execute(null);
        Assert.NotNull(_store.Read(Scratchpad));
    }

    [Fact]
    public void Search_FindsNotesByTitleAndContent()
    {
        _store.Write("Keycloak setup", "realm export");
        _store.Write("Sprint review", "RabbitMQ queue definition");
        _store.Write("Ideas", "nothing relevant");
        var viewModel = Start();

        viewModel.SearchText = "rabbit";
        Assert.Equal([Scratchpad, "Sprint review"], viewModel.Notes.Select(note => note.Name));

        viewModel.SearchText = "KEY";
        Assert.Equal([Scratchpad, "Keycloak setup"], viewModel.Notes.Select(note => note.Name));

        viewModel.SearchText = string.Empty;
        Assert.Equal(4, viewModel.Notes.Count);
    }

    [Fact]
    public void FailedSave_IsReportedInsteadOfThrown()
    {
        var viewModel = Start();
        _store.FailWrites = true;

        Assert.False(viewModel.Capture("Note", CaptureKind.QuickNote));

        Assert.True(viewModel.HasError);
        Assert.Equal("The disk is full.", viewModel.SaveState);
    }

    [Fact]
    public void OpenExternally_CreatesTheScratchpadFileFirst()
    {
        var viewModel = Start();

        viewModel.OpenExternallyCommand.Execute(null);

        Assert.Equal(Scratchpad, _store.OpenedInEditor);
        Assert.Equal(ScratchpadDocument.Empty, _store.Read(Scratchpad));
    }

    [Theory]
    [InlineData("2026-11-27T08:05:00", "today, 08:05")]
    [InlineData("2026-11-26T23:59:00", "yesterday")]
    [InlineData("2026-11-24T10:00:00", "24 Nov")]
    [InlineData("2025-12-31T10:00:00", "31 Dec 2025")]
    public void FormatDate_IsRelativeForRecentNotes(string modified, string expected)
    {
        Assert.Equal(expected, NotesWidgetViewModel.FormatDate(DateTime.Parse(modified, System.Globalization.CultureInfo.InvariantCulture), _time.Now));
    }

    private NotesWidgetViewModel Start()
    {
        var viewModel = new NotesWidgetViewModel(_store, new NotesSettingsSource(_settingsStore), _time);
        viewModel.Start();
        return viewModel;
    }
}
