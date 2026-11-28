using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meerkat.Abstractions;

namespace Meerkat.App.Overlay;

/// <param name="IsAcknowledged">
/// True when the user has put away every card behind the chip. The problem is still there, so the chip
/// keeps its colour, but as an outline: filled means news, outlined means known.
/// </param>
public sealed record StatusChipViewModel(string Label, string Summary, WidgetSeverity Severity, bool IsAcknowledged = false);

/// <param name="WidgetId">The widget that reported the alert; its page opens when the card is clicked.</param>
/// <param name="Key">Identifies the alert for as long as it says the same thing; a card that was put away is remembered by it.</param>
/// <param name="Icon">A glyph that tells what the alert is about; its colour tells how grave it is.</param>
public sealed record AlertCardViewModel(
    string Title,
    string Detail,
    string AgeText,
    string? WidgetId = null,
    WidgetSeverity Severity = WidgetSeverity.Error,
    string Key = "",
    string Icon = "");

public sealed record StatusCardViewModel(WidgetStatusCard Card, string? WidgetId = null);

/// <summary>What one widget currently reports to the shell.</summary>
/// <param name="AdditionalStatuses">Further entries of the widget in the status bar, shown right after <paramref name="Status"/>.</param>
/// <param name="Icon">The glyph of the widget, used for alerts that bring none of their own.</param>
public sealed record WidgetReport(
    WidgetStatus? Status,
    IReadOnlyList<WidgetAlert> Alerts,
    WidgetStatusCard? Card = null,
    string? WidgetId = null,
    IReadOnlyList<WidgetStatus>? AdditionalStatuses = null,
    string? Icon = null);

/// <summary>
/// Backs the always-visible status bar: a chip per widget entry, plus a card for every error. Warnings are
/// only counted, because the bar must stay small enough to ignore while everything works.
/// </summary>
public sealed partial class StatusBarViewModel : ObservableObject
{
    public const int MaxAlertCards = 3;

    // What a card shows when neither its alert nor its widget says what it is about.
    private const string FallbackIcon = "";

    // Cards the user put away, with the time they return on their own; null keeps a card away for as long
    // as its alert says the same thing.
    private readonly Dictionary<string, DateTimeOffset?> _hidden = [];
    private (IReadOnlyList<WidgetReport> Reports, DateTimeOffset Now, string PanelHotkeyText, string? Notice)? _lastUpdate;

    [ObservableProperty]
    private string? _moreHintsCount;

    [ObservableProperty]
    private string _moreHintsText = string.Empty;

    [ObservableProperty]
    private bool _hasAlerts;

    /// <summary>Shown when the pointer rests on the logo; the bar itself does not advertise the hotkey.</summary>
    [ObservableProperty]
    private string _detailsToolTip = string.Empty;

    /// <summary>A message from the shell itself, e.g. a pending update.</summary>
    [ObservableProperty]
    private string? _notice;

    public ObservableCollection<StatusChipViewModel> Chips { get; } = [];

    public ObservableCollection<AlertCardViewModel> Alerts { get; } = [];

    /// <summary>What widgets keep in view on the user's request, shown below everything that reports a problem.</summary>
    public ObservableCollection<StatusCardViewModel> Cards { get; } = [];

    /// <summary>Raised when a card was clicked, with the id of the widget whose page should be shown, if any.</summary>
    public event EventHandler<string?>? WidgetRequested;

    /// <summary>Raised when the bar itself was clicked.</summary>
    public event EventHandler? PanelToggleRequested;

    /// <summary>Raised when the cards the user put away changed, for the shell to remember them.</summary>
    public event EventHandler? HiddenAlertsChanged;

    public IReadOnlyDictionary<string, DateTimeOffset?> HiddenAlerts => _hidden;

    /// <summary>A card is the same card as long as the same widget says the same thing in its title.</summary>
    public static string GetAlertKey(string? widgetId, WidgetAlert alert) => $"{widgetId}|{alert.Title}";

    /// <summary>Takes over the cards that were put away in an earlier session.</summary>
    public void SetHiddenAlerts(IReadOnlyDictionary<string, DateTimeOffset?> hidden)
    {
        _hidden.Clear();
        foreach (var (key, until) in hidden)
        {
            _hidden[key] = until;
        }
    }

