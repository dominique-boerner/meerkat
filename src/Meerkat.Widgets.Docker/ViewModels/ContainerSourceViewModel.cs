using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meerkat.Abstractions;
using Meerkat.Widgets.Docker.Containers;
using Meerkat.Widgets.Docker.Settings;

namespace Meerkat.Widgets.Docker.ViewModels;

/// <summary>How a source of containers is named, how often it is asked and what may be done with it.</summary>
public sealed class ContainerSourceOptions
{
    public const string LocalId = "local";

    private const string DefaultDistributionLabel = "Default distribution";

    /// <summary>Identifies the source in the settings, where the user's choices about it are kept.</summary>
    public required string Id { get; init; }

    /// <summary>The WSL distribution or the host; read anew for every poll, because settings may change.</summary>
    public required Func<string> Label { get; init; }

    public required Func<TimeSpan> ListInterval { get; init; }

    public required Func<TimeSpan> StatsInterval { get; init; }

    public required Func<int> MaxLogLines { get; init; }

    /// <summary>Returns the command line shown above the log of a container, given its name.</summary>
    public required Func<string, string> DescribeLogCommand { get; init; }

    public bool IsRemote { get; init; }

    /// <summary>Whether containers of this source may be started, stopped and restarted.</summary>
    public bool CanControl { get; init; } = true;

    public static ContainerSourceOptions ForLocal(DockerSettingsSource settings) => new()
    {
        Id = LocalId,
        Label = () => string.IsNullOrWhiteSpace(settings.Current.Distribution) ? DefaultDistributionLabel : settings.Current.Distribution,
        ListInterval = () => settings.Current.ListInterval,
        StatsInterval = () => settings.Current.StatsInterval,
        MaxLogLines = () => settings.Current.EffectiveMaxLogLines,
        DescribeLogCommand = container =>
        {
            var distribution = string.IsNullOrWhiteSpace(settings.Current.Distribution) ? string.Empty : $" -d {settings.Current.Distribution}";
            return $"wsl{distribution} docker logs -f --tail {WslCliContainerProvider.LogTailLines} {container}";
        },
    };

    public static ContainerSourceOptions ForRemote(DockerHost host, DockerSettingsSource settings) => new()
    {
        Id = host.Id,
        Label = () => host.DisplayName,

        // The figures arrive together with the list, so both loops follow the same interval.
        ListInterval = () => DockerWidgetSettings.GetInterval(host),
        StatsInterval = () => DockerWidgetSettings.GetInterval(host),
        MaxLogLines = () => settings.Current.EffectiveMaxLogLines,
        DescribeLogCommand = container => $"ssh {host.Address} docker logs -f --tail {WslCliContainerProvider.LogTailLines} {container}",
        IsRemote = true,
        CanControl = host.AllowActions,
    };
}

