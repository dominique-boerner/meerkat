using Meerkat.Abstractions;
using Meerkat.Widgets.Docker.Containers;
using Meerkat.Widgets.Docker.Settings;
using Meerkat.Widgets.Docker.ViewModels;

namespace Meerkat.Tests.Docker;

public class DockerWidgetViewModelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly FakeContainerProvider _provider = new();
    private readonly FakeOverlayState _overlayState = new();
    private readonly FakeOverlayCommands _overlayCommands = new();
    private readonly InMemoryWidgetSettingsStore _settingsStore = new();

    [Fact]
    public async Task Polling_IsSuspendedWhileOverlayIsHidden()
    {
        var viewModel = CreateViewModel();
        _provider.Containers.Add(new ContainerInfo("abc", "web", "nginx", "running", "Up"));

        viewModel.Start();
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.Equal(0, _provider.ListCalls);

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
        _provider.Containers.Add(new ContainerInfo("ccc", "queue", "rabbitmq", "restarting", "Restarting"));
        _provider.Stats.Add(new ContainerStats("bbb", "web", 1.5, 1024 * 1024, 2048L * 1024 * 1024, 0.05));
        _overlayState.Set(isVisible: true, OverlayMode.Interactive);

        viewModel.Start();
        await WaitUntilAsync(() => viewModel.Containers.Count == 3 && viewModel.Containers[2].CpuText != "–");
        await viewModel.StopAsync();

        Assert.True(viewModel.IsInteractive);
        Assert.Equal("Ctrl+Alt+O", viewModel.ModeHotkeyText);
        Assert.Equal("1 of 3 running", viewModel.Summary);
        Assert.Equal("List every 5 s · Stats every 3 s", viewModel.IntervalText);
        Assert.Equal(["db", "queue", "web"], viewModel.Containers.Select(row => row.Name));
        Assert.Equal(["stopped", "restarting", "running"], viewModel.Containers.Select(row => row.StatusText));
        Assert.Equal("–", viewModel.Containers[0].MemoryText);
        Assert.Equal(1.5, viewModel.Containers[2].CpuPercent);
        Assert.Equal("1.0 MiB", viewModel.Containers[2].MemoryText.Replace(',', '.'));
    }

    [Fact]
    public async Task ListFailure_IsShownAsFailureCardAndClearedOnRecovery()
    {
        _settingsStore.Save(Widgets.Docker.DockerWidget.WidgetId, new DockerWidgetSettings { Distribution = "Ubuntu-24.04" });
        var viewModel = CreateViewModel();
        _provider.ListFailure = new ContainerProviderException(
            ContainerFailureKind.WslUnavailable, "wsl -d Ubuntu-24.04 docker ps -a", -1, "WSL_E_DISTRO_NOT_FOUND");

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.Failure);
        Assert.Equal("WSL not reachable", viewModel.Failure.Title);
        Assert.Contains("\"Ubuntu-24.04\"", viewModel.Failure.Message);
        Assert.Equal("wsl -d Ubuntu-24.04 docker ps -a\n→ exit code -1", viewModel.Failure.Detail);
        Assert.True(viewModel.Failure.CanChooseDistribution);
        Assert.Null(viewModel.ErrorMessage);

        _provider.ListFailure = null;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Null(viewModel.Failure);
        Assert.True(viewModel.IsEmpty);
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
    public void ShowLogs_OpensPanelForTheSelectedContainerOnly()
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

    private DockerWidgetViewModel CreateViewModel() =>
        new(_provider, _overlayState, _overlayCommands, new DockerSettingsSource(_settingsStore));

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
