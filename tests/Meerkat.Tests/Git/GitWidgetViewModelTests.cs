using Meerkat.Abstractions;
using Meerkat.App.Security;
using Meerkat.Tests.Docker;
using Meerkat.Widgets.Git;
using Meerkat.Widgets.Git.Providers;
using Meerkat.Widgets.Git.Settings;
using Meerkat.Widgets.Git.ViewModels;

namespace Meerkat.Tests.Git;

public class GitWidgetViewModelTests
{
    private static readonly GitProject Core = new("1", "shop/shop-core");

    private readonly FakeGitProviderFactory _factory = new();
    private readonly InMemoryWidgetSettingsStore _settingsStore = new();
    private readonly InMemorySecretStore _secrets = new();
    private readonly FakeOverlayState _overlayState = new();
    private readonly FakeOverlayCommands _overlayCommands = new();

    private FakeGitProvider GitLab => _factory.Providers[GitProviderKind.GitLab];

    [Fact]
    public async Task WithoutToken_AsksToConnectTheProvider()
    {
        var viewModel = CreateViewModel(new GitWidgetSettings(), token: null);
        viewModel.Start();

        await viewModel.RefreshAsync(CancellationToken.None);
        await viewModel.StopAsync();

        Assert.NotNull(viewModel.Failure);
        Assert.Equal("Connect GitLab", viewModel.Failure.Title);
        Assert.True(viewModel.Failure.CanOpenSettings);
        Assert.False(viewModel.Failure.CanRenewToken);
    }

    [Fact]
    public async Task Refresh_DescribesEveryPipelineStateAndCountsTheTabs()
    {
        var now = DateTimeOffset.Now;
        GitLab.Runs.AddRange(
        [
            Run("11", "feature/import", PipelineStatus.Failed, now.AddMinutes(-6)),
            Run("12", "main", PipelineStatus.Running, now.AddMinutes(-4)),
            Run("13", "release/4.2", PipelineStatus.Waiting, now.AddHours(-2)),
            Run("14", "docs", PipelineStatus.Success, now.AddDays(-3)),
        ]);
        GitLab.Stages["11"] = [new("build", PipelineStatus.Success, null), new("test", PipelineStatus.Failed, "https://git.example.com/job/1")];
        GitLab.Stages["12"] = [new("build", PipelineStatus.Success, null), new("test", PipelineStatus.Running, null), new("package", PipelineStatus.Canceled, null)];
        GitLab.Stages["13"] = [new("build", PipelineStatus.Success, null), new("deploy", PipelineStatus.Waiting, null)];
        GitLab.ReviewRequests.Add(new ChangeRequest("shop/shop-core", "!12", "Validate order numbers", "Robin", now.AddMinutes(-35), "https://git.example.com/mr/12"));
        GitLab.ReviewRequests.Add(new ChangeRequest("shop/shop-core", "!13", "Second", "Kim", now, "https://git.example.com/mr/13"));
        var viewModel = CreateViewModel(new GitWidgetSettings { Projects = [Core] }, token: "secret");
        viewModel.Start();

        await viewModel.RefreshAsync(CancellationToken.None);
        await viewModel.StopAsync();

        Assert.Null(viewModel.Failure);
        Assert.Equal(["failed", "running · 1/3", "waiting · deploy", "successful"], viewModel.Pipelines.Select(row => row.StatusText));
        Assert.Equal(["6 min ago", "for 4 min", "2 h ago", "3 d ago"], viewModel.Pipelines.Select(row => row.TimeText));
        Assert.Equal("shop-core", viewModel.Pipelines[0].ProjectName);
        Assert.Equal("Failed in test · Pipeline #11 · 6 min ago", viewModel.Pipelines[0].DetailText);
        Assert.Equal("https://git.example.com/job/1", viewModel.Pipelines[0].JobLogUrl);

        // Green pipelines need no explanation, so their stages are not requested.
        Assert.Equal(["11", "12", "13"], GitLab.StageRequests);

        Assert.Equal(["Pipelines", "Reviews for me", "My MRs"], viewModel.Tabs.Select(tab => tab.Label));
        Assert.Equal([4, 2, 0], viewModel.Tabs.Select(tab => tab.Count));
        Assert.True(viewModel.Tabs[1].NeedsAttention);
        Assert.Equal("2 reviews waiting for you", viewModel.ReviewNotice);
        Assert.Equal("GitLab · 1 project", viewModel.ProviderSummary);
        Assert.Equal("every 60 s", viewModel.IntervalText);
    }