    public void Update(IReadOnlyList<WidgetReport> reports, DateTimeOffset now, string panelHotkeyText, string? notice)
    {
        _lastUpdate = (reports, now, panelHotkeyText, notice);
        ForgetWhatIsOver(reports, now);

        Replace(Chips, reports.SelectMany(report =>
        {
            var isAcknowledged = IsAcknowledged(report);
            return new[] { report.Status }.Concat(report.AdditionalStatuses ?? []).OfType<WidgetStatus>().Select(status =>
                new StatusChipViewModel(status.Label, status.Summary, status.Severity, isAcknowledged && status.Severity != WidgetSeverity.Ok));
        }));

        Replace(Cards, reports.Where(report => report.Card is not null).Select(report => new StatusCardViewModel(report.Card!, report.WidgetId)));

        var alerts = reports.SelectMany(report => report.Alerts.Select(alert => (Alert: alert, Report: report))).ToList();

        // Errors always get a card, warnings only if their widget asks for it; errors come first.
        // A card that was put away is gone from the bar only: its chip keeps its colour and the panel still tells.
        var errors = alerts
            .Where(entry => IsCardWorthy(entry.Alert) && !_hidden.ContainsKey(GetAlertKey(entry.Report.WidgetId, entry.Alert)))
            .OrderByDescending(entry => entry.Alert.Severity)
            .ThenBy(entry => entry.Alert.Since)
            .ToList();
        Replace(Alerts, errors.Take(MaxAlertCards).Select(entry => new AlertCardViewModel(
            entry.Alert.Title,
            entry.Alert.Detail,
            FormatAge(now - entry.Alert.Since),
            entry.Report.WidgetId,
            entry.Alert.Severity,
            GetAlertKey(entry.Report.WidgetId, entry.Alert),
            entry.Alert.Icon ?? entry.Report.Icon ?? FallbackIcon)));

        var hiddenErrors = errors.Count - Alerts.Count;
        var warnings = alerts.Count(entry => !IsCardWorthy(entry.Alert));
        MoreHintsCount = hiddenErrors + warnings > 0 ? $"+{hiddenErrors + warnings}" : null;
        MoreHintsText = (hiddenErrors, warnings) switch
        {
            (0, 1) => "more hint – only in the panel",
            (0, _) => "more hints – only in the panel",
            _ => "more – see the panel",
        };

        HasAlerts = alerts.Count > 0;
        DetailsToolTip = $"Details ({panelHotkeyText})";
        Notice = notice;
    }

    public static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        return age switch
        {
            { TotalHours: < 1 } => $"{(int)age.TotalMinutes} min ago",
            { TotalDays: < 1 } => $"{(int)age.TotalHours} h ago",
            _ => $"{(int)age.TotalDays} d ago",
        };
    }

    [RelayCommand]
    private void OpenWidget(string? widgetId) => WidgetRequested?.Invoke(this, widgetId);

    [RelayCommand]
    private void OpenAlert(AlertCardViewModel card) => WidgetRequested?.Invoke(this, card.WidgetId);

    [RelayCommand]
    private void TogglePanel() => PanelToggleRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Puts a card away until its alert has something new to say.</summary>
    [RelayCommand]
    private void DismissAlert(AlertCardViewModel card) => Hide(card, until: null);

    /// <summary>Puts a card away for an hour, whatever happens in between.</summary>
    [RelayCommand]
    private void MuteAlertForAnHour(AlertCardViewModel card) => Hide(card, (_lastUpdate?.Now ?? DateTimeOffset.Now).AddHours(1));

    /// <summary>Puts a card away for the rest of the day.</summary>
    [RelayCommand]
    private void MuteAlertUntilTomorrow(AlertCardViewModel card)
    {
        var now = _lastUpdate?.Now ?? DateTimeOffset.Now;
        Hide(card, new DateTimeOffset(now.Date.AddDays(1), now.Offset));
    }

    private static bool IsCardWorthy(WidgetAlert alert) => alert.Severity == WidgetSeverity.Error || alert.Prominent;

    /// <summary>Whether the widget has cards to show and the user has put every one of them away.</summary>
    private bool IsAcknowledged(WidgetReport report)
    {
        var cards = report.Alerts.Where(IsCardWorthy).ToList();
        return cards.Count > 0 && cards.All(alert => _hidden.ContainsKey(GetAlertKey(report.WidgetId, alert)));
    }

    private void Hide(AlertCardViewModel card, DateTimeOffset? until)
    {
        _hidden[card.Key] = until;
        if (_lastUpdate is { } last)
        {
            Update(last.Reports, last.Now, last.PanelHotkeyText, last.Notice);
        }

        HiddenAlertsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A card whose time is up returns, and an alert that is over is forgotten, so that it shows again
    /// should it come back. Only widgets that have reported are judged: right after the start none has,
    /// and nothing may be forgotten then.
    /// </summary>
    private void ForgetWhatIsOver(IReadOnlyList<WidgetReport> reports, DateTimeOffset now)
    {
        var reporting = reports.Where(report => report.Status is not null || report.Alerts.Count > 0).ToList();
        var current = reporting.SelectMany(report => report.Alerts.Select(alert => GetAlertKey(report.WidgetId, alert))).ToHashSet();
        var prefixes = reporting.Select(report => $"{report.WidgetId}|").ToList();
        var over = _hidden
            .Where(entry => entry.Value <= now
                || (!current.Contains(entry.Key) && prefixes.Any(prefix => entry.Key.StartsWith(prefix, StringComparison.Ordinal))))
            .Select(entry => entry.Key)
            .ToList();
        if (over.Count == 0)
        {
            return;
        }

        foreach (var key in over)
        {
            _hidden.Remove(key);
        }

        HiddenAlertsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        var updated = items.ToList();

        // The bar is refreshed often; leaving an unchanged list alone avoids needless redraws.
        if (collection.SequenceEqual(updated))
        {
            return;
        }

        collection.Clear();
        foreach (var item in updated)
        {
            collection.Add(item);
        }
    }
}
