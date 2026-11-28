using Meerkat.Abstractions;
using Meerkat.App.Overlay;

namespace Meerkat.Tests.App;

/// <summary>Cards of the status bar can be put away without losing sight of what they stood for.</summary>
public sealed class StatusBarDismissTests
{
    private const string FailedRunKey = "git|1 workflow run failed";

    private static readonly DateTimeOffset Now = new(2026, 11, 28, 14, 0, 0, TimeSpan.Zero);

    private static readonly WidgetAlert FailedRun = new(WidgetSeverity.Error, "1 workflow run failed", "shop · main · OSV-Scan", Now.AddDays(-4));
    private static readonly WidgetAlert Vulnerabilities = new(WidgetSeverity.Error, "8 critical vulnerabilities in 11 images", "perl-base and 4 more", Now.AddHours(-1), Icon: "shield");

    private readonly StatusBarViewModel _statusBar = new();
    private int _changes;

    public StatusBarDismissTests() => _statusBar.HiddenAlertsChanged += (_, _) => _changes++;

    [Fact]
    public void ACardSaysWhatItIsAboutWithItsIcon()
    {
        Update(Git(FailedRun), Docker(Vulnerabilities), new WidgetReport(null, [FailedRun]));

        // The alert's own icon wins over that of its widget; without either a general sign is shown.
        Assert.Equal(["git-icon", "shield", ""], _statusBar.Alerts.OrderBy(card => card.WidgetId is null).ThenByDescending(card => card.WidgetId).Select(card => card.Icon));
    }

    [Fact]
    public void ADismissedCard_LeavesTheBarAndItsChipTurnsIntoAnOutline()
    {
        Update(Git(FailedRun), Docker(Vulnerabilities));
        Assert.All(_statusBar.Chips, chip => Assert.False(chip.IsAcknowledged));

        _statusBar.DismissAlertCommand.Execute(_statusBar.Alerts.Single(card => card.WidgetId == "git"));

        Assert.Equal(["8 critical vulnerabilities in 11 images"], _statusBar.Alerts.Select(card => card.Title));
        Assert.Null(_statusBar.MoreHintsCount);
        Assert.Equal([FailedRunKey], _statusBar.HiddenAlerts.Keys);
        Assert.Null(_statusBar.HiddenAlerts[FailedRunKey]);
        Assert.Equal(1, _changes);

        // Still red, because the problem is still there, but known: only the chips of that widget, and only the troubled ones.
        var git = _statusBar.Chips.Single(chip => chip.Label == "Workflow runs");
        Assert.Equal((WidgetSeverity.Error, true), (git.Severity, git.IsAcknowledged));
        Assert.Equal([false, false], _statusBar.Chips.Where(chip => chip.Label != "Workflow runs").Select(chip => chip.IsAcknowledged));

        // The next refresh, with the same alert and another detail, does not bring the card back.
        Update(Git(FailedRun with { Detail = "shop · main · OSV-Scan · retried" }), Docker(Vulnerabilities));
        Assert.Single(_statusBar.Alerts);
    }

    [Fact]
    public void AChipWithSeveralCards_IsOnlyKnownOnceAllOfThemArePutAway()
    {
        var second = FailedRun with { Title = "Token expires tomorrow" };
        Update(Git(FailedRun, second));

        _statusBar.DismissAlertCommand.Execute(_statusBar.Alerts[0]);
        Assert.False(_statusBar.Chips.Single().IsAcknowledged);

        _statusBar.DismissAlertCommand.Execute(_statusBar.Alerts[0]);
        Assert.True(_statusBar.Chips.Single().IsAcknowledged);
    }

