using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Meerkat.App.Updates;

public sealed class GitHubReleaseFeed : IReleaseFeed, IDisposable
{
    private const string ReleasesUrl = "https://api.github.com/repos/" + GitHubReleaseParser.Repository + "/releases?per_page=30";
    private const int BufferSize = 81920;

    private readonly HttpClient _client = CreateClient();

    public async Task<ReleaseInfo?> GetLatestAsync(bool includePrerelease, bool selfContained, CancellationToken cancellationToken)
    {
        var json = await GetTextAsync(ReleasesUrl, cancellationToken).ConfigureAwait(false);
        return GitHubReleaseParser.SelectLatest(json, includePrerelease, selfContained);
    }

    public async Task DownloadAsync(string url, string destinationPath, IProgress<long> progress, CancellationToken cancellationToken)
    {
        using var response = await _client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            await using (destination.ConfigureAwait(false))
            {
                var buffer = new byte[BufferSize];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    total += read;
                    progress.Report(total);
                }
            }
        }
    }

    public async Task<string> GetTextAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _client.Dispose();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();

        // The GitHub API rejects requests without a user agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Meerkat-Updater", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
