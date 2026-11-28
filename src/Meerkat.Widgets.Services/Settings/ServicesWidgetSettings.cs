using Meerkat.Abstractions;

namespace Meerkat.Widgets.Services.Settings;

/// <summary>How the state of a service is read from the answer of its endpoint.</summary>
public enum ServiceKind
{
    /// <summary>A status page that offers the Statuspage API ("/api/v2/summary.json"), as many providers do.</summary>
    StatusPage,

    /// <summary>Any address; an answer with a success status code means the service is fine.</summary>
    HttpStatus,

    /// <summary>A JSON document in which one value tells the state.</summary>
    CustomJson,
}

public sealed class ServiceEndpoint
{
    public const double DefaultIntervalSeconds = 300;

    private const string SummaryPath = "/api/v2/summary.json";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public ServiceKind Kind { get; set; } = ServiceKind.StatusPage;

    /// <summary>For a status page its base address, otherwise the address that is requested.</summary>
    public string Url { get; set; } = string.Empty;

    public double IntervalSeconds { get; set; } = DefaultIntervalSeconds;

    /// <summary>Custom JSON: where the value that tells the state is found, e.g. "status.indicator" or "checks[0].state".</summary>
    public string StatePath { get; set; } = string.Empty;

    /// <summary>Custom JSON: values of the state that mean everything is fine.</summary>
    public List<string> OkValues { get; set; } = [];

    /// <summary>Custom JSON: values that mean a disturbance; every other value counts as an outage.</summary>
    public List<string> WarningValues { get; set; } = [];

    /// <summary>Custom JSON: optional path of a text that describes the state.</summary>
    public string DescriptionPath { get; set; } = string.Empty;

    /// <summary>Set by "mute until resolved"; cleared on its own once the service is fine again.</summary>
    public bool IsMuted { get; set; }

    /// <summary>The address that is actually requested.</summary>
    public Uri GetRequestUri()
    {
        var url = Url.Trim();
        if (Kind == ServiceKind.StatusPage && !url.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            url = url.TrimEnd('/') + SummaryPath;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : throw new ArgumentException("The address must start with https:// or http://.");
    }

    /// <summary>The page a person would open to read about the service's state.</summary>
    public Uri GetPageUri()
    {
        var request = GetRequestUri();
        return Kind == ServiceKind.StatusPage ? new Uri(request.GetLeftPart(UriPartial.Authority)) : request;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("Enter a name.");
        }

        GetRequestUri();
        if (Kind == ServiceKind.CustomJson)
        {
            if (string.IsNullOrWhiteSpace(StatePath))
            {
                throw new ArgumentException("Enter where the state is found in the JSON, e.g. status.indicator.");
            }

            if (OkValues.Count == 0)
            {
                throw new ArgumentException("Enter at least one value that means everything is fine.");
            }
        }
    }

    public ServiceEndpoint Clone() => new()
    {
        Id = Id,
        Name = Name,
        Kind = Kind,
        Url = Url,
        IntervalSeconds = IntervalSeconds,
        StatePath = StatePath,
        OkValues = [.. OkValues],
        WarningValues = [.. WarningValues],
        DescriptionPath = DescriptionPath,
        IsMuted = IsMuted,
    };
}

public sealed class ServicesWidgetSettings
{
    private const double MinimumIntervalSeconds = 30;

    public List<ServiceEndpoint> Endpoints { get; set; } = [];

    // The endpoints belong to someone else; a floor keeps a typo from flooding them with requests.
    public static TimeSpan GetInterval(ServiceEndpoint endpoint) => TimeSpan.FromSeconds(
        double.IsFinite(endpoint.IntervalSeconds) ? Math.Max(MinimumIntervalSeconds, endpoint.IntervalSeconds) : ServiceEndpoint.DefaultIntervalSeconds);
}

public sealed class ServicesSettingsSource
{
    private readonly IWidgetSettingsStore _store;
    private ServicesWidgetSettings? _current;

    public ServicesSettingsSource(IWidgetSettingsStore store)
    {
        _store = store;

        // Settings changed from outside, such as in the settings file, replace what was loaded.
        store.Reloaded += (_, _) =>
        {
            _current = null;
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? Changed;

    public ServicesWidgetSettings Current => _current ??= _store.Load<ServicesWidgetSettings>(ServicesWidget.WidgetId);

    /// <summary>Persists a change made to <see cref="Current"/> in place, without announcing new settings.</summary>
    public void Save() => _store.Save(ServicesWidget.WidgetId, Current);

    public void Update(ServicesWidgetSettings settings)
    {
        _current = settings;
        _store.Save(ServicesWidget.WidgetId, settings);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
