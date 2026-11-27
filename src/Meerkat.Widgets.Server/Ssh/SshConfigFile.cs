using System.IO;

namespace Meerkat.Widgets.Server.Ssh;

public static class SshConfigFile
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");

    /// <summary>Returns the host aliases a user can connect to by name; patterns such as "*" are no hosts.</summary>
    public static IReadOnlyList<string> ReadHostAliases(string path)
    {
        try
        {
            return File.Exists(path) ? ParseHostAliases(File.ReadAllLines(path)) : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static IReadOnlyList<string> ParseHostAliases(IEnumerable<string> lines)
    {
        var aliases = new List<string>();
        foreach (var line in lines)
        {
            // The keyword may be followed by spaces or an equals sign: "Host a b" and "Host=a" are both valid.
            var parts = line.Trim().Split([' ', '\t', '='], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[0].Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            aliases.AddRange(parts.Skip(1).Where(alias => !alias.Contains('*') && !alias.Contains('?') && !alias.StartsWith('!')));
        }

        return [.. aliases.Distinct(StringComparer.Ordinal)];
    }
}
