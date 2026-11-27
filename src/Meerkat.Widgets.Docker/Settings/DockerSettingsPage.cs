using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Meerkat.Abstractions;
using Meerkat.Widgets.Docker.Containers;
using Meerkat.Widgets.Docker.Views;

namespace Meerkat.Widgets.Docker.Settings;

public sealed class DockerSettingsPage(DockerSettingsSource settingsSource, IWslDistributionCatalog distributionCatalog)
    : IWidgetSettingsPage
{
    private DockerSettingsPageViewModel? _viewModel;

    public FrameworkElement CreateView()
    {
        _viewModel = new DockerSettingsPageViewModel(settingsSource.Current);
        _ = _viewModel.LoadDistributionsAsync(distributionCatalog);
        return new DockerSettingsView { DataContext = _viewModel };
    }

    public void Apply()
    {
        if (_viewModel is not null)
        {
            settingsSource.Update(_viewModel.ToSettings());
        }
    }
}

public sealed record DistributionOption(string? Name, string DisplayName);

public sealed partial class DockerSettingsPageViewModel : ObservableObject
{
    [ObservableProperty]
    private DistributionOption _selectedDistribution;

    [ObservableProperty]
    private double _listIntervalSeconds;

    [ObservableProperty]
    private double _statsIntervalSeconds;

    [ObservableProperty]
    private int _maxLogLines;

    public DockerSettingsPageViewModel(DockerWidgetSettings settings)
    {
        var defaultOption = new DistributionOption(null, "Default distribution");
        Distributions.Add(defaultOption);
        _selectedDistribution = settings.Distribution is { Length: > 0 } configured ? AddOption(configured) : defaultOption;
        _listIntervalSeconds = settings.ListIntervalSeconds;
        _statsIntervalSeconds = settings.StatsIntervalSeconds;
        _maxLogLines = settings.MaxLogLines;
    }

    public ObservableCollection<DistributionOption> Distributions { get; } = [];

    public async Task LoadDistributionsAsync(IWslDistributionCatalog catalog)
    {
        try
        {
            foreach (var name in await catalog.ListAsync(CancellationToken.None))
            {
                AddOption(name);
            }
        }
        catch (Exception)
        {
            // Without WSL the list simply stays limited to the default and the configured distribution.
        }
    }

    public DockerWidgetSettings ToSettings() => new()
    {
        Distribution = SelectedDistribution.Name,
        ListIntervalSeconds = ListIntervalSeconds,
        StatsIntervalSeconds = StatsIntervalSeconds,
        MaxLogLines = MaxLogLines,
    };

    private DistributionOption AddOption(string name)
    {
        var existing = Distributions.FirstOrDefault(option => option.Name == name);
        if (existing is not null)
        {
            return existing;
        }

        var option = new DistributionOption(name, name);
        Distributions.Add(option);
        return option;
    }
}
