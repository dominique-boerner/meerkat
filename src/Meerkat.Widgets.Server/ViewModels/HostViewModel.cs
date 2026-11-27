using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Meerkat.Abstractions;
using Meerkat.Widgets.Server.Monitoring;
using Meerkat.Widgets.Server.Settings;
using Meerkat.Widgets.Server.Ssh;

namespace Meerkat.Widgets.Server.ViewModels;

public enum HostState
{
    /// <summary>Not checked yet.</summary>
    Pending,
    Online,
    Unreachable,
    AuthenticationFailed,
    HostKeyChanged,
    HostKeyUnknown,
    Paused,
}

public sealed record ServiceChipViewModel(string Name, string StateText, bool IsActive);

public sealed partial class HostViewModel : ObservableObject
{
    private const double BytesPerGibibyte = 1024d * 1024 * 1024;
    private const double BytesPerGigabyte = 1e9;

    private static readonly TimeSpan HistoryWindow = TimeSpan.FromMinutes(30);

    private readonly List<(DateTimeOffset Time, double Percent)> _cpuSamples = [];

    // When each problem was first seen, so that alerts can show how long it has lasted.
    private readonly Dictionary<string, DateTimeOffset> _problemsSince = [];

    private HostSnapshot? _snapshot;
    private CpuTimes? _previousCpu;
    private DateTimeOffset? _lastContact;
    private DateTimeOffset? _lastChecked;
    private string _failureReason = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnline))]
    [NotifyPropertyChangedFor(nameof(IsConnectionFailure))]
    [NotifyPropertyChangedFor(nameof(IsHostKeyProblem))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(IsPaused))]
    private HostState _state;

    [ObservableProperty]
    private WidgetSeverity _severity;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private string _osText = string.Empty;

    [ObservableProperty]
    private string _cpuText = "–";

    [ObservableProperty]
    private IReadOnlyList<double> _cpuHistory = [];

    [ObservableProperty]
    private string _loadText = string.Empty;

    [ObservableProperty]
    private string _coresText = string.Empty;

    [ObservableProperty]
    private string _ramText = string.Empty;

    [ObservableProperty]
    private double _ramPercent;

    [ObservableProperty]
    private string _diskText = string.Empty;

    [ObservableProperty]
    private double _diskPercent;

    [ObservableProperty]
    private WidgetSeverity _diskSeverity;

    [ObservableProperty]
    private IReadOnlyList<ServiceChipViewModel> _services = [];

    [ObservableProperty]
    private string _footerText = string.Empty;

    [ObservableProperty]
    private string _compactUptime = string.Empty;

    [ObservableProperty]
    private string _compactLoad = string.Empty;

    [ObservableProperty]
    private string _compactRam = string.Empty;

    [ObservableProperty]
    private string _compactDisk = string.Empty;

    [ObservableProperty]
    private string _failureTitle = string.Empty;

    [ObservableProperty]
    private string _failureText = string.Empty;

    [ObservableProperty]
    private string? _fingerprint;

    [ObservableProperty]
    private bool _isBusy;

    public HostViewModel(ServerHost host)
    {
        Host = host;
        _state = host.IsPaused ? HostState.Paused : HostState.Pending;
    }

    public ServerHost Host { get; }

    public string Name => Host.Name;

    public string Address => Host.Address;

    public bool IsOnline => State == HostState.Online;

    public bool IsConnectionFailure => State is HostState.Unreachable or HostState.AuthenticationFailed;

    public bool IsHostKeyProblem => State is HostState.HostKeyChanged or HostState.HostKeyUnknown;

    /// <summary>Nothing to show but the name: not checked yet, or paused.</summary>
    public bool IsIdle => State is HostState.Pending or HostState.Paused;

    public bool IsPaused => State == HostState.Paused;

    public IReadOnlyList<WidgetAlert> Alerts { get; private set; } = [];

    public void ApplySnapshot(HostSnapshot snapshot, DateTimeOffset now)
    {
        var culture = CultureInfo.CurrentCulture;
        _snapshot = snapshot;
        _lastContact = now;
        _lastChecked = now;

        if (_previousCpu is { } previous && snapshot.Cpu.UsageSince(previous) is { } usage)
        {
            _cpuSamples.Add((now, usage));
            _cpuSamples.RemoveAll(sample => now - sample.Time > HistoryWindow);
            CpuHistory = [.. _cpuSamples.Select(sample => sample.Percent)];
            CpuText = $"{usage.ToString("0", culture)} %";
        }

        _previousCpu = snapshot.Cpu;

        OsText = $"{snapshot.OsName} · up {FormatUptime(snapshot.Uptime)}";
        LoadText = string.Join(" · ", new[] { snapshot.Load1, snapshot.Load5, snapshot.Load15 }.Select(load => load.ToString("0.00", culture)));
        CoresText = snapshot.Cores == 1 ? "1 core" : $"{snapshot.Cores} cores";

        RamPercent = snapshot.MemoryUsedPercent;
        RamText = $"{((snapshot.MemoryTotalBytes - snapshot.MemoryAvailableBytes) / BytesPerGibibyte).ToString("0.0", culture)}"
            + $" / {(snapshot.MemoryTotalBytes / BytesPerGibibyte).ToString("0.#", culture)} GiB";

        DiskPercent = snapshot.DiskUsedPercent;
        DiskText = $"{(snapshot.DiskUsedBytes / BytesPerGigabyte).ToString("0", culture)}"
            + $" / {(snapshot.DiskTotalBytes / BytesPerGigabyte).ToString("0", culture)} GB · {DiskPercent.ToString("0", culture)} %";
        DiskSeverity = DiskPercent >= Host.DiskErrorPercent ? WidgetSeverity.Error
            : DiskPercent >= Host.DiskWarningPercent ? WidgetSeverity.Warning
            : WidgetSeverity.Ok;

        Services = [.. snapshot.Services.Select(service => new ServiceChipViewModel(service.Name, service.State, service.IsActive))];

        CompactUptime = FormatUptime(snapshot.Uptime);
        CompactLoad = $"Load {snapshot.Load1.ToString("0.00", culture)}";
        CompactRam = $"RAM {RamPercent.ToString("0", culture)} %";
        CompactDisk = $"Disk {DiskPercent.ToString("0", culture)} %";

        Fingerprint = null;
        State = HostState.Online;
        UpdateProblems(now);
        UpdateTimes(now);
    }

    public void ApplyFailure(Exception exception, DateTimeOffset now)
    {
        _lastChecked = now;
        _previousCpu = null;

        var failure = exception as ProbeException;
        _failureReason = exception.Message;
        Fingerprint = failure?.Fingerprint;
        State = failure?.Kind switch
        {
            ProbeFailureKind.HostKeyChanged => HostState.HostKeyChanged,
            ProbeFailureKind.HostKeyUnknown => HostState.HostKeyUnknown,
            ProbeFailureKind.AuthenticationFailed => HostState.AuthenticationFailed,
            _ => HostState.Unreachable,
        };

        UpdateProblems(now);
        UpdateTimes(now);
    }

    /// <summary>
    /// Changes whenever monitoring is paused or resumed. A polling loop that started under an older value
    /// ends itself, so pausing and resuming quickly cannot leave two loops checking the same host.
    /// </summary>
    public int MonitoringGeneration { get; private set; }

    public void SetPaused(bool isPaused)
    {
        MonitoringGeneration++;
        Host.IsPaused = isPaused;
        State = isPaused ? HostState.Paused : HostState.Pending;
        _problemsSince.Clear();
        Severity = WidgetSeverity.Ok;
        Alerts = [];
    }

    /// <summary>Refreshes texts that contain an age, such as "checked 12 s ago".</summary>
    public void UpdateTimes(DateTimeOffset now)
    {
        if (State == HostState.Online && _snapshot is { } snapshot && _lastChecked is { } checkedAt)
        {
            var authentication = Host.Authentication switch
            {
                SshAuthentication.KeyFile => "Key file",
                SshAuthentication.Password => "Password",
                _ => "SSH agent",
            };
            FooterText = $"{authentication} · response {snapshot.ResponseTime.TotalMilliseconds.ToString("0", CultureInfo.CurrentCulture)} ms"
                + $" · checked {FormatAge(now - checkedAt)} ago";
        }
        else if (IsConnectionFailure)
        {
            var since = _problemsSince.GetValueOrDefault(ConnectionProblemKey, now);
            var lastContact = _lastContact is { } contact
                ? $" · last contact {contact.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}"
                : string.Empty;
            FailureTitle = State == HostState.AuthenticationFailed ? "Sign-in failed" : "Not reachable";
            FailureText = $"{FailureTitle} · {_failureReason} for {FormatAge(now - since)}{lastContact}";
        }
        else if (State == HostState.HostKeyChanged)
        {
            FailureTitle = $"{Name}: host key has changed";
            FailureText = "Meerkat no longer connects until you have verified the new key. After reinstalling the host this is normal; otherwise it is a warning sign.";
        }
        else if (State == HostState.HostKeyUnknown)
        {
            FailureTitle = $"{Name}: host key is not trusted yet";
            FailureText = "Meerkat only connects to hosts whose key is known. Trust the key if you expect this host at this address.";
        }
    }

    private const string ConnectionProblemKey = "connection";
    private const string DiskProblemKey = "disk";

    /// <summary>Derives severity and alerts from the current state and remembers since when each problem exists.</summary>
    private void UpdateProblems(DateTimeOffset now)
    {
        var problems = new List<(string Key, WidgetSeverity Severity, string Title, string Detail)>();
        switch (State)
        {
            case HostState.Unreachable:
                problems.Add((ConnectionProblemKey, WidgetSeverity.Error, $"{Name} is not reachable", _failureReason));
                break;
            case HostState.AuthenticationFailed:
                problems.Add((ConnectionProblemKey, WidgetSeverity.Error, $"{Name}: sign-in failed", _failureReason));
                break;
            case HostState.HostKeyChanged:
                problems.Add((ConnectionProblemKey, WidgetSeverity.Error, $"{Name}: host key has changed", "Verify the new key in the panel"));
                break;
            case HostState.HostKeyUnknown:
                problems.Add((ConnectionProblemKey, WidgetSeverity.Error, $"{Name}: host key is not trusted yet", "Trust the key in the panel"));
                break;
            case HostState.Online:
                if (DiskSeverity != WidgetSeverity.Ok)
                {
                    problems.Add((DiskProblemKey, DiskSeverity, $"{Name}: disk {DiskPercent.ToString("0", CultureInfo.CurrentCulture)} % full", DiskText));
                }

                problems.AddRange(Services
                    .Where(service => !service.IsActive)
                    .Select(service => ($"service:{service.Name}", WidgetSeverity.Warning, $"{Name}: {service.Name} is {service.StateText}", "systemd service")));
                break;
        }

        foreach (var key in _problemsSince.Keys.Except(problems.Select(problem => problem.Key)).ToList())
        {
            _problemsSince.Remove(key);
        }

        foreach (var problem in problems)
        {
            _problemsSince.TryAdd(problem.Key, now);
        }

        Alerts = [.. problems.Select(problem => new WidgetAlert(problem.Severity, problem.Title, problem.Detail, _problemsSince[problem.Key]))];
        var severity = problems.Count == 0 ? WidgetSeverity.Ok : problems.Max(problem => problem.Severity);

        // A host that needs attention opens itself; one that recovered folds away again.
        if (severity != Severity)
        {
            IsExpanded = severity != WidgetSeverity.Ok;
        }

        Severity = severity;
    }

    private static string FormatUptime(TimeSpan uptime) => uptime switch
    {
        { TotalDays: >= 2 } => $"{(int)uptime.TotalDays} days",
        { TotalDays: >= 1 } => "1 day",
        { TotalHours: >= 1 } => $"{(int)uptime.TotalHours} h",
        _ => $"{(int)uptime.TotalMinutes} min",
    };

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        return age switch
        {
            { TotalMinutes: < 1 } => $"{(int)age.TotalSeconds} s",
            { TotalHours: < 1 } => $"{(int)age.TotalMinutes} min",
            { TotalDays: < 1 } => $"{(int)age.TotalHours} h",
            _ => $"{(int)age.TotalDays} d",
        };
    }
}
