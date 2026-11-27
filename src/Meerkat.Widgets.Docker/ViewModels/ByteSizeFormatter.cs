using System.Globalization;

namespace Meerkat.Widgets.Docker.ViewModels;

public static class ByteSizeFormatter
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB"];

    public static string Format(long bytes, CultureInfo? culture = null)
    {
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < Units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        var format = unitIndex == 0 ? "0" : "0.0";
        return $"{value.ToString(format, culture ?? CultureInfo.CurrentCulture)} {Units[unitIndex]}";
    }
}
