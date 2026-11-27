namespace Meerkat.Widgets.Git.Providers;

/// <summary>
/// Serves made-up data without any network access. Used for screenshots and for working on the widget's
/// layout without an account; enabled through the MEERKAT_GIT_DEMO environment variable.
/// </summary>
public sealed class DemoGitProvider(GitProviderKind kind) : IGitProvider
{
    private static readonly GitProject Core = new("1", "acme/shop-core");
    private static readonly GitProject Portal = new("2", "acme/shop-portal");

    public GitProviderKind Kind => kind;

    public Uri TokenPageUrl => new("https://example.com/tokens");

    public Task<string> GetUserNameAsync(CancellationToken cancellationToken) => Task.FromResult("demo");

    public Task<IReadOnlyList<GitProject>> ListProjectsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GitProject>>([Core, Portal, new GitProject("3", "acme/shop-docs")]);

    public Task<IReadOnlyList<PipelineRun>> ListPipelinesAsync(
        IReadOnlyList<GitProject> projects,
        bool onlyMine,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        return Task.FromResult<IReadOnlyList<PipelineRun>>(
        [
            Run(Core, "48211", "feature/order-import", PipelineStatus.Failed, now.AddMinutes(-6)),
            Run(Core, "48215", "main", PipelineStatus.Running, now.AddMinutes(-4)),
            Run(Portal, "48190", "feature/deadline-view", PipelineStatus.Success, now.AddMinutes(-22)),
            Run(Portal, "48174", "main", PipelineStatus.Success, now.AddHours(-1)),
            Run(Core, "48102", "release/4.2", PipelineStatus.Waiting, now.AddHours(-2)),
        ]);
    }

    public Task<IReadOnlyList<PipelineStage>> GetStagesAsync(PipelineRun run, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PipelineStage>>(run.Status switch
        {
            PipelineStatus.Failed =>
            [
                new("build", PipelineStatus.Success, null),
                new("lint", PipelineStatus.Success, null),
                new("test:unit", PipelineStatus.Success, null),
                new("test:integration", PipelineStatus.Failed, "https://example.com/jobs/1"),
                new("package", PipelineStatus.Canceled, null),
            ],
            PipelineStatus.Running =>
            [
                new("build", PipelineStatus.Success, null),
                new("lint", PipelineStatus.Success, null),
                new("test:unit", PipelineStatus.Success, null),
                new("test:integration", PipelineStatus.Running, null),
                new("package", PipelineStatus.Canceled, null),
            ],
            _ =>
            [
                new("build", PipelineStatus.Success, null),
                new("deploy", PipelineStatus.Waiting, null),
            ],
        });

    public Task<IReadOnlyList<ChangeRequest>> ListReviewRequestsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ChangeRequest>>(
        [
            new("acme/shop-core", Reference(412), "Validate order numbers on import", "Robin", DateTimeOffset.Now.AddMinutes(-35), "https://example.com/1"),
            new("acme/shop-portal", Reference(98), "Show deadlines in the dashboard", "Kim", DateTimeOffset.Now.AddHours(-3), "https://example.com/2"),
        ]);

    public Task<IReadOnlyList<ChangeRequest>> ListMyChangeRequestsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ChangeRequest>>(
        [
            new("acme/shop-core", Reference(415), "Retry failed imports", "demo", DateTimeOffset.Now.AddMinutes(-6), "https://example.com/3"),
            new("acme/shop-core", Reference(409), "Release 4.2", "demo", DateTimeOffset.Now.AddHours(-2), "https://example.com/4"),
            new("acme/shop-portal", Reference(97), "Update dependencies", "demo", DateTimeOffset.Now.AddDays(-1), "https://example.com/5"),
        ]);

    public Task RetryAsync(PipelineRun run, CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
    }

    private static PipelineRun Run(GitProject project, string id, string branch, PipelineStatus status, DateTimeOffset time) =>
        new(project, id, $"#{id}", branch, WorkflowName: null, status, time, time, "https://example.com/pipelines/" + id);

    private string Reference(int number) => kind == GitProviderKind.GitLab ? $"!{number}" : $"#{number}";
}
