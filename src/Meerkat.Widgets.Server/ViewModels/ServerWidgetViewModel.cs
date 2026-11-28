using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meerkat.Abstractions;
using Meerkat.Widgets.Server.Monitoring;
using Meerkat.Widgets.Server.Settings;
using Meerkat.Ssh;

namespace Meerkat.Widgets.Server.ViewModels;

public sealed partial class ServerWidgetViewModel(
    IServerProbe probe,
    ServerSettingsSource settingsSource,
    IOverlayState overlayState,
    IOverlayCommands overlayCommands,
    TimeProvider timeProvider) : ObservableObject
{
    private const string StatusLabel = "Server";

    private static readonly TimeSpan ClockInterval = TimeSpan.FromSeconds(5);

    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _hostsCancellation;
    private Task _clockLoop = Task.CompletedTask;
    private Task _hostLoops = Task.CompletedTask;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string? _actionError;

    /// <summary>Raised when <see cref="Status"/> or <see cref="Alerts"/> changed.</summary>
    public event EventHandler? StatusChanged;

    public ObservableCollection<HostViewModel> Hosts { get; } = [];

    /// <summary>The widget's entry in the status bar; null while no host is configured.</summary>
    public WidgetStatus? Status { get; private set; }

    public IReadOnlyList<WidgetAlert> Alerts { get; private set; } = [];

    public void Start()
    {
        if (_cancellation is not null)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        settingsSource.Changed += OnSettingsChanged;
        LoadHosts();
        _clockLoop = RunClockLoopAsync(_cancellation.Token);
    }

    public async Task StopAsync()
    {
        if (_cancellation is null)
        {
            return;
        }

        settingsSource.Changed -= OnSettingsChanged;
        await _cancellation.CancelAsync();
        await StopHostLoopsAsync();
        await _clockLoop;
        _cancellation.Dispose();
        _cancellation = null;
    }

    /// <summary>Checks one host now. Also used by the polling loops.</summary>
    public async Task CheckAsync(HostViewModel host, CancellationToken cancellationToken)
    {
        if (host.IsPaused)
        {
            return;
        }

        try
        {
            // Polling never accepts a host key on its own; that takes an explicit decision by the user.
            var snapshot = await probe.ProbeAsync(host.Host, HostKeyPolicy.Strict, cancellationToken);
            host.ApplySnapshot(snapshot, timeProvider.GetLocalNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            host.ApplyFailure(exception, timeProvider.GetLocalNow());
        }

        UpdateStatus();
    }

    /// <summary>What the widget offers in the action search, for the servers watched right now.</summary>
    public IReadOnlyList<WidgetAction> Actions => Hosts.Count == 0 ? [] :
    [
        new()
        {
            Id = "open-terminal",
            Title = "Open SSH terminal",
            TargetPrompt = "Choose a server …",
            Targets = () =>
            [
                new WidgetActionTargetGroup("Servers",
                [
                    .. Hosts.Select(host => new WidgetActionTarget(
                        host.Host.Id,
                        host.Name,
                        host.Address,
                        host.IsOnline ? WidgetSeverity.Ok : host.IsIdle ? null : WidgetSeverity.Error)),
                ]),
            ],
            Run = (target, _) =>
            {
                if (Hosts.FirstOrDefault(host => host.Host.Id == target?.Id) is not { } host)
                {
                    return Task.FromResult(WidgetActionResult.Failure($"{target?.Title ?? "The server"} is no longer watched"));
                }

                try
                {
                    probe.OpenTerminal(host.Host);
                    return Task.FromResult(WidgetActionResult.Done);
                }
                catch (Exception exception)
                {
                    return Task.FromResult(WidgetActionResult.Failure("The terminal could not be opened", exception.Message));
                }
            },
        },
        new()
        {
            Id = "check-servers",
            Title = "Check all servers now",
            Run = async (_, _) =>
            {
                var hosts = Hosts.Where(host => !host.IsPaused).ToList();
                await Task.WhenAll(hosts.Select(RecheckAsync));
                var problems = hosts.Count(host => !host.IsOnline);
                return problems == 0
                    ? WidgetActionResult.Success(hosts.Count == 1 ? "The server is fine" : $"All {hosts.Count} servers are fine")
                    : WidgetActionResult.Failure(problems == 1 ? "1 server has a problem" : $"{problems} servers have a problem", string.Join(", ", hosts.Where(host => !host.IsOnline).Select(host => host.Name)));
            },
        },
    ];

    [RelayCommand]
    private async Task RecheckAsync(HostViewModel host)
    {
        host.IsBusy = true;
        try
        {
            await CheckAsync(host, _cancellation?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Widget was stopped during the check.
        }
        finally
        {
            host.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TrustHostKeyAsync(HostViewModel host)
    {
        host.IsBusy = true;
        try
        {
            var snapshot = await probe.TrustHostKeyAsync(host.Host, _cancellation?.Token ?? CancellationToken.None);
            host.ApplySnapshot(snapshot, timeProvider.GetLocalNow());
        }
        catch (OperationCanceledException)
        {
            // Widget was stopped while connecting.
        }
        catch (Exception exception)
        {
            host.ApplyFailure(exception, timeProvider.GetLocalNow());
        }
        finally
        {
            host.IsBusy = false;
        }

        UpdateStatus();
    }

    [RelayCommand]
    private void PauseHost(HostViewModel host) => SetPaused(host, isPaused: true);

    [RelayCommand]
    private void ResumeHost(HostViewModel host) => SetPaused(host, isPaused: false);

    [RelayCommand]
    private void ToggleExpanded(HostViewModel host) => host.IsExpanded = !host.IsExpanded;

    [RelayCommand]
    private void OpenTerminal(HostViewModel host)
    {
        try
        {
            probe.OpenTerminal(host.Host);
            ActionError = null;
        }
        catch (Exception exception)
        {
            ActionError = $"The terminal could not be opened: {exception.Message}";
        }
    }

    [RelayCommand]
    private void OpenSettings() => overlayCommands.OpenSettings();

    private void SetPaused(HostViewModel host, bool isPaused)
    {
        host.SetPaused(isPaused);

        // Saved without announcing new settings: reloading all hosts would throw away their CPU history.
        settingsSource.Save();
        if (!isPaused && _hostsCancellation is { } cancellation)
        {
            _hostLoops = Task.WhenAll(_hostLoops, RunHostLoopAsync(host, cancellation.Token));
        }

        UpdateStatus();
    }

    private void LoadHosts()
    {
        var hosts = settingsSource.Current.Hosts;
        Hosts.Clear();
        foreach (var host in hosts)
        {
            Hosts.Add(new HostViewModel(host));
        }

        IsEmpty = Hosts.Count == 0;
        Summary = Hosts.Count == 1 ? "1 host" : $"{Hosts.Count} hosts";
        UpdateStatus();

        _hostsCancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation?.Token ?? CancellationToken.None);
        var cancellationToken = _hostsCancellation.Token;
        _hostLoops = Task.WhenAll(Hosts.Where(host => !host.IsPaused).Select(host => RunHostLoopAsync(host, cancellationToken)));
    }

    private async Task StopHostLoopsAsync()
    {
        if (_hostsCancellation is null)
        {
            return;
        }

        await _hostsCancellation.CancelAsync();
        await _hostLoops;
        _hostsCancellation.Dispose();
        _hostsCancellation = null;
    }

    private async void OnSettingsChanged(object? sender, EventArgs e)
    {
        // Hosts may have been added, removed or changed; starting over is simpler and safer than patching loops.
        await StopHostLoopsAsync();
        if (_cancellation is { IsCancellationRequested: false })
        {
            LoadHosts();
        }
    }

    private async Task RunHostLoopAsync(HostViewModel host, CancellationToken cancellationToken)
    {
        var generation = host.MonitoringGeneration;
        try
        {
            while (host.MonitoringGeneration == generation)
            {
                await WaitUntilVisibleAsync(cancellationToken);
                await CheckAsync(host, cancellationToken);
                await Task.Delay(settingsSource.Current.GetInterval(host.Host), timeProvider, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Widget was stopped or the hosts were reloaded.
        }
    }

    /// <summary>Keeps "checked 12 s ago" style texts moving between checks.</summary>
    private async Task RunClockLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(ClockInterval, timeProvider, cancellationToken);
                if (overlayState.IsPanelOpen)
                {
                    var now = timeProvider.GetLocalNow();
                    foreach (var host in Hosts)
                    {
                        host.UpdateTimes(now);
                    }
                }
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

    /// <summary>Derives what the shell shows about this widget and keeps hosts with problems at the top.</summary>
    private void UpdateStatus()
    {
        var monitored = Hosts.Where(host => !host.IsPaused).ToList();
        if (monitored.Count == 0)
        {
            Status = null;
            Alerts = [];
        }
        else
        {
            Alerts = [.. monitored.SelectMany(host => host.Alerts)];
            Status = new WidgetStatus(
                StatusLabel,
                $"{monitored.Count(host => host.IsOnline)}/{monitored.Count}",
                Alerts.Count == 0 ? WidgetSeverity.Ok : Alerts.Max(alert => alert.Severity));
        }

        var ordered = Hosts
            .OrderByDescending(host => host.IsPaused ? WidgetSeverity.Ok : host.Severity)
            .ThenBy(host => host.IsPaused)
            .ThenBy(host => host.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var currentIndex = Hosts.IndexOf(ordered[index]);
            if (currentIndex != index)
            {
                Hosts.Move(currentIndex, index);
            }
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
