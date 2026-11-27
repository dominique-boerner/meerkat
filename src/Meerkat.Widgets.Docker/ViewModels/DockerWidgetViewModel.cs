using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meerkat.Abstractions;
using Meerkat.Widgets.Docker.Containers;
using Meerkat.Widgets.Docker.Settings;

namespace Meerkat.Widgets.Docker.ViewModels;

/// <summary>
/// The Docker page: the local Docker instance in WSL, followed by the instances on other machines the
/// user added. Each source polls on its own; this class adds them up for the shell.
/// </summary>
public sealed partial class DockerWidgetViewModel(
    IContainerProvider localProvider,
    IOverlayState overlayState,
    IOverlayCommands overlayCommands,
    DockerSettingsSource settingsSource,
    TimeProvider timeProvider,
    Func<DockerHost, IContainerProvider>? remoteProviderFactory = null) : ObservableObject
{
    private const string StatusLabel = "Docker";

    private bool _isStarted;

    [ObservableProperty]
    private LogPanelViewModel? _logPanel;

    /// <summary>What the heading says besides the summary: the distribution, or how many sources there are.</summary>
    [ObservableProperty]
    private string _sourceLabel = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>Raised when <see cref="Status"/> or <see cref="Alerts"/> changed.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>The local source first, then the remote ones in the order of the settings.</summary>
    public ObservableCollection<ContainerSourceViewModel> Sources { get; } = [];

    /// <summary>The widget's entry in the status bar; null until a source has answered.</summary>
    public WidgetStatus? Status { get; private set; }

    public IReadOnlyList<WidgetAlert> Alerts { get; private set; } = [];

    public void Start()
    {
        if (_isStarted)
        {
            return;
        }

        _isStarted = true;
        settingsSource.Changed += OnSettingsChanged;
        AddSource(new ContainerSourceViewModel(localProvider, overlayState, overlayCommands, ContainerSourceOptions.ForLocal(settingsSource), timeProvider));
        AddRemoteSources();
    }

    public async Task StopAsync()
    {
        if (!_isStarted)
        {
            return;
        }

        _isStarted = false;
        settingsSource.Changed -= OnSettingsChanged;
        var sources = Sources.ToList();
        Sources.Clear();
        await Task.WhenAll(sources.Select(RemoveSourceAsync));
        LogPanel = null;
    }

    [RelayCommand]
    private Task RefreshAsync() => Task.WhenAll(Sources.Select(source => source.RefreshCommand.ExecuteAsync(null)));

    [RelayCommand]
    private void OpenSettings() => overlayCommands.OpenSettings();

    private void AddRemoteSources()
    {
        if (remoteProviderFactory is null)
        {
            return;
        }

        foreach (var host in settingsSource.Current.RemoteHosts)
        {
            AddSource(new ContainerSourceViewModel(
                remoteProviderFactory(host),
                overlayState,
                overlayCommands,
                ContainerSourceOptions.ForRemote(host, settingsSource),
                timeProvider));
        }
    }

    private void AddSource(ContainerSourceViewModel source)
    {
        source.IsExpanded = !settingsSource.Current.CollapsedSources.Contains(source.Id);
        source.StatusChanged += OnSourceStatusChanged;
        source.PropertyChanged += OnSourcePropertyChanged;
        Sources.Add(source);
        UpdateHeadings();
        source.Start();
    }

    private async Task RemoveSourceAsync(ContainerSourceViewModel source)
    {
        source.StatusChanged -= OnSourceStatusChanged;
        source.PropertyChanged -= OnSourcePropertyChanged;
        await source.StopAsync();
    }

    private async void OnSettingsChanged(object? sender, EventArgs e)
    {
        // Hosts may have been added, removed or changed; the local source reads its settings as it goes.
        var removed = Sources.Where(source => source.IsRemote).ToList();
        foreach (var source in removed)
        {
            Sources.Remove(source);
        }

        // Right away, not after the removed sources have stopped: a lone source needs no heading.
        UpdateHeadings();
        await Task.WhenAll(removed.Select(RemoveSourceAsync));
        if (_isStarted)
        {
            AddRemoteSources();
            UpdateHeadings();
            UpdateStatus();
        }
    }

    private void OnSourceStatusChanged(object? sender, EventArgs e) => UpdateStatus();

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ContainerSourceViewModel changed)
        {
            return;
        }

        if (e.PropertyName == nameof(ContainerSourceViewModel.IsExpanded))
        {
            RememberExpansion(changed);
            return;
        }

        if (e.PropertyName != nameof(ContainerSourceViewModel.LogPanel))
        {
            return;
        }

        // The page has room for one log; opening another one closes the one that was shown.
        if (changed.LogPanel is not null)
        {
            foreach (var other in Sources.Where(source => source != changed && source.LogPanel is not null))
            {
                other.CloseLogsCommand.Execute(null);
            }
        }

        LogPanel = Sources.Select(source => source.LogPanel).FirstOrDefault(panel => panel is not null);
    }

    private void RememberExpansion(ContainerSourceViewModel source)
    {
        var collapsed = settingsSource.Current.CollapsedSources;
        var isListed = collapsed.Contains(source.Id);
        if (isListed == !source.IsExpanded)
        {
            return;
        }

        if (source.IsExpanded)
        {
            collapsed.Remove(source.Id);
        }
        else
        {
            collapsed.Add(source.Id);
        }

        // Saved without announcing new settings, which would rebuild the sources and lose what they show.
        settingsSource.Save();
    }

    private void UpdateHeadings()
    {
        foreach (var source in Sources)
        {
            source.ShowHeading = Sources.Count > 1;
        }
    }

    /// <summary>Adds up what the sources report, so that the shell sees one widget.</summary>
    private void UpdateStatus()
    {
        var reporting = Sources.Where(source => source.Status is not null).ToList();
        var running = reporting.Sum(source => source.RunningCount);
        var total = reporting.Sum(source => source.TotalCount);

        Alerts = [.. Sources.SelectMany(source => source.Alerts)];
        Status = reporting.Count == 0
            ? null
            : new WidgetStatus(
                StatusLabel,

                // With nothing to count, as while the only source cannot be reached, a dash says more than 0/0.
                total > 0 ? $"{running}/{total}" : "–",
                reporting.Max(source => source.Status!.Severity));

        Summary = Sources.Count == 1 ? Sources[0].Summary : reporting.Count == 0 ? string.Empty : $"{running} of {total} running";
        SourceLabel = Sources.Count == 1 ? Sources[0].Label : $"{Sources.Count} sources";
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
