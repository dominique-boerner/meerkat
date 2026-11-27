using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Meerkat.App.Updates;

/// <summary>A version as used in release tags, e.g. 1.2.0 or 1.2.0-beta.1.</summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string Prerelease = "")
    : IComparable<SemanticVersion>
{
    public bool IsPrerelease => Prerelease.Length > 0;

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    public static bool TryParse([NotNullWhen(true)] string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // Build metadata ("+commit") never takes part in precedence; a leading "v" comes from Git tags.
        var core = text.Trim().TrimStart('v', 'V').Split('+', 2)[0];
        var parts = core.Split('-', 2);
        var numbers = parts[0].Split('.');
        if (numbers.Length != 3
            || !TryParseNumber(numbers[0], out var major)
            || !TryParseNumber(numbers[1], out var minor)
            || !TryParseNumber(numbers[2], out var patch))
        {
            return false;
        }

        var prerelease = parts.Length > 1 ? parts[1] : string.Empty;
        if (parts.Length > 1 && prerelease.Length == 0)
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result == 0)
        {
            result = Minor.CompareTo(other.Minor);
        }

        if (result == 0)
        {
            result = Patch.CompareTo(other.Patch);
        }

        return result != 0 ? result : ComparePrerelease(Prerelease, other.Prerelease);
    }

    public override string ToString() =>
        IsPrerelease ? $"{Major}.{Minor}.{Patch}-{Prerelease}" : $"{Major}.{Minor}.{Patch}";

    private static int ComparePrerelease(string left, string right)
    {
        // A release ranks above every pre-release of the same version.
        if (left.Length == 0 || right.Length == 0)
        {
            return right.Length.CompareTo(left.Length);
        }

        var leftIdentifiers = left.Split('.');
        var rightIdentifiers = right.Split('.');
        for (var index = 0; index < Math.Min(leftIdentifiers.Length, rightIdentifiers.Length); index++)
        {
            var result = CompareIdentifier(leftIdentifiers[index], rightIdentifiers[index]);
            if (result != 0)
            {
                return result;
            }
        }

        return leftIdentifiers.Length.CompareTo(rightIdentifiers.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftIsNumber = TryParseNumber(left, out var leftNumber);
        var rightIsNumber = TryParseNumber(right, out var rightNumber);
        return (leftIsNumber, rightIsNumber) switch
        {
            (true, true) => leftNumber.CompareTo(rightNumber),
            (true, false) => -1,
            (false, true) => 1,
            _ => string.CompareOrdinal(left, right),
        };
    }

    private static bool TryParseNumber(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
