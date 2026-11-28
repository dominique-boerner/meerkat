using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Meerkat.Abstractions;

namespace Meerkat.App.Actions;

/// <summary>An action as the palette offers it: with the widget it comes from, or the shell itself.</summary>
/// <param name="WidgetId">The widget whose action it is; null for an action of the shell.</param>
/// <param name="Area">The heading the action is listed under, which is the title of its widget.</param>
/// <param name="Icon">The glyph of the widget.</param>
public sealed record PaletteAction(string? WidgetId, string Area, string Icon, WidgetAction Action)
{
    /// <summary>Identifies the action across widgets, for remembering what was used last.</summary>
    public string Key => $"{WidgetId}/{Action.Id}";
}

/// <summary>A stretch of a title that either was typed or was not, for showing what the search matched.</summary>
public sealed record TextSegment(string Text, bool IsMatch);

/// <summary>A line of the palette that can be chosen: an action or, in the second step, a target.</summary>
public sealed partial class PaletteRowViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The heading above this row; set on the first row of a group only.</summary>
    public string? Group { get; init; }

    public required IReadOnlyList<TextSegment> Title { get; init; }

    public string? Hint { get; init; }

    public string Icon { get; init; } = string.Empty;

    /// <summary>The state of a target, shown as a dot in place of the icon.</summary>
    public WidgetSeverity? Severity { get; init; }

    public bool HasDot => Severity is not null;

    /// <summary>False for a target the action cannot be applied to; it is shown but cannot be chosen.</summary>
    public bool IsAvailable { get; init; } = true;

    internal PaletteAction? Action { get; init; }

    internal WidgetActionTarget? Target { get; init; }
}

public enum PaletteStage
{
    /// <summary>The user looks for an action.</summary>
    Actions,

    /// <summary>The chosen action asks what to apply it to.</summary>
    Targets,

    /// <summary>The action asks whether it shall really run.</summary>
    Confirmation,

    Running,

    Succeeded,

    Failed,
}

/// <summary>
/// The action search: one field that finds what the widgets offer to do, asks for a target and a
/// confirmation where an action needs them, runs it and says how it went. Everything is done with the
/// keyboard, so the view only passes on keys.
/// </summary>
public sealed partial class ActionPaletteViewModel : ObservableObject, IDisposable
{
    public const int MaxRecent = 3;

    private const string RecentGroup = "RECENTLY USED";
    private const string SearchPlaceholder = "Search actions …";

    /// <summary>How long a success stays to be read before the palette closes on its own.</summary>
    private static readonly TimeSpan SuccessDisplayTime = TimeSpan.FromMilliseconds(1300);

    private readonly IReadOnlyList<PaletteAction> _actions;
    private readonly IReadOnlyList<string> _recent;
    private readonly CancellationTokenSource _lifetime = new();

