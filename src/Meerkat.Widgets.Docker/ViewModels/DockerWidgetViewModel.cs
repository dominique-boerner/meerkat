using System.Collections.ObjectModel;
using System.Globalization;
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
    DockerSettingsSource settingsSource) : ObservableObject
{
    private const string DefaultDistributionLabel = "Default distribution";

    private readonly string?[] _errors = new string?[Enum.GetValues<ErrorSource>().Length];

    private CancellationTokenSource? _cancellation;
    private Task _pollingLoops = Task.CompletedTask;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisplayMode))]
    private bool _isInteractive;

    [ObservableProperty]
    private string _modeHotkeyText = string.Empty;

    /// <summary>Message from the shell shown in the footer instead of the hotkey hint, e.g. a pending update.</summary>
    [ObservableProperty]
    private string? _notice;

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

    [ObservableProperty]
    private string _intervalText = string.Empty;

    private enum ErrorSource
    {
        Stats,
        Action,
    }

    public ObservableCollection<ContainerRowViewModel> Containers { get; } = [];

    public bool IsDisplayMode => !IsInteractive;

    public bool IsEmpty => HasLoaded && Failure is null && Containers.Count == 0;

    public void Start()
    {
        if (_cancellation is not null)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        var cancellationToken = _cancellation.Token;

        overlayState.Changed += OnOverlayStateChanged;
        OnOverlayStateChanged(this, EventArgs.Empty);
        UpdateSettingsTexts();

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

        overlayState.Changed -= OnOverlayStateChanged;
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
        UpdateSettingsTexts();

        IReadOnlyList<ContainerInfo> containers;
        try
        {
            containers = await provider.ListAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Failure = ContainerFailureViewModel.From(exception, DistributionLabel);
            Summary = string.Empty;
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

    private void SetError(ErrorSource source, string? message)
    {
        _errors[(int)source] = message;
        ErrorMessage = _errors.FirstOrDefault(error => error is not null);
    }

    private void UpdateSettingsTexts()
    {
        var settings = settingsSource.Current;
        var culture = CultureInfo.CurrentCulture;
        DistributionLabel = string.IsNullOrWhiteSpace(settings.Distribution) ? DefaultDistributionLabel : settings.Distribution;
        IntervalText = $"List every {settings.ListInterval.TotalSeconds.ToString("0.#", culture)} s"
            + $" · Stats every {settings.StatsInterval.TotalSeconds.ToString("0.#", culture)} s";
    }

    private void OnOverlayStateChanged(object? sender, EventArgs e)
    {
        IsInteractive = overlayState.Mode == OverlayMode.Interactive;
        ModeHotkeyText = overlayState.ModeHotkeyText;
        Notice = overlayState.Notice;
    }
}
