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

    /// <summary>Normalizes stored text for display; falls back to the raw text if it cannot be parsed.</summary>
    public static string ToDisplayText(string storedText) =>
        TryParse(storedText, out var gesture) ? gesture.ToString() : storedText;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(Key is >= Key.D0 and <= Key.D9 ? ((int)(Key - Key.D0)).ToString() : Key.ToString());
        return string.Join('+', parts);
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
