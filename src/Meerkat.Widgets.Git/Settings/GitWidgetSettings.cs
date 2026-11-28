using System.Text.Json.Serialization;
using Meerkat.Abstractions;
using Meerkat.Widgets.Git.Providers;

namespace Meerkat.Widgets.Git.Settings;

public sealed class GitWidgetSettings
{
    public const string DefaultGitLabUrl = "https://gitlab.com";
    public const string DefaultGitHubUrl = "https://github.com";

    private const double MinimumIntervalSeconds = 15;

    public GitProviderKind Provider { get; set; } = GitProviderKind.GitLab;

    public string ServerUrl { get; set; } = DefaultGitLabUrl;

    /// <summary>Projects whose pipelines are shown.</summary>
    public List<GitProject> Projects { get; set; } = [];

    /// <summary>Limits pipelines to those triggered by the signed-in user.</summary>
    public bool OnlyMyBranches { get; set; } = true;

    public double IntervalSeconds { get; set; } = 60;

    // Frequent polling quickly exhausts the request quota of the hosting service.
    [JsonIgnore]
    public TimeSpan Interval =>
        TimeSpan.FromSeconds(double.IsFinite(IntervalSeconds) ? Math.Max(MinimumIntervalSeconds, IntervalSeconds) : MinimumIntervalSeconds);

    /// <summary>The token is stored per provider and server, so switching back and forth keeps both.</summary>
    [JsonIgnore]
    public string SecretKey =>
        $"git/{Provider}/{(Uri.TryCreate(ServerUrl, UriKind.Absolute, out var url) ? url.Host : ServerUrl)}".ToLowerInvariant();
}

public sealed class GitSettingsSource
{
    private readonly IWidgetSecretStore _secrets;
    private readonly IWidgetSettingsStore _store;
    private GitWidgetSettings? _current;

    public GitSettingsSource(IWidgetSettingsStore store, IWidgetSecretStore secrets)
    {
        _store = store;
        _secrets = secrets;

        // Settings changed from outside, such as in the settings file, replace what was loaded.
        store.Reloaded += (_, _) =>
        {
            _current = null;
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? Changed;

    public GitWidgetSettings Current => _current ??= _store.Load<GitWidgetSettings>(GitWidget.WidgetId);

    public string? ReadToken(GitWidgetSettings settings) => _secrets.Read(settings.SecretKey);

    public void RemoveToken(GitWidgetSettings settings) => _secrets.Delete(settings.SecretKey);

    /// <param name="newToken">Replaces the stored token; null or empty keeps it.</param>
    public void Update(GitWidgetSettings settings, string? newToken)
    {
        if (!string.IsNullOrWhiteSpace(newToken))
        {
            _secrets.Write(settings.SecretKey, newToken.Trim());
        }

        _current = settings;
        _store.Save(GitWidget.WidgetId, settings);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
