using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Meerkat.Widgets.Docker.Containers;

public static partial class DockerOutputParser
{
    // .NET parses at most seven fractional digits, Docker emits nine.
    private const int MaxFractionDigits = 7;

    private static readonly (string Suffix, double Factor)[] ByteUnits =
    [
        // Longer suffixes first so "MiB" is not mistaken for "B".
        ("KiB", 1024d),
        ("MiB", 1024d * 1024),
        ("GiB", 1024d * 1024 * 1024),
        ("TiB", 1024d * 1024 * 1024 * 1024),
        ("kB", 1000d),
        ("KB", 1000d),
        ("MB", 1000d * 1000),
        ("GB", 1000d * 1000 * 1000),
        ("TB", 1000d * 1000 * 1000 * 1000),
        ("B", 1d),
    ];

    public static IReadOnlyList<ContainerInfo> ParseContainerList(string output) =>
        [.. ParseLines<PsRow>(output).Select(row => new ContainerInfo(
            row.Id ?? string.Empty,
            row.Names ?? string.Empty,
            row.Image ?? string.Empty,
            row.State ?? string.Empty,
            row.Status ?? string.Empty))];

    public static IReadOnlyList<ContainerStats> ParseStats(string output) =>
        [.. ParseLines<StatsRow>(output).Select(ToStats)];

    public static double? ParsePercent(string? text)
    {
        var trimmed = text?.Trim().TrimEnd('%');
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    public static long? ParseByteSize(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        foreach (var (suffix, factor) in ByteUnits)
        {
            if (trimmed.EndsWith(suffix, StringComparison.Ordinal)
                && double.TryParse(trimmed[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return (long)Math.Round(value * factor);
            }
        }

        return null;
    }

    /// <summary>Splits a line produced by <c>docker logs --timestamps</c> into timestamp and message.</summary>
    public static LogLine ParseLogLine(string rawLine, bool isError, DateTimeOffset receivedAt)
    {
        var match = TimestampedLogLine().Match(rawLine);
        if (!match.Success)
        {
            return new LogLine(receivedAt, rawLine, isError);
        }

        var fraction = match.Groups["fraction"].Value;
        if (fraction.Length > MaxFractionDigits + 1)
        {
            fraction = fraction[..(MaxFractionDigits + 1)];
        }

        var normalized = match.Groups["seconds"].Value + fraction + match.Groups["zone"].Value;
        return DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp)
            ? new LogLine(timestamp, match.Groups["text"].Value, isError)
            : new LogLine(receivedAt, rawLine, isError);
    }

    [GeneratedRegex(@"^(?<seconds>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?<fraction>\.\d+)?(?<zone>Z|[+-]\d{2}:\d{2}) ?(?<text>.*)$")]
    private static partial Regex TimestampedLogLine();

    private static ContainerStats ToStats(StatsRow row)
    {
        var memoryParts = (row.MemUsage ?? string.Empty).Split('/', 2);
        return new ContainerStats(
            row.Id ?? row.Container ?? string.Empty,
            row.Name ?? string.Empty,
            ParsePercent(row.CpuPerc),
            ParseByteSize(memoryParts[0]),
            memoryParts.Length > 1 ? ParseByteSize(memoryParts[1]) : null,
            ParsePercent(row.MemPerc));
    }

    private static IEnumerable<T> ParseLines<T>(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            // Non-JSON lines (e.g. warnings printed by the CLI) are skipped instead of failing the whole poll.
            if (!line.StartsWith('{'))
            {
                continue;
            }

            T? row;
            try
            {
                row = JsonSerializer.Deserialize<T>(line);
            }
            catch (JsonException)
            {
                continue;
            }

            if (row is not null)
            {
                yield return row;
            }
        }
    }

    private sealed class PsRow
    {
        [JsonPropertyName("ID")]
        public string? Id { get; set; }

        public string? Names { get; set; }

        public string? Image { get; set; }

        public string? State { get; set; }

        public string? Status { get; set; }
    }

    private sealed class StatsRow
    {
        [JsonPropertyName("ID")]
        public string? Id { get; set; }

        public string? Container { get; set; }

        public string? Name { get; set; }

        [JsonPropertyName("CPUPerc")]
        public string? CpuPerc { get; set; }

        public string? MemUsage { get; set; }

        public string? MemPerc { get; set; }
    }
}
