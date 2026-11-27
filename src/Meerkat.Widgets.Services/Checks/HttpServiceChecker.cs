using System.Diagnostics;
using System.Net.Http;
using Meerkat.Abstractions;
using Meerkat.Widgets.Services.Settings;

namespace Meerkat.Widgets.Services.Checks;

/// <summary>Asks an endpoint with a single GET request and reads the state of the service from the answer.</summary>
public sealed class HttpServiceChecker(HttpClient client) : IServiceChecker
{
    // Summaries of status pages are a few hundred kilobytes at most; anything larger is not one.
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout, MaxResponseContentBufferSize = MaxResponseBytes };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Meerkat");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, */*;q=0.5");
        return client;
    }

    public async Task<ServiceCheck> CheckAsync(ServiceEndpoint endpoint, CancellationToken cancellationToken)
    {
        var uri = endpoint.GetRequestUri();
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            var reason = exception is TaskCanceledException ? "No answer in time" : "Not reachable";

            // For a plain address being reachable is the very thing that is checked. For the others the
            // endpoint only reports on the service, so its absence says nothing about the service.
            return endpoint.Kind == ServiceKind.HttpStatus
                ? new ServiceCheck(new ServiceState(WidgetSeverity.Error, reason), null, stopwatch.Elapsed, exception.Message)
                : throw new ServiceCheckException($"{reason}: {exception.Message}", exception);
        }

        using (response)
        {
            var statusCode = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var elapsed = stopwatch.Elapsed;
            if (endpoint.Kind == ServiceKind.HttpStatus)
            {
                var state = response.IsSuccessStatusCode
                    ? new ServiceState(WidgetSeverity.Ok, "Reachable")
                    : new ServiceState(WidgetSeverity.Error, $"Answers with HTTP {statusCode}");
                return new ServiceCheck(state, statusCode, elapsed, $"HTTP {statusCode} {response.ReasonPhrase}".Trim());
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ServiceCheckException($"The endpoint answered with HTTP {statusCode}.");
            }

            var (parsed, excerpt) = endpoint.Kind == ServiceKind.StatusPage
                ? ServiceResponseParser.ParseStatusPage(body)
                : ServiceResponseParser.ParseCustomJson(body, endpoint);
            return new ServiceCheck(parsed, statusCode, elapsed, excerpt);
        }
    }
}