    [Fact]
    public void ADismissedCard_ReturnsWhenItsAlertHasSomethingNewToSay()
    {
        Update(Git(FailedRun));
        _statusBar.DismissAlertCommand.Execute(_statusBar.Alerts.Single());
        Assert.Empty(_statusBar.Alerts);

        Update(Git(FailedRun with { Title = "2 workflow runs failed" }));

        Assert.Equal("2 workflow runs failed", _statusBar.Alerts.Single().Title);
        Assert.False(_statusBar.Chips.Single().IsAcknowledged);

        // What it said before is over and forgotten.
        Assert.Empty(_statusBar.HiddenAlerts);
        Assert.Equal(2, _changes);
    }

    [Fact]
    public void AnAlertThatWasOver_ShowsAgainWhenItComesBack()
    {
        Update(Git(FailedRun));
        _statusBar.DismissAlertCommand.Execute(_statusBar.Alerts.Single());

        Update(Git());
        Assert.Empty(_statusBar.HiddenAlerts);

        Update(Git(FailedRun));
        Assert.Single(_statusBar.Alerts);
    }

    [Fact]
    public void AMutedCard_ReturnsWhenItsTimeIsUp()
    {
        Update(Git(FailedRun));
        _statusBar.MuteAlertForAnHourCommand.Execute(_statusBar.Alerts.Single());
        Assert.Equal(Now.AddHours(1), _statusBar.HiddenAlerts[FailedRunKey]);

        _statusBar.Update([Git(FailedRun)], Now.AddMinutes(59), "Ctrl+Alt+M", null);
        Assert.Empty(_statusBar.Alerts);

        _statusBar.Update([Git(FailedRun)], Now.AddMinutes(60), "Ctrl+Alt+M", null);
        Assert.Single(_statusBar.Alerts);
        Assert.Empty(_statusBar.HiddenAlerts);

        // "Until tomorrow" ends with the day.
        _statusBar.MuteAlertUntilTomorrowCommand.Execute(_statusBar.Alerts.Single());
        Assert.Equal(new DateTimeOffset(2026, 11, 29, 0, 0, 0, TimeSpan.Zero), _statusBar.HiddenAlerts[FailedRunKey]);
    }

    [Fact]
    public void TheMenuOfACard_AlsoOpensItsWidget()
    {
        string? requested = null;
        _statusBar.WidgetRequested += (_, widgetId) => requested = widgetId;
        Update(Docker(Vulnerabilities));

        _statusBar.OpenAlertCommand.Execute(_statusBar.Alerts.Single());

        Assert.Equal("docker", requested);
    }

    [Fact]
    public void WhatWasPutAwayBefore_SurvivesARestartUntilTheWidgetHasReported()
    {
        _statusBar.SetHiddenAlerts(new Dictionary<string, DateTimeOffset?> { [FailedRunKey] = null, ["docker|something old"] = null });

        // Right after the start no widget has anything to say yet; that is no reason to forget.
        Update(new WidgetReport(null, [], WidgetId: "git"), new WidgetReport(null, [], WidgetId: "docker"));
        Assert.Equal(2, _statusBar.HiddenAlerts.Count);
        Assert.Equal(0, _changes);

        Update(Git(FailedRun), Docker(Vulnerabilities));

        Assert.Equal(["8 critical vulnerabilities in 11 images"], _statusBar.Alerts.Select(card => card.Title));
        Assert.Equal([FailedRunKey], _statusBar.HiddenAlerts.Keys);
    }

    private void Update(params WidgetReport[] reports) => _statusBar.Update(reports, Now, "Ctrl+Alt+M", notice: null);

    private static WidgetReport Git(params WidgetAlert[] alerts) =>
        new(new WidgetStatus("Workflow runs", "5/6", alerts.Length > 0 ? WidgetSeverity.Error : WidgetSeverity.Ok), alerts, WidgetId: "git", Icon: "git-icon");

    private static WidgetReport Docker(params WidgetAlert[] alerts) => new(
        new WidgetStatus("Docker", "17/19", WidgetSeverity.Ok),
        alerts,
        WidgetId: "docker",
        AdditionalStatuses: [new WidgetStatus("Images", "6/17", WidgetSeverity.Error)],
        Icon: "docker-icon");
}
