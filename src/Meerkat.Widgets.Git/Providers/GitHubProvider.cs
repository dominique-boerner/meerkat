using System.Net.Http;
using System.Text.Json;

namespace Meerkat.Widgets.Git.Providers;

/// <summary>
/// Talks to the GitHub REST API. The client's base address must point at the API root, i.e.
/// "https://api.github.com/" or ".../api/v3/" on GitHub Enterprise Server.
/// </summary>
public sealed class GitHubProvider(HttpClient client, Uri serverUrl) : IGitProvider
{
    private const int RunsPerRepository = 30;
    private const int RunsShownPerRepository = 3;
    private const int MaxRuns = 8;

    private readonly GitApiClient _api = new(client, "GitHub");
    private string? _userName;

    public GitProviderKind Kind => GitProviderKind.GitHub;

    public Uri TokenPageUrl => new(serverUrl, "/settings/tokens");

    public async Task<string> GetUserNameAsync(CancellationToken cancellationToken)
    {
        if (_userName is null)
        {
            using var user = await _api.GetAsync("user", cancellationToken).ConfigureAwait(false);
            _userName = user.RootElement.GetText("login");
        }

        return _userName;
    }

    public async Task<IReadOnlyList<GitProject>> ListProjectsAsync(CancellationToken cancellationToken)
    {
        using var repositories = await _api.GetAsync("user/repos?sort=pushed&per_page=100", cancellationToken).ConfigureAwait(false);
        return [.. repositories.RootElement.Items()
            .Select(repository => repository.GetText("full_name"))
            .Select(fullName => new GitProject(fullName, fullName))];
    }

    public async Task<IReadOnlyList<PipelineRun>> ListPipelinesAsync(
        IReadOnlyList<GitProject> projects,
        bool onlyMine,
        CancellationToken cancellationToken)
    {
        var filter = onlyMine
            ? $"&actor={Uri.EscapeDataString(await GetUserNameAsync(cancellationToken).ConfigureAwait(false))}"
            : string.Empty;

        var perRepository = await Task.WhenAll(projects.Select(async project =>
        {
            using var runs = await _api
                .GetAsync($"repos/{project.Id}/actions/runs?per_page={RunsPerRepository}{filter}", cancellationToken)
                .ConfigureAwait(false);

            // A branch can have several workflows; each one has its own current state.
            return runs.RootElement.GetObject("workflow_runs").Items()
                .Select(run => ToRun(project, run))
                .GroupBy(run => (run.Ref, run.WorkflowName))
                .Select(group => group.First())
                .Take(RunsShownPerRepository)
                .ToList();
        })).ConfigureAwait(false);

        return [.. perRepository.SelectMany(runs => runs).OrderByDescending(run => run.UpdatedAt).Take(MaxRuns)];
    }

    public async Task<IReadOnlyList<PipelineStage>> GetStagesAsync(PipelineRun run, CancellationToken cancellationToken)
    {
        using var jobs = await _api
            .GetAsync($"repos/{run.Project.Id}/actions/runs/{run.Id}/jobs?per_page=100", cancellationToken)
            .ConfigureAwait(false);

        // GitHub has no stages; its jobs take that role.
        return [.. jobs.RootElement.GetObject("jobs").Items().Select(job =>
        {
            var status = ToStatus(job.GetText("status"), job.GetText("conclusion"));
            return new PipelineStage(job.GetText("name"), status, status == PipelineStatus.Failed ? job.GetText("html_url") : null);
        })];
    }

    public Task<IReadOnlyList<ChangeRequest>> ListReviewRequestsAsync(CancellationToken cancellationToken) =>
        SearchPullRequestsAsync("is:pr is:open review-requested:@me", cancellationToken);

    public Task<IReadOnlyList<ChangeRequest>> ListMyChangeRequestsAsync(CancellationToken cancellationToken) =>
        SearchPullRequestsAsync("is:pr is:open author:@me", cancellationToken);

    public Task RetryAsync(PipelineRun run, CancellationToken cancellationToken) =>
        _api.PostAsync($"repos/{run.Project.Id}/actions/runs/{run.Id}/rerun-failed-jobs", cancellationToken);

    public void Dispose() => client.Dispose();

    private async Task<IReadOnlyList<ChangeRequest>> SearchPullRequestsAsync(string query, CancellationToken cancellationToken)
    {
        using var result = await _api
            .GetAsync($"search/issues?q={Uri.EscapeDataString(query)}&sort=updated&per_page=20", cancellationToken)
            .ConfigureAwait(false);
        return [.. result.RootElement.GetObject("items").Items().Select(ToChangeRequest)];
    }

    private static PipelineRun ToRun(GitProject project, JsonElement run) => new(
        project,
        run.GetText("id"),
        $"#{run.GetText("run_number")}",
        run.GetText("head_branch"),
        run.GetText("name"),
        ToStatus(run.GetText("status"), run.GetText("conclusion")),
        run.GetTime("updated_at") ?? DateTimeOffset.MinValue,
        run.GetTime("run_started_at") ?? run.GetTime("created_at"),
        run.GetText("html_url"));

    private static ChangeRequest ToChangeRequest(JsonElement pullRequest)
    {
        // The search result names the repository only through its API address ".../repos/owner/name".
        const string marker = "/repos/";
        var repositoryUrl = pullRequest.GetText("repository_url");
        var markerIndex = repositoryUrl.LastIndexOf(marker, StringComparison.Ordinal);
        return new ChangeRequest(
            markerIndex >= 0 ? repositoryUrl[(markerIndex + marker.Length)..] : string.Empty,
            $"#{pullRequest.GetText("number")}",
            pullRequest.GetText("title"),
            pullRequest.GetObject("user").GetText("login"),
            pullRequest.GetTime("updated_at") ?? DateTimeOffset.MinValue,
            pullRequest.GetText("html_url"));
    }

    private static PipelineStatus ToStatus(string status, string conclusion) => status switch
    {
        "completed" => conclusion switch
        {
            "success" => PipelineStatus.Success,
            "failure" or "timed_out" or "startup_failure" => PipelineStatus.Failed,
            "action_required" => PipelineStatus.Waiting,
            _ => PipelineStatus.Canceled,
        },
        "in_progress" => PipelineStatus.Running,
        _ => PipelineStatus.Waiting,
    };
}
