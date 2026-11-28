using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meerkat.Abstractions;
using Meerkat.Ssh;
using Meerkat.Widgets.Server.Monitoring;
using Meerkat.Widgets.Server.Views;

namespace Meerkat.Widgets.Server.Settings;

public sealed class ServerSettingsPage : IWidgetSettingsPage
{
    private readonly ServerSettingsSource _settingsSource;
    private readonly IServerProbe _probe;
    private readonly MachineRegistry _machines;
    private ServerSettingsPageViewModel? _viewModel;

    public ServerSettingsPage(ServerSettingsSource settingsSource, IServerProbe probe, MachineRegistry machines)
    {
        _settingsSource = settingsSource;
        _probe = probe;
        _machines = machines;

        // A machine entered while the settings are open is offered here right away.
        machines.Changed += (_, _) => _viewModel?.SetMachines(machines.Machines);
    }

    public FrameworkElement CreateView()
    {
        var viewModel = new ServerSettingsPageViewModel(_settingsSource.Current, _machines.Machines, _probe);
        viewModel.Changed += (_, _) => _settingsSource.Update(viewModel.ToSettings());
        _viewModel = viewModel;
        return new ServerSettingsView { DataContext = viewModel };
    }
}

/// <summary>One of the shared machines and what the Server widget watches on it.</summary>
public sealed partial class ServerMachineOptionViewModel : ObservableObject
{
    private readonly ServerMachineUse _use;
    private readonly IServerProbe? _probe;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _servicesText;

    [ObservableProperty]
    private double _diskWarningPercent;

    [ObservableProperty]
    private double _diskErrorPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private bool _isPaused;

    /// <summary>True while the details of the machine are shown for editing.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>What keeps the entries from being stored; null while they are fine.</summary>
    [ObservableProperty]
    private string? _error;

    /// <summary>What the last check of the connection found.</summary>
    [ObservableProperty]
    private string _checkResult = string.Empty;

    [ObservableProperty]
    private bool _isCheckFailed;

    public ServerMachineOptionViewModel(SshMachine machine, ServerMachineUse use, IServerProbe? probe)
    {
        Machine = machine;
        _use = use;
        _probe = probe;
        _servicesText = string.Join(", ", use.Services);
        _diskWarningPercent = use.DiskWarningPercent;
        _diskErrorPercent = use.DiskErrorPercent;
        _isPaused = use.IsPaused;
    }

    public SshMachine Machine { get; }

    public string Name => Machine.DisplayName;

    public string Destination => Machine.Destination;

    public string Summary => IsPaused ? "paused"
        : Services.Count == 0 ? "no services"
        : string.Join(", ", Services);

    public bool IsValid => Error is null;

    private List<string> Services =>
        [.. ServicesText.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    public bool CanCheck => _probe is not null;

    public ServerMachineUse ToUse() => new()
    {
        MachineId = Machine.Id,
        Services = Services,
        IntervalSeconds = _use.IntervalSeconds,
        DiskWarningPercent = DiskWarningPercent,
        DiskErrorPercent = DiskErrorPercent,
        IsPaused = IsPaused,
    };

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        // Judged before anybody hears of the change, so that listeners already know whether it may be stored.
        if (e.PropertyName is nameof(ServicesText) or nameof(DiskWarningPercent) or nameof(DiskErrorPercent))
        {
            Error = Validate();
        }

        base.OnPropertyChanged(e);
    }

    [RelayCommand]
    private void ToggleOpen() => IsOpen = !IsOpen;

    /// <summary>Connects once and tells what was found, so that a mistyped service shows before it is missed.</summary>
    [RelayCommand]
    private async Task CheckAsync()
    {
        if (_probe is null)
        {
            return;
        }

        var use = ToUse();

        IsCheckFailed = false;
        CheckResult = "Connecting …";
        try
        {
            var snapshot = await _probe.ProbeAsync(ServerHost.From(Machine, use), HostKeyPolicy.Strict, CancellationToken.None);
            var inactive = snapshot.Services.Where(service => !service.IsActive).Select(service => service.Name).ToList();
            CheckResult = inactive.Count == 0
                ? $"Connected ({snapshot.OsName})."
                : $"Connected ({snapshot.OsName}). Not active: {string.Join(", ", inactive)}.";
        }
        catch (Exception exception)
        {
            IsCheckFailed = true;
            CheckResult = exception.Message;
        }
    }

    private string? Validate()
    {
        if (!double.IsFinite(DiskWarningPercent) || !double.IsFinite(DiskErrorPercent) || DiskWarningPercent > DiskErrorPercent)
        {
            return "The yellow disk threshold must not be above the red one.";
        }

        try
        {
            ServerProbe.BuildScript(Services);
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or ProbeException)
        {
            return exception.Message;
        }
    }
}

public sealed partial class ServerSettingsPageViewModel : ObservableObject
{
    private readonly IServerProbe? _probe;
    private IReadOnlyList<SshMachine> _allMachines = [];

    // Not edited here, but part of the settings this page writes back as a whole.
    private readonly ServerWidgetSettings _original;

    // While the page fills itself in, nothing the user did has changed.
    private readonly bool _isLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFast), nameof(IsNormal), nameof(IsEconomical))]
    private RefreshRate _refresh;

    /// <summary>True while the machines that can be added are offered for choosing.</summary>
    [ObservableProperty]
    private bool _isPickerOpen;

    /// <param name="machines">The machines all widgets share; the page chooses which of them this widget watches.</param>
    /// <param name="probe">Used to check a machine on request; without it no check is offered.</param>
    public ServerSettingsPageViewModel(ServerWidgetSettings settings, IReadOnlyList<SshMachine> machines, IServerProbe? probe = null)
    {
        _original = settings;
        _probe = probe;
        _refresh = settings.Refresh;
        SetMachines(machines);
        _isLoading = false;
    }

    /// <summary>Raised when something that is stored was changed and is valid.</summary>
    public event EventHandler? Changed;

    /// <summary>The machines the widget watches.</summary>
    public ObservableCollection<ServerMachineOptionViewModel> Machines { get; } = [];

    /// <summary>The machines of the shared list the widget does not watch yet; offered by "+ Machine".</summary>
    public ObservableCollection<SshMachine> AvailableMachines { get; } = [];

    // One property per choice. Each only reacts to being selected; deselecting follows from the others.
    public bool IsFast
    {
        get => Refresh == RefreshRate.Fast;
        set => Select(value, RefreshRate.Fast);
    }

    public bool IsNormal
    {
        get => Refresh == RefreshRate.Normal;
        set => Select(value, RefreshRate.Normal);
    }

    public bool IsEconomical
    {
        get => Refresh == RefreshRate.Economical;
        set => Select(value, RefreshRate.Economical);
    }

    public bool HasMachines => Machines.Count > 0;

    public bool CanAddMachine => AvailableMachines.Count > 0;

    /// <summary>Says why there is nothing to add, where that is the case.</summary>
    public string? AddMachineHint => CanAddMachine ? null
        : _allMachines.Count == 0 ? "Enter a machine on the Machines page first."
        : "Every machine is listed already. Enter more on the Machines page.";

    public string RefreshInfo =>
        "How often the machines are checked: every 15, 30 or 120 seconds. Every check opens an SSH connection. Another figure, also per machine, can be set in settings.json.";

    public string MachinesInfo =>
        "Machines are entered once, on the Machines page; \"+ Machine\" adds one of them here. For services, enter the names you would pass to \"systemctl status\" on the machine, such as docker, nginx, postgresql; leave the field empty to watch none. The disk thresholds say from which fill level a disk is shown yellow or red.";

    /// <summary>Takes over the shared machines as they are now, keeping what was entered for those that stay.</summary>
    public void SetMachines(IReadOnlyList<SshMachine> machines)
    {
        var chosen = _isLoading ? _original.Machines : [.. Machines.Select(option => option.ToUse())];
        var open = Machines.Where(option => option.IsOpen).Select(option => option.Machine.Id).ToHashSet();
        _allMachines = machines;

        foreach (var option in Machines)
        {
            option.PropertyChanged -= OnMachineChanged;
        }

        Machines.Clear();
        foreach (var use in chosen)
        {
            // A machine that was removed from the shared list is gone here as well.
            if (machines.FirstOrDefault(machine => machine.Id == use.MachineId) is { } machine)
            {
                Add(machine, use).IsOpen = open.Contains(machine.Id);
            }
        }

        RefreshAvailable();
    }

    public ServerWidgetSettings ToSettings() => new()
    {
        Refresh = Refresh,
        CustomIntervalSeconds = _original.CustomIntervalSeconds,
        Machines = [.. Machines.Select(option => option.ToUse())],
    };

    /// <summary>Lets the widget watch one of the shared machines.</summary>
    [RelayCommand]
    private void AddMachine(SshMachine machine)
    {
        IsPickerOpen = false;
        if (Machines.Any(option => option.Machine.Id == machine.Id))
        {
            return;
        }

        // Opened right away: whoever adds a machine will want to say what to watch on it.
        Add(machine, new ServerMachineUse { MachineId = machine.Id }).IsOpen = true;
        RefreshAvailable();
        RaiseChanged();
    }

    /// <summary>Takes the machine off this widget; it stays on the shared list.</summary>
    [RelayCommand]
    private void RemoveMachine(ServerMachineOptionViewModel option)
    {
        option.PropertyChanged -= OnMachineChanged;
        Machines.Remove(option);
        RefreshAvailable();
        if (Machines.All(remaining => remaining.IsValid))
        {
            RaiseChanged();
        }
    }

    partial void OnRefreshChanged(RefreshRate value) => RaiseChanged();

    private ServerMachineOptionViewModel Add(SshMachine machine, ServerMachineUse use)
    {
        var option = new ServerMachineOptionViewModel(machine, use, _probe);
        option.PropertyChanged += OnMachineChanged;
        Machines.Add(option);
        return option;
    }

    private void RefreshAvailable()
    {
        AvailableMachines.Clear();
        foreach (var machine in _allMachines.Where(machine => Machines.All(option => option.Machine.Id != machine.Id)))
        {
            AvailableMachines.Add(machine);
        }

        OnPropertyChanged(nameof(HasMachines));
        OnPropertyChanged(nameof(CanAddMachine));
        OnPropertyChanged(nameof(AddMachineHint));
    }

    private void OnMachineChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Entries that are no good yet are not stored; what was stored last stays until they are.
        if (e.PropertyName is nameof(ServerMachineOptionViewModel.ServicesText)
                or nameof(ServerMachineOptionViewModel.DiskWarningPercent) or nameof(ServerMachineOptionViewModel.DiskErrorPercent)
                or nameof(ServerMachineOptionViewModel.IsPaused)
            && Machines.All(option => option.IsValid))
        {
            RaiseChanged();
        }
    }

    private void RaiseChanged()
    {
        if (!_isLoading)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Select(bool isSelected, RefreshRate rate)
    {
        if (isSelected)
        {
            Refresh = rate;
        }
    }
}
