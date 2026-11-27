using Meerkat.Abstractions;
using Meerkat.Widgets.Docker.Containers;
using Meerkat.Widgets.Docker.Settings;
using Meerkat.Widgets.Docker.ViewModels;

namespace Meerkat.Tests.Docker;

public class ContainerSourceViewModelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly FakeContainerProvider _provider = new();
    private readonly FakeOverlayState _overlayState = new();
    private readonly FakeOverlayCommands _overlayCommands = new();
    private readonly InMemoryWidgetSettingsStore _settingsStore = new();

    [Fact]
    public async Task Polling_IsSuspendedWhileMeerkatIsHidden()
    {
        var viewModel = CreateViewModel();
        _provider.Containers.Add(new ContainerInfo("abc", "web", "nginx", "running", "Up"));

        viewModel.Start();
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.Equal(0, _provider.ListCalls);
        Assert.Null(viewModel.Status);

        _overlayState.Set(isVisible: true);
        await WaitUntilAsync(() => viewModel.Containers.Count == 1);

        await viewModel.StopAsync();
    }

    [Fact]
    public async Task Polling_ShowsContainersSortedWithStatsAndSummary()
    {
        var viewModel = CreateViewModel();
        _provider.Containers.Add(new ContainerInfo("bbb", "web", "nginx", "running", "Up 1 minute"));
        _provider.Containers.Add(new ContainerInfo("aaa", "db", "postgres", "exited", "Exited (0)"));
        _provider.Stats.Add(new ContainerStats("bbb", "web", 1.5, 1024 * 1024, 2048L * 1024 * 1024, 0.05));
        _overlayState.Set(isVisible: true);

        viewModel.Start();
        await WaitUntilAsync(() => viewModel.Containers.Count == 2 && viewModel.Containers[1].CpuText != "–");
        await viewModel.StopAsync();

        Assert.Equal("1 of 2 running", viewModel.Summary);
        Assert.Equal(["db", "web"], viewModel.Containers.Select(row => row.Name));
        Assert.Equal(["stopped", "running"], viewModel.Containers.Select(row => row.StatusText));
        Assert.Equal("–", viewModel.Containers[0].MemoryText);
        Assert.Equal(1.5, viewModel.Containers[1].CpuPercent);
        Assert.Equal("1.0 MiB", viewModel.Containers[1].MemoryText.Replace(',', '.'));
    }

    [Fact]
    public async Task Status_IsCalmWhileContainersRunOrAreSimplyStopped()
    {
        var viewModel = CreateViewModel();
        _provider.Containers.Add(new ContainerInfo("bbb", "web", "nginx", "running", "Up 1 minute"));
        _provider.Containers.Add(new ContainerInfo("aaa", "db", "postgres", "exited", "Exited (0)"));
        var notifications = 0;
        viewModel.StatusChanged += (_, _) => notifications++;

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(new WidgetStatus("Docker", "1/2", WidgetSeverity.Ok), viewModel.Status);
        Assert.Empty(viewModel.Alerts);
        Assert.Empty(viewModel.RestartingContainers);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task Status_WarnsAboutContainersInARestartLoop()
    {
        var viewModel = CreateViewModel();
        _provider.Containers.Add(new ContainerInfo("bbb", "web", "nginx", "running", "Up 1 minute"));
        _provider.Containers.Add(new ContainerInfo("ccc", "queue", "rabbitmq", "restarting", "Restarting (1) 5 seconds ago"));

        await viewModel.RefreshCommand.ExecuteAsync(null);
        var firstSeen = Assert.Single(viewModel.Alerts).Since;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(new WidgetStatus("Docker", "1/2", WidgetSeverity.Warning), viewModel.Status);
        var alert = Assert.Single(viewModel.Alerts);
        Assert.Equal(WidgetSeverity.Warning, alert.Severity);
        Assert.Equal("queue keeps restarting", alert.Title);
        Assert.Equal("Restarting (1) 5 seconds ago", alert.Detail);
        Assert.Equal(firstSeen, alert.Since);
        Assert.Equal("queue", Assert.Single(viewModel.RestartingContainers).Name);
    }

    [Fact]
    public async Task ListFailure_IsShownAsFailureCardAndReportedAsError()
    {
        _settingsStore.Save(Widgets.Docker.DockerWidget.WidgetId, new DockerWidgetSettings { Distribution = "Ubuntu-24.04" });
        var viewModel = CreateViewModel();
        _provider.Containers.Add(new ContainerInfo("abc", "web", "nginx", "running", "Up"));
        await viewModel.RefreshCommand.ExecuteAsync(null);

        _provider.ListFailure = new ContainerProviderException(
            ContainerFailureKind.WslUnavailable, "wsl -d Ubuntu-24.04 docker ps -a", -1, "WSL_E_DISTRO_NOT_FOUND");
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.Failure);
        Assert.Equal("WSL not reachable", viewModel.Failure.Title);
        Assert.Contains("\"Ubuntu-24.04\"", viewModel.Failure.Message);
        Assert.Equal("wsl -d Ubuntu-24.04 docker ps -a\n→ exit code -1", viewModel.Failure.Detail);
        Assert.Equal("Choose distribution", viewModel.Failure.SettingsAction);
        Assert.Null(viewModel.ErrorMessage);

        // Nothing is known to be running while Docker cannot be asked.
        Assert.Equal(new WidgetStatus("Docker", "0/1", WidgetSeverity.Error), viewModel.Status);
        var alert = Assert.Single(viewModel.Alerts);
        Assert.Equal(WidgetSeverity.Error, alert.Severity);
        Assert.Equal("WSL not reachable", alert.Title);
        Assert.EndsWith("· Ubuntu-24.04", alert.Detail);

        _provider.ListFailure = null;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Null(viewModel.Failure);
        Assert.Equal(WidgetSeverity.Ok, viewModel.Status?.Severity);
        Assert.Empty(viewModel.Alerts);
    }

    [Fact]
    public async Task RestartCommand_DelegatesToProvider()
    {
        var viewModel = CreateViewModel();
        var row = new ContainerRowViewModel(new ContainerInfo("abc", "web", "nginx", "running", "Up"));

        await viewModel.RestartContainerCommand.ExecuteAsync(row);

        Assert.Equal(["restart abc"], _provider.Actions);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public void ShowLogs_OpensTheLogViewerForTheSelectedContainerOnly()
    {
        var viewModel = CreateViewModel();
        var first = new ContainerRowViewModel(new ContainerInfo("abc", "web", "nginx", "running", "Up"));
        var second = new ContainerRowViewModel(new ContainerInfo("def", "db", "postgres", "running", "Up"));
        viewModel.Containers.Add(first);
        viewModel.Containers.Add(second);

        viewModel.ShowLogsCommand.Execute(first);
        viewModel.ShowLogsCommand.Execute(second);

        Assert.False(first.IsLogTarget);
        Assert.True(second.IsLogTarget);
        Assert.Equal("db", viewModel.LogPanel?.ContainerName);
        Assert.Equal("wsl docker logs -f --tail 200 db", viewModel.LogPanel?.CommandText);

        viewModel.LogPanel?.CloseCommand.Execute(null);

        Assert.Null(viewModel.LogPanel);
        Assert.False(second.IsLogTarget);
    }

    [Fact]
    public void OpenSettingsCommand_AsksTheShell()
    {
        CreateViewModel().OpenSettingsCommand.Execute(null);

        Assert.Equal(1, _overlayCommands.OpenSettingsCalls);
    }

    private ContainerSourceViewModel CreateViewModel() =>
        new(_provider, _overlayState, _overlayCommands, ContainerSourceOptions.ForLocal(new DockerSettingsSource(_settingsStore)), TimeProvider.System);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition was not met in time.");
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }
}
