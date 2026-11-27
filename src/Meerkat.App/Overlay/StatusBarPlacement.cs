using System.Windows;

namespace Meerkat.App.Overlay;

/// <summary>
/// Where the status bar sits on the screen. The position is kept as fractions of the distance the bar
/// can travel between the screen edges, not in pixels: 0/0 is the top left corner, 1/1 the bottom right.
/// That way it means the same place on every monitor, resolution and scaling.
/// </summary>
/// <param name="GrowsLeft">The bar is in the right half, so the cards extend from its right edge to the left.</param>
/// <param name="GrowsUp">The bar is in the lower half, so the cards stack above it instead of below.</param>
/// <param name="Margin">Distance of the bar and its cards from the two screen edges they are anchored to.</param>
public sealed record StatusBarPlacement(bool GrowsLeft, bool GrowsUp, Thickness Margin)
{
    public const double DefaultX = 1;
    public const double DefaultY = 0;

    private const double HorizontalEdge = 16;
    private const double VerticalEdge = 12;

    public static StatusBarPlacement Compute(double x, double y, Size area, Size bar)
    {
        x = Normalize(x, DefaultX);
        y = Normalize(y, DefaultY);
        var left = HorizontalEdge + (x * TravelX(area, bar));
        var top = VerticalEdge + (y * TravelY(area, bar));

        // The cards grow towards the middle of the screen, so they never leave it wherever the bar is.
        var growsLeft = x >= 0.5;
        var growsUp = y >= 0.5;
        return new StatusBarPlacement(
            growsLeft,
            growsUp,
            new Thickness(
                growsLeft ? 0 : left,
                growsUp ? 0 : top,
                growsLeft ? Math.Max(0, area.Width - left - bar.Width) : 0,
                growsUp ? Math.Max(0, area.Height - top - bar.Height) : 0));
    }

    /// <summary>Turns the top left corner of the bar, as it is being dragged, back into the stored fractions.</summary>
    public static (double X, double Y) FromBarPosition(Point topLeft, Size area, Size bar) => (
        ToFraction(topLeft.X - HorizontalEdge, TravelX(area, bar), DefaultX),
        ToFraction(topLeft.Y - VerticalEdge, TravelY(area, bar), DefaultY));

    private static double TravelX(Size area, Size bar) => Math.Max(0, area.Width - bar.Width - (2 * HorizontalEdge));

    private static double TravelY(Size area, Size bar) => Math.Max(0, area.Height - bar.Height - (2 * VerticalEdge));

    private static double ToFraction(double offset, double travel, double fallback) =>
        travel > 0 ? Math.Clamp(offset / travel, 0, 1) : fallback;

    private static double Normalize(double value, double fallback) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : fallback;
}
