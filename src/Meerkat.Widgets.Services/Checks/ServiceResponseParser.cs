using System.Text.Json;
using System.Text.RegularExpressions;
using Meerkat.Abstractions;
using Meerkat.Widgets.Services.Settings;

namespace Meerkat.Widgets.Services.Checks;

/// <summary>Reads the state of a service from the JSON its endpoint returned.</summary>
public static partial class ServiceResponseParser
{
    private const int MaxExcerptLength = 1500;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Parses the summary of a status page that offers the Statuspage API.</summary>
    public static (ServiceState State, string Excerpt) ParseStatusPage(string json)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Object)
        {
            throw new ServiceCheckException("The answer is not the summary of a status page: it has no \"status\".");
        }

        var severity = GetString(status, "indicator") switch
        {
            "none" => WidgetSeverity.Ok,
            "major" or "critical" => WidgetSeverity.Error,

            // "minor", "maintenance" and anything a provider may add: worth knowing, but not an outage.
            _ => WidgetSeverity.Warning,
        };

        var incident = EnumerateArray(root, "incidents").FirstOrDefault();
        var update = incident.ValueKind == JsonValueKind.Object ? EnumerateArray(incident, "incident_updates").FirstOrDefault() : default;
        var components = EnumerateArray(root, "components")
            .Where(component => !(component.TryGetProperty("group", out var group) && group.ValueKind == JsonValueKind.True))
            .Select(component => new ComponentState(GetString(component, "name") ?? string.Empty, ToSeverity(GetString(component, "status"))))
            .Where(component => component.Name.Length > 0)
            .ToList();

        var state = new ServiceState(
            severity,
            GetString(status, "description") ?? severity.ToString(),
            incident.ValueKind == JsonValueKind.Object ? GetString(incident, "name") : null,
            update.ValueKind == JsonValueKind.Object ? Clean(GetString(update, "body")) : null,
            update.ValueKind == JsonValueKind.Object ? GetDate(update, "created_at") : null,
            incident.ValueKind == JsonValueKind.Object ? GetDate(incident, "started_at") ?? GetDate(incident, "created_at") : null,
            components);
        return (state, Shorten("{\n  \"status\": " + JsonSerializer.Serialize(status, Indented).Replace("\n", "\n  ") + "\n}"));
    }

    /// <summary>Reads the state from a JSON document of any shape, as described by the endpoint's settings.</summary>
    public static (ServiceState State, string Excerpt) ParseCustomJson(string json, ServiceEndpoint endpoint)
    {
        using var document = Parse(json);
        var value = ReadPath(document.RootElement, endpoint.StatePath)
            ?? throw new ServiceCheckException($"The answer has no value at \"{endpoint.StatePath}\".");

        var severity = Matches(endpoint.OkValues, value) ? WidgetSeverity.Ok
            : Matches(endpoint.WarningValues, value) ? WidgetSeverity.Warning
            : WidgetSeverity.Error;
        var description = string.IsNullOrWhiteSpace(endpoint.DescriptionPath) ? null : ReadPath(document.RootElement, endpoint.DescriptionPath);
        return (new ServiceState(severity, Clean(description) ?? value), Shorten(JsonSerializer.Serialize(document.RootElement, Indented)));
    }

    /// <summary>
    /// Follows a path such as "status.indicator" or "checks[0].state" and returns the value found there as
    /// text, or null if the path leads nowhere.
    /// </summary>
    public static string? ReadPath(JsonElement root, string path)
    {
        var current = root;
        foreach (Match step in PathStepPattern().Matches(path))
        {
            if (step.Groups["index"].Success)
            {
                var index = int.Parse(step.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
                {
                    return null;
                }

                current = current[index];
            }
            else if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(step.Groups["name"].Value, out current))
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Object or JsonValueKind.Array => null,
            _ => current.GetRawText(),
        };
    }

    private static JsonDocument Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new ServiceCheckException("The answer is not valid JSON.", exception);
        }
    }

    private static WidgetSeverity ToSeverity(string? componentStatus) => componentStatus switch
    {
        "operational" => WidgetSeverity.Ok,
        "major_outage" => WidgetSeverity.Error,
        _ => WidgetSeverity.Warning,
    };

    private static bool Matches(IEnumerable<string> values, string value) =>
        values.Any(candidate => string.Equals(candidate.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<JsonElement> EnumerateArray(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
            : [];

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? GetDate(JsonElement element, string name) =>
        DateTimeOffset.TryParse(GetString(element, name), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    /// <summary>Messages of status pages may contain markup and line breaks; they are shown as one plain line.</summary>
    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return WhitespacePattern().Replace(TagPattern().Replace(text, " "), " ").Trim();
    }

    private static string Shorten(string text) => text.Length <= MaxExcerptLength ? text : text[..MaxExcerptLength] + "\n…";

    [GeneratedRegex(@"(?<name>[^.\[\]]+)|\[(?<index>\d+)\]")]
    private static partial Regex PathStepPattern();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
