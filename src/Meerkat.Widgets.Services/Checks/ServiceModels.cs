using Meerkat.Abstractions;
using Meerkat.Widgets.Services.Settings;

namespace Meerkat.Widgets.Services.Checks;

public sealed record ComponentState(string Name, WidgetSeverity Severity);

/// <summary>What an endpoint says about its service.</summary>
/// <param name="StatusText">The state in a few words, e.g. "All Systems Operational".</param>
/// <param name="Incident">Title of the current incident, if the service reports one.</param>
/// <param name="LatestUpdate">The most recent message about that incident.</param>
/// <param name="Since">When the incident started according to the service.</param>
public sealed record ServiceState(
    WidgetSeverity Severity,
    string StatusText,
    string? Incident = null,
    string? LatestUpdate = null,
    DateTimeOffset? LatestUpdateAt = null,
    DateTimeOffset? Since = null,
    IReadOnlyList<ComponentState>? Components = null);

/// <summary>A state together with what was received, for showing a test request in the settings.</summary>
/// <param name="StatusCode">HTTP status code, or null if no answer arrived.</param>
/// <param name="Excerpt">The part of the answer the state was read from.</param>
public sealed record ServiceCheck(ServiceState State, int? StatusCode, TimeSpan Elapsed, string Excerpt);

/// <summary>The state could not be determined, which says nothing about the service itself.</summary>
public sealed class ServiceCheckException(string message, Exception? innerException = null) : Exception(message, innerException);

public interface IServiceChecker
{
    Task<ServiceCheck> CheckAsync(ServiceEndpoint endpoint, CancellationToken cancellationToken);
}