    [Fact]
    public async Task GitHub_UsesItsOwnVocabularyOnTheSameLayout()
    {
        var settings = new GitWidgetSettings { Provider = GitProviderKind.GitHub, ServerUrl = GitWidgetSettings.DefaultGitHubUrl };
        _factory.Providers[GitProviderKind.GitHub].Runs.Add(
            new PipelineRun(new GitProject("acme/shop", "acme/shop"), "9", "#41", "main", "CI", PipelineStatus.Failed, DateTimeOffset.Now, null, "https://github.com/acme/shop/actions/runs/9"));
        var viewModel = CreateViewModel(settings, token: "secret");
        viewModel.Start();

        await viewModel.RefreshAsync(CancellationToken.None);
        await viewModel.StopAsync();

        Assert.Equal(["Workflow runs", "Reviews for me", "My PRs"], viewModel.Tabs.Select(tab => tab.Label));
        Assert.Equal("GitHub · 0 repositories", viewModel.ProviderSummary);
        var row = Assert.Single(viewModel.Pipelines);
        Assert.Equal("main · CI", row.RefText);
        Assert.StartsWith("Failed · Workflow run #41", row.DetailText);
        Assert.Equal("https://github.com/acme/shop/actions/runs/9", row.JobLogUrl);
    }

    [Fact]
    public async Task RejectedToken_KeepsTheLastDataAndOffersToRenewIt()
    {
        GitLab.Runs.Add(Run("11", "main", PipelineStatus.Success, DateTimeOffset.Now));
        var viewModel = CreateViewModel(new GitWidgetSettings { Projects = [Core] }, token: "secret");
        viewModel.Start();
        await viewModel.RefreshAsync(CancellationToken.None);

        GitLab.Failure = new GitProviderException(GitFailureKind.Unauthorized, "GitLab responded with 401.");
        await viewModel.RefreshAsync(CancellationToken.None);
        viewModel.RenewTokenCommand.Execute(null);
        await viewModel.StopAsync();

        Assert.NotNull(viewModel.Failure);
        Assert.Equal("Access token expired or invalid", viewModel.Failure.Title);
        Assert.StartsWith("GitLab responded with 401. The last data from ", viewModel.Failure.Message);
        Assert.True(viewModel.Failure.CanRenewToken);
        Assert.True(viewModel.IsStale);
        Assert.Single(viewModel.Pipelines);
        Assert.Equal([GitLab.TokenPageUrl], _overlayCommands.OpenedUrls);
    }

    [Fact]
    public async Task RateLimit_PausesUntilTheProviderAcceptsRequestsAgain()
    {
        var retryAt = DateTimeOffset.Now.AddMinutes(20);
        GitLab.Failure = new GitProviderException(GitFailureKind.RateLimited, "GitLab responded with 429.", retryAt);
        var viewModel = CreateViewModel(new GitWidgetSettings(), token: "secret");
        viewModel.Start();

        var nextAttempt = await viewModel.RefreshAsync(CancellationToken.None);
        await viewModel.StopAsync();

        Assert.Equal(retryAt, nextAttempt);
        Assert.Equal("GitLab: rate limit reached", viewModel.Failure?.Title);
        Assert.Contains($"Next refresh at {retryAt:HH:mm}", viewModel.Failure?.Message);
        Assert.False(viewModel.Failure?.CanOpenSettings);
    }

    [Fact]
    public async Task Commands_OpenLinksRetryPipelinesAndSwitchTabs()
    {
        GitLab.Runs.Add(Run("11", "main", PipelineStatus.Failed, DateTimeOffset.Now));
        var viewModel = CreateViewModel(new GitWidgetSettings { Projects = [Core] }, token: "secret");
        _overlayState.Set(isVisible: false, OverlayMode.Interactive);
        viewModel.Start();
        await viewModel.RefreshAsync(CancellationToken.None);

        viewModel.OpenUrlCommand.Execute("https://git.example.com/p/11");
        viewModel.OpenUrlCommand.Execute("not a link");
        await viewModel.RetryCommand.ExecuteAsync(viewModel.Pipelines[0]);
        viewModel.SelectTabCommand.Execute(viewModel.Tabs[2]);
        await viewModel.StopAsync();

        Assert.Equal([new Uri("https://git.example.com/p/11")], _overlayCommands.OpenedUrls);
        Assert.Equal(["11"], GitLab.Retried);
        Assert.Equal([false, false, true], viewModel.Tabs.Select(tab => tab.IsSelected));
        Assert.True(viewModel.ShowMyChangeRequests);
        Assert.False(viewModel.ShowPipelines);
        Assert.True(GitLab.IsDisposed);
    }

