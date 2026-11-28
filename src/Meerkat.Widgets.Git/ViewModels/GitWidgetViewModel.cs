using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meerkat.Abstractions;
using Meerkat.Widgets.Git.Providers;
using Meerkat.Widgets.Git.Settings;

namespace Meerkat.Widgets.Git.ViewModels;

public sealed partial class GitWidgetViewModel : ObservableObject
{
    private static readonly TimeSpan ClockInterval = TimeSpan.FromSeconds(5);

    private readonly IGitProviderFactory _providerFactory;
    private readonly GitSettingsSource _settingsSource;
    private readonly IOverlayState _overlayState;
    private readonly IOverlayCommands _overlayCommands;
    private readonly TimeProvider _timeProvider;

    private CancellationTokenSource? _cancellation;
    private Task _loops = Task.CompletedTask;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IGitProvider? _provider;
    private DateTimeOffset? _lastRefreshed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStale))]
    private GitFailureViewModel? _failure;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPipelines))]
    [NotifyPropertyChangedFor(nameof(ShowReviewRequests))]
    [NotifyPropertyChangedFor(nameof(ShowMyChangeRequests))]
    private GitTab _selectedTab = GitTab.Pipelines;

    [ObservableProperty]
    private GitTerminology _terminology;

    [ObservableProperty]
    private string _providerSummary = string.Empty;

    [ObservableProperty]
    private string _ageText = string.Empty;

    [ObservableProperty]
    private string? _actionError;

    // When each problem was first seen, so that alerts can show how long it has lasted.
    private DateTimeOffset? _failingSince;
    private DateTimeOffset? _reviewsWaitingSince;

    public GitWidgetViewModel(
        IGitProviderFactory providerFactory,
        GitSettingsSource settingsSource,
        IOverlayState overlayState,
        IOverlayCommands overlayCommands,
        TimeProvider timeProvider)
    {
        _providerFactory = providerFactory;
        _settingsSource = settingsSource;
        _overlayState = overlayState;
        _overlayCommands = overlayCommands;
        _timeProvider = timeProvider;
        Terminology = GitTerminology.For(settingsSource.Current.Provider);
        Tabs = [new GitTabViewModel(GitTab.Pipelines) { IsSelected = true }, new GitTabViewModel(GitTab.ReviewRequests), new GitTabViewModel(GitTab.MyChangeRequests)];
        ApplySettingsTexts();
    }

    public ObservableCollection<PipelineRowViewModel> Pipelines { get; } = [];

    public ObservableCollection<ChangeRequestRowViewModel> ReviewRequests { get; } = [];

    public ObservableCollection<ChangeRequestRowViewModel> MyChangeRequests { get; } = [];

    public IReadOnlyList<GitTabViewModel> Tabs { get; }

    /// <summary>Raised when <see cref="Status"/> or <see cref="Alerts"/> changed.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>True while a failure hides whether the shown data is still current.</summary>
    public bool IsStale => Failure is not null;

    public bool ShowPipelines => SelectedTab == GitTab.Pipelines;

    public bool ShowReviewRequests => SelectedTab == GitTab.ReviewRequests;

    public bool ShowMyChangeRequests => SelectedTab == GitTab.MyChangeRequests;

    /// <summary>The widget's entry in the status bar; null while no provider is connected.</summary>
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

        _settingsSource.Changed += OnSettingsChanged;
        RebuildProvider();

        _loops = Task.WhenAll(RunPollingLoopAsync(cancellationToken), RunClockLoopAsync(cancellationToken));
    }

    public async Task StopAsync()
    {
        if (_cancellation is null)
        {
            return;
        }

        _settingsSource.Changed -= OnSettingsChanged;
        await _cancellation.CancelAsync();
        await _loops;
        _cancellation.Dispose();
        _cancellation = null;
        _provider?.Dispose();
        _provider = null;
    }

    /// <summary>Loads everything once. Returns when the provider wants the next request at the earliest.</summary>
    public async Task<DateTimeOffset?> RefreshAsync(CancellationToken cancellationToken)
    {
        if (_provider is not { } provider)
        {
            // Creating the provider may have failed with a message of its own, e.g. for an invalid server URL.
            Failure ??= GitFailureViewModel.NotConfigured(Terminology);
            UpdateStatus();
            return null;
        }

        try
        {
            var settings = _settingsSource.Current;
            var runsTask = provider.ListPipelinesAsync(settings.Projects, settings.OnlyMyBranches, cancellationToken);
            var reviewsTask = provider.ListReviewRequestsAsync(cancellationToken);
            var mineTask = provider.ListMyChangeRequestsAsync(cancellationToken);
            await Task.WhenAll(runsTask, reviewsTask, mineTask);

            // Stages explain everything that is not simply green, and are not worth a request otherwise.
            var runs = await runsTask;
            var stages = await Task.WhenAll(runs.Select(run =>
                run.Status is PipelineStatus.Success or PipelineStatus.Canceled
                    ? Task.FromResult<IReadOnlyList<PipelineStage>>([])
                    : provider.GetStagesAsync(run, cancellationToken)));

            var now = _timeProvider.GetLocalNow();
            Replace(Pipelines, runs.Select((run, index) => new PipelineRowViewModel(run, stages[index], Terminology, now)));
            Replace(ReviewRequests, (await reviewsTask).Select(request => new ChangeRequestRowViewModel(request, now)));
            Replace(MyChangeRequests, (await mineTask).Select(request => new ChangeRequestRowViewModel(request, now)));

            _lastRefreshed = now;
            Failure = null;
            UpdateSummaries();
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Failure = GitFailureViewModel.From(exception, Terminology, _lastRefreshed);
            UpdateStatus();
            return (exception as GitProviderException)?.RetryAt;
        }
    }

    /// <summary>What the widget offers in the action search, for the pipelines and reviews known right now.</summary>
    public IReadOnlyList<WidgetAction> Actions
    {
        get
        {
            var actions = new List<WidgetAction>();
            var pipeline = Terminology.PipelineSingular.ToLowerInvariant();
            if (Pipelines.MaxBy(row => row.Run.UpdatedAt) is { } latest)
            {
                actions.Add(new WidgetAction
                {
                    Id = "open-latest-pipeline",
                    Title = $"Open latest {pipeline}",
                    Hint = $"{latest.ProjectName} · {latest.Run.Ref}",
                    Run = (_, _) =>
                    {
                        OpenUrl(latest.Run.WebUrl);
                        return Task.FromResult(WidgetActionResult.Done);
                    },
                });
            }

            if (_provider is { } provider && Pipelines.Where(row => row.IsFailed).MaxBy(row => row.Run.UpdatedAt) is { } failed)
            {
                actions.Add(new WidgetAction
                {
                    Id = "retry-failed-pipeline",
                    Title = $"Run failed {pipeline} again",
                    Hint = $"{failed.ProjectName} · {failed.Run.Ref} · #{failed.Run.Number}",
                    Run = async (_, cancellationToken) =>
                    {
                        try
                        {
                            await provider.RetryAsync(failed.Run, cancellationToken);
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            return WidgetActionResult.Failure($"#{failed.Run.Number} could not be run again", exception.Message);
                        }

                        _wake.TrySetResult();
                        return WidgetActionResult.Success($"#{failed.Run.Number} of {failed.ProjectName} runs again");
                    },
                });
            }

            if (ReviewRequests.Count > 0)
            {
                actions.Add(new WidgetAction
                {
                    Id = "open-reviews",
                    Title = "Open reviews for me",
                    Hint = $"{ReviewRequests.Count} open",
                    Run = (_, _) =>
                    {
                        // One request is opened where it is reviewed; several are listed for choosing.
                        if (ReviewRequests is [var only])
                        {
                            OpenUrl(only.WebUrl);
                        }
                        else
                        {
                            SelectTab(Tabs.First(tab => tab.Tab == GitTab.ReviewRequests));
                            _overlayCommands.ShowWidget(GitWidget.WidgetId);
                        }

                        return Task.FromResult(WidgetActionResult.Done);
                    },
                });
            }

            return actions;
        }
    }

    [RelayCommand]
    private void Refresh() => _wake.TrySetResult();

    [RelayCommand]
    private void SelectTab(GitTabViewModel tab)
    {
        SelectedTab = tab.Tab;
        foreach (var candidate in Tabs)
        {
            candidate.IsSelected = candidate == tab;
        }
    }

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var address))
        {
            _overlayCommands.OpenUrl(address);
        }
    }

    [RelayCommand]
    private async Task RetryAsync(PipelineRowViewModel pipeline)
    {
        if (_provider is not { } provider)
        {
            return;
        }

        try
        {
            await provider.RetryAsync(pipeline.Run, _cancellation?.Token ?? CancellationToken.None);
            ActionError = null;
            _wake.TrySetResult();
        }
        catch (OperationCanceledException)
        {
            // Widget was stopped while the request was running.
        }
        catch (Exception exception)
        {
            ActionError = $"Retry failed: {exception.Message}";
        }
    }

    [RelayCommand]
    private void RenewToken()
    {
        if (_provider is { } provider)
        {
            _overlayCommands.OpenUrl(provider.TokenPageUrl);
        }
    }

    [RelayCommand]
    private void OpenSettings() => _overlayCommands.OpenSettings();

    private async Task RunPollingLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await WaitUntilVisibleAsync(cancellationToken);

                // Created before the refresh so a request arriving meanwhile is not lost.
                var wake = _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var retryAt = await RefreshAsync(cancellationToken);

                var delay = _settingsSource.Current.Interval;
                if (retryAt is { } time && time - _timeProvider.GetUtcNow() > delay)
                {
                    delay = time - _timeProvider.GetUtcNow();
                }

                await Task.WhenAny(Task.Delay(delay, _timeProvider, cancellationToken), wake.Task);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException)
        {
            // Widget was stopped.
        }
    }

    /// <summary>Keeps "6 min ago" style texts moving between refreshes.</summary>
    private async Task RunClockLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(ClockInterval, _timeProvider, cancellationToken);
                if (_overlayState.IsVisible)
                {
                    UpdateTimes();
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
        while (!_overlayState.IsVisible)
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged(object? sender, EventArgs e) => changed.TrySetResult();

            _overlayState.Changed += OnChanged;
            try
            {
                // Re-check after subscribing so a change between the loop condition and the subscription is not missed.
                if (!_overlayState.IsVisible)
                {
                    await changed.Task.WaitAsync(cancellationToken);
                }
            }
            finally
            {
                _overlayState.Changed -= OnChanged;
            }
        }
    }

    private void RebuildProvider()
    {
        var settings = _settingsSource.Current;
        Terminology = GitTerminology.For(settings.Provider);
        ApplySettingsTexts();

        _provider?.Dispose();
        _provider = null;
        try
        {
            _provider = _providerFactory.Create(settings, _settingsSource.ReadToken(settings));
            Failure = _provider is null ? GitFailureViewModel.NotConfigured(Terminology) : null;
        }
        catch (Exception exception)
        {
            Failure = GitFailureViewModel.From(exception, Terminology, lastRefreshed: null);
        }

        UpdateStatus();
    }

    private void ApplySettingsTexts()
    {
        Tabs[0].Label = Terminology.PipelinesTitle;
        Tabs[1].Label = "Reviews for me";
        Tabs[2].Label = $"My {Terminology.ChangeRequestsAbbreviation}";
        UpdateSummaries();
    }

    private void UpdateSummaries()
    {
        ProviderSummary = $"{Terminology.ProviderName} · {Terminology.CountProjects(_settingsSource.Current.Projects.Count)}";
        Tabs[0].Count = Pipelines.Count;
        Tabs[1].Count = ReviewRequests.Count;
        Tabs[1].NeedsAttention = ReviewRequests.Count > 0;
        Tabs[2].Count = MyChangeRequests.Count;
        UpdateTimes();
        UpdateStatus();
    }

    /// <summary>Derives what the shell shows about this widget from the loaded data and the current failure.</summary>
    private void UpdateStatus()
    {
        var now = _timeProvider.GetLocalNow();
        var alerts = new List<WidgetAlert>();

        if (Failure is { AlertSeverity: { } severity } failure)
        {
            _failingSince ??= now;
            Status = new WidgetStatus(Terminology.PipelinesTitle, "–", severity);
            alerts.Add(new WidgetAlert(severity, failure.Title, failure.Message, _failingSince.Value));
        }
        else if (Failure is not null)
        {
            // Not connected yet: nothing to report, and nothing the user needs to be nagged about.
            _failingSince = null;
            Status = null;
        }
        else
        {
            _failingSince = null;
            var failed = Pipelines.Where(row => row.IsFailed).ToList();
            Status = new WidgetStatus(
                Terminology.PipelinesTitle,
                $"{Pipelines.Count - failed.Count}/{Pipelines.Count}",
                failed.Count > 0 ? WidgetSeverity.Error : WidgetSeverity.Ok);

            if (failed.Count > 0)
            {
                var noun = failed.Count == 1 ? Terminology.PipelineSingular : Terminology.PipelinesTitle;
                var byProject = failed
                    .GroupBy(row => row.ProjectName)
                    .Select(group => failed.Count == 1 ? $"{group.Key} · {group.First().RefText}" : $"{group.Key} ({group.Count()})");
                alerts.Add(new WidgetAlert(
                    WidgetSeverity.Error,
                    $"{failed.Count} {noun.ToLowerInvariant()} failed",
                    string.Join(" · ", byProject),
                    failed.Min(row => row.Run.UpdatedAt)));
            }
        }

        if (Failure is null && ReviewRequests.Count > 0)
        {
            _reviewsWaitingSince ??= now;
            alerts.Add(new WidgetAlert(
                WidgetSeverity.Warning,
                ReviewRequests.Count == 1 ? "1 review waiting for you" : $"{ReviewRequests.Count} reviews waiting for you",
                string.Join(" · ", ReviewRequests.Select(request => request.Reference)),
                _reviewsWaitingSince.Value));
        }
        else if (Failure is null)
        {
            _reviewsWaitingSince = null;
        }

        Alerts = alerts;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateTimes()
    {
        var now = _timeProvider.GetLocalNow();
        AgeText = _lastRefreshed is { } refreshed ? $"{RelativeTime.Format(now - refreshed)} ago" : string.Empty;
        foreach (var pipeline in Pipelines)
        {
            pipeline.UpdateTime(now);
        }

        foreach (var changeRequest in ReviewRequests.Concat(MyChangeRequests))
        {
            changeRequest.UpdateTime(now);
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // A different provider, server or project list invalidates everything on screen.
        Pipelines.Clear();
        ReviewRequests.Clear();
        MyChangeRequests.Clear();
        _lastRefreshed = null;
        RebuildProvider();
        _wake.TrySetResult();
    }

    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }
}
