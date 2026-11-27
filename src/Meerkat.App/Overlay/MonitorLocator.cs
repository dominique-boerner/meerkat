using Meerkat.App.Interop;
using Meerkat.App.SettingsUi;
using WinForms = System.Windows.Forms;

namespace Meerkat.App.Overlay;

public static class MonitorLocator
{
    /// <summary>Working area in physical pixels; falls back to the primary monitor when the device is gone.</summary>
    public static System.Drawing.Rectangle ResolveWorkingArea(string? deviceName)
    {
        var screens = WinForms.Screen.AllScreens;
        var screen = screens.FirstOrDefault(candidate => candidate.DeviceName == deviceName)
            ?? WinForms.Screen.PrimaryScreen
            ?? screens[0];
        return screen.WorkingArea;
    }

    public static IReadOnlyList<MonitorOption> GetOptions()
    {
        var options = new List<MonitorOption> { new(null, "Primary monitor") };
        options.AddRange(WinForms.Screen.AllScreens
            .OrderBy(screen => screen.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Select(screen => new MonitorOption(screen.DeviceName, Describe(screen))));
        return options;
    }

    private static string Describe(WinForms.Screen screen)
    {
        var number = new string([.. screen.DeviceName.Where(char.IsAsciiDigit)]);
        var label = number.Length > 0 ? number : screen.DeviceName;
        return $"{label} – {screen.Bounds.Width} × {screen.Bounds.Height} ({GetScalePercent(screen)} %)";
    }

    private static int GetScalePercent(WinForms.Screen screen)
    {
        var center = new NativeMethods.NativePoint
        {
            X = screen.Bounds.Left + (screen.Bounds.Width / 2),
            Y = screen.Bounds.Top + (screen.Bounds.Height / 2),
        };
        var monitor = NativeMethods.MonitorFromPoint(center, NativeMethods.MonitorDefaultToNearest);
        return NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MdtEffectiveDpi, out var dpiX, out _) == 0
            ? (int)Math.Round(dpiX / NativeMethods.DefaultDpi * 100)
            : 100;
    }
}
