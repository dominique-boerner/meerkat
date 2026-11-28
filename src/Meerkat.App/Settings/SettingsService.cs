using System.IO;
using System.Text.Json;
using Meerkat.Abstractions;

namespace Meerkat.App.Settings;

public sealed class SettingsService : IWidgetSettingsStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    // An editor writes a file in several steps; the file is read once they have settled.
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(400);

    private readonly string _filePath;
    private FileSystemWatcher? _watcher;
    private Timer? _settleTimer;

    /// <summary>What the file held when it was last read or written; tells own writes from those of others.</summary>
    private string? _knownText;

    public SettingsService(string filePath)
    {
        _filePath = filePath;
        Current = TryRead(out var settings) ? settings : new OverlaySettings();
    }

    public event EventHandler? Reloaded;

    public OverlaySettings Current { get; private set; }

    public string FilePath => _filePath;

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        // Write to a temp file first so a crash mid-write cannot leave a truncated settings file behind.
        var tempPath = _filePath + ".tmp";
        var text = JsonSerializer.Serialize(Current, SerializerOptions);
        _knownText = text;
        File.WriteAllText(tempPath, text);
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

    /// <summary>
    /// Takes over what somebody else wrote into the file, such as the user with an editor. A file that is
    /// no valid JSON, as it may be halfway through an edit, is left alone until it is.
    /// </summary>
    /// <returns>True if the settings were replaced.</returns>
    public bool ReloadIfChanged()
    {
        var known = _knownText;
        if (!TryRead(out var settings) || _knownText == known)
        {
            _knownText = known;
            return false;
        }

        Current = settings;
        Reloaded?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Follows changes made to the file from outside.</summary>
    /// <param name="dispatch">Runs the reload where the settings are used, which is the UI thread.</param>
    public void Watch(Action<Action> dispatch)
    {
        if (_watcher is not null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        _settleTimer = new Timer(_ => dispatch(() => ReloadIfChanged()));
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(_filePath)!, Path.GetFileName(_filePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        FileSystemEventHandler onChanged = (_, _) => _settleTimer?.Change(SettleTime, Timeout.InfiniteTimeSpan);
        _watcher.Changed += onChanged;
        _watcher.Created += onChanged;
        _watcher.Renamed += (_, _) => _settleTimer?.Change(SettleTime, Timeout.InfiniteTimeSpan);
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _settleTimer?.Dispose();
    }

    private bool TryRead(out OverlaySettings settings)
    {
        settings = null!;
        try
        {
            if (!File.Exists(_filePath))
            {
                return false;
            }

            var text = File.ReadAllText(_filePath);
            if (JsonSerializer.Deserialize<OverlaySettings>(text, SerializerOptions) is not { } parsed)
            {
                return false;
            }

            _knownText = text;
            settings = parsed;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // A broken settings file must not prevent startup; defaults are used until the next save.
            return false;
        }
    }
}
