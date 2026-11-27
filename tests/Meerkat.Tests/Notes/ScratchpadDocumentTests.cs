using Meerkat.Widgets.Notes.Markdown;
using Meerkat.Widgets.Notes.Scratchpad;

namespace Meerkat.Tests.Notes;

public class ScratchpadDocumentTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 15, 42, 0);

    [Fact]
    public void AddTask_AppendsToTheEndOfTheTaskList()
    {
        var text = "## Tasks\n\n- [x] Review\n- [ ] Check mapping\n\n## Quick notes\n\n## Commands\n\nfree text\n";

        var updated = ScratchpadDocument.AddTask(text, "Align queue definition");

        Assert.Equal("## Tasks\n\n- [x] Review\n- [ ] Check mapping\n- [ ] Align queue definition\n\n## Quick notes\n\n## Commands\n\nfree text\n", updated);
    }

    [Fact]
    public void AddTask_StartsTheListBelowTheHeading()
    {
        var updated = ScratchpadDocument.AddTask(ScratchpadDocument.Empty, "First");

        Assert.Equal("## Tasks\n\n- [ ] First\n\n## Quick notes\n", updated);
    }

    [Fact]
    public void AddQuickNote_PutsTheNewestOnTopOfToday()
    {
        var first = ScratchpadDocument.AddQuickNote(ScratchpadDocument.Empty, "Rückfrage ans Team", Now.AddHours(-4));
        var second = ScratchpadDocument.AddQuickNote(first, "Test token expires on Friday", Now);

        Assert.Equal(
            "## Tasks\n\n## Quick notes\n\n### 2026-11-27\n\n- 15:42 Test token expires on Friday\n- 11:42 Rückfrage ans Team\n",
            second);
    }

    [Fact]
    public void AddQuickNote_StartsANewDayAboveEarlierOnes()
    {
        var yesterday = ScratchpadDocument.AddQuickNote(ScratchpadDocument.Empty, "Old", Now.AddDays(-1));

        var updated = ScratchpadDocument.AddQuickNote(yesterday, "New", Now);

        Assert.Equal(
            "## Tasks\n\n## Quick notes\n\n### 2026-11-27\n\n- 15:42 New\n\n### 2026-11-26\n\n- 15:42 Old\n",
            updated);
    }

    [Fact]
    public void Changes_LeaveTheRestOfTheNoteUntouched()
    {
        var free = "## Commands\n\n```\ndocker exec -it broker rabbitmqctl list_queues\n\n```\n\n\nTrailing   spaces  stay \n";
        var text = "## Tasks\n\n## Quick notes\n\n" + free;

        var updated = ScratchpadDocument.AddQuickNote(ScratchpadDocument.AddTask(text, "Task"), "Note", Now);

        Assert.EndsWith(free, updated);
        Assert.StartsWith("## Tasks\n\n- [ ] Task\n\n## Quick notes\n\n### 2026-11-27\n\n- 15:42 Note\n\n## Commands", updated);
    }

    [Fact]
    public void MissingHeadings_AreRecreatedWithTheNextEntry()
    {
        var withTask = ScratchpadDocument.AddTask("My own text\n", "Task");
        Assert.Equal("## Tasks\n\n- [ ] Task\n\nMy own text\n", withTask);

        var withNote = ScratchpadDocument.AddQuickNote("## Tasks\n\n- [ ] Task\n\n## Commands\n\nfree\n", "Note", Now);
        Assert.Equal("## Tasks\n\n- [ ] Task\n\n## Quick notes\n\n### 2026-11-27\n\n- 15:42 Note\n\n## Commands\n\nfree\n", withNote);
    }

    [Fact]
    public void AddQuickNote_KeepsMultiLineInputOnOneLine()
    {
        var updated = ScratchpadDocument.AddQuickNote(ScratchpadDocument.Empty, "first\r\n## second", Now);

        Assert.Contains("- 15:42 first ## second\n", updated);
    }

    [Fact]
    public void GetTasks_ReadsOnlyTheTaskSection()
    {
        var text = "## Tasks\n\n- [x] Done\n- [ ] Open\n\n## Quick notes\n\n- [ ] not a task of the list\n";

        Assert.Equal([new ScratchpadTask("Done", true), new ScratchpadTask("Open", false)], ScratchpadDocument.GetTasks(text));
    }

    [Fact]
    public void RemoveCompletedTasks_ReturnsWhatItRemoved()
    {
        var text = "## Tasks\n\n- [x] Done\n- [ ] Open\n- [X] Also done\n\n## Quick notes\n\n- [x] stays\n";

        var (updated, removed) = ScratchpadDocument.RemoveCompletedTasks(text);

        Assert.Equal("## Tasks\n\n- [ ] Open\n\n## Quick notes\n\n- [x] stays\n", updated);
        Assert.Equal(["Done", "Also done"], removed);
    }

    [Fact]
    public void AddToArchive_PutsTheNewestDayFirst()
    {
        var first = ScratchpadDocument.AddToArchive(string.Empty, new DateOnly(2026, 11, 26), ["A"]);
        var second = ScratchpadDocument.AddToArchive(first, new DateOnly(2026, 11, 27), ["B", "C"]);

        Assert.Equal("# Archive\n\n## 2026-11-27\n\n- [x] B\n- [x] C\n\n## 2026-11-26\n\n- [x] A\n", second);
    }

    [Theory]
    [InlineData("Token expires", CaptureKind.QuickNote, CaptureKind.QuickNote, "Token expires")]
    [InlineData("[] Review", CaptureKind.QuickNote, CaptureKind.Task, "Review")]
    [InlineData("[ ] Review", CaptureKind.QuickNote, CaptureKind.Task, "Review")]
    [InlineData("- Review", CaptureKind.QuickNote, CaptureKind.Task, "Review")]
    [InlineData("- [ ] Review", CaptureKind.QuickNote, CaptureKind.Task, "Review")]
    [InlineData("-v does not work", CaptureKind.QuickNote, CaptureKind.QuickNote, "-v does not work")]
    [InlineData("  Review  ", CaptureKind.Task, CaptureKind.Task, "Review")]
    [InlineData("[] Review", CaptureKind.Task, CaptureKind.Task, "Review")]
    public void QuickCapture_RecognizesTasksByTheirPrefix(string input, CaptureKind defaultKind, CaptureKind expectedKind, string expectedText)
    {
        Assert.Equal(new QuickCapture(expectedKind, expectedText), QuickCapture.Parse(input, defaultKind));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    public void QuickCapture_IgnoresEmptyInput(string input)
    {
        Assert.Null(QuickCapture.Parse(input, CaptureKind.QuickNote));
    }

    [Fact]
    public void Blocks_SplitAtBlankLinesHeadingsAndCodeFences()
    {
        var text = "## Tasks\n- [ ] A\n- [x] B\n\n\nSome **bold** text\n```\ncode\n\nmore\n```\n";

        var blocks = MarkdownDocument.Split(text);

        Assert.Equal(
            [
                new MarkdownBlock("## Tasks", 0),
                new MarkdownBlock("- [ ] A\n- [x] B", 0),
                new MarkdownBlock("Some **bold** text", 2),
                new MarkdownBlock("```\ncode\n\nmore\n```", 0),
            ],
            blocks);
        Assert.Equal([BlockKind.Heading, BlockKind.Text, BlockKind.Text, BlockKind.Code], blocks.Select(block => block.Kind));
        Assert.Equal(2, blocks[0].HeadingLevel);
        Assert.Equal("Tasks", blocks[0].HeadingText);
        Assert.Equal("code\n\nmore", blocks[3].CodeText);
        Assert.Equal(text, MarkdownDocument.Join(blocks));
    }

    [Fact]
    public void Lines_AreRecognizedByTheirMarker()
    {
        Assert.Equal(new MarkdownLine(LineKind.Task, "Open"), MarkdownLine.Parse("- [ ] Open"));
        Assert.Equal(new MarkdownLine(LineKind.Task, "Done", IsDone: true), MarkdownLine.Parse("* [x] Done"));
        Assert.Equal(new MarkdownLine(LineKind.Timed, "Note", Time: "15:42"), MarkdownLine.Parse("- 15:42 Note"));
        Assert.Equal(new MarkdownLine(LineKind.Bullet, "Item"), MarkdownLine.Parse("- Item"));
        Assert.Equal(new MarkdownLine(LineKind.Plain, "Just text"), MarkdownLine.Parse("Just text"));
        Assert.Equal("- [x] Open", MarkdownLine.ToggleTask("- [ ] Open"));
        Assert.Equal("  - [ ] Done", MarkdownLine.ToggleTask("  - [x] Done"));
    }

    [Fact]
    public void Inline_SplitsCodeBoldAndItalic()
    {
        var runs = MarkdownInline.Parse("Fails if `egov.import` **already** *exists* in a_b_c");

        Assert.Equal(
            [
                new InlineRun("Fails if ", InlineStyle.None),
                new InlineRun("egov.import", InlineStyle.Code),
                new InlineRun(" ", InlineStyle.None),
                new InlineRun("already", InlineStyle.Bold),
                new InlineRun(" ", InlineStyle.None),
                new InlineRun("exists", InlineStyle.Italic),
                new InlineRun(" in a_b_c", InlineStyle.None),
            ],
            runs);
    }
}
