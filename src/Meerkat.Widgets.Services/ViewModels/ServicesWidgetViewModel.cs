using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meerkat.Abstractions;
using Meerkat.Widgets.Services.Checks;
using Meerkat.Widgets.Services.Settings;

namespace Meerkat.Widgets.Services.ViewModels;

public sealed partial class ServicesWidgetViewModel(
    IServiceChecker checker,
    ServicesSettingsSource settingsSource,
    IOverlayState overlayState,
    IOverlayCommands overlayCommands,
    TimeProvider timeProvider) : ObservableObject
{
    private const string StatusLabel = "Services";

    private static readonly TimeSpan ClockInterval = TimeSpan.FromSeconds(30);

    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _servicesCancellation;
    private Task _serviceLoops = Task.CompletedTask;
    private Task _clockLoop = Task.CompletedTask;

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string _heading = "EXTERNAL SERVICES";

    public event EventHandler? StatusChanged;

    public ObservableCollection<ServiceViewModel> Services { get; } = [];

    /// <summary>The widget's entry in the status bar; null while no service is configured.</summary>
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
        LoadServices();
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
        await StopServiceLoopsAsync();
        await _clockLoop;
        _cancellation.Dispose();
        _cancellation = null;
    }

    /// <summary>Checks one service now. Also used by the polling loops.</summary>
    public async Task CheckAsync(ServiceViewModel service, CancellationToken cancellationToken)
    {
        try
        {
            var check = await checker.CheckAsync(service.Endpoint, cancellationToken);
            if (service.Apply(check.State, timeProvider.GetLocalNow()))
            {
                // "Mute until resolved" has done its job and must not silence the next incident.
                settingsSource.Save();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The state last learned stays; an endpoint that cannot be asked says nothing about its service.
            service.ApplyFailure(exception.Message, timeProvider.GetLocalNow());
        }

        UpdateStatus();
    }

    [RelayCommand]
    private async Task RecheckAsync(ServiceViewModel service)
    {
        service.IsBusy = true;
        try
        {
            await CheckAsync(service, _cancellation?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Widget was stopped during the check.
        }
        finally
        {
            service.IsBusy = false;
        }
    }

    [RelayCommand]
    private void ToggleMute(ServiceViewModel service)
    {
        service.SetMuted(!service.IsMuted);

        // Saved without announcing new settings: reloading the services would throw away what is known about them.
        settingsSource.Save();
        UpdateStatus();
    }

    [RelayCommand]
    private void OpenPage(ServiceViewModel service) => overlayCommands.OpenUrl(service.Endpoint.GetPageUri());

    [RelayCommand]
    private void OpenSettings() => overlayCommands.OpenSettings();

    private void LoadServices()
    {
        Services.Clear();
        foreach (var endpoint in settingsSource.Current.Endpoints)
        {
            Services.Add(new ServiceViewModel(endpoint));
        }

        IsEmpty = Services.Count == 0;
        Heading = IsEmpty ? "EXTERNAL SERVICES" : $"EXTERNAL SERVICES · {Services.Count}";
        UpdateStatus();

        _servicesCancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation?.Token ?? CancellationToken.None);
        var cancellationToken = _servicesCancellation.Token;
        _serviceLoops = Task.WhenAll(Services.Select(service => RunServiceLoopAsync(service, cancellationToken)));
    }

    private async Task StopServiceLoopsAsync()
    {
        if (_servicesCancellation is null)
        {
            return;
        }

        await _servicesCancellation.CancelAsync();
        await _serviceLoops;
        _servicesCancellation.Dispose();
        _servicesCancellation = null;
    }

    private async void OnSettingsChanged(object? sender, EventArgs e)
    {
        // Services may have been added, removed or changed; starting over is simpler and safer than patching loops.
        await StopServiceLoopsAsync();
        if (_cancellation is { IsCancellationRequested: false })
        {
            LoadServices();
        }
    }

    private async Task RunServiceLoopAsync(ServiceViewModel service, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await WaitUntilVisibleAsync(cancellationToken);
                await CheckAsync(service, cancellationToken);
                await Task.Delay(ServicesWidgetSettings.GetInterval(service.Endpoint), timeProvider, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Widget was stopped or the services were reloaded.
        }
    }

    /// <summary>Keeps texts such as "since 18 min" moving between checks.</summary>
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
                    foreach (var service in Services)
                    {
                        service.UpdateTimes(now);
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

    /// <summary>Derives what the shell shows about this widget and keeps services with problems at the top.</summary>
    private void UpdateStatus()
    {
        if (Services.Count == 0)
        {
            Status = null;
            Alerts = [];
        }
        else
        {
            Alerts = [.. Services.Select(service => service.Alert).OfType<WidgetAlert>()];
            Status = new WidgetStatus(
                StatusLabel,
                $"{Services.Count(service => service.IsKnown && !service.IsProblem)}/{Services.Count}",
                Alerts.Count == 0 ? WidgetSeverity.Ok : Alerts.Max(alert => alert.Severity));
        }

        // Services in the same state keep the order the user gave them in the settings.
        var endpoints = settingsSource.Current.Endpoints;
        var ordered = Services
            .OrderByDescending(service => service.IsProblem ? service.Severity : WidgetSeverity.Ok)
            .ThenBy(service => endpoints.IndexOf(service.Endpoint))
            .ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var currentIndex = Services.IndexOf(ordered[index]);
            if (currentIndex != index)
            {
                Services.Move(currentIndex, index);
            }
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
