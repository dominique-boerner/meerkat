using System.IO;

namespace Meerkat.App;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Meerkat");

    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "settings.json");

    public static string ErrorLogFile { get; } = Path.Combine(DataDirectory, "error.log");

    /// <summary>Where the user installs plugins. Programs do not belong into the roaming profile the settings are in.</summary>
    public static string UserPluginDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Meerkat",
        "plugins");

    /// <summary>
    /// The folders plugins are looked for in: the user's own, and one next to the program, where an
    /// administrator can install plugins for everybody who uses the computer.
    /// </summary>
    public static IReadOnlyList<string> PluginDirectories { get; } = [UserPluginDirectory, Path.Combine(AppContext.BaseDirectory, "plugins")];

    /// <summary>Settings path as users know it from Explorer, without their profile directory.</summary>
    public static string SettingsFileDisplayPath => Path.Combine("%AppData%", "Meerkat", "settings.json");
}