/// <summary>The containers of one Docker instance: the local one in WSL, or one on another machine.</summary>
public sealed partial class ContainerSourceViewModel(
    IContainerProvider provider,
    IOverlayState overlayState,
    IOverlayCommands overlayCommands,
    ContainerSourceOptions options,
    TimeProvider timeProvider) : ObservableObject
{
    private const string StatusLabel = "Docker";

    private readonly string?[] _errors = new string?[Enum.GetValues<ErrorSource>().Length];

    // When each problem was first seen, so that alerts can show how long it has lasted.
    private readonly Dictionary<string, DateTimeOffset> _restartingSince = [];
    private DateTimeOffset? _failingSince;

    private CancellationTokenSource? _cancellation;
    private Task _pollingLoops = Task.CompletedTask;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private LogPanelViewModel? _logPanel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private ContainerFailureViewModel? _failure;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _hasLoaded;

    [ObservableProperty]
    private string _label = options.Label();

    /// <summary>Set while several sources share the page, which then needs a heading per source.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContentVisible))]
    private bool _showHeading;

    /// <summary>False while the user has folded the section up; the source keeps being asked all the same.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContentVisible))]
    private bool _isExpanded = true;

    /// <summary>What a folded section still shows about its state, next to the heading.</summary>
    [ObservableProperty]
    private WidgetSeverity _severity;

    [ObservableProperty]
    private string _summary = string.Empty;

    private enum ErrorSource
    {
        Stats,
        Action,
    }

    /// <summary>Raised when <see cref="Status"/> or <see cref="Alerts"/> changed.</summary>
    public event EventHandler? StatusChanged;

    public ObservableCollection<ContainerRowViewModel> Containers { get; } = [];

    /// <summary>Containers stuck in a restart loop; shown as banners above the list.</summary>
    public ObservableCollection<ContainerRowViewModel> RestartingContainers { get; } = [];

    public bool IsEmpty => HasLoaded && Failure is null && Containers.Count == 0;

    public string Id => options.Id;

    // Without a heading there is nothing to unfold the section with, so it is always shown then.
    public bool IsContentVisible => IsExpanded || !ShowHeading;

    public bool IsRemote => options.IsRemote;

    public bool CanControl => options.CanControl;

    /// <summary>Containers known to be running; none while the source cannot be asked.</summary>
    public int RunningCount => Failure is null ? Containers.Count(row => row.IsRunning) : 0;

    public int TotalCount => Containers.Count;

    /// <summary>The widget's entry in the status bar; null until the first poll has finished.</summary>
    public WidgetStatus? Status { get; private set; }

    public IReadOnlyList<WidgetAlert> Alerts { get; private set; } = [];

    public void Start()
    {
        if (_cancellation is not null)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        var cancellationToken = _cancellation.Token;
        Label = options.Label();

        _pollingLoops = Task.WhenAll(
            RunPollingLoopAsync(options.ListInterval, RefreshListAsync, cancellationToken),
            RunPollingLoopAsync(options.StatsInterval, RefreshStatsAsync, cancellationToken));
    }

    public async Task StopAsync()
    {
        if (_cancellation is null)
        {
            return;
        }

        await _cancellation.CancelAsync();
        await _pollingLoops;
        _cancellation.Dispose();
        _cancellation = null;
        CloseLogs();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var cancellationToken = _cancellation?.Token ?? CancellationToken.None;
        try
        {
            await RefreshListAsync(cancellationToken);
            await RefreshStatsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Widget was stopped during the refresh.
        }
    }

    [RelayCommand]
    private void OpenSettings() => overlayCommands.OpenSettings();

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand(CanExecute = nameof(CanControl))]
    private Task StartContainerAsync(ContainerRowViewModel container) =>
        RunActionAsync(token => provider.StartAsync(container.Id, token));

    [RelayCommand(CanExecute = nameof(CanControl))]
    private Task StopContainerAsync(ContainerRowViewModel container) =>
        RunActionAsync(token => provider.StopAsync(container.Id, token));

    [RelayCommand(CanExecute = nameof(CanControl))]
    private Task RestartContainerAsync(ContainerRowViewModel container) =>
        RunActionAsync(token => provider.RestartAsync(container.Id, token));

    [RelayCommand]
    private void ShowLogs(ContainerRowViewModel container)
    {
        CloseLogs();
        container.IsLogTarget = true;

        var panel = new LogPanelViewModel(
            provider,
            container.Id,
            container.Name,
            options.MaxLogLines(),
            options.DescribeLogCommand(container.Name),
            CloseLogs);
        LogPanel = panel;
        panel.Start();
    }

    [RelayCommand]
    private void CloseLogs()
    {
        foreach (var row in Containers)
        {
            row.IsLogTarget = false;
        }

        LogPanel?.Stop();
        LogPanel = null;
    }

    private async Task RunPollingLoopAsync(
        Func<TimeSpan> interval,
        Func<CancellationToken, Task> poll,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await WaitUntilVisibleAsync(cancellationToken);
                await poll(cancellationToken);
                await Task.Delay(interval(), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Widget was stopped.
        }
    }

    private async Task WaitUntilVisibleAsync(CancellationToken cancellationToken)
    {
        while (!overlayState.IsVisible)
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged(object? sender, EventArgs e) => changed.TrySetResult();

            overlayState.Changed += OnChanged;
            try
            {
                // Re-check after subscribing so a change between the loop condition and the subscription is not missed.
                if (!overlayState.IsVisible)
                {
                    await changed.Task.WaitAsync(cancellationToken);
                }
            }
            finally
            {
                overlayState.Changed -= OnChanged;
            }
        }
    }

    private async Task RefreshListAsync(CancellationToken cancellationToken)
    {
        Label = options.Label();

        IReadOnlyList<ContainerInfo> containers;
        try
        {
            containers = await provider.ListAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Failure = ContainerFailureViewModel.From(exception, Label);
            Summary = string.Empty;
            UpdateStatus();
            return;
        }

        var ordered = containers.OrderBy(container => container.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var existing = Containers.ToDictionary(row => row.Id);

        for (var index = 0; index < ordered.Count; index++)
        {
            var info = ordered[index];
            if (existing.Remove(info.Id, out var row))
            {
                row.Apply(info);
                var currentIndex = Containers.IndexOf(row);
                if (currentIndex != index)
                {
                    Containers.Move(currentIndex, index);
                }
            }
            else
            {
                Containers.Insert(index, new ContainerRowViewModel(info));
            }
        }

        foreach (var removed in existing.Values)
        {
            Containers.Remove(removed);
        }

        Failure = null;
        HasLoaded = true;
        Summary = $"{Containers.Count(row => row.IsRunning)} of {Containers.Count} running";
        OnPropertyChanged(nameof(IsEmpty));
        UpdateStatus();
    }

    private async Task RefreshStatsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stats = await provider.GetStatsAsync(cancellationToken);
            foreach (var row in Containers)
            {
                row.ApplyStats(stats.FirstOrDefault(row.Matches));
            }

            SetError(ErrorSource.Stats, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // While the list itself fails, the failure card already explains the problem.
            SetError(ErrorSource.Stats, Failure is null ? exception.Message : null);
        }
    }

    private async Task RunActionAsync(Func<CancellationToken, Task> action)
    {
        var cancellationToken = _cancellation?.Token ?? CancellationToken.None;
        try
        {
            await action(cancellationToken);
            SetError(ErrorSource.Action, null);
            await RefreshListAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Widget was stopped while the action was running.
        }
        catch (Exception exception)
        {
            SetError(ErrorSource.Action, exception.Message);
        }
    }

    /// <summary>Derives what the shell shows about this widget from the current list and failure.</summary>
    private void UpdateStatus()
    {
        var now = timeProvider.GetLocalNow();
        var running = Containers.Count(row => row.IsRunning);

        if (Failure is { } failure)
        {
            _failingSince ??= now;

            // While Docker cannot be reached nothing is known to be running, whatever the last list said.
            Status = new WidgetStatus(StatusLabel, Containers.Count > 0 ? $"0/{Containers.Count}" : "–", WidgetSeverity.Error);
            Alerts = [new WidgetAlert(WidgetSeverity.Error, failure.Title, $"{failure.Message} · {Label}", _failingSince.Value)];
        }
        else
        {
            _failingSince = null;
            var restarting = Containers.Where(row => row.StatusKind == ContainerStatusKind.Restarting).ToList();
            foreach (var name in _restartingSince.Keys.Except(restarting.Select(row => row.Name)).ToList())
            {
                _restartingSince.Remove(name);
            }

            foreach (var row in restarting)
            {
                _restartingSince.TryAdd(row.Name, now);
            }

            Status = new WidgetStatus(
                StatusLabel,
                $"{running}/{Containers.Count}",
                restarting.Count > 0 ? WidgetSeverity.Warning : WidgetSeverity.Ok);
            Alerts = [.. restarting.Select(row => new WidgetAlert(
                WidgetSeverity.Warning,
                $"{row.Name} keeps restarting",

                // On the own machine the container name says it all; elsewhere it matters where it runs.
                options.IsRemote ? $"{row.Status} · {Label}" : row.Status,
                _restartingSince[row.Name]))];
        }

        var restartingRows = Failure is null
            ? Containers.Where(row => row.StatusKind == ContainerStatusKind.Restarting).ToList()
            : [];
        if (!RestartingContainers.SequenceEqual(restartingRows))
        {
            RestartingContainers.Clear();
            foreach (var row in restartingRows)
            {
                RestartingContainers.Add(row);
            }
        }

        Severity = Status?.Severity ?? WidgetSeverity.Ok;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetError(ErrorSource source, string? message)
    {
        _errors[(int)source] = message;
        ErrorMessage = _errors.FirstOrDefault(error => error is not null);
    }
}
