using System.Net.Http;
using System.Text.Json;

namespace Meerkat.Widgets.Git.Providers;

/// <summary>Talks to the GitLab REST API (v4). The client's base address must point at ".../api/v4/".</summary>
public sealed class GitLabProvider(HttpClient client, Uri serverUrl) : IGitProvider
{
    private const int PipelinesPerProject = 30;
    private const int BranchesPerProject = 3;
    private const int MaxPipelines = 8;

    private readonly GitApiClient _api = new(client, "GitLab");
    private string? _userName;

    public GitProviderKind Kind => GitProviderKind.GitLab;

    public Uri TokenPageUrl => new(serverUrl, "/-/user_settings/personal_access_tokens");

    public async Task<string> GetUserNameAsync(CancellationToken cancellationToken)
    {
        if (_userName is null)
        {
            using var user = await _api.GetAsync("user", cancellationToken).ConfigureAwait(false);
            _userName = user.RootElement.GetText("username");
        }

        return _userName;
    }

    public async Task<IReadOnlyList<GitProject>> ListProjectsAsync(CancellationToken cancellationToken)
    {
        using var projects = await _api
            .GetAsync("projects?membership=true&simple=true&archived=false&order_by=last_activity_at&per_page=100", cancellationToken)
            .ConfigureAwait(false);
        return [.. projects.RootElement.Items()
            .Select(project => new GitProject(project.GetText("id"), project.GetText("path_with_namespace")))];
    }

    public async Task<IReadOnlyList<PipelineRun>> ListPipelinesAsync(
        IReadOnlyList<GitProject> projects,
        bool onlyMine,
        CancellationToken cancellationToken)
    {
        var filter = onlyMine
            ? $"&username={Uri.EscapeDataString(await GetUserNameAsync(cancellationToken).ConfigureAwait(false))}"
            : string.Empty;

        var perProject = await Task.WhenAll(projects.Select(async project =>
        {
            using var pipelines = await _api
                .GetAsync(
                    $"projects/{Uri.EscapeDataString(project.Id)}/pipelines?per_page={PipelinesPerProject}&order_by=updated_at{filter}",
                    cancellationToken)
                .ConfigureAwait(false);

            // The list is newest first, so the first entry per branch is that branch's current state.
            return pipelines.RootElement.Items()
                .Select(pipeline => ToRun(project, pipeline))
                .GroupBy(run => run.Ref)
                .Select(group => group.First())
                .Take(BranchesPerProject)
                .ToList();
        })).ConfigureAwait(false);

        return [.. perProject.SelectMany(runs => runs).OrderByDescending(run => run.UpdatedAt).Take(MaxPipelines)];
    }

    public async Task<IReadOnlyList<PipelineStage>> GetStagesAsync(PipelineRun run, CancellationToken cancellationToken)
    {
        using var jobs = await _api
            .GetAsync($"projects/{Uri.EscapeDataString(run.Project.Id)}/pipelines/{run.Id}/jobs?per_page=100", cancellationToken)
            .ConfigureAwait(false);

        // Jobs are created stage by stage, so ascending ids give the order in which stages run.
        return [.. jobs.RootElement.Items()
            .OrderBy(job => long.TryParse(job.GetText("id"), out var id) ? id : 0)
            .GroupBy(job => job.GetText("stage"))
            .Select(stage => ToStage(stage.Key, [.. stage]))];
    }

    public async Task<IReadOnlyList<ChangeRequest>> ListReviewRequestsAsync(CancellationToken cancellationToken)
    {
        var userName = Uri.EscapeDataString(await GetUserNameAsync(cancellationToken).ConfigureAwait(false));
        return await ListMergeRequestsAsync(
            $"merge_requests?scope=all&state=opened&reviewer_username={userName}&per_page=20",
            cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ChangeRequest>> ListMyChangeRequestsAsync(CancellationToken cancellationToken) =>
        ListMergeRequestsAsync("merge_requests?scope=created_by_me&state=opened&per_page=20", cancellationToken);

    public Task RetryAsync(PipelineRun run, CancellationToken cancellationToken) =>
        _api.PostAsync($"projects/{Uri.EscapeDataString(run.Project.Id)}/pipelines/{run.Id}/retry", cancellationToken);

    public void Dispose() => client.Dispose();

    private async Task<IReadOnlyList<ChangeRequest>> ListMergeRequestsAsync(string url, CancellationToken cancellationToken)
    {
        using var mergeRequests = await _api.GetAsync(url, cancellationToken).ConfigureAwait(false);
        return [.. mergeRequests.RootElement.Items().Select(ToChangeRequest)];
    }

    private static PipelineRun ToRun(GitProject project, JsonElement pipeline)
    {
        var updatedAt = pipeline.GetTime("updated_at") ?? DateTimeOffset.MinValue;
        return new PipelineRun(
            project,
            pipeline.GetText("id"),
            $"#{pipeline.GetText("id")}",
            pipeline.GetText("ref"),
            WorkflowName: null,
            ToStatus(pipeline.GetText("status")),
            updatedAt,
            pipeline.GetTime("started_at") ?? pipeline.GetTime("created_at"),
            pipeline.GetText("web_url"));
    }

    private static PipelineStage ToStage(string name, IReadOnlyList<JsonElement> jobs)
    {
        var statuses = jobs.Select(job => ToStatus(job.GetText("status"))).ToList();
        var failedJob = jobs.FirstOrDefault(job => ToStatus(job.GetText("status")) == PipelineStatus.Failed);

        var status = statuses switch
        {
            _ when statuses.Contains(PipelineStatus.Failed) => PipelineStatus.Failed,
            _ when statuses.Contains(PipelineStatus.Running) => PipelineStatus.Running,
            _ when statuses.Contains(PipelineStatus.Waiting) => PipelineStatus.Waiting,
            _ when statuses.Contains(PipelineStatus.Success) => PipelineStatus.Success,
            _ => PipelineStatus.Canceled,
        };
        return new PipelineStage(name, status, status == PipelineStatus.Failed ? failedJob.GetText("web_url") : null);
    }

    private static ChangeRequest ToChangeRequest(JsonElement mergeRequest)
    {
        // "group/project!12" identifies the merge request across projects.
        var fullReference = mergeRequest.GetObject("references").GetText("full");
        var separator = fullReference.LastIndexOf('!');
        return new ChangeRequest(
            separator > 0 ? fullReference[..separator] : string.Empty,
            $"!{mergeRequest.GetText("iid")}",
            mergeRequest.GetText("title"),
            mergeRequest.GetObject("author").GetText("name"),
            mergeRequest.GetTime("updated_at") ?? DateTimeOffset.MinValue,
            mergeRequest.GetText("web_url"));
    }

    private static PipelineStatus ToStatus(string status) => status switch
    {
        "success" => PipelineStatus.Success,
        "failed" => PipelineStatus.Failed,
        "running" => PipelineStatus.Running,
        "pending" or "waiting_for_resource" or "preparing" or "scheduled" or "manual" => PipelineStatus.Waiting,
        _ => PipelineStatus.Canceled,
    };
}