    [Fact]
    public void DisplayMode_AlwaysShowsPipelines()
    {
        var viewModel = CreateViewModel(new GitWidgetSettings(), token: "secret");

        viewModel.SelectTabCommand.Execute(viewModel.Tabs[1]);

        Assert.True(viewModel.ShowPipelines);
        Assert.False(viewModel.ShowReviewRequests);
    }

    [Fact]
    public async Task SettingsPage_LoadsProjectsAndStoresTheTokenOutsideTheSettings()
    {
        GitLab.Projects.AddRange([Core, new GitProject("2", "shop/shop-portal")]);
        var source = new GitSettingsSource(_settingsStore, _secrets);
        var page = new GitSettingsPageViewModel(source, _factory) { Token = " new-token " };

        await page.TestConnectionCommand.ExecuteAsync(null);
        page.Projects[1].IsSelected = true;
        source.Update(page.ToSettings(), page.Token);

        Assert.False(page.IsConnectionError);
        Assert.StartsWith("Connected as dev", page.ConnectionStatus);
        Assert.Equal(["shop/shop-core", "shop/shop-portal"], page.Projects.Select(option => option.Project.Name));
        Assert.Equal([new GitProject("2", "shop/shop-portal")], source.Current.Projects);
        Assert.Equal("new-token", _secrets.Secrets["git/gitlab/gitlab.com"]);
        Assert.Equal("new-token", source.ReadToken(source.Current));
    }

    [Fact]
    public async Task SettingsPage_SwitchingTheProviderFollowsWithTheDefaultServer()
    {
        var page = new GitSettingsPageViewModel(new GitSettingsSource(_settingsStore, _secrets), _factory);
        page.Projects.Add(new ProjectOptionViewModel(Core, isSelected: true));

        page.IsGitHub = true;
        await page.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal(GitWidgetSettings.DefaultGitHubUrl, page.ServerUrl);
        Assert.Empty(page.Projects);
        Assert.Equal("Repositories", page.ProjectsLabel);
        Assert.True(page.IsConnectionError);
        Assert.Equal("Enter an access token first.", page.ConnectionStatus);
    }

    [Theory]
    [InlineData(40, "40 s")]
    [InlineData(59.9, "59 s")]
    [InlineData(360, "6 min")]
    [InlineData(7200, "2 h")]
    [InlineData(259200, "3 d")]
    [InlineData(-5, "0 s")]
    public void RelativeTime_UsesOneRoundedDownUnit(double seconds, string expected)
    {
        Assert.Equal(expected, RelativeTime.Format(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void CredentialManager_RoundTripsASecret()
    {
        var store = new CredentialManagerSecretStore("Meerkat.Tests:");
        var key = $"roundtrip/{Guid.NewGuid():N}";
        try
        {
            Assert.Null(store.Read(key));

            store.Write(key, "tökén-123");
            Assert.Equal("tökén-123", store.Read(key));

            store.Write(key, "replaced");
            Assert.Equal("replaced", store.Read(key));
        }
        finally
        {
            store.Delete(key);
        }

        Assert.Null(store.Read(key));
        store.Delete(key);
    }

    private GitWidgetViewModel CreateViewModel(GitWidgetSettings settings, string? token)
    {
        _settingsStore.Save(GitWidget.WidgetId, settings);
        if (token is not null)
        {
            _secrets.Write(settings.SecretKey, token);
        }

        return new GitWidgetViewModel(
            _factory,
            new GitSettingsSource(_settingsStore, _secrets),
            _overlayState,
            _overlayCommands,
            TimeProvider.System);
    }

    private static PipelineRun Run(string id, string branch, PipelineStatus status, DateTimeOffset time) =>
        new(Core, id, $"#{id}", branch, WorkflowName: null, status, time.AddSeconds(-1), time.AddSeconds(-1), $"https://git.example.com/p/{id}");
}
