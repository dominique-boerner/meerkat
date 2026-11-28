using Meerkat.Abstractions;
using Meerkat.App.Actions;

namespace Meerkat.Tests.App;

public sealed class ActionPaletteTests
{
    private readonly List<string> _ran = [];
    private readonly List<TimeSpan> _closed = [];
    private readonly List<string> _used = [];
    private readonly List<string> _opened = [];

    [Theory]
    [InlineData("Restart container", "rest", new[] { 0, 1, 2, 3 })]
    [InlineData("Restart container", "rc", new[] { 0, 8 })]
    [InlineData("Open SSH terminal", "ssh", new[] { 5, 6, 7 })]
    [InlineData("Restart container", "RESTART", new[] { 0, 1, 2, 3, 4, 5, 6 })]
    public void Search_FindsLettersInOrderAndPrefersWordStarts(string text, string query, int[] expected) =>
        Assert.Equal(expected, ActionSearch.Match(text, query)!.Positions);

    [Fact]
    public void Search_NeedsEveryLetterInOrder()
    {
        Assert.Null(ActionSearch.Match("Restart container", "xyz"));
        Assert.Null(ActionSearch.Match("Stop", "pots"));
        Assert.Null(ActionSearch.Match("Stop", "stopped"));
    }

    [Fact]
    public void Search_RanksAWordAboveScatteredLetters() =>
        Assert.True(ActionSearch.Match("Stop container", "stop")!.Score > ActionSearch.Match("Scan the open ports", "stop")!.Score);

    [Fact]
    public void Search_IgnoresLettersScatteredByChance()
    {
        // "scan" is in there letter by letter, but nobody who types it means this.
        Assert.Null(ActionSearch.Match("Restart container", "scan"));
        Assert.NotNull(ActionSearch.Match("Restart container", "rst"));
        Assert.NotNull(ActionSearch.Match("Restart container", "tain"));
    }

    [Fact]
    public void EmptyField_ListsRecentActionsFirstAndTheRestByArea()
    {
        var palette = Create(recent: ["docker/stop", "gone/action", "git/open"]);

        Assert.Equal(["Stop container…", "Open pipeline", "Restart container…", "Scan images", "Quick note"], palette.Rows.Select(Text));
        Assert.Equal(["RECENTLY USED", null, "DOCKER", null, "NOTES"], palette.Rows.Select(row => row.Group));
        Assert.True(palette.Rows[0].IsSelected);
    }

    [Fact]
    public void Typing_ShowsMatchesBestFirstWithWhatWasTyped()
    {
        var palette = Create();

        palette.Query = "scan";

        var row = Assert.Single(palette.Rows);
        Assert.Equal("DOCKER", row.Group);
        Assert.Equal([new TextSegment("Scan", true), new TextSegment(" images", false)], row.Title);
    }

    [Fact]
    public void Typing_TheNameOfAWidgetListsItsActions()
    {
        var palette = Create();

        palette.Query = "docker";

        Assert.Equal(["Restart container…", "Stop container…", "Scan images"], palette.Rows.Select(Text));
    }

    [Fact]
    public async Task ActionWithoutTarget_RunsAndClosesAfterSayingSo()
    {
        var palette = Create();
        palette.Query = "scan";

        await palette.AcceptAsync();

        Assert.Equal(["scan"], _ran);
        Assert.Equal(PaletteStage.Succeeded, palette.Stage);
        Assert.Equal("Scanning", palette.Message);
        Assert.True(Assert.Single(_closed) > TimeSpan.Zero);
        Assert.Equal(["docker/scan"], _used);
    }

    [Fact]
    public async Task ActionThatOnlyOpensSomething_ClosesAtOnce()
    {
        var palette = Create();
        palette.Query = "pipeline";

        await palette.AcceptAsync();

        Assert.Equal(TimeSpan.Zero, Assert.Single(_closed));
    }

    [Fact]
    public async Task ActionWithTargets_AsksForOneAndSkipsWhatCannotBeChosen()
    {
        var palette = Create();
        palette.Query = "restart";

        await palette.AcceptAsync();

        Assert.Equal(PaletteStage.Targets, palette.Stage);
        Assert.Equal("Restart container ›", palette.Chip);
        Assert.Equal(string.Empty, palette.Query);
        Assert.Equal(["web", "db", "prod-web"], palette.Rows.Select(Text));
        Assert.Equal(["LOCAL", null, "PRODUCTION · READ ONLY"], palette.Rows.Select(row => row.Group));
        Assert.Equal("running · not allowed", palette.Rows[2].Hint);

        // Down from the last row that can be chosen leads around to the first, not onto the one that cannot.
        palette.Move(1);
        palette.Move(1);
        Assert.Equal("web", Text(palette.SelectedRow!));

        palette.Move(-1);
        await palette.AcceptAsync();

        Assert.Equal(["restart db"], _ran);
        Assert.Equal(PaletteStage.Succeeded, palette.Stage);
    }

    [Fact]
    public async Task Targets_AreNarrowedByTyping()
    {
        var palette = Create();
        palette.Query = "restart";
        await palette.AcceptAsync();

        palette.Query = "db";

        Assert.Equal("db", Text(Assert.Single(palette.Rows)));
    }

