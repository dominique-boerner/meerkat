using System.Globalization;
using System.IO;

namespace Meerkat.App.Diagnostics;

public sealed class ErrorLog(string filePath)
{
    private readonly Lock _gate = new();

    public void Write(string context, Exception exception)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                var timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
                File.AppendAllText(filePath, $"{timestamp} {context}: {exception}{Environment.NewLine}");
            }
        }
        catch (Exception logFailure) when (logFailure is IOException or UnauthorizedAccessException)
        {
            // Logging is best effort; failing to log must never become a second failure.
        }
    }
}
