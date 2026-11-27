using System.IO;
using System.Security.Cryptography;

namespace Meerkat.App.Updates;

public static class ChecksumFile
{
    /// <summary>Finds the hash of a file in "sha256sum" format: one "&lt;hash&gt;  &lt;file name&gt;" entry per line.</summary>
    public static string? FindHash(string content, string fileName)
    {
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == fileName)
            {
                return parts[0];
            }
        }

        return null;
    }

    public static string ComputeHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
