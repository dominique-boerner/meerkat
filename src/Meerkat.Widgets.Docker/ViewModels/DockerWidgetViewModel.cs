using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meerkat.Abstractions;
using Meerkat.Widgets.Docker.Containers;
using Meerkat.Widgets.Docker.Settings;

namespace Meerkat.Widgets.Docker.ViewModels;

public sealed partial class DockerWidgetViewModel(
    IContainerProvider provider,
    IOverlayState overlayState,
    IOverlayCommands overlayCommands,
    DockerSettingsSource settingsSource,
    TimeProvider timeProvider) : ObservableObject
{
    private const string DefaultDistributionLabel = "Default distribution";
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
    private string _distributionLabel = DefaultDistributionLabel;

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
        UpdateDistributionLabel();

        _pollingLoops = Task.WhenAll(
            RunPollingLoopAsync(() => settingsSource.Current.ListInterval, RefreshListAsync, cancellationToken),
            RunPollingLoopAsync(() => settingsSource.Current.StatsInterval, RefreshStatsAsync, cancellationToken));
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
    private Task StartContainerAsync(ContainerRowViewModel container) =>
        RunActionAsync(token => provider.StartAsync(container.Id, token));

    [RelayCommand]
    private Task StopContainerAsync(ContainerRowViewModel container) =>
        RunActionAsync(token => provider.StopAsync(container.Id, token));

    [RelayCommand]
    private Task RestartContainerAsync(ContainerRowViewModel container) =>
        RunActionAsync(token => provider.RestartAsync(container.Id, token));

    [RelayCommand]
    private void ShowLogs(ContainerRowViewModel container)
    {
        CloseLogs();
        container.IsLogTarget = true;

        var settings = settingsSource.Current;
        var distribution = string.IsNullOrWhiteSpace(settings.Distribution) ? string.Empty : $" -d {settings.Distribution}";
        var panel = new LogPanelViewModel(
            provider,
            container.Id,
            container.Name,
            settings.EffectiveMaxLogLines,
            $"wsl{distribution} docker logs -f --tail {WslCliContainerProvider.LogTailLines} {container.Name}",
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
        UpdateDistributionLabel();

        IReadOnlyList<ContainerInfo> containers;
        try
        {
            containers = await provider.ListAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Failure = ContainerFailureViewModel.From(exception, DistributionLabel);
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
            Alerts = [new WidgetAlert(WidgetSeverity.Error, failure.Title, $"{failure.Message} · {DistributionLabel}", _failingSince.Value)];
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
                row.Status,
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

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetError(ErrorSource source, string? message)
    {
        _errors[(int)source] = message;
        ErrorMessage = _errors.FirstOrDefault(error => error is not null);
    }

    private void UpdateDistributionLabel()
    {
        var distribution = settingsSource.Current.Distribution;
        DistributionLabel = string.IsNullOrWhiteSpace(distribution) ? DefaultDistributionLabel : distribution;
    }
}
