using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Meerkat.Widgets.Docker.Containers;

namespace Meerkat.Widgets.Docker.ViewModels;

public enum ContainerStatusKind
{
    Running,
    Restarting,
    Paused,
    Stopped,
}

public sealed partial class ContainerRowViewModel : ObservableObject
{
    private const string NoValue = "–";

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _image = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusKind))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsStopped))]
    private string _state = string.Empty;

    /// <summary>Docker's own status phrase (e.g. "Up 2 hours"), shown as tooltip.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private double _cpuPercent;

    [ObservableProperty]
    private string _cpuText = NoValue;

    [ObservableProperty]
    private string _memoryText = NoValue;

    [ObservableProperty]
    private string _memoryPercentText = string.Empty;

    [ObservableProperty]
    private bool _isLogTarget;

    public ContainerRowViewModel(ContainerInfo info)
    {
        Id = info.Id;
        Apply(info);
    }

    public string Id { get; }

    public ContainerStatusKind StatusKind => State.ToLowerInvariant() switch
    {
        "running" => ContainerStatusKind.Running,
        "restarting" => ContainerStatusKind.Restarting,
        "paused" => ContainerStatusKind.Paused,
        _ => ContainerStatusKind.Stopped,
    };

    public string StatusText => StatusKind switch
    {
        ContainerStatusKind.Running => "running",
        ContainerStatusKind.Restarting => "restarting",
        ContainerStatusKind.Paused => "paused",
        _ => "stopped",
    };

    public bool IsRunning => StatusKind == ContainerStatusKind.Running;

    public bool IsStopped => !IsRunning;

    public void Apply(ContainerInfo info)
    {
        Name = info.Name;
        Image = info.Image;
        State = info.State;
        Status = info.Status;
    }

    public void ApplyStats(ContainerStats? stats)
    {
        var culture = CultureInfo.CurrentCulture;
        CpuPercent = Math.Clamp(stats?.CpuPercent ?? 0, 0, 100);
        CpuText = stats?.CpuPercent is { } cpu ? FormatPercent(cpu, culture) : NoValue;
        MemoryText = stats?.MemoryUsageBytes is { } usage ? ByteSizeFormatter.Format(usage, culture) : NoValue;
        MemoryPercentText = stats is { MemoryUsageBytes: not null, MemoryPercent: { } percent }
            ? FormatPercent(percent, culture)
            : string.Empty;
    }

    public bool Matches(ContainerStats stats) =>
        (stats.Id.Length > 0 && (Id.StartsWith(stats.Id, StringComparison.Ordinal)
                                 || stats.Id.StartsWith(Id, StringComparison.Ordinal)))
        || (stats.Name.Length > 0 && stats.Name == Name);

    private static string FormatPercent(double value, CultureInfo culture) => $"{value.ToString("0.0", culture)} %";
}
