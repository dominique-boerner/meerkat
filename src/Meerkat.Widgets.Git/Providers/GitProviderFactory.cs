using System.Net.Http;
using System.Net.Http.Headers;
using Meerkat.Widgets.Git.Settings;

namespace Meerkat.Widgets.Git.Providers;

public sealed class GitProviderFactory : IGitProviderFactory
{
    public const string DemoEnvironmentVariable = "MEERKAT_GIT_DEMO";

    private const string GitHubHost = "github.com";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    public IGitProvider? Create(GitWidgetSettings settings, string? token)
    {
        if (Environment.GetEnvironmentVariable(DemoEnvironmentVariable) == "1")
        {
            return new DemoGitProvider(settings.Provider);
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        // The token travels in a request header, so plain http would expose it on the network.
        if (!Uri.TryCreate(settings.ServerUrl, UriKind.Absolute, out var serverUrl) || serverUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new GitProviderException(GitFailureKind.Other, "The server URL must start with https://.");
        }

        var client = new HttpClient { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Meerkat", "1.0"));

        if (settings.Provider == GitProviderKind.GitLab)
        {
            client.BaseAddress = new Uri(serverUrl, "/api/v4/");
            client.DefaultRequestHeaders.Add("PRIVATE-TOKEN", token.Trim());
            return new GitLabProvider(client, serverUrl);
        }

        // github.com serves its API from a separate host; GitHub Enterprise Server serves it below /api/v3.
        client.BaseAddress = string.Equals(serverUrl.Host, GitHubHost, StringComparison.OrdinalIgnoreCase)
            ? new Uri("https://api.github.com/")
            : new Uri(serverUrl, "/api/v3/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return new GitHubProvider(client, serverUrl);
    }
}
