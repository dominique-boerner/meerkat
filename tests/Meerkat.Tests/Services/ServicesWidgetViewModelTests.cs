using Meerkat.Abstractions;
using Meerkat.Tests.Docker;
using Meerkat.Tests.Notes;
using Meerkat.Widgets.Services.Checks;
using Meerkat.Widgets.Services.Settings;
using Meerkat.Widgets.Services.ViewModels;

namespace Meerkat.Tests.Services;

public class ServicesWidgetViewModelTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 15, 42, 0);

    // The clock of the tests runs in UTC; times reported by services carry their offset explicitly.
    private static readonly DateTimeOffset NowUtc = new(Now, TimeSpan.Zero);

    private readonly FakeServiceChecker _checker = new();
    private readonly InMemoryWidgetSettingsStore _settingsStore = new();
    private readonly FakeOverlayState _overlayState = new();
    private readonly FakeOverlayCommands _overlayCommands = new();
    private readonly ManualTimeProvider _time = new(Now);

    [Fact]
    public async Task WithoutServices_NothingIsReported()
    {
        var viewModel = CreateViewModel();

        viewModel.Start();

        Assert.True(viewModel.IsEmpty);
        Assert.Null(viewModel.Status);
        Assert.Empty(viewModel.Alerts);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task Disturbance_IsReportedAsProminentWarningAndMovesToTheTop()
    {
        var viewModel = await StartAsync("GitHub", "Docker Hub", "npm");
        _checker.States["Docker Hub"] = new ServiceState(
            WidgetSeverity.Warning,
            "Partial System Outage",
            "Increased error rate when pulling images",
            "Cause found, fix is being rolled out.",
            NowUtc.AddMinutes(-6),
            NowUtc.AddMinutes(-18),
            [new ComponentState("Web", WidgetSeverity.Ok), new ComponentState("Registry", WidgetSeverity.Warning)]);

        await CheckAllAsync(viewModel);

        Assert.Equal(new WidgetStatus("Services", "2/3", WidgetSeverity.Warning), viewModel.Status);
        var alert = Assert.Single(viewModel.Alerts);
        Assert.Equal(
            new WidgetAlert(WidgetSeverity.Warning, "Docker Hub: Partial System Outage", "Registry: Increased error rate when pulling images", NowUtc.AddMinutes(-18), Prominent: true),
            alert);

        Assert.Equal(["Docker Hub", "GitHub", "npm"], viewModel.Services.Select(service => service.Name));
        var service = viewModel.Services[0];
        Assert.True(service.IsProblem);
        Assert.Equal("since 18 min", service.SinceText);
        Assert.Equal("Latest update 6 min ago: Cause found, fix is being rolled out.", service.LatestUpdateText);
        Assert.Equal(["Registry", "Web"], service.Components.Select(component => component.Name));
        Assert.Equal("just now", viewModel.Services[1].CheckedText);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task Resolved_ReturnsToTheConfiguredOrder()
    {
        var viewModel = await StartAsync("GitHub", "Docker Hub", "npm");
        _checker.States["npm"] = new ServiceState(WidgetSeverity.Error, "Major Outage");
        await CheckAllAsync(viewModel);
        Assert.Equal("npm", viewModel.Services[0].Name);
        Assert.Equal(WidgetSeverity.Error, viewModel.Status?.Severity);

        _checker.States.Remove("npm");
        await CheckAllAsync(viewModel);

        Assert.Equal(["GitHub", "Docker Hub", "npm"], viewModel.Services.Select(service => service.Name));
        Assert.Equal(new WidgetStatus("Services", "3/3", WidgetSeverity.Ok), viewModel.Status);
        Assert.Empty(viewModel.Alerts);
        Assert.Empty(viewModel.Services[2].SinceText);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task Mute_SilencesTheStatusBarUntilTheServiceIsFineAgain()
    {
        var viewModel = await StartAsync("GitHub", "npm");
        _checker.States["npm"] = new ServiceState(WidgetSeverity.Warning, "Degraded");
        await CheckAllAsync(viewModel);
        var npm = viewModel.Services[0];

        viewModel.ToggleMuteCommand.Execute(npm);

        Assert.True(npm.IsMuted);
        Assert.Empty(viewModel.Alerts);

        // The count stays honest; only the color and the card are held back.
        Assert.Equal(new WidgetStatus("Services", "1/2", WidgetSeverity.Ok), viewModel.Status);
        Assert.True(_settingsStore.Load<ServicesWidgetSettings>("services").Endpoints.Single(endpoint => endpoint.Name == "npm").IsMuted);

        _checker.States.Remove("npm");
        await CheckAllAsync(viewModel);
        Assert.False(npm.IsMuted);
        Assert.False(_settingsStore.Load<ServicesWidgetSettings>("services").Endpoints.Single(endpoint => endpoint.Name == "npm").IsMuted);

        // The next incident is announced again.
        _checker.States["npm"] = new ServiceState(WidgetSeverity.Warning, "Degraded");
        await CheckAllAsync(viewModel);
        Assert.Single(viewModel.Alerts);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task FailedCheck_KeepsTheLastKnownStateAndSaysSo()
    {
        _checker.Failures["GitLab"] = "The endpoint answered with HTTP 404.";

        var viewModel = await StartAsync("GitHub", "GitLab");

        var gitLab = viewModel.Services.Single(service => service.Name == "GitLab");
        Assert.False(gitLab.IsKnown);
        Assert.False(gitLab.IsProblem);
        Assert.Equal("The endpoint answered with HTTP 404.", gitLab.CheckError);

        // Not knowing is not an alarm, but the service does not count as fine either.
        Assert.Equal(new WidgetStatus("Services", "1/2", WidgetSeverity.Ok), viewModel.Status);
        Assert.Empty(viewModel.Alerts);

        _checker.Failures.Clear();
        await CheckAllAsync(viewModel);
        Assert.True(gitLab.IsKnown);
        Assert.Null(gitLab.CheckError);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task ProblemWithoutStartTime_CountsFromTheFirstSighting()
    {
        var viewModel = await StartAsync("API");
        _checker.States["API"] = new ServiceState(WidgetSeverity.Error, "Answers with HTTP 503");
        await CheckAllAsync(viewModel);

        _time.Now = Now.AddMinutes(25);
        await CheckAllAsync(viewModel);

        Assert.Equal("since 25 min", viewModel.Services[0].SinceText);
        Assert.Equal(NowUtc, Assert.Single(viewModel.Alerts).Since);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task Commands_OpenTheStatusPageAndTheSettings()
    {
        var viewModel = await StartAsync("GitHub");

        viewModel.OpenPageCommand.Execute(viewModel.Services[0]);
        viewModel.OpenSettingsCommand.Execute(null);

        Assert.Equal([new Uri("https://github.example.com/")], _overlayCommands.OpenedUrls);
        Assert.Equal(1, _overlayCommands.OpenSettingsCalls);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task SettingsPage_TestsAndAddsAnEndpointWithoutSavingAnythingYet()
    {
        var settings = new ServicesWidgetSettings();
        var page = new ServicesSettingsPageViewModel(settings, _checker);
        Assert.Equal(["GitHub", "npm", "Docker Hub", "Maven Central"], page.Presets.Select(preset => preset.Name));

        page.AddEndpointCommand.Execute(null);
        Assert.True(page.IsStatusPage);
        Assert.False(page.IsHttpStatus || page.IsCustomJson);

        page.SaveCommand.Execute(null);
        Assert.Equal("Enter a name.", page.FormError);

        page.Name = "Docker Hub";
        page.Url = "https://www.dockerstatus.com";
        Assert.Contains("https://www.dockerstatus.com/api/v2/summary.json", page.RequestHint);
        _checker.States["Docker Hub"] = new ServiceState(WidgetSeverity.Warning, "Partial System Outage");
        await page.TestCommand.ExecuteAsync(null);

        Assert.True(page.HasTestResult);
        Assert.Equal("200 · 184 ms", page.TestSummary);
        Assert.Equal("Result: yellow · “Partial System Outage”", page.TestResultText);
        Assert.Equal(WidgetSeverity.Warning, page.TestSeverity);
        Assert.Empty(page.Endpoints);

        page.SaveCommand.Execute(null);
        Assert.False(page.IsFormOpen);
        Assert.Equal(["Docker Hub"], page.ToSettings().Endpoints.Select(endpoint => endpoint.Name));
        Assert.Empty(settings.Endpoints);

        // A service that is on the list is no longer offered as a preset.
        Assert.Equal(["GitHub", "npm", "Maven Central"], page.Presets.Select(preset => preset.Name));
        page.AddPresetCommand.Execute(page.Presets[0]);
        Assert.Equal(["Docker Hub", "GitHub"], page.Endpoints.Select(endpoint => endpoint.Name));
    }

    [Fact]
    public void SettingsPage_EditsCustomJsonEndpointsInPlace()
    {
        var settings = new ServicesWidgetSettings
        {
            Endpoints = [new ServiceEndpoint { Name = "API", Kind = ServiceKind.CustomJson, Url = "https://api.example.com/health", StatePath = "state", OkValues = ["ok", "up"], IntervalSeconds = 900 }],
        };
        var page = new ServicesSettingsPageViewModel(settings, _checker);

        page.EditEndpointCommand.Execute(page.Endpoints[0]);
        Assert.True(page.IsCustomJson);
        Assert.Equal("ok, up", page.OkValues);
        Assert.Equal(900, page.SelectedInterval.Seconds);

        page.OkValues = string.Empty;
        page.SaveCommand.Execute(null);
        Assert.NotNull(page.FormError);

        page.OkValues = "healthy";
        page.WarningValues = "degraded; slow";
        page.SaveCommand.Execute(null);

        var saved = Assert.Single(page.ToSettings().Endpoints);
        Assert.Equal(settings.Endpoints[0].Id, saved.Id);
        Assert.Equal(["healthy"], saved.OkValues);
        Assert.Equal(["degraded", "slow"], saved.WarningValues);
    }

    private async Task<ServicesWidgetViewModel> StartAsync(params string[] names)
    {
        _settingsStore.Save("services", new ServicesWidgetSettings
        {
            Endpoints = [.. names.Select(name => new ServiceEndpoint { Name = name, Url = $"https://{name.Replace(" ", string.Empty).ToLowerInvariant()}.example.com" })],
        });
        var viewModel = CreateViewModel();
        viewModel.Start();
        await CheckAllAsync(viewModel);
        return viewModel;
    }

    private static async Task CheckAllAsync(ServicesWidgetViewModel viewModel)
    {
        foreach (var service in viewModel.Services.ToList())
        {
            await viewModel.CheckAsync(service, CancellationToken.None);
        }
    }

    // The overlay stays hidden, so the polling loops wait and every check in a test is one the test asked for.
    private ServicesWidgetViewModel CreateViewModel() =>
        new(_checker, new ServicesSettingsSource(_settingsStore), _overlayState, _overlayCommands, _time);

    private sealed class FakeServiceChecker : IServiceChecker
    {
        public Dictionary<string, ServiceState> States { get; } = [];

        public Dictionary<string, string> Failures { get; } = [];

        public Task<ServiceCheck> CheckAsync(ServiceEndpoint endpoint, CancellationToken cancellationToken)
        {
            if (Failures.TryGetValue(endpoint.Name, out var failure))
            {
                return Task.FromException<ServiceCheck>(new ServiceCheckException(failure));
            }

            var state = States.GetValueOrDefault(endpoint.Name) ?? new ServiceState(WidgetSeverity.Ok, "All Systems Operational");
            return Task.FromResult(new ServiceCheck(state, 200, TimeSpan.FromMilliseconds(184), "{ }"));
        }
    }
}
