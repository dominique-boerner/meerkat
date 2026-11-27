using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meerkat.Widgets.Docker.Containers;

namespace Meerkat.Widgets.Docker.ViewModels;

public sealed partial class LogPanelViewModel : ObservableObject
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);

    private readonly IContainerProvider _provider;
    private readonly string _containerId;
    private readonly Action _close;

    // Holds everything received (bounded); Lines is the filtered projection shown in the list.
    private readonly Queue<LogLine> _buffer = new();

    private CancellationTokenSource? _cancellation;

    [ObservableProperty]
    private ObservableCollection<LogLine> _lines = [];

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _showStdout = true;

    [ObservableProperty]
    private bool _showStderr = true;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private bool _isLive;

    /// <summary>Set by the view: true while the list is scrolled to the end and follows new lines.</summary>
    [ObservableProperty]
    private bool _isFollowing = true;

    [ObservableProperty]
    private string _lineCountText = string.Empty;

    public LogPanelViewModel(
        IContainerProvider provider,
        string containerId,
        string containerName,
        int maxLines,
        string commandText,
        Action close)
    {
        _provider = provider;
        _containerId = containerId;
        _close = close;
        ContainerName = containerName;
        MaxLines = maxLines;
        CommandText = commandText;
        UpdateLineCount();
    }

    public string ContainerName { get; }

    public int MaxLines { get; }

    public string CommandText { get; }

    public void Start()
    {
        if (_cancellation is not null)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        _ = PumpAsync(_cancellation.Token);
    }

    public void Stop()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        IsLive = false;
    }

    public void Append(IReadOnlyList<LogLine> batch)
    {
        foreach (var line in batch)
        {
            _buffer.Enqueue(line);
            if (!IsPaused && Matches(line))
            {
                Lines.Add(line);
            }

            if (_buffer.Count > MaxLines)
            {
                var dropped = _buffer.Dequeue();
                if (!IsPaused && Lines.Count > 0 && ReferenceEquals(Lines[0], dropped))
                {
                    Lines.RemoveAt(0);
                }
            }
        }

        UpdateLineCount();
    }

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    [RelayCommand]
    private void Clear()
    {
        _buffer.Clear();
        Rebuild();
    }

    [RelayCommand]
    private void Close() => _close();

    partial void OnFilterTextChanged(string value) => Rebuild();

    partial void OnShowStdoutChanged(bool value) => Rebuild();

    partial void OnShowStderrChanged(bool value) => Rebuild();

    partial void OnIsPausedChanged(bool value)
    {
        // While paused the visible list is frozen; resuming catches up with everything buffered meanwhile.
        if (!value)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        Lines = new ObservableCollection<LogLine>(_buffer.Where(Matches));
        UpdateLineCount();
    }

    private bool Matches(LogLine line) =>
        (line.IsError ? ShowStderr : ShowStdout)
        && (FilterText.Length == 0 || line.Text.Contains(FilterText, StringComparison.OrdinalIgnoreCase));

    private void UpdateLineCount()
    {
        var culture = CultureInfo.CurrentCulture;
        LineCountText = $"{Lines.Count.ToString("N0", culture)} of max. {MaxLines.ToString("N0", culture)} lines";
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        // Lines are read off the UI thread and handed over in batches, so a chatty container
        // costs one UI update per flush interval instead of one per line.
        var channel = Channel.CreateBounded<LogLine>(MaxLines);
        _ = Task.Run(() => ReadStreamAsync(channel.Writer, cancellationToken), CancellationToken.None);
        IsLive = true;

        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken))
            {
                var batch = new List<LogLine>();
                while (batch.Count < MaxLines && channel.Reader.TryRead(out var line))
                {
                    batch.Add(line);
                }

                Append(batch);
                await Task.Delay(FlushInterval, cancellationToken);
            }

            Append([new LogLine(DateTimeOffset.Now, "Log stream ended", IsError: false)]);
        }
        catch (OperationCanceledException)
        {
            // Panel was closed.
        }
        catch (Exception exception)
        {
            Append([new LogLine(DateTimeOffset.Now, $"Log stream failed: {exception.Message}", IsError: true)]);
        }

        IsLive = false;
    }

    private async Task ReadStreamAsync(ChannelWriter<LogLine> writer, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var line in _provider.StreamLogsAsync(_containerId, cancellationToken).ConfigureAwait(false))
            {
                await writer.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            }

            writer.TryComplete();
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception);
        }
    }
}
