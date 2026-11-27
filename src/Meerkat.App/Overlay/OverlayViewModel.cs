using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Meerkat.App.Updates;

namespace Meerkat.App.Overlay;

public sealed partial class OverlayViewModel : ObservableObject
{
    private Size _bounds;

    [ObservableProperty]
    private bool _isInteractive;

    [ObservableProperty]
    private string _modeHotkeyText = string.Empty;

    /// <summary>Backs the update card; null where no update support is wired up.</summary>
    public UpdateViewModel? Update { get; init; }

    /// <summary>Widgets and panels currently shown on the overlay.</summary>
    public ObservableCollection<WidgetHostViewModel> Hosts { get; } = [];

    public void AddHost(WidgetHostViewModel host)
    {
        host.IsInteractive = IsInteractive;
        ClampToBounds(host);
        Hosts.Add(host);
    }

    public void RemoveHost(WidgetHostViewModel host) => Hosts.Remove(host);

    public void SetBounds(Size bounds)
    {
        _bounds = bounds;
        ClampAll();
    }

    public void ClampAll()
    {
        foreach (var host in Hosts)
        {
            ClampToBounds(host);
        }
    }

    partial void OnIsInteractiveChanged(bool value)
    {
        foreach (var host in Hosts)
        {
            host.IsInteractive = value;
        }
    }

    private void ClampToBounds(WidgetHostViewModel host)
    {
        // Before the window has been laid out there is nothing meaningful to clamp against.
        if (_bounds is { Width: > 0, Height: > 0 })
        {
            host.ClampTo(_bounds);
        }
    }
}
