using System.ComponentModel;
using System.Windows;
using Meerkat.Abstractions;
using Meerkat.Widgets.Docker.Settings;
using Meerkat.Widgets.Docker.ViewModels;
using Meerkat.Widgets.Docker.Views;

namespace Meerkat.Widgets.Docker;

public sealed class DockerWidget(
    DockerWidgetViewModel viewModel,
    DockerSettingsPage settingsPage,
    IOverlayPanelHost panelHost) : IOverlayWidget
{
    public const string WidgetId = "docker";

    private const string LogPanelId = WidgetId + ".logs";

    private static readonly Rect LogPanelDefaultBounds = new(60, 420, 820, 360);

    private DockerWidgetView? _view;
    private DockerHeaderView? _headerView;
    private IDisposable? _logPanelHandle;

    public string Id => WidgetId;

    public string Title => "Docker";

    public FrameworkElement View => _view ??= new DockerWidgetView { DataContext = viewModel };

    public FrameworkElement HeaderView => _headerView ??= new DockerHeaderView { DataContext = viewModel };

    public Size DefaultSize => new(470, 330);

    public IWidgetSettingsPage SettingsPage => settingsPage;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await viewModel.StopAsync();
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DockerWidgetViewModel.LogPanel))
        {
            return;
        }

        _logPanelHandle?.Dispose();
        _logPanelHandle = viewModel.LogPanel is { } logPanel
            ? panelHost.Show(new OverlayPanel(
                LogPanelId,
                $"Logs · {logPanel.ContainerName}",
                new LogPanelView { DataContext = logPanel },
                new LogPanelHeaderView { DataContext = logPanel },
                LogPanelDefaultBounds))
            : null;
    }
}
