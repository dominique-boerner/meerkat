using System.Windows;
using Meerkat.App.Overlay;

namespace Meerkat.Tests.App;

public class StatusBarPlacementTests
{
    private static readonly Size Bar = new(300, 28);
    private static readonly Size FullHd = new(1920, 1040);
    private static readonly Size Ultrawide = new(3440, 1400);

    [Fact]
    public void Default_IsTheTopRightCorner()
    {
        var placement = StatusBarPlacement.Compute(StatusBarPlacement.DefaultX, StatusBarPlacement.DefaultY, FullHd, Bar);

        Assert.Equal(new StatusBarPlacement(GrowsLeft: true, GrowsUp: false, new Thickness(0, 12, 16, 0)), placement);
    }

    [Theory]
    [InlineData(0, 0, false, false, 16, 12, 0, 0)]
    [InlineData(0, 1, false, true, 16, 0, 0, 12)]
    [InlineData(1, 1, true, true, 0, 0, 16, 12)]
    public void Corners_KeepTheSameDistanceFromTheScreenEdges(double x, double y, bool growsLeft, bool growsUp, double left, double top, double right, double bottom)
    {
        var placement = StatusBarPlacement.Compute(x, y, FullHd, Bar);

        Assert.Equal(new StatusBarPlacement(growsLeft, growsUp, new Thickness(left, top, right, bottom)), placement);
    }

    [Fact]
    public void Position_MeansTheSamePlaceOnEveryScreenSize()
    {
        // Three quarters of the way to the right: left of the window buttons, on a small and on a wide screen.
        var small = StatusBarPlacement.Compute(0.75, 0, FullHd, Bar);
        var wide = StatusBarPlacement.Compute(0.75, 0, Ultrawide, Bar);

        Assert.Equal(0.25 * (1920 - 300 - 32), small.Margin.Right - 16, precision: 6);
        Assert.Equal(0.25 * (3440 - 300 - 32), wide.Margin.Right - 16, precision: 6);
        Assert.True(small.GrowsLeft && wide.GrowsLeft);
    }

    [Theory]
    [InlineData(0.2, 0.1)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.9, 0.95)]
    public void DraggedPosition_ConvertsBackToTheSameFractions(double x, double y)
    {
        var placement = StatusBarPlacement.Compute(x, y, FullHd, Bar);
        var topLeft = new Point(
            placement.GrowsLeft ? FullHd.Width - placement.Margin.Right - Bar.Width : placement.Margin.Left,
            placement.GrowsUp ? FullHd.Height - placement.Margin.Bottom - Bar.Height : placement.Margin.Top);

        var (actualX, actualY) = StatusBarPlacement.FromBarPosition(topLeft, FullHd, Bar);

        Assert.Equal(x, actualX, precision: 6);
        Assert.Equal(y, actualY, precision: 6);
    }

    [Fact]
    public void Dragging_StopsAtTheScreenEdges()
    {
        Assert.Equal((0, 0), StatusBarPlacement.FromBarPosition(new Point(-500, -500), FullHd, Bar));
        Assert.Equal((1, 1), StatusBarPlacement.FromBarPosition(new Point(5000, 5000), FullHd, Bar));
    }

    [Fact]
    public void BrokenValues_FallBackToTheDefault()
    {
        var placement = StatusBarPlacement.Compute(double.NaN, double.PositiveInfinity, FullHd, Bar);

        Assert.Equal(StatusBarPlacement.Compute(1, 0, FullHd, Bar), placement);
        Assert.Equal(StatusBarPlacement.Compute(1, 0, FullHd, Bar), StatusBarPlacement.Compute(7, -3, FullHd, Bar));
    }
}
