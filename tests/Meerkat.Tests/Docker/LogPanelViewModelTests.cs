using Meerkat.Widgets.Docker.Containers;
using Meerkat.Widgets.Docker.ViewModels;

namespace Meerkat.Tests.Docker;

public class LogPanelViewModelTests
{
    private const int MaxLines = 100;

    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Append_KeepsOnlyTheNewestLines()
    {
        var panel = CreatePanel();
        var lines = Enumerable.Range(0, MaxLines + 25).Select(index => Line($"line {index}")).ToList();

        panel.Append(lines[..60]);
        panel.Append(lines[60..]);

        Assert.Equal(MaxLines, panel.Lines.Count);
        Assert.Equal("line 25", panel.Lines[0].Text);
        Assert.Equal($"line {MaxLines + 24}", panel.Lines[^1].Text);
    }

    [Fact]
    public void Filter_NarrowsVisibleLinesByTextAndStream()
    {
        var panel = CreatePanel();
        panel.Append([Line("GET /health"), Line("connection refused", isError: true), Line("GET /users")]);

        panel.FilterText = "get";
        Assert.Equal(["GET /health", "GET /users"], panel.Lines.Select(line => line.Text));

        panel.FilterText = string.Empty;
        panel.ShowStdout = false;
        Assert.Equal(["connection refused"], panel.Lines.Select(line => line.Text));

        panel.Append([Line("ignored stdout"), Line("another error", isError: true)]);
        Assert.Equal(["connection refused", "another error"], panel.Lines.Select(line => line.Text));
    }

    [Fact]
    public void Pause_FreezesTheViewAndCatchesUpOnResume()
    {
        var panel = CreatePanel();
        panel.Append([Line("before")]);

        panel.TogglePauseCommand.Execute(null);
        panel.Append([Line("during")]);
        Assert.Equal(["before"], panel.Lines.Select(line => line.Text));

        panel.TogglePauseCommand.Execute(null);
        Assert.Equal(["before", "during"], panel.Lines.Select(line => line.Text));
    }

    [Fact]
    public void Clear_DropsEverythingReceivedSoFar()
    {
        var panel = CreatePanel();
        panel.Append([Line("old")]);

        panel.ClearCommand.Execute(null);
        panel.Append([Line("new")]);

        Assert.Equal(["new"], panel.Lines.Select(line => line.Text));
        Assert.StartsWith("1 of max. 100", panel.LineCountText);
    }

    [Theory]
    [InlineData("2026-11-27T12:14:02.118123456Z hello world", "hello world", 118)]
    [InlineData("2026-11-27T13:14:02+01:00 with offset", "with offset", 0)]
    [InlineData("2026-11-27T12:14:02.5Z ", "", 500)]
    public void ParseLogLine_SplitsDockerTimestamp(string raw, string expectedText, int expectedMilliseconds)
    {
        var line = DockerOutputParser.ParseLogLine(raw, isError: false, Now);

        Assert.Equal(expectedText, line.Text);
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 12, 14, 2, expectedMilliseconds, TimeSpan.Zero), line.Timestamp.AddTicks(-(line.Timestamp.Ticks % TimeSpan.TicksPerMillisecond)));
    }

    [Fact]
    public void ParseLogLine_FallsBackToReceiveTimeWithoutTimestamp()
    {
        var line = DockerOutputParser.ParseLogLine("Error response from daemon: No such container", isError: true, Now);

        Assert.Equal(new LogLine(Now, "Error response from daemon: No such container", true), line);
    }

    private static LogPanelViewModel CreatePanel() =>
        new(new FakeContainerProvider(), "abc", "web", MaxLines, "wsl docker logs -f --tail 200 web", () => { });

    private static LogLine Line(string text, bool isError = false) => new(Now, text, isError);
}
