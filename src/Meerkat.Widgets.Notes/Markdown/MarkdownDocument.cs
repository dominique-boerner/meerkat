using System.Text;
using System.Text.RegularExpressions;

namespace Meerkat.Widgets.Notes.Markdown;

public enum BlockKind
{
    Text,
    Heading,
    Code,
}

/// <summary>
/// A piece of a note that is shown and edited as one unit: a heading, a fenced code block, or the lines
/// between two blank lines.
/// </summary>
/// <param name="Text">The Markdown source of the block, lines separated by "\n".</param>
/// <param name="BlankLinesBefore">Kept so that saving a note does not change the spacing the user chose.</param>
public sealed partial record MarkdownBlock(string Text, int BlankLinesBefore)
{
    public BlockKind Kind =>
        MarkdownDocument.IsFence(FirstLine) ? BlockKind.Code
        : HeadingPattern().IsMatch(FirstLine) ? BlockKind.Heading
        : BlockKind.Text;

    /// <summary>1 to 6 for a heading, otherwise 0.</summary>
    public int HeadingLevel => HeadingPattern().Match(FirstLine) is { Success: true } match ? match.Groups[1].Length : 0;

    /// <summary>The heading without its marker; empty for other blocks.</summary>
    public string HeadingText => HeadingPattern().Match(FirstLine) is { Success: true } match ? match.Groups[2].Value : string.Empty;

    /// <summary>The content of a code block without its fences.</summary>
    public string CodeText
    {
        get
        {
            var lines = Text.Split('\n');
            var end = lines.Length > 1 && MarkdownDocument.IsFence(lines[^1]) ? lines.Length - 1 : lines.Length;
            return string.Join('\n', lines[1..end]);
        }
    }

    private string FirstLine => Text.Split('\n', 2)[0];

    [GeneratedRegex(@"^(#{1,6})\s+(.*?)\s*#*\s*$")]
    internal static partial Regex HeadingPattern();
}

public static class MarkdownDocument
{
    public static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    public static bool IsFence(string line) => line.TrimStart().StartsWith("```", StringComparison.Ordinal);

    public static IReadOnlyList<MarkdownBlock> Split(string text)
    {
        var blocks = new List<MarkdownBlock>();
        var current = new List<string>();
        var blankLines = 0;
        var blankLinesBeforeCurrent = 0;
        var isInCode = false;

        void Flush()
        {
            if (current.Count > 0)
            {
                blocks.Add(new MarkdownBlock(string.Join('\n', current), blankLinesBeforeCurrent));
                current.Clear();
            }
        }

        void Begin()
        {
            if (current.Count == 0)
            {
                blankLinesBeforeCurrent = blankLines;
                blankLines = 0;
            }
        }

        foreach (var line in Normalize(text).Split('\n'))
        {
            if (isInCode)
            {
                current.Add(line);
                if (IsFence(line))
                {
                    isInCode = false;
                    Flush();
                }
            }
            else if (IsFence(line))
            {
                Flush();
                Begin();
                current.Add(line);
                isInCode = true;
            }
            else if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                blankLines++;
            }
            else if (MarkdownBlock.HeadingPattern().IsMatch(line))
            {
                // A heading stands on its own even when the text follows without a blank line.
                Flush();
                Begin();
                current.Add(line);
                Flush();
            }
            else
            {
                Begin();
                current.Add(line);
            }
        }

        Flush();
        return blocks;
    }

    public static string Join(IEnumerable<MarkdownBlock> blocks)
    {
        var builder = new StringBuilder();
        foreach (var block in blocks)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append('\n', block.BlankLinesBefore).Append(block.Text);
        }

        if (builder.Length > 0)
        {
            builder.Append('\n');
        }

        return builder.ToString();
    }
}
