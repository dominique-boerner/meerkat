using System.IO;

namespace Meerkat.App;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Meerkat");

    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "settings.json");

    public static string ErrorLogFile { get; } = Path.Combine(DataDirectory, "error.log");

    /// <summary>Settings path as users know it from Explorer, without their profile directory.</summary>
    public static string SettingsFileDisplayPath => Path.Combine("%AppData%", "Meerkat", "settings.json");
}
