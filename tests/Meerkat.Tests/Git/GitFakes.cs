using System.Net;
using System.Net.Http;
using System.Text;
using Meerkat.Abstractions;
using Meerkat.Widgets.Git.Providers;
using Meerkat.Widgets.Git.Settings;

namespace Meerkat.Tests.Git;

/// <summary>Answers requests from canned responses, matched by the start of path and query.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly List<(string Prefix, HttpStatusCode Status, string Body, (string Name, string Value)[] Headers)> _responses = [];

    public List<string> Requests { get; } = [];

    public FakeHttpHandler Respond(string prefix, string body, HttpStatusCode status = HttpStatusCode.OK, params (string Name, string Value)[] headers)
    {
        _responses.Add((prefix, status, body, headers));
        return this;
    }

    public HttpClient CreateClient(string baseAddress) => new(this) { BaseAddress = new Uri(baseAddress) };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var target = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
        Requests.Add($"{request.Method} {target}");

        // The most specific prefix wins, so "/pipelines/5/jobs" is not answered by "/pipelines".
        var match = _responses
            .Where(response => target.StartsWith(response.Prefix, StringComparison.Ordinal))
            .OrderByDescending(response => response.Prefix.Length)
            .FirstOrDefault();
        if (match.Prefix is null)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        }

        var message = new HttpResponseMessage(match.Status) { Content = new StringContent(match.Body, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in match.Headers)
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        return Task.FromResult(message);
    }
}

internal sealed class InMemorySecretStore : IWidgetSecretStore
{
    public Dictionary<string, string> Secrets { get; } = [];

    public string? Read(string key) => Secrets.GetValueOrDefault(key);

    public void Write(string key, string secret) => Secrets[key] = secret;

    public void Delete(string key) => Secrets.Remove(key);
}

internal sealed class FakeGitProvider(GitProviderKind kind) : IGitProvider
{
    public GitProviderKind Kind => kind;

    public Uri TokenPageUrl => new("https://git.example.com/tokens");

    public List<GitProject> Projects { get; } = [];

    public List<PipelineRun> Runs { get; } = [];

    public Dictionary<string, IReadOnlyList<PipelineStage>> Stages { get; } = [];

    public List<ChangeRequest> ReviewRequests { get; } = [];

    public List<ChangeRequest> MyChangeRequests { get; } = [];

    public List<string> Retried { get; } = [];

    public List<string> StageRequests { get; } = [];

    public Exception? Failure { get; set; }

    public bool IsDisposed { get; private set; }

    public Task<string> GetUserNameAsync(CancellationToken cancellationToken) => Result("dev");

    public Task<IReadOnlyList<GitProject>> ListProjectsAsync(CancellationToken cancellationToken) =>
        Result<IReadOnlyList<GitProject>>([.. Projects]);

    public Task<IReadOnlyList<PipelineRun>> ListPipelinesAsync(IReadOnlyList<GitProject> projects, bool onlyMine, CancellationToken cancellationToken) =>
        Result<IReadOnlyList<PipelineRun>>([.. Runs]);

    public Task<IReadOnlyList<PipelineStage>> GetStagesAsync(PipelineRun run, CancellationToken cancellationToken)
    {
        StageRequests.Add(run.Id);
        return Result(Stages.GetValueOrDefault(run.Id, []));
    }

    public Task<IReadOnlyList<ChangeRequest>> ListReviewRequestsAsync(CancellationToken cancellationToken) =>
        Result<IReadOnlyList<ChangeRequest>>([.. ReviewRequests]);

    public Task<IReadOnlyList<ChangeRequest>> ListMyChangeRequestsAsync(CancellationToken cancellationToken) =>
        Result<IReadOnlyList<ChangeRequest>>([.. MyChangeRequests]);

    public Task RetryAsync(PipelineRun run, CancellationToken cancellationToken)
    {
        Retried.Add(run.Id);
        return Task.CompletedTask;
    }

    public void Dispose() => IsDisposed = true;

    private Task<T> Result<T>(T value) => Failure is null ? Task.FromResult(value) : Task.FromException<T>(Failure);
}

/// <summary>Hands out one prepared provider per kind, as long as a token is present.</summary>
internal sealed class FakeGitProviderFactory : IGitProviderFactory
{
    public Dictionary<GitProviderKind, FakeGitProvider> Providers { get; } = new()
    {
        [GitProviderKind.GitLab] = new FakeGitProvider(GitProviderKind.GitLab),
        [GitProviderKind.GitHub] = new FakeGitProvider(GitProviderKind.GitHub),
    };

    public List<string?> Tokens { get; } = [];

    public IGitProvider? Create(GitWidgetSettings settings, string? token)
    {
        Tokens.Add(token);
        return string.IsNullOrWhiteSpace(token) ? null : Providers[settings.Provider];
    }
}
