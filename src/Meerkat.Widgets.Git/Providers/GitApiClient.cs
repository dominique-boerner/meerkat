using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Meerkat.Widgets.Git.Providers;

/// <summary>Sends API requests and translates transport and HTTP failures into <see cref="GitProviderException"/>.</summary>
internal sealed class GitApiClient(HttpClient client, string providerName)
{
    public async Task<JsonDocument> GetAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, relativeUrl, cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new GitProviderException(GitFailureKind.Other, $"{providerName} sent an unreadable response: {exception.Message}");
        }
    }

    public async Task PostAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, relativeUrl, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativeUrl, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(method, relativeUrl);
            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new GitProviderException(GitFailureKind.Unreachable, exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitProviderException(GitFailureKind.Unreachable, "The request timed out.");
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new GitProviderException(GitFailureKind.Unauthorized, $"{providerName} responded with {status}.");
            }

            if (IsRateLimited(response))
            {
                throw new GitProviderException(
                    GitFailureKind.RateLimited,
                    $"{providerName} responded with {status}.",
                    GetRetryTime(response));
            }

            throw new GitProviderException(
                GitFailureKind.Other,
                response.StatusCode == HttpStatusCode.Forbidden
                    ? $"{providerName} responded with 403. The access token may lack the required scope."
                    : $"{providerName} responded with {status}.");
        }
    }

    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.TooManyRequests
        || (response.StatusCode == HttpStatusCode.Forbidden && GetHeader(response, "x-ratelimit-remaining") == "0");

    private static DateTimeOffset? GetRetryTime(HttpResponseMessage response)
    {
        // GitHub reports the reset time as Unix seconds, GitLab and others as a delay.
        if (long.TryParse(GetHeader(response, "x-ratelimit-reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var reset))
        {
            return DateTimeOffset.FromUnixTimeSeconds(reset);
        }

        return response.Headers.RetryAfter?.Delta is { } delay ? DateTimeOffset.UtcNow + delay : null;
    }

    private static string? GetHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}

internal static class JsonElementExtensions
{
    public static string GetText(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                _ => string.Empty,
            }
            : string.Empty;

    public static DateTimeOffset? GetTime(this JsonElement element, string property) =>
        DateTimeOffset.TryParse(
            element.GetText(property),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var time)
            ? time
            : null;

    public static JsonElement GetObject(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? value : default;

    public static IEnumerable<JsonElement> Items(this JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];
}
