using System.Diagnostics.CodeAnalysis;
using System.Windows.Input;

namespace Meerkat.App.Hotkeys;

public readonly record struct HotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    private static readonly Dictionary<string, ModifierKeys> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = ModifierKeys.Control,
        ["Control"] = ModifierKeys.Control,
        ["Alt"] = ModifierKeys.Alt,
        ["Shift"] = ModifierKeys.Shift,
        ["Win"] = ModifierKeys.Windows,
        ["Windows"] = ModifierKeys.Windows,
    };

    public static bool TryParse([NotNullWhen(true)] string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        var parts = (text ?? string.Empty).Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts.Any(part => part.Length == 0))
        {
            return false;
        }

        var modifiers = ModifierKeys.None;
        foreach (var part in parts[..^1])
        {
            if (!ModifierNames.TryGetValue(part, out var modifier))
            {
                return false;
            }

            modifiers |= modifier;
        }

        if (!TryParseKey(parts[^1], out var key))
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, key);
        return true;
    }

    private static readonly Key[] ModifierOnlyKeys =
    [
        Key.LeftCtrl, Key.RightCtrl, Key.LeftAlt, Key.RightAlt, Key.LeftShift, Key.RightShift, Key.LWin, Key.RWin,
    ];

    /// <summary>
    /// Builds a gesture from a key press. A global hotkey needs a modifier and a key that is not itself
    /// a modifier; anything else would fire during normal typing.
    /// </summary>
    public static bool TryCreate(Key key, ModifierKeys modifiers, out HotkeyGesture gesture)
    {
        gesture = new HotkeyGesture(modifiers, key);
        return modifiers != ModifierKeys.None && key != Key.None && !ModifierOnlyKeys.Contains(key);
    }

    /// <summary>The held modifiers as text, e.g. "Ctrl+Alt"; empty when none is held.</summary>
    public static string FormatModifiers(ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        return string.Join('+', parts);
    }

    /// <summary>Normalizes stored text for display; falls back to the raw text if it cannot be parsed.</summary>
    public static string ToDisplayText(string storedText) =>
        TryParse(storedText, out var gesture) ? gesture.ToString() : storedText;

    public override string ToString()
    {
        var keyName = Key is >= Key.D0 and <= Key.D9 ? ((int)(Key - Key.D0)).ToString() : Key.ToString();
        return Modifiers == ModifierKeys.None ? keyName : $"{FormatModifiers(Modifiers)}+{keyName}";
    }

    private static bool TryParseKey(string text, out Key key)
    {
        if (text.Length == 1 && char.IsAsciiDigit(text[0]))
        {
            key = Key.D0 + (text[0] - '0');
            return true;
        }

        // Enum.TryParse would also accept raw numeric values, which are not meaningful key names.
        if (char.IsAsciiDigit(text[0]) || !Enum.TryParse(text, ignoreCase: true, out key) || key == Key.None)
        {
            key = Key.None;
            return false;
        }

        return true;
    }
}
