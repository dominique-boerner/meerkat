using System.Runtime.CompilerServices;
using Meerkat.Abstractions;
using Meerkat.Widgets.Docker.Containers;
using Meerkat.Widgets.Docker.Processes;

namespace Meerkat.Tests.Docker;

internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = [];

    public ProcessResult Result { get; set; } = new(0, string.Empty, string.Empty);

    public Exception? StartFailure { get; set; }

    public List<ProcessOutputLine> StreamLines { get; } = [];

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        Calls.Add((fileName, arguments));
        return StartFailure is null ? Task.FromResult(Result) : Task.FromException<ProcessResult>(StartFailure);
    }

    public async IAsyncEnumerable<ProcessOutputLine> StreamAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Calls.Add((fileName, arguments));
        foreach (var line in StreamLines)
        {
            await Task.Yield();
            yield return line;
        }
    }
}

internal sealed class FakeOverlayState : IOverlayState
{
    public bool IsVisible { get; private set; }

    public bool IsPanelOpen { get; private set; }

    public string PanelHotkeyText => "Ctrl+Alt+M";

    public event EventHandler? Changed;

    public void Set(bool isVisible, bool isPanelOpen = false)
    {
        IsVisible = isVisible;
        IsPanelOpen = isPanelOpen;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class FakeOverlayCommands : IOverlayCommands
{
    public int OpenSettingsCalls { get; private set; }

    public List<Uri> OpenedUrls { get; } = [];

    public void OpenSettings() => OpenSettingsCalls++;

    public void OpenUrl(Uri url) => OpenedUrls.Add(url);
}

internal sealed class InMemoryWidgetSettingsStore : IWidgetSettingsStore
{
    private readonly Dictionary<string, object> _values = [];

    public T Load<T>(string widgetId)
        where T : class, new() =>
        _values.TryGetValue(widgetId, out var value) ? (T)value : new T();

    public void Save<T>(string widgetId, T settings)
        where T : class =>
        _values[widgetId] = settings;
}

internal sealed class FakeContainerProvider : IContainerProvider
{
    private int _listCalls;

    public int ListCalls => Volatile.Read(ref _listCalls);

    public List<ContainerInfo> Containers { get; } = [];

    public List<ContainerStats> Stats { get; } = [];

    public List<string> Actions { get; } = [];

    public Exception? ListFailure { get; set; }

    public Task<IReadOnlyList<ContainerInfo>> ListAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _listCalls);
        return ListFailure is null
            ? Task.FromResult<IReadOnlyList<ContainerInfo>>([.. Containers])
            : Task.FromException<IReadOnlyList<ContainerInfo>>(ListFailure);
    }

    public Task<IReadOnlyList<ContainerStats>> GetStatsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ContainerStats>>([.. Stats]);

    public async IAsyncEnumerable<LogLine> StreamLogsAsync(
        string containerId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield break;
    }

    public Task StartAsync(string containerId, CancellationToken cancellationToken) => Record("start", containerId);

    public Task StopAsync(string containerId, CancellationToken cancellationToken) => Record("stop", containerId);

    public Task RestartAsync(string containerId, CancellationToken cancellationToken) => Record("restart", containerId);

    private Task Record(string action, string containerId)
    {
        Actions.Add($"{action} {containerId}");
        return Task.CompletedTask;
    }
}
