using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Meerkat.Abstractions;

namespace Meerkat.App.Overlay;

public sealed record StatusChipViewModel(string Label, string Summary, WidgetSeverity Severity);

public sealed record AlertCardViewModel(string Title, string Detail, string AgeText);

/// <summary>What one widget currently reports to the shell.</summary>
public sealed record WidgetReport(WidgetStatus? Status, IReadOnlyList<WidgetAlert> Alerts, WidgetStatusCard? Card = null);

/// <summary>
/// Backs the always-visible status bar: one chip per widget, plus a card for every error. Warnings are
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

    [ObservableProperty]
    private string _detailsHint = string.Empty;

    /// <summary>A message from the shell itself, e.g. a pending update.</summary>
    [ObservableProperty]
    private string? _notice;

    public ObservableCollection<StatusChipViewModel> Chips { get; } = [];

    public ObservableCollection<AlertCardViewModel> Alerts { get; } = [];

    /// <summary>What widgets keep in view on the user's request, shown below everything that reports a problem.</summary>
    public ObservableCollection<WidgetStatusCard> Cards { get; } = [];

    public void Update(IReadOnlyList<WidgetReport> reports, DateTimeOffset now, string panelHotkeyText, string? notice)
    {
        Replace(Chips, reports
            .Select(report => report.Status)
            .OfType<WidgetStatus>()
            .Select(status => new StatusChipViewModel(status.Label, status.Summary, status.Severity)));

        Replace(Cards, reports.Select(report => report.Card).OfType<WidgetStatusCard>());

        var alerts = reports.SelectMany(report => report.Alerts).ToList();
        var errors = alerts.Where(alert => alert.Severity == WidgetSeverity.Error).OrderBy(alert => alert.Since).ToList();
        Replace(Alerts, errors.Take(MaxAlertCards).Select(alert => new AlertCardViewModel(alert.Title, alert.Detail, FormatAge(now - alert.Since))));

        var hiddenErrors = errors.Count - Alerts.Count;
        var warnings = alerts.Count(alert => alert.Severity == WidgetSeverity.Warning);
        MoreHintsCount = hiddenErrors + warnings > 0 ? $"+{hiddenErrors + warnings}" : null;
        MoreHintsText = (hiddenErrors, warnings) switch
        {
            (0, 1) => "more hint – only in the panel",
            (0, _) => "more hints – only in the panel",
            _ => "more – see the panel",
        };

        HasAlerts = alerts.Count > 0;
        DetailsHint = $"Details: {panelHotkeyText}";
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
