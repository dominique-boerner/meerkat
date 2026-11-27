using System.Globalization;
using System.Text.RegularExpressions;
using Meerkat.Widgets.Notes.Markdown;

namespace Meerkat.Widgets.Notes.Scratchpad;

public sealed record ScratchpadTask(string Text, bool IsDone);

/// <summary>
/// The scratchpad is an ordinary Markdown note with two sections Meerkat writes to: "Tasks" and
/// "Quick notes". Every change here touches only the lines it adds or removes inside those sections,
/// so whatever else the user keeps in the note stays exactly as written.
/// </summary>
public static partial class ScratchpadDocument
{
    public const string TasksHeading = "Tasks";
    public const string QuickNotesHeading = "Quick notes";
    public const string DateFormat = "yyyy-MM-dd";

    public static string Empty => $"## {TasksHeading}\n\n## {QuickNotesHeading}\n";

    /// <summary>Appends an open task to the end of the task list.</summary>
    public static string AddTask(string text, string task)
    {
        var lines = ToLines(text);
        var section = EnsureSection(lines, TasksHeading);
        var lastTask = Enumerable.Range(section.Start, section.End - section.Start).LastOrDefault(index => MarkdownLine.IsTask(lines[index]), -1);
        var entry = $"- [ ] {SingleLine(task)}";
        if (lastTask >= 0)
        {
            lines.Insert(lastTask + 1, entry);
        }
        else
        {
            InsertBelowHeading(lines, section.Start - 1, [entry]);
        }

        return ToText(lines);
    }

    /// <summary>
    /// Puts a note with the time of day on top of the quick notes. The notes of each day sit below a line
    /// with its date, which is how earlier days stay recognizable.
    /// </summary>
    public static string AddQuickNote(string text, string note, DateTime now)
    {
        var lines = ToLines(text);
        var section = EnsureSection(lines, QuickNotesHeading);
        var entry = $"- {now.ToString("HH:mm", CultureInfo.InvariantCulture)} {SingleLine(note)}";
        var dateLine = $"### {now.ToString(DateFormat, CultureInfo.InvariantCulture)}";

        var first = Enumerable.Range(section.Start, section.End - section.Start).FirstOrDefault(index => !IsBlank(lines[index]), -1);
        if (first >= 0 && lines[first].TrimEnd() == dateLine)
        {
            InsertBelowHeading(lines, first, [entry]);
        }
        else
        {
            InsertBelowHeading(lines, section.Start - 1, [dateLine, string.Empty, entry]);
        }

        return ToText(lines);
    }

    public static IReadOnlyList<ScratchpadTask> GetTasks(string text)
    {
        var lines = ToLines(text);
        return FindSection(lines, TasksHeading) is { } section
            ? [.. lines[section.Start..section.End].Where(MarkdownLine.IsTask).Select(MarkdownLine.Parse).Select(line => new ScratchpadTask(line.Text, line.IsDone))]
            : [];
    }

    /// <summary>Removes the checked tasks and returns them, so that they can be archived.</summary>
    public static (string Text, IReadOnlyList<string> Removed) RemoveCompletedTasks(string text)
    {
        var lines = ToLines(text);
        var removed = new List<string>();
        if (FindSection(lines, TasksHeading) is { } section)
        {
            for (var index = section.End - 1; index >= section.Start; index--)
            {
                if (MarkdownLine.IsTask(lines[index]) && MarkdownLine.Parse(lines[index]).IsDone)
                {
                    removed.Insert(0, MarkdownLine.Parse(lines[index]).Text);
                    lines.RemoveAt(index);

                    // The blank lines around a list that is gone now would otherwise add up.
                    if (index > 0 && index < lines.Count && IsBlank(lines[index - 1]) && IsBlank(lines[index]))
                    {
                        lines.RemoveAt(index);
                    }
                }
            }
        }

        return removed.Count > 0 ? (ToText(lines), removed) : (text, removed);
    }

    /// <summary>Adds completed tasks to the archive note under the day they were completed, newest day first.</summary>
    public static string AddToArchive(string archive, DateOnly day, IReadOnlyList<string> tasks)
    {
        const string title = "# Archive";
        var lines = ToLines(archive);
        if (lines.Count == 0 || lines[0].TrimEnd() != title)
        {
            lines.InsertRange(0, lines.Count == 0 ? [title] : [title, string.Empty]);
        }

        InsertBelowHeading(lines, 0, [$"## {day.ToString(DateFormat, CultureInfo.InvariantCulture)}", string.Empty, .. tasks.Select(task => $"- [x] {task}")]);
        return ToText(lines);
    }

    /// <summary>Returns the lines of a section without its heading: from <c>Start</c> up to, but excluding, <c>End</c>.</summary>
    private static (int Start, int End)? FindSection(List<string> lines, string heading)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (HeadingPattern().Match(lines[index]) is { Success: true } match
                && string.Equals(match.Groups[2].Value, heading, StringComparison.OrdinalIgnoreCase))
            {
                // A section ends where a heading of the same or a higher level begins.
                var level = match.Groups[1].Length;
                var end = lines.FindIndex(index + 1, line => HeadingPattern().Match(line) is { Success: true } next && next.Groups[1].Length <= level);
                return (index + 1, end < 0 ? lines.Count : end);
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a section and recreates its heading if the user removed it: tasks at the very top, quick notes
    /// right below the tasks.
    /// </summary>
    private static (int Start, int End) EnsureSection(List<string> lines, string heading)
    {
        if (FindSection(lines, heading) is { } existing)
        {
            return existing;
        }

        var position = heading == QuickNotesHeading && FindSection(lines, TasksHeading) is { } tasks ? tasks.End : 0;
        var inserted = new List<string> { $"## {heading}" };
        if (position < lines.Count)
        {
            inserted.Add(string.Empty);
        }

        if (position > 0 && !IsBlank(lines[position - 1]))
        {
            inserted.Insert(0, string.Empty);
        }

        lines.InsertRange(position, inserted);
        return FindSection(lines, heading)!.Value;
    }

    /// <summary>Inserts lines as the first content below a heading, separated by blank lines from what surrounds them.</summary>
    private static void InsertBelowHeading(List<string> lines, int headingIndex, IReadOnlyList<string> content)
    {
        var position = headingIndex + 1;
        if (position < lines.Count && IsBlank(lines[position]))
        {
            position++;
        }
        else
        {
            lines.Insert(position++, string.Empty);
        }

        lines.InsertRange(position, content);

        // List items may follow directly; anything else would otherwise merge with the new lines.
        var next = position + content.Count;
        if (next < lines.Count && !IsBlank(lines[next]) && !ListItemPattern().IsMatch(lines[next]))
        {
            lines.Insert(next, string.Empty);
        }
    }

    private static List<string> ToLines(string text)
    {
        var lines = MarkdownDocument.Normalize(text).Split('\n').ToList();

        // The text ends with a line break, which would otherwise count as an empty last line.
        while (lines.Count > 0 && IsBlank(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static string ToText(List<string> lines) => lines.Count == 0 ? string.Empty : string.Join('\n', lines) + "\n";

    private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);

    private static string SingleLine(string text) => LineBreakPattern().Replace(text.Trim(), " ");

    [GeneratedRegex(@"^(#{1,6})\s+(.*?)\s*#*\s*$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^\s*[-*+] ")]
    private static partial Regex ListItemPattern();

    [GeneratedRegex(@"\s*[\r\n]+\s*")]
    private static partial Regex LineBreakPattern();
}
