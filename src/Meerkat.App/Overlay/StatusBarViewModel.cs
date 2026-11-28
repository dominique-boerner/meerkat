using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meerkat.Abstractions;

namespace Meerkat.App.Overlay;

public sealed record StatusChipViewModel(string Label, string Summary, WidgetSeverity Severity);

/// <param name="WidgetId">The widget that reported the alert; its page opens when the card is clicked.</param>
public sealed record AlertCardViewModel(string Title, string Detail, string AgeText, string? WidgetId = null, WidgetSeverity Severity = WidgetSeverity.Error);

public sealed record StatusCardViewModel(WidgetStatusCard Card, string? WidgetId = null);

/// <summary>What one widget currently reports to the shell.</summary>
/// <param name="AdditionalStatuses">Further entries of the widget in the status bar, shown right after <paramref name="Status"/>.</param>
public sealed record WidgetReport(
    WidgetStatus? Status,
    IReadOnlyList<WidgetAlert> Alerts,
    WidgetStatusCard? Card = null,
    string? WidgetId = null,
    IReadOnlyList<WidgetStatus>? AdditionalStatuses = null);

/// <summary>
/// Backs the always-visible status bar: a chip per widget entry, plus a card for every error. Warnings are
/// only counted, because the bar must stay small enough to ignore while everything works.
/// </summary>
public sealed partial class StatusBarViewModel : ObservableObject
{
    public const int MaxAlertCards = 3;

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

    public void Update(IReadOnlyList<WidgetReport> reports, DateTimeOffset now, string panelHotkeyText, string? notice)
    {
        Replace(Chips, reports
            .SelectMany(report => new[] { report.Status }.Concat(report.AdditionalStatuses ?? []))
            .OfType<WidgetStatus>()
            .Select(status => new StatusChipViewModel(status.Label, status.Summary, status.Severity)));

        Replace(Cards, reports.Where(report => report.Card is not null).Select(report => new StatusCardViewModel(report.Card!, report.WidgetId)));

        var alerts = reports.SelectMany(report => report.Alerts.Select(alert => (Alert: alert, report.WidgetId))).ToList();

        // Errors always get a card, warnings only if their widget asks for it; errors come first.
        var errors = alerts
            .Where(entry => entry.Alert.Severity == WidgetSeverity.Error || entry.Alert.Prominent)
            .OrderByDescending(entry => entry.Alert.Severity)
            .ThenBy(entry => entry.Alert.Since)
            .ToList();
        Replace(Alerts, errors.Take(MaxAlertCards).Select(entry => new AlertCardViewModel(
            entry.Alert.Title,
            entry.Alert.Detail,
            FormatAge(now - entry.Alert.Since),
            entry.WidgetId,
            entry.Alert.Severity)));

        var hiddenErrors = errors.Count - Alerts.Count;
        var warnings = alerts.Count - errors.Count;
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

    [RelayCommand]
    private void OpenWidget(string? widgetId) => WidgetRequested?.Invoke(this, widgetId);

    [RelayCommand]
    private void TogglePanel() => PanelToggleRequested?.Invoke(this, EventArgs.Empty);

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