    [Fact]
    public async Task Confirmation_IsAskedBeforeRunningAndCanBeTakenBack()
    {
        var palette = Create();
        palette.Query = "stop";
        await palette.AcceptAsync();
        await palette.AcceptAsync();

        Assert.Equal(PaletteStage.Confirmation, palette.Stage);
        Assert.Equal("Stop web?", palette.Message);
        Assert.Equal("Stop", palette.PrimaryText);
        Assert.Equal("Stop container › web", palette.Chip);
        Assert.Empty(_ran);
        Assert.Equal(string.Empty, palette.Query);

        palette.Back();
        Assert.Equal(PaletteStage.Targets, palette.Stage);

        await palette.AcceptAsync();
        await palette.AcceptAsync();

        Assert.Equal(["stop web"], _ran);
    }

    [Fact]
    public async Task Failure_StaysAndLeadsToTheWidget()
    {
        var palette = Create();
        palette.Query = "restart";
        await palette.AcceptAsync();
        palette.Query = "web";

        await palette.AcceptAsync();

        Assert.Equal(PaletteStage.Failed, palette.Stage);
        Assert.Equal("web did not stay up", palette.Message);
        Assert.Equal("Exited (1)", palette.Detail);
        Assert.Equal("Open Docker", palette.PrimaryText);
        Assert.Empty(_closed);

        await palette.AcceptAsync();

        Assert.Equal(["docker"], _opened);
        Assert.Equal(TimeSpan.Zero, Assert.Single(_closed));
    }

    [Fact]
    public async Task ActionThatThrows_IsReportedAsAFailure()
    {
        var palette = new ActionPaletteViewModel(
            [new PaletteAction(null, "Meerkat", string.Empty, new WidgetAction { Id = "boom", Title = "Explode", Run = (_, _) => throw new InvalidOperationException("no fuse") })],
            []);

        await palette.AcceptAsync();

        Assert.Equal(PaletteStage.Failed, palette.Stage);
        Assert.Equal("Explode failed", palette.Message);
        Assert.Equal("no fuse", palette.Detail);

        // The shell has no page to look into.
        Assert.Null(palette.PrimaryText);
    }

    [Fact]
    public async Task TextThatMatchesNothing_IsOfferedToTheActionThatTakesText()
    {
        var palette = Create();

        palette.Query = "check staging deploy";

        Assert.Empty(palette.Rows);
        Assert.Equal("No action for “check staging deploy”.", palette.EmptyText);
        Assert.Equal("Save as quick note", palette.TextOffer);

        await palette.AcceptAsync();

        Assert.Equal(["note check staging deploy"], _ran);
        Assert.Equal(["notes/quick-note"], _used);
    }

    [Fact]
    public async Task Escape_GoesBackOneStepAndClosesFromTheFirst()
    {
        var palette = Create();
        palette.Query = "restart";
        await palette.AcceptAsync();

        palette.Back();

        Assert.Equal(PaletteStage.Actions, palette.Stage);
        Assert.Null(palette.Chip);
        Assert.Empty(_closed);

        palette.Back();

        Assert.Equal(TimeSpan.Zero, Assert.Single(_closed));
    }

    private static string Text(PaletteRowViewModel row) => string.Concat(row.Title.Select(segment => segment.Text));

    private ActionPaletteViewModel Create(IReadOnlyList<string>? recent = null)
    {
        IReadOnlyList<WidgetActionTargetGroup> Containers() =>
        [
            new("Local", [new("web", "web", "exited", WidgetSeverity.Error), new("db", "db", "running", WidgetSeverity.Ok)]),
            new("Production · read only", [new("prod-web", "prod-web", "running", WidgetSeverity.Ok, "not allowed")]),
        ];

        Task<WidgetActionResult> Ran(string what, WidgetActionResult result)
        {
            _ran.Add(what);
            return Task.FromResult(result);
        }

        var palette = new ActionPaletteViewModel(
            [
                new PaletteAction("docker", "Docker", string.Empty, new WidgetAction
                {
                    Id = "restart",
                    Title = "Restart container",
                    Targets = Containers,
                    Run = (target, _) => target!.Id == "web"
                        ? Task.FromResult(WidgetActionResult.Failure("web did not stay up", "Exited (1)"))
                        : Ran($"restart {target.Id}", WidgetActionResult.Success("db was restarted")),
                }),
                new PaletteAction("docker", "Docker", string.Empty, new WidgetAction
                {
                    Id = "stop",
                    Title = "Stop container",
                    Targets = Containers,
                    Confirm = target => new WidgetActionConfirmation($"Stop {target!.Title}?", "Others will notice.", "Stop"),
                    Run = (target, _) => Ran($"stop {target!.Id}", WidgetActionResult.Success("stopped")),
                }),
                new PaletteAction("docker", "Docker", string.Empty, new WidgetAction
                {
                    Id = "scan",
                    Title = "Scan images",
                    Run = (_, _) => Ran("scan", WidgetActionResult.Success("Scanning")),
                }),
                new PaletteAction("git", "Git", string.Empty, new WidgetAction
                {
                    Id = "open",
                    Title = "Open pipeline",
                    Run = (_, _) => Ran("open", WidgetActionResult.Done),
                }),
                new PaletteAction("notes", "Notes", string.Empty, new WidgetAction
                {
                    Id = "quick-note",
                    Title = "Quick note",
                    TextTitle = "Save as quick note",
                    Run = (_, _) => Ran("capture", WidgetActionResult.Done),
                    RunWithText = (text, _) => Ran($"note {text}", WidgetActionResult.Success("Saved")),
                }),
            ],
            recent ?? []);
        palette.CloseRequested += (_, delay) => _closed.Add(delay);
        palette.ActionUsed += (_, key) => _used.Add(key);
        palette.WidgetRequested += (_, widgetId) => _opened.Add(widgetId);
        return palette;
    }
}
