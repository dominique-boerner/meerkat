using System.IO;
using System.Windows;
using System.Windows.Threading;
using Meerkat.Abstractions;

namespace Meerkat.Plugins.Sample;

/// <summary>
/// Watches the free space of the system drive. Meerkat finds the class because it is public and
/// implements <see cref="IOverlayWidget"/>, and hands the constructor the services it asks for.
/// </summary>
public sealed class DiskWidget(IOverlayState overlayState) : IOverlayWidget
{
    private const double WarningThresholdGigabytes = 20;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private DiskView? _view;
    private DateTimeOffset? _lowSince;

    public event EventHandler? StatusChanged;

    public string Id => "sample-disk";

    public string Title => "Disk";

    public string Icon => "\uEDA2";

    public FrameworkElement View => _view ??= new DiskView();

    public WidgetStatus? Status { get; private set; }

    public IReadOnlyList<WidgetAlert> Alerts { get; private set; } = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer.Tick += OnTick;
        _timer.Start();
        Refresh();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        return Task.CompletedTask;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Skip work nobody can see.
        if (overlayState.IsVisible)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C");
        var freeGigabytes = drive.AvailableFreeSpace / 1e9;
        var isLow = freeGigabytes < WarningThresholdGigabytes;
        _lowSince = isLow ? _lowSince ?? DateTimeOffset.Now : null;

        Status = new WidgetStatus("Disk", $"{freeGigabytes:0} GB", isLow ? WidgetSeverity.Warning : WidgetSeverity.Ok);
        Alerts = isLow
            ? [new WidgetAlert(WidgetSeverity.Warning, $"Drive {drive.Name} is almost full", $"{freeGigabytes:0} GB free", _lowSince!.Value)]
            : [];
        _view?.Show(drive.Name, freeGigabytes, drive.TotalSize / 1e9);

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}