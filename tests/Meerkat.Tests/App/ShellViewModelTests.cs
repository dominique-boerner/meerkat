using Meerkat.Abstractions;
using Meerkat.App.Overlay;

namespace Meerkat.Tests.App;

public class ShellViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StatusBar_ShowsOnlyChipsWhileEverythingIsCalm()
    {
        var statusBar = new StatusBarViewModel();

        statusBar.Update(
            [
                new WidgetReport(new WidgetStatus("Docker", "6/6", WidgetSeverity.Ok), []),
                new WidgetReport(new WidgetStatus("Pipelines", "5/5", WidgetSeverity.Ok), []),
                new WidgetReport(null, []),
            ],
            Now,
            "Ctrl+Alt+M",
            notice: null);

        Assert.Equal(
            [new StatusChipViewModel("Docker", "6/6", WidgetSeverity.Ok), new StatusChipViewModel("Pipelines", "5/5", WidgetSeverity.Ok)],
            statusBar.Chips);
        Assert.Empty(statusBar.Alerts);
        Assert.False(statusBar.HasAlerts);
        Assert.Null(statusBar.MoreHintsCount);
        Assert.Null(statusBar.Notice);
    }

    [Fact]
    public void StatusBar_ShowsErrorsAsCardsAndOnlyCountsWarnings()
    {
        var statusBar = new StatusBarViewModel();

        statusBar.Update(
            [
                new WidgetReport(
                    new WidgetStatus("Docker", "0/6", WidgetSeverity.Error),
                    [new WidgetAlert(WidgetSeverity.Error, "Docker daemon is not running", "Ubuntu-24.04", Now.AddMinutes(-1))]),
                new WidgetReport(
                    new WidgetStatus("Pipelines", "2/5", WidgetSeverity.Error),
                    [
                        new WidgetAlert(WidgetSeverity.Warning, "2 reviews waiting for you", "!12 · !13", Now.AddHours(-3)),
                        new WidgetAlert(WidgetSeverity.Error, "3 pipelines failed", "shop-core (2) · shop-portal (1)", Now.AddMinutes(-4)),
                    ]),
            ],
            Now,
            "Ctrl+Alt+M",
            "Update 1.3.0 ready");

        // Oldest problem first: it is the most likely cause of the ones that followed.
        Assert.Equal(
            [
                new AlertCardViewModel("3 pipelines failed", "shop-core (2) · shop-portal (1)", "4 min ago"),
                new AlertCardViewModel("Docker daemon is not running", "Ubuntu-24.04", "1 min ago"),
            ],
            statusBar.Alerts);
        Assert.Equal("+1", statusBar.MoreHintsCount);
        Assert.Equal("more hint – only in the panel", statusBar.MoreHintsText);
        Assert.True(statusBar.HasAlerts);
        Assert.Equal("Details (Ctrl+Alt+M)", statusBar.DetailsToolTip);
        Assert.Equal("Update 1.3.0 ready", statusBar.Notice);
    }

    [Fact]
    public void StatusBar_ShowsTheCardsWidgetsKeepInView()
    {
        var statusBar = new StatusBarViewModel();
        var card = new WidgetStatusCard("Tasks · 1 of 2 done", [new WidgetStatusCardLine("Review", IsDone: true), new WidgetStatusCardLine("Check mapping")]);

        statusBar.Update([new WidgetReport(null, [], card, "notes"), new WidgetReport(null, [])], Now, "Ctrl+Alt+M", notice: null);
        Assert.Equal([new StatusCardViewModel(card, "notes")], statusBar.Cards);

        // A widget without status and alerts shows nothing but its card.
        Assert.Empty(statusBar.Chips);
        Assert.False(statusBar.HasAlerts);

        statusBar.Update([new WidgetReport(null, [])], Now, "Ctrl+Alt+M", notice: null);
        Assert.Empty(statusBar.Cards);
    }

    [Fact]
    public void StatusBar_AsksForThePageOfTheWidgetBehindAClickedCard()
    {
        var statusBar = new StatusBarViewModel();
        statusBar.Update(
            [
                new WidgetReport(
                    new WidgetStatus("Pipelines", "5/6", WidgetSeverity.Error),
                    [new WidgetAlert(WidgetSeverity.Error, "1 workflow run failed", "main", Now.AddMinutes(-4))],
                    WidgetId: "git"),
            ],
            Now,
            "Ctrl+Alt+M",
            notice: null);
        var requested = new List<string?>();
        var toggles = 0;
        statusBar.WidgetRequested += (_, widgetId) => requested.Add(widgetId);
        statusBar.PanelToggleRequested += (_, _) => toggles++;

        statusBar.OpenWidgetCommand.Execute(statusBar.Alerts[0].WidgetId);
        statusBar.OpenWidgetCommand.Execute(null);
        statusBar.TogglePanelCommand.Execute(null);

        Assert.Equal(["git", null], requested);
        Assert.Equal(1, toggles);
    }

    [Fact]
    public void StatusBar_LimitsTheNumberOfCards()
    {
        var statusBar = new StatusBarViewModel();
        var alerts = Enumerable.Range(1, 5)
            .Select(index => new WidgetAlert(WidgetSeverity.Error, $"Problem {index}", string.Empty, Now.AddMinutes(-index)))
            .ToList();

        statusBar.Update([new WidgetReport(new WidgetStatus("Server", "0/5", WidgetSeverity.Error), alerts)], Now, "Ctrl+Alt+M", null);

        Assert.Equal(StatusBarViewModel.MaxAlertCards, statusBar.Alerts.Count);
        Assert.Equal("+2", statusBar.MoreHintsCount);
        Assert.Equal("more – see the panel", statusBar.MoreHintsText);
    }

    [Theory]
    [InlineData(20, "just now")]
    [InlineData(240, "4 min ago")]
    [InlineData(7200, "2 h ago")]
    [InlineData(259200, "3 d ago")]
    public void StatusBar_FormatsAges(double seconds, string expected)
    {
        Assert.Equal(expected, StatusBarViewModel.FormatAge(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Panel_SelectsTheFirstWidgetAndFollowsTheUser()
    {
        var panel = new PanelViewModel();
        var docker = new WidgetEntryViewModel("docker", "Docker", "D");
        var git = new WidgetEntryViewModel("git", "Git", "G");

        panel.AddWidget(docker);
        panel.AddWidget(git);
        Assert.Same(docker, panel.SelectedWidget);

        panel.SelectCommand.Execute(git);
        Assert.Same(git, panel.SelectedWidget);
        Assert.False(docker.IsSelected);
        Assert.True(git.IsSelected);

        // Disabling the selected widget must not leave the panel empty.
        panel.RemoveWidget(git);
        Assert.Same(docker, panel.SelectedWidget);
        Assert.True(docker.IsSelected);
    }

    [Fact]
    public void Panel_SummarizesHowMuchNeedsAttention()
    {
        var panel = new PanelViewModel();

        panel.SetAttentionCount(0);
        Assert.Equal("All quiet", panel.AttentionText);

        panel.SetAttentionCount(1);
        Assert.Equal("1 needs attention", panel.AttentionText);

        panel.SetAttentionCount(2);
        Assert.Equal("2 need attention", panel.AttentionText);
    }

    [Fact]
    public void Panel_RaisesCloseAndSettingsRequests()
    {
        var panel = new PanelViewModel();
        var closed = 0;
        var settingsRequested = 0;
        panel.CloseRequested += (_, _) => closed++;
        panel.SettingsRequested += (_, _) => settingsRequested++;

        panel.CloseCommand.Execute(null);
        panel.OpenSettingsCommand.Execute(null);

        Assert.Equal(1, closed);
        Assert.Equal(1, settingsRequested);
    }
}
