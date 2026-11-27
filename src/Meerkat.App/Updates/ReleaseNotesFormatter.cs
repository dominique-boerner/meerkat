namespace Meerkat.App.Updates;

/// <summary>Turns the Markdown of a GitHub release into text that reads well without a Markdown renderer.</summary>
public static class ReleaseNotesFormatter
{
    private const string Bullet = "•  ";

    public static string ToPlainText(string markdown)
    {
        var lines = new List<string>();
        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.Trim();

            // The compare link GitHub appends is noise next to the "full release notes" link.
            if (line.Contains("Full Changelog", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryGetBulletText(line, out var bulletText))
            {
                line = Bullet + bulletText;
            }
            else
            {
                line = StripInlineMarkup(line.TrimStart('#').Trim());
            }

            // Collapse runs of blank lines.
            if (line.Length > 0 || (lines.Count > 0 && lines[^1].Length > 0))
            {
                lines.Add(line);
            }
        }

        return string.Join(Environment.NewLine, lines).Trim();
    }

    public static IReadOnlyList<string> GetHighlights(string markdown, int maximum) =>
        [.. markdown.Split('\n')
            .Select(line => TryGetBulletText(line.Trim(), out var text) ? text : null)
            .OfType<string>()
            .Take(maximum)];

    private static bool TryGetBulletText(string line, out string text)
    {
        if (line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith("- ", StringComparison.Ordinal))
        {
            text = StripInlineMarkup(line[2..].Trim());
            return text.Length > 0;
        }

        text = string.Empty;
        return false;
    }

    private static string StripInlineMarkup(string text) =>
        text.Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);
}
