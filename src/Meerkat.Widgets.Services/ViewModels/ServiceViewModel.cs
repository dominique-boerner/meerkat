using CommunityToolkit.Mvvm.ComponentModel;
using Meerkat.Abstractions;
using Meerkat.Widgets.Services.Checks;
using Meerkat.Widgets.Services.Settings;

namespace Meerkat.Widgets.Services.ViewModels;

/// <summary>One external service in the panel: its last known state and when that was learned.</summary>
public sealed partial class ServiceViewModel(ServiceEndpoint endpoint) : ObservableObject
{
    public const int MaxComponents = 6;

    private DateTimeOffset? _problemSince;
    private DateTimeOffset? _lastChecked;
    private DateTimeOffset? _latestUpdateAt;
    private string? _latestUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProblem))]
    private WidgetSeverity _severity;

    /// <summary>False until the first check succeeded; the state is not known then.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProblem))]
    private bool _isKnown;

    [ObservableProperty]
    private string _statusText = "Not checked yet";

    [ObservableProperty]
    private string? _incident;

    [ObservableProperty]
    private string? _latestUpdateText;

    [ObservableProperty]
    private IReadOnlyList<ComponentState> _components = [];

    [ObservableProperty]
    private string _sinceText = string.Empty;

    [ObservableProperty]
    private string _checkedText = string.Empty;

    [ObservableProperty]
    private bool _isMuted = endpoint.IsMuted;

    /// <summary>Why the last check failed, or null. The state shown is then the one last learned.</summary>
    [ObservableProperty]
    private string? _checkError;

    [ObservableProperty]
    private bool _isBusy;

    public ServiceEndpoint Endpoint { get; } = endpoint;

    public string Name => Endpoint.Name;

    public bool IsProblem => IsKnown && Severity != WidgetSeverity.Ok;

    /// <summary>What the status bar shows about this service, or null if it is fine or muted.</summary>
    public WidgetAlert? Alert
    {
        get
        {
            if (!IsProblem || IsMuted)
            {
                return null;
            }

            var affected = string.Join(", ", Components.Where(component => component.Severity != WidgetSeverity.Ok).Take(2).Select(component => component.Name));
            var detail = (affected, Incident) switch
            {
                ({ Length: > 0 }, { Length: > 0 }) => $"{affected}: {Incident}",
                ({ Length: > 0 }, _) => affected,
                _ => Incident ?? string.Empty,
            };

            // A disturbance of a service one depends on explains failures elsewhere, so it is shown at once.
            return new WidgetAlert(Severity, $"{Name}: {StatusText}", detail, _problemSince ?? DateTimeOffset.Now, Prominent: true);
        }
    }

    /// <summary>Takes over a state that was just learned. Returns true if that ended a muted problem.</summary>
    public bool Apply(ServiceState state, DateTimeOffset now)
    {
        Severity = state.Severity;
        IsKnown = true;
        StatusText = state.StatusText;
        Incident = state.Incident;
        CheckError = null;
        _latestUpdate = state.LatestUpdate;
        _latestUpdateAt = state.LatestUpdateAt;
        _lastChecked = now;

        // Affected components first: they are the reason the list is shown at all.
        var components = (state.Components ?? []).OrderByDescending(component => component.Severity).ToList();
        Components = IsProblem ? [.. components.Take(MaxComponents)] : [];

        // The service knows better than Meerkat when its trouble began; otherwise the first sighting counts.
        _problemSince = IsProblem ? state.Since ?? _problemSince ?? now : null;

        var muteEnded = !IsProblem && IsMuted;
        if (muteEnded)
        {
            Endpoint.IsMuted = false;
            IsMuted = false;
        }

        UpdateTimes(now);
        return muteEnded;
    }

    public void ApplyFailure(string message, DateTimeOffset now)
    {
        CheckError = message;
        UpdateTimes(now);
    }

    public void SetMuted(bool isMuted)
    {
        Endpoint.IsMuted = isMuted;
        IsMuted = isMuted;
    }

    public void UpdateTimes(DateTimeOffset now)
    {
        SinceText = _problemSince is { } since ? $"since {FormatSpan(now - since)}" : string.Empty;
        CheckedText = _lastChecked is { } lastChecked ? FormatAgo(now - lastChecked) : string.Empty;
        LatestUpdateText = (_latestUpdate, _latestUpdateAt) switch
        {
            ({ Length: > 0 }, { } at) => $"Latest update {FormatAgo(now - at)}: {_latestUpdate}",
            ({ Length: > 0 }, _) => $"Latest update: {_latestUpdate}",
            _ => null,
        };
    }

    public static string FormatSpan(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "under 1 min",
        { TotalHours: < 1 } => $"{(int)span.TotalMinutes} min",
        { TotalDays: < 1 } => $"{(int)span.TotalHours} h",
        _ => $"{(int)span.TotalDays} d",
    };

    private static string FormatAgo(TimeSpan span) => span < TimeSpan.FromMinutes(1) ? "just now" : $"{FormatSpan(span)} ago";
}
