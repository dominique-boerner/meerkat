using Microsoft.Win32;

namespace Meerkat.App.Startup;

/// <summary>Whether Meerkat starts on its own when the user signs in to Windows.</summary>
public interface IAutostart
{
    bool IsEnabled { get; }

    void SetEnabled(bool isEnabled);
}

/// <summary>
/// Uses the "Run" key of the current user, the same place Windows shows under "Startup apps". It needs no
/// administrator rights and no installer, and the user can switch the entry off there as well.
/// </summary>
/// <param name="executablePath">The program to start; Meerkat is a single file that may be placed anywhere.</param>
public sealed class RegistryAutostart(
    string executablePath,
    string runKeyPath = RegistryAutostart.DefaultRunKeyPath,
    string valueName = "Meerkat") : IAutostart
{
    public const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // The entry counts as set wherever it points to: the setting is the user's choice, not a statement about
    // this copy of the program. It is only written when the user changes it, so that running a second copy,
    // for example a development build, does not redirect the entry to itself.
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath);
            return key?.GetValue(valueName) is string { Length: > 0 };
        }
    }

    public void SetEnabled(bool isEnabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(runKeyPath);
        if (isEnabled)
        {
            // Quoted, because Windows splits the entry at spaces like a command line.
            key.SetValue(valueName, $"\"{executablePath}\"");
        }
        else
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }
}
