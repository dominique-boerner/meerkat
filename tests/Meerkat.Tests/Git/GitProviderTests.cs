using System.Net;
using Meerkat.Widgets.Git.Providers;
using Meerkat.Widgets.Git.Settings;

namespace Meerkat.Tests.Git;

public class GitProviderTests
{
    private const string GitLabApi = "https://gitlab.example.com/api/v4/";
    private const string GitHubApi = "https://api.github.com/";

    private static readonly GitProject GitLabProject = new("7", "shop/shop-core");
    private static readonly GitProject GitHubRepository = new("acme/shop", "acme/shop");

    private readonly FakeHttpHandler _http = new();

    [Fact]
    public async Task GitLab_ListsTheLatestPipelinePerBranchOfTheUser()
    {
        _http.Respond("/api/v4/user", """{"username":"dev"}""")
            .Respond("/api/v4/projects/7/pipelines", """
                [
                  {"id":103,"ref":"main","status":"running","updated_at":"2026-11-27T12:10:00Z","created_at":"2026-11-27T12:06:00Z","web_url":"https://gitlab.example.com/p/103"},
                  {"id":102,"ref":"feature/import","status":"failed","updated_at":"2026-11-27T12:04:00Z","created_at":"2026-11-27T12:00:00Z","web_url":"https://gitlab.example.com/p/102"},
                  {"id":101,"ref":"main","status":"success","updated_at":"2026-11-27T11:00:00Z","created_at":"2026-11-27T10:50:00Z","web_url":"https://gitlab.example.com/p/101"},
                  {"id":100,"ref":"release/4.2","status":"manual","updated_at":"2026-11-27T10:00:00Z","created_at":"2026-11-27T09:50:00Z","web_url":"https://gitlab.example.com/p/100"}
                ]
                """);
        using var provider = new GitLabProvider(_http.CreateClient(GitLabApi), new Uri("https://gitlab.example.com"));

        var runs = await provider.ListPipelinesAsync([GitLabProject], onlyMine: true, CancellationToken.None);

        Assert.Equal(["103", "102", "100"], runs.Select(run => run.Id));
        Assert.Equal([PipelineStatus.Running, PipelineStatus.Failed, PipelineStatus.Waiting], runs.Select(run => run.Status));
        Assert.Equal("#103", runs[0].Number);
        Assert.Equal("main", runs[0].Ref);
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 12, 6, 0, TimeSpan.Zero), runs[0].StartedAt);
        Assert.Contains(_http.Requests, request => request.Contains("/projects/7/pipelines") && request.EndsWith("&username=dev"));
    }

    [Fact]
    public async Task GitLab_SummarizesJobsAsStagesInExecutionOrder()
    {
        _http.Respond("/api/v4/projects/7/pipelines/102/jobs", """
            [
              {"id":504,"stage":"package","name":"zip","status":"skipped","web_url":"https://gitlab.example.com/j/504"},
              {"id":503,"stage":"test","name":"integration","status":"failed","web_url":"https://gitlab.example.com/j/503"},
              {"id":502,"stage":"test","name":"unit","status":"success","web_url":"https://gitlab.example.com/j/502"},
              {"id":501,"stage":"build","name":"compile","status":"success","web_url":"https://gitlab.example.com/j/501"}
            ]
            """);
        using var provider = new GitLabProvider(_http.CreateClient(GitLabApi), new Uri("https://gitlab.example.com"));
        var run = new PipelineRun(GitLabProject, "102", "#102", "feature/import", null, PipelineStatus.Failed, DateTimeOffset.UtcNow, null, string.Empty);

        var stages = await provider.GetStagesAsync(run, CancellationToken.None);

        Assert.Equal(
            [
                new PipelineStage("build", PipelineStatus.Success, null),
                new PipelineStage("test", PipelineStatus.Failed, "https://gitlab.example.com/j/503"),
                new PipelineStage("package", PipelineStatus.Canceled, null),
            ],
            stages);
    }

    [Fact]
    public async Task GitLab_ListsMergeRequestsWaitingForReview()
    {
        _http.Respond("/api/v4/user", """{"username":"dev"}""")
            .Respond("/api/v4/merge_requests", """
                [{"iid":12,"title":"Validate order numbers","web_url":"https://gitlab.example.com/mr/12","updated_at":"2026-11-27T12:00:00Z",
                  "author":{"name":"Robin"},"references":{"full":"shop/shop-core!12"}}]
                """);
        using var provider = new GitLabProvider(_http.CreateClient(GitLabApi), new Uri("https://gitlab.example.com"));

        var request = Assert.Single(await provider.ListReviewRequestsAsync(CancellationToken.None));

        Assert.Equal("shop/shop-core", request.ProjectName);
        Assert.Equal("!12", request.Reference);
        Assert.Equal("Validate order numbers", request.Title);
        Assert.Equal("Robin", request.Author);
        Assert.Contains(_http.Requests, sent => sent.Contains("reviewer_username=dev") && sent.Contains("state=opened"));
    }

    [Fact]
    public async Task GitLab_RetriesAPipelineAndPointsToItsTokenPage()
    {
        _http.Respond("/api/v4/projects/7/pipelines/102/retry", "{}", HttpStatusCode.Created);
        using var provider = new GitLabProvider(_http.CreateClient(GitLabApi), new Uri("https://gitlab.example.com"));
        var run = new PipelineRun(GitLabProject, "102", "#102", "main", null, PipelineStatus.Failed, DateTimeOffset.UtcNow, null, string.Empty);

        await provider.RetryAsync(run, CancellationToken.None);

        Assert.Equal(["POST /api/v4/projects/7/pipelines/102/retry"], _http.Requests);
        Assert.Equal("https://gitlab.example.com/-/user_settings/personal_access_tokens", provider.TokenPageUrl.AbsoluteUri);
    }

    [Fact]
    public async Task GitLab_ReportsARejectedToken()
    {
        _http.Respond("/api/v4/user", """{"message":"401 Unauthorized"}""", HttpStatusCode.Unauthorized);
        using var provider = new GitLabProvider(_http.CreateClient(GitLabApi), new Uri("https://gitlab.example.com"));

        var exception = await Assert.ThrowsAsync<GitProviderException>(() => provider.GetUserNameAsync(CancellationToken.None));

        Assert.Equal(GitFailureKind.Unauthorized, exception.Kind);
        Assert.Equal("GitLab responded with 401.", exception.Message);
    }

    [Fact]
    public async Task GitHub_ListsTheLatestRunPerBranchAndWorkflow()
    {
        _http.Respond("/user", """{"login":"dev"}""")
            .Respond("/repos/acme/shop/actions/runs", """
                {"workflow_runs":[
                  {"id":9003,"run_number":41,"name":"CI","head_branch":"main","status":"in_progress","conclusion":null,"updated_at":"2026-11-27T12:10:00Z","run_started_at":"2026-11-27T12:08:00Z","html_url":"https://github.com/acme/shop/actions/runs/9003"},
                  {"id":9002,"run_number":7,"name":"Release","head_branch":"main","status":"completed","conclusion":"failure","updated_at":"2026-11-27T12:00:00Z","run_started_at":"2026-11-27T11:55:00Z","html_url":"https://github.com/acme/shop/actions/runs/9002"},
                  {"id":9001,"run_number":40,"name":"CI","head_branch":"main","status":"completed","conclusion":"success","updated_at":"2026-11-27T11:00:00Z","run_started_at":"2026-11-27T10:55:00Z","html_url":"https://github.com/acme/shop/actions/runs/9001"}
                ]}
                """);
        using var provider = new GitHubProvider(_http.CreateClient(GitHubApi), new Uri("https://github.com"));

        var runs = await provider.ListPipelinesAsync([GitHubRepository], onlyMine: true, CancellationToken.None);

        Assert.Equal(["9003", "9002"], runs.Select(run => run.Id));
        Assert.Equal([PipelineStatus.Running, PipelineStatus.Failed], runs.Select(run => run.Status));
        Assert.Equal("#41", runs[0].Number);
        Assert.Equal("CI", runs[0].WorkflowName);
        Assert.Contains(_http.Requests, request => request.Contains("/repos/acme/shop/actions/runs") && request.EndsWith("&actor=dev"));
    }

    [Fact]
    public async Task GitHub_TreatsJobsAsStages()
    {
        _http.Respond("/repos/acme/shop/actions/runs/9002/jobs", """
            {"jobs":[
              {"name":"build","status":"completed","conclusion":"success","html_url":"https://github.com/acme/shop/actions/runs/9002/job/1"},
              {"name":"test","status":"completed","conclusion":"failure","html_url":"https://github.com/acme/shop/actions/runs/9002/job/2"},
              {"name":"deploy","status":"completed","conclusion":"skipped","html_url":"https://github.com/acme/shop/actions/runs/9002/job/3"},
              {"name":"notify","status":"queued","conclusion":null,"html_url":"https://github.com/acme/shop/actions/runs/9002/job/4"}
            ]}
            """);
        using var provider = new GitHubProvider(_http.CreateClient(GitHubApi), new Uri("https://github.com"));
        var run = new PipelineRun(GitHubRepository, "9002", "#7", "main", "Release", PipelineStatus.Failed, DateTimeOffset.UtcNow, null, string.Empty);

        var stages = await provider.GetStagesAsync(run, CancellationToken.None);

        Assert.Equal(
            [
                new PipelineStage("build", PipelineStatus.Success, null),
                new PipelineStage("test", PipelineStatus.Failed, "https://github.com/acme/shop/actions/runs/9002/job/2"),
                new PipelineStage("deploy", PipelineStatus.Canceled, null),
                new PipelineStage("notify", PipelineStatus.Waiting, null),
            ],
            stages);
    }

    [Fact]
    public async Task GitHub_FindsPullRequestsThroughSearch()
    {
        _http.Respond("/search/issues", """
            {"items":[{"number":98,"title":"Show deadlines","html_url":"https://github.com/acme/shop/pull/98","updated_at":"2026-11-27T12:00:00Z",
              "user":{"login":"kim"},"repository_url":"https://api.github.com/repos/acme/shop"}]}
            """);
        using var provider = new GitHubProvider(_http.CreateClient(GitHubApi), new Uri("https://github.com"));

        var mine = Assert.Single(await provider.ListMyChangeRequestsAsync(CancellationToken.None));
        await provider.ListReviewRequestsAsync(CancellationToken.None);

        Assert.Equal(new ChangeRequest("acme/shop", "#98", "Show deadlines", "kim", new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero), "https://github.com/acme/shop/pull/98"), mine);
        Assert.Contains(_http.Requests, request => request.Contains("q=is:pr is:open author:@me"));
        Assert.Contains(_http.Requests, request => request.Contains("q=is:pr is:open review-requested:@me"));
    }

    [Fact]
    public async Task GitHub_ReportsWhenTheRateLimitResets()
    {
        var reset = new DateTimeOffset(2026, 11, 27, 13, 42, 0, TimeSpan.Zero);
        _http.Respond(
            "/user",
            """{"message":"API rate limit exceeded"}""",
            HttpStatusCode.Forbidden,
            ("x-ratelimit-remaining", "0"),
            ("x-ratelimit-reset", reset.ToUnixTimeSeconds().ToString()));
        using var provider = new GitHubProvider(_http.CreateClient(GitHubApi), new Uri("https://github.com"));

        var exception = await Assert.ThrowsAsync<GitProviderException>(() => provider.GetUserNameAsync(CancellationToken.None));

        Assert.Equal(GitFailureKind.RateLimited, exception.Kind);
        Assert.Equal(reset, exception.RetryAt);
    }

    [Fact]
    public async Task GitHub_ExplainsAForbiddenRequestThatIsNotARateLimit()
    {
        _http.Respond("/repos/acme/shop/actions/runs/9002/rerun-failed-jobs", "{}", HttpStatusCode.Forbidden);
        using var provider = new GitHubProvider(_http.CreateClient(GitHubApi), new Uri("https://github.com"));
        var run = new PipelineRun(GitHubRepository, "9002", "#7", "main", "Release", PipelineStatus.Failed, DateTimeOffset.UtcNow, null, string.Empty);

        var exception = await Assert.ThrowsAsync<GitProviderException>(() => provider.RetryAsync(run, CancellationToken.None));

        Assert.Equal(GitFailureKind.Other, exception.Kind);
        Assert.Contains("scope", exception.Message);
    }

    [Fact]
    public void Factory_NeedsATokenAndASecureServerAddress()
    {
        var factory = new GitProviderFactory();

        Assert.Null(factory.Create(new GitWidgetSettings(), token: null));
        Assert.Throws<GitProviderException>(() => factory.Create(new GitWidgetSettings { ServerUrl = "http://gitlab.internal" }, "token"));
        Assert.Throws<GitProviderException>(() => factory.Create(new GitWidgetSettings { ServerUrl = "not a url" }, "token"));

        using var gitLab = factory.Create(new GitWidgetSettings(), "token");
        using var gitHub = factory.Create(new GitWidgetSettings { Provider = GitProviderKind.GitHub, ServerUrl = GitWidgetSettings.DefaultGitHubUrl }, "token");
        Assert.IsType<GitLabProvider>(gitLab);
        Assert.IsType<GitHubProvider>(gitHub);
        Assert.Equal("https://github.com/settings/tokens", gitHub.TokenPageUrl.AbsoluteUri);
    }
}
