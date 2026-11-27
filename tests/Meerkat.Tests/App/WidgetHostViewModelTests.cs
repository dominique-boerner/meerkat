using System.Windows;
using Meerkat.App.Overlay;
using Meerkat.App.Settings;

namespace Meerkat.Tests.App;

public class WidgetHostViewModelTests
{
    private static readonly Size Bounds = new(1000, 800);

    [Fact]
    public void MoveTo_KeepsWidgetInsideBounds()
    {
        var host = CreateHost();

        host.MoveTo(5000, -5000, Bounds);

        Assert.Equal(700, host.Left);
        Assert.Equal(0, host.Top);
    }

    [Fact]
    public void ResizeBy_RespectsMinimumSizeAndBounds()
    {
        var host = CreateHost();

        host.ResizeBy(-5000, 5000, Bounds);

        Assert.Equal(WidgetHostViewModel.MinWidth, host.Width);
        Assert.Equal(700, host.Height);
    }

    [Fact]
    public void ClampTo_PullsWidgetBackOntoSmallerMonitor()
    {
        var host = CreateHost();
        host.ApplyLayout(new WidgetLayout { Left = 1500, Top = 900, Width = 600, Height = 400 });

        host.ClampTo(new Size(500, 300));

        Assert.Equal(0, host.Left);
        Assert.Equal(0, host.Top);
        Assert.Equal(500, host.Width);
        Assert.Equal(300, host.Height);
    }

    [Fact]
    public void OverlayViewModel_PropagatesModeAndClampsNewHosts()
    {
        var overlay = new OverlayViewModel { IsInteractive = true };
        overlay.SetBounds(new Size(500, 300));
        var host = CreateHost();
        host.ApplyLayout(new WidgetLayout { Left = 400, Top = 250, Width = 300, Height = 200 });

        overlay.AddHost(host);
        Assert.True(host.IsInteractive);
        Assert.Equal(200, host.Left);
        Assert.Equal(100, host.Top);

        overlay.IsInteractive = false;
        Assert.False(host.IsInteractive);
    }

    private static WidgetHostViewModel CreateHost()
    {
        var layout = new WidgetLayout { Left = 100, Top = 100, Width = 300, Height = 200 };
        var host = new WidgetHostViewModel("test", "Test", layout);
        host.ApplyLayout(layout);
        return host;
    }
}
