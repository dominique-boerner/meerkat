using System.Text.Json.Serialization;

namespace Meerkat.Widgets.Docker.Settings;

public sealed class DockerWidgetSettings
{
    public const int DefaultMaxLogLines = 5000;

    private const double MinimumIntervalSeconds = 1;
    private const int MinimumLogLines = 100;
    private const int MaximumLogLines = 100_000;

    /// <summary>WSL distribution name; null or empty uses the default distribution.</summary>
    public string? Distribution { get; set; }

    public double ListIntervalSeconds { get; set; } = 5;

    public double StatsIntervalSeconds { get; set; } = 3;

    public int MaxLogLines { get; set; } = DefaultMaxLogLines;

    [JsonIgnore]
    public TimeSpan ListInterval => ToInterval(ListIntervalSeconds);

    [JsonIgnore]
    public TimeSpan StatsInterval => ToInterval(StatsIntervalSeconds);

    [JsonIgnore]
    public int EffectiveMaxLogLines => Math.Clamp(MaxLogLines, MinimumLogLines, MaximumLogLines);

    // A hand-edited settings file must not be able to turn polling into a busy loop.
    private static TimeSpan ToInterval(double seconds) =>
        TimeSpan.FromSeconds(double.IsFinite(seconds) ? Math.Max(MinimumIntervalSeconds, seconds) : MinimumIntervalSeconds);
}
