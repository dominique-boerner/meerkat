using System.IO;
using System.Text.Json;
using Meerkat.Abstractions;

namespace Meerkat.App.Settings;

public sealed class SettingsService : IWidgetSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _filePath;

    public SettingsService(string filePath)
    {
        _filePath = filePath;
        Current = Read(filePath);
    }

    public OverlaySettings Current { get; }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        // Write to a temp file first so a crash mid-write cannot leave a truncated settings file behind.
        var tempPath = _filePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(Current, SerializerOptions));
        File.Move(tempPath, _filePath, overwrite: true);
    }

    public T Load<T>(string widgetId)
        where T : class, new()
    {
        if (!Current.WidgetSettings.TryGetValue(widgetId, out var element))
        {
            return new T();
        }

        try
        {
            return element.Deserialize<T>(SerializerOptions) ?? new T();
        }
        catch (JsonException)
        {
            return new T();
        }
    }

    public void Save<T>(string widgetId, T settings)
        where T : class
    {
        Current.WidgetSettings[widgetId] = JsonSerializer.SerializeToElement(settings, SerializerOptions);
        Save();
    }

    private static OverlaySettings Read(string filePath)
    {
        try
        {
            return File.Exists(filePath)
                ? JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(filePath), SerializerOptions) ?? new OverlaySettings()
                : new OverlaySettings();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // A broken settings file must not prevent startup; defaults are used until the next save.
            return new OverlaySettings();
        }
    }
}
