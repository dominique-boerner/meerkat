using System.Windows;
using Meerkat.Abstractions;
using Meerkat.Widgets.Docker.Settings;
using Meerkat.Widgets.Docker.ViewModels;
using Meerkat.Widgets.Docker.Views;

namespace Meerkat.Widgets.Docker;

public sealed class DockerWidget(DockerWidgetViewModel viewModel, DockerSettingsPage settingsPage) : IOverlayWidget
{
    public const string WidgetId = "docker";

    private DockerWidgetView? _view;
    private DockerHeaderView? _headerView;

    public event EventHandler? StatusChanged
    {
        add => viewModel.StatusChanged += value;
        remove => viewModel.StatusChanged -= value;
    }

    public string Id => WidgetId;

    public string Title => "Docker";

    public string Icon => "";

    public FrameworkElement View => _view ??= new DockerWidgetView { DataContext = viewModel };

    public FrameworkElement HeaderView => _headerView ??= new DockerHeaderView { DataContext = viewModel };

    public IWidgetSettingsPage SettingsPage => settingsPage;

    public WidgetStatus? Status => viewModel.Status;

    public IReadOnlyList<WidgetAlert> Alerts => viewModel.Alerts;

    public IReadOnlyList<WidgetStatus> AdditionalStatuses => viewModel.AdditionalStatuses;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        viewModel.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => viewModel.StopAsync();
}