    private PaletteAction? _action;
    private WidgetActionTarget? _target;
    private IReadOnlyList<WidgetActionTargetGroup> _targetGroups = [];

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosing), nameof(IsConfirmation), nameof(IsRunning), nameof(IsSucceeded), nameof(IsFailed), nameof(HasChip))]
    private PaletteStage _stage;

    /// <summary>What was chosen so far, shown in front of the field: the action, then the action and its target.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChip))]
    private string? _chip;

    [ObservableProperty]
    private string _placeholder = SearchPlaceholder;

    /// <summary>Said in place of the list when it is empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyText))]
    private string? _emptyText;

    /// <summary>What Enter does with a text no action matched, such as "Save as quick note"; null when nothing takes it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTextOffer))]
    private string? _textOffer;

    /// <summary>The question of a confirmation, or how the action went.</summary>
    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    private string? _detail;

    /// <summary>The word on the button Enter presses: what a confirmation goes ahead with, or where a failure leads.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrimaryAction))]
    private string? _primaryText;

    /// <param name="actions">What can be done right now, in the order it is listed.</param>
    /// <param name="recent">Keys of the actions used last, the latest first.</param>
    public ActionPaletteViewModel(IReadOnlyList<PaletteAction> actions, IReadOnlyList<string> recent)
    {
        _actions = actions;
        _recent = recent;
        ShowActions();
    }

    /// <summary>Raised when the palette has nothing more to show; with a time to wait first when there is something to read.</summary>
    public event EventHandler<TimeSpan>? CloseRequested;

    /// <summary>Raised with the key of an action that ran, for remembering it.</summary>
    public event EventHandler<string>? ActionUsed;

    /// <summary>Raised with the id of the widget whose page is to be opened after a failure.</summary>
    public event EventHandler<string>? WidgetRequested;

    public ObservableCollection<PaletteRowViewModel> Rows { get; } = [];

    public PaletteRowViewModel? SelectedRow => Rows.FirstOrDefault(row => row.IsSelected);

    /// <summary>True while the field takes input and the list is shown.</summary>
    public bool IsChoosing => Stage is PaletteStage.Actions or PaletteStage.Targets;

    public bool IsConfirmation => Stage == PaletteStage.Confirmation;

    public bool IsRunning => Stage == PaletteStage.Running;

    public bool IsSucceeded => Stage == PaletteStage.Succeeded;

    public bool IsFailed => Stage == PaletteStage.Failed;

    public bool HasChip => Chip is not null && Stage != PaletteStage.Actions;

    public bool HasEmptyText => EmptyText is not null;

    public bool HasTextOffer => TextOffer is not null;

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    public bool HasPrimaryAction => PrimaryText is not null;

    /// <summary>Moves the selection by one row, past those that cannot be chosen, and around the ends.</summary>
    public void Move(int direction)
    {
        var available = Rows.Where(row => row.IsAvailable).ToList();
        if (!IsChoosing || available.Count == 0)
        {
            return;
        }

        var current = SelectedRow is { } selected ? available.IndexOf(selected) : -1;
        Select(available[((current + direction) % available.Count + available.Count) % available.Count]);
    }

    public void Select(PaletteRowViewModel row)
    {
        if (!row.IsAvailable)
        {
            return;
        }

        foreach (var other in Rows)
        {
            other.IsSelected = other == row;
        }

        OnPropertyChanged(nameof(SelectedRow));
    }

    /// <summary>What Enter does: choose the selected row, confirm, or follow a failure to its widget.</summary>
    public async Task AcceptAsync()
    {
        switch (Stage)
        {
            case PaletteStage.Actions when SelectedRow?.Action is { } action:
                Choose(action);
                break;
            case PaletteStage.Actions when TextOffer is not null && _actions.FirstOrDefault(candidate => candidate.Action.RunWithText is not null) is { } taker:
                var text = Query.Trim();
                _action = taker;
                await RunAsync(taker, token => taker.Action.RunWithText!(text, token));
                break;
            case PaletteStage.Targets when _action is { } chosen && SelectedRow?.Target is { } target:
                await ProceedAsync(chosen, target);
                break;
            case PaletteStage.Confirmation when _action is { } confirmed:
                var confirmedTarget = _target;
                await RunAsync(confirmed, token => confirmed.Action.Run(confirmedTarget, token));
                break;
            case PaletteStage.Failed:
                if (_action?.WidgetId is { } widgetId)
                {
                    WidgetRequested?.Invoke(this, widgetId);
                }

                Close();
                break;
        }
    }

    /// <summary>What Esc does: one step back, and out of the palette from the first one.</summary>
    public void Back()
    {
        switch (Stage)
        {
            case PaletteStage.Targets:
                ShowActions();
                break;
            case PaletteStage.Confirmation when _action is { Action.Targets: not null } action:
                ShowTargets(action);
                break;
            case PaletteStage.Confirmation:
                ShowActions();
                break;
            default:
                // An action that runs is not held back by closing; only its result goes unseen.
                Close();
                break;
        }
    }

    /// <summary>Lets go of what the palette waits for; an action that already runs is asked to stop.</summary>
    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    partial void OnQueryChanged(string value)
    {
        if (Stage == PaletteStage.Actions)
        {
            FillActions();
        }
        else if (Stage == PaletteStage.Targets)
        {
            FillTargets();
        }
    }

    private void Choose(PaletteAction action)
    {
        _action = action;
        if (action.Action.Targets is null)
        {
            _ = ProceedAsync(action, null);
        }
        else
        {
            ShowTargets(action);
        }
    }

    private void ShowActions()
    {
        _action = null;
        _target = null;
        Chip = null;
        Placeholder = SearchPlaceholder;
        Stage = PaletteStage.Actions;
        if (Query.Length == 0)
        {
            FillActions();
        }
        else
        {
            Query = string.Empty;
        }
    }

    private void ShowTargets(PaletteAction action)
    {
        try
        {
            _targetGroups = action.Action.Targets!();
        }
        catch (Exception exception)
        {
            ShowFailure(action, WidgetActionResult.Failure($"{action.Action.Title} is not possible right now", exception.Message));
            return;
        }

        _target = null;
        Chip = $"{action.Action.Title} ›";
        Placeholder = action.Action.TargetPrompt ?? "Choose …";
        Stage = PaletteStage.Targets;
        if (Query.Length == 0)
        {
            FillTargets();
        }
        else
        {
            Query = string.Empty;
        }
    }

    private void FillActions()
    {
        var rows = new List<PaletteRowViewModel>();
        if (string.IsNullOrWhiteSpace(Query))
        {
            var recent = _recent.Select(key => _actions.FirstOrDefault(action => action.Key == key)).OfType<PaletteAction>().Take(MaxRecent).ToList();
            rows.AddRange(recent.Select((action, index) => CreateRow(action, index == 0 ? RecentGroup : null, null)));

            string? area = null;
            foreach (var action in _actions.Except(recent))
            {
                rows.Add(CreateRow(action, action.Area == area ? null : action.Area.ToUpperInvariant(), null));
                area = action.Area;
            }
        }
        else
        {
            // The best match first; a heading wherever the next one comes from another widget.
            string? area = null;
            foreach (var (action, match) in _actions
                         .Select(action => (Action: action, Match: Match(action)))
                         .Where(found => found.Match is not null)
                         .OrderByDescending(found => found.Match!.Score))
            {
                rows.Add(CreateRow(action, action.Area == area ? null : action.Area.ToUpperInvariant(), match));
                area = action.Area;
            }
        }

        var takesText = rows.Count == 0 ? _actions.FirstOrDefault(action => action.Action.RunWithText is not null) : null;
        TextOffer = takesText is null ? null : takesText.Action.TextTitle ?? takesText.Action.Title;
        Replace(rows, rows.Count == 0 ? $"No action for “{Query.Trim()}”." : null);
    }

    private void FillTargets()
    {
        var rows = new List<PaletteRowViewModel>();
        foreach (var group in _targetGroups)
        {
            var isFirst = true;
            foreach (var target in group.Targets)
            {
                var match = ActionSearch.Match(target.Title, Query);
                if (match is null)
                {
                    continue;
                }

                rows.Add(new PaletteRowViewModel
                {
                    Group = isFirst ? group.Title.ToUpperInvariant() : null,
                    Title = Segment(target.Title, match.Positions),
                    Hint = target.Unavailable is null ? target.Hint : string.Join(" · ", new[] { target.Hint, target.Unavailable }.Where(part => !string.IsNullOrEmpty(part))),
                    Severity = target.Severity,
                    IsAvailable = target.Unavailable is null,
                    Target = target,
                });
                isFirst = false;
            }
        }

        TextOffer = null;
        Replace(rows, rows.Count > 0 ? null : string.IsNullOrWhiteSpace(Query) ? "There is nothing to choose from right now." : $"Nothing matches “{Query.Trim()}”.");
    }

    private void Replace(IReadOnlyList<PaletteRowViewModel> rows, string? emptyText)
    {
        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        EmptyText = emptyText;
        if (Rows.FirstOrDefault(row => row.IsAvailable) is { } first)
        {
            first.IsSelected = true;
        }

        OnPropertyChanged(nameof(SelectedRow));
    }

    /// <summary>A title that holds what was typed; failing that, a widget whose name does, so that "docker" lists all it offers.</summary>
    private ActionMatch? Match(PaletteAction action) =>
        ActionSearch.Match(action.Action.Title, Query) ?? (ActionSearch.Match(action.Area, Query) is null ? null : new ActionMatch(0, []));

    private static PaletteRowViewModel CreateRow(PaletteAction action, string? group, ActionMatch? match)
    {
        // The dots tell that the action will ask for something before it runs.
        var title = action.Action.Targets is null ? action.Action.Title : action.Action.Title + "…";
        return new PaletteRowViewModel
        {
            Group = group,
            Title = Segment(title, match?.Positions ?? []),
            Hint = action.Action.Hint,
            Icon = action.Icon,
            Action = action,
        };
    }

    private static IReadOnlyList<TextSegment> Segment(string text, IReadOnlyList<int> positions)
    {
        var matched = new HashSet<int>(positions);
        var segments = new List<TextSegment>();
        var start = 0;
        for (var index = 1; index <= text.Length; index++)
        {
            if (index == text.Length || matched.Contains(index) != matched.Contains(start))
            {
                segments.Add(new TextSegment(text[start..index], matched.Contains(start)));
                start = index;
            }
        }

        return segments;
    }

    private async Task ProceedAsync(PaletteAction action, WidgetActionTarget? target)
    {
        _target = target;
        Chip = target is null ? $"{action.Action.Title} ›" : $"{action.Action.Title} › {target.Title}";

        WidgetActionConfirmation? confirmation;
        try
        {
            confirmation = action.Action.Confirm?.Invoke(target);
        }
        catch (Exception exception)
        {
            ShowFailure(action, WidgetActionResult.Failure($"{action.Action.Title} is not possible right now", exception.Message));
            return;
        }

        if (confirmation is null)
        {
            await RunAsync(action, token => action.Action.Run(target, token));
            return;
        }

        Message = confirmation.Question;
        Detail = confirmation.Detail;
        PrimaryText = confirmation.ConfirmText;
        Stage = PaletteStage.Confirmation;

        // What was typed found the target and has nothing to say next to the question.
        Query = string.Empty;
    }

    private async Task RunAsync(PaletteAction action, Func<CancellationToken, Task<WidgetActionResult>> run)
    {
        Message = $"{action.Action.Title} …";
        Detail = null;
        PrimaryText = null;
        Stage = PaletteStage.Running;
        Query = string.Empty;

        WidgetActionResult result;
        try
        {
            result = await run(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            // An action that throws has not said what went wrong in its own words; its error has to do.
            result = WidgetActionResult.Failure($"{action.Action.Title} failed", exception.Message);
        }

        ActionUsed?.Invoke(this, action.Key);
        if (!result.Succeeded)
        {
            ShowFailure(action, result);
        }
        else if (result.Message is null)
        {
            Close();
        }
        else
        {
            Message = result.Message;
            Detail = result.Detail;
            Stage = PaletteStage.Succeeded;
            CloseRequested?.Invoke(this, SuccessDisplayTime);
        }
    }

    private void ShowFailure(PaletteAction action, WidgetActionResult result)
    {
        _action = action;
        Message = result.Message ?? $"{action.Action.Title} failed";
        Detail = result.Detail;

        // The page of the widget is where the problem can be looked at; the shell has no page of its own.
        PrimaryText = action.WidgetId is null ? null : $"Open {action.Area}";
        Stage = PaletteStage.Failed;
    }

    private void Close() => CloseRequested?.Invoke(this, TimeSpan.Zero);
}
