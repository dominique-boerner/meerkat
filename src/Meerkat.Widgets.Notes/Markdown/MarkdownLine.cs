using System.Text.RegularExpressions;

namespace Meerkat.Widgets.Notes.Markdown;

public enum LineKind
{
    Plain,
    Bullet,
    Task,

    /// <summary>A list item that starts with a time of day, as quick notes do.</summary>
    Timed,
}

/// <summary>One line of a text block, split into what its marker means and the text that follows.</summary>
public sealed partial record MarkdownLine(LineKind Kind, string Text, bool IsDone = false, string? Time = null)
{
    public static MarkdownLine Parse(string line)
    {
        if (TaskPattern().Match(line) is { Success: true } task)
        {
            return new MarkdownLine(LineKind.Task, task.Groups[3].Value, IsDone: task.Groups[2].Value != " ");
        }

        if (TimedPattern().Match(line) is { Success: true } timed)
        {
            return new MarkdownLine(LineKind.Timed, timed.Groups[2].Value, Time: timed.Groups[1].Value);
        }

        return BulletPattern().Match(line) is { Success: true } bullet
            ? new MarkdownLine(LineKind.Bullet, bullet.Groups[1].Value)
            : new MarkdownLine(LineKind.Plain, line.Trim());
    }

    public static bool IsTask(string line) => TaskPattern().IsMatch(line);

    /// <summary>Checks or unchecks a task line; any other line is returned unchanged.</summary>
    public static string ToggleTask(string line) => TaskPattern().Replace(
        line,
        match => $"{match.Groups[1].Value}[{(match.Groups[2].Value == " " ? "x" : " ")}] {match.Groups[3].Value}");

    [GeneratedRegex(@"^(\s*[-*+] )\[([ xX])\] ?(.*)$")]
    private static partial Regex TaskPattern();

    [GeneratedRegex(@"^\s*[-*+] (\d{1,2}:\d{2}) (.*)$")]
    private static partial Regex TimedPattern();

    [GeneratedRegex(@"^\s*[-*+] (.*)$")]
    private static partial Regex BulletPattern();
}

[Flags]
public enum InlineStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
}

public sealed record InlineRun(string Text, InlineStyle Style);

public static partial class MarkdownInline
{
    /// <summary>Splits a line into runs for inline code, bold and italic text; everything else stays literal.</summary>
    public static IReadOnlyList<InlineRun> Parse(string text)
    {
        var runs = new List<InlineRun>();
        var position = 0;
        foreach (Match match in InlinePattern().Matches(text))
        {
            if (match.Index > position)
            {
                runs.Add(new InlineRun(text[position..match.Index], InlineStyle.None));
            }

            runs.Add(match switch
            {
                _ when match.Groups["code"].Success => new InlineRun(match.Groups["code"].Value, InlineStyle.Code),
                _ when match.Groups["bold"].Success => new InlineRun(match.Groups["bold"].Value, InlineStyle.Bold),
                _ => new InlineRun(match.Groups["italic"].Value, InlineStyle.Italic),
            });
            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            runs.Add(new InlineRun(text[position..], InlineStyle.None));
        }

        return runs;
    }

    // Underscores are not treated as emphasis: they are far more common in identifiers than in prose here.
    [GeneratedRegex(@"`(?<code>[^`]+)`|\*\*(?<bold>\S(?:.*?\S)?)\*\*|\*(?<italic>\S(?:.*?\S)?)\*")]
    private static partial Regex InlinePattern();
}
