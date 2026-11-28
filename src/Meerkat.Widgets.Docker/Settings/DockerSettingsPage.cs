using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meerkat.Abstractions;
using Meerkat.Ssh;
using Meerkat.Widgets.Docker.Containers;
using Meerkat.Widgets.Docker.Security;
using Meerkat.Widgets.Docker.Views;

namespace Meerkat.Widgets.Docker.Settings;

public sealed class DockerSettingsPage : IWidgetSettingsPage
{
    private readonly DockerSettingsSource _settingsSource;
    private readonly IWslDistributionCatalog _distributionCatalog;
    private readonly MachineRegistry _machines;
    private readonly IDockerShellFactory _shells;
    private readonly ISshClient _ssh;
    private DockerSettingsPageViewModel? _viewModel;

    public DockerSettingsPage(
        DockerSettingsSource settingsSource,
        IWslDistributionCatalog distributionCatalog,
        MachineRegistry machines,
        IDockerShellFactory shells,
        ISshClient ssh)
    {
        _settingsSource = settingsSource;
        _distributionCatalog = distributionCatalog;
        _machines = machines;
        _shells = shells;
        _ssh = ssh;

        // A machine entered while the settings are open is offered here right away.
        machines.Changed += (_, _) => _viewModel?.SetMachines(machines.Machines);
    }

    public FrameworkElement CreateView()
    {
        var viewModel = new DockerSettingsPageViewModel(
            _settingsSource.Current,
            _machines.Machines,
            _shells,
            machine => new SshContainerProvider(_ssh, () => _machines.GetTarget(machine)).CheckAsync(CancellationToken.None));
        viewModel.Changed += (_, _) => _settingsSource.Update(viewModel.ToSettings());
        _viewModel = viewModel;
        _ = viewModel.LoadDistributionsAsync(_distributionCatalog);
        return new DockerSettingsView { DataContext = viewModel };
    }
}

public sealed record DistributionOption(string? Name, string DisplayName);

public sealed record ThresholdOption(ReportThreshold Threshold, string DisplayName);

/// <summary>Tells whether a Docker system has a scanner for images and offers to install one.</summary>
public sealed partial class ScannerStatusViewModel(IImageScanner? scanner, Action? installed = null) : ObservableObject
{
    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private bool _canInstall;

    [ObservableProperty]
    private string? _error;

    /// <summary>Asks the system whether it has a scanner. Does nothing where systems cannot be asked.</summary>
    public async Task DetectAsync()
    {
        if (scanner is null)
        {
            return;
        }

        CanInstall = false;
        Error = null;
        Text = "checking …";
        try
        {
            var info = await scanner.DetectAsync(CancellationToken.None);
            Text = info is null ? "no scanner" : $"{info.Name} {info.Version}";
            CanInstall = info is null;
        }
        catch (Exception exception) when (exception is ScannerException or DockerShellException or ArgumentException)
        {
            // Whether the system can be reached is the business of the Docker page; here it only means "unknown".
            Text = "scanner unknown";
            Error = exception.Message;
        }
    }

    public void Clear()
    {
        Text = string.Empty;
        CanInstall = false;
        Error = null;
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (scanner is null)
        {
            return;
        }

        CanInstall = false;
        Error = null;
        Text = "installing …";
        try
        {
            var info = await scanner.InstallAsync(CancellationToken.None);
            Text = $"{info.Name} {info.Version}";
            installed?.Invoke();
        }
        catch (Exception exception) when (exception is ScannerException or DockerShellException or ArgumentException)
        {
            Text = "no scanner";
            CanInstall = true;
            Error = exception is ScannerException failure ? $"{failure.Message} {failure.Detail}" : exception.Message;
        }
    }
}

/// <summary>One of the shared machines that the Docker widget shows, and what it may do there.</summary>
public sealed partial class DockerMachineOptionViewModel : ObservableObject
{
    private readonly DockerMachineUse _use;
    private readonly Func<SshMachine, Task<string>>? _checkDocker;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessText))]
    private bool _allowActions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessText))]
    private bool _scanImages;

    /// <summary>True while the options of the machine are shown for editing.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>What the last check of the connection found.</summary>
    [ObservableProperty]
    private string _checkResult = string.Empty;

    [ObservableProperty]
    private bool _isCheckFailed;

    /// <param name="checkDocker">Asks the Docker daemon of a machine for its version; without it no check is offered.</param>
    public DockerMachineOptionViewModel(SshMachine machine, DockerMachineUse use, IDockerShellFactory? shells, Func<SshMachine, Task<string>>? checkDocker = null)
    {
        Machine = machine;
        _use = use;
        _checkDocker = checkDocker;
        _allowActions = use.AllowActions;
        _scanImages = use.ScanImages;

        // A scanner the user just installed is meant to be used, so scanning is switched on with it.
        Scanner = new ScannerStatusViewModel(
            shells is null ? null : new TrivyScanner(shells.ForHost(DockerHost.From(machine, use, DockerHost.DefaultIntervalSeconds))),
            () => ScanImages = true);
        _ = Scanner.DetectAsync();
    }

    public SshMachine Machine { get; }

    public string Name => Machine.DisplayName;

    public string Destination => Machine.Destination;

    /// <summary>What the widget may do on the machine, in a word or two.</summary>
    public string AccessText => $"{(AllowActions ? "start/stop allowed" : "read-only")}{(ScanImages ? " · scanned" : string.Empty)}";

    public bool CanCheck => _checkDocker is not null;

    public ScannerStatusViewModel Scanner { get; }

    public DockerMachineUse ToUse() => new()
    {
        MachineId = Machine.Id,
        ShowContainers = true,
        AllowActions = AllowActions,
        ScanImages = ScanImages,
        IntervalSeconds = _use.IntervalSeconds,
    };

    [RelayCommand]
    private void ToggleOpen() => IsOpen = !IsOpen;

    /// <summary>Connects once and asks for Docker, so that a machine without it shows here and not on the Docker page.</summary>
    [RelayCommand]
    private async Task CheckAsync()
    {
        if (_checkDocker is null)
        {
            return;
        }

        IsCheckFailed = false;
        CheckResult = "Connecting …";
        try
        {
            CheckResult = $"Connected. Docker {await _checkDocker(Machine)}.";
        }
        catch (Exception exception) when (exception is ContainerProviderException or ArgumentException or InvalidOperationException)
        {
            var failure = ViewModels.ContainerFailureViewModel.From(exception, Name);
            IsCheckFailed = true;
            CheckResult = $"{failure.Title}. {failure.Message}";
        }
    }
}

public sealed partial class DockerSettingsPageViewModel : ObservableObject
{
    private readonly IDockerShellFactory? _shells;
    private readonly Func<SshMachine, Task<string>>? _checkDocker;
    private IReadOnlyList<SshMachine> _allMachines = [];

    // Not edited here, but part of the settings this page writes back as a whole.
    private readonly DockerWidgetSettings _original;

    // While the page fills itself in, nothing the user did has changed.
    private readonly bool _isLoading = true;

    [ObservableProperty]
    private DistributionOption _selectedDistribution;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFast), nameof(IsNormal), nameof(IsEconomical))]
    private RefreshRate _refresh;

    [ObservableProperty]
    private bool _scanLocalImages;

    [ObservableProperty]
    private ThresholdOption _selectedThreshold;

    /// <summary>Time of day of the daily scan, as typed: "HH:mm".</summary>
    [ObservableProperty]
    private string _dailyScanTime;

    /// <summary>Set while the time typed is none; it is stored once it is.</summary>
    [ObservableProperty]
    private string? _dailyScanTimeError;

    [ObservableProperty]
    private bool _onlyFixable;

    [ObservableProperty]
    private bool _skipOnBattery;

    [ObservableProperty]
    private bool _isIgnoredListOpen;

    /// <summary>True while the machines that can be added are offered for choosing.</summary>
    [ObservableProperty]
    private bool _isPickerOpen;

    /// <param name="machines">The machines all widgets share; the page chooses which of them this widget shows.</param>
    /// <param name="shells">Used to look for a scanner on the systems and to install one; without it neither is offered.</param>
    /// <param name="checkDocker">Asks the Docker daemon of a machine for its version; without it no check is offered.</param>
    public DockerSettingsPageViewModel(
        DockerWidgetSettings settings,
        IReadOnlyList<SshMachine>? machines = null,
        IDockerShellFactory? shells = null,
        Func<SshMachine, Task<string>>? checkDocker = null)
    {
        _original = settings;
        _shells = shells;
        _checkDocker = checkDocker;

        var defaultOption = new DistributionOption(null, "Default distribution");
        Distributions.Add(defaultOption);
        _selectedDistribution = settings.Distribution is { Length: > 0 } configured ? AddOption(configured) : defaultOption;
        _refresh = settings.Refresh;

        var security = settings.Security;
        _scanLocalImages = security.ScanLocalImages;
        _selectedThreshold = Thresholds.First(option => option.Threshold == security.ReportFrom);
        _dailyScanTime = security.DailyScanTime;
        _onlyFixable = security.OnlyFixable;
        _skipOnBattery = security.SkipOnBattery;
        foreach (var ignored in security.Ignored)
        {
            Ignored.Add(ignored);
        }

        Ignored.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IgnoredLinkText));
            OnPropertyChanged(nameof(HasIgnored));
            RaiseChanged();
        };

        LocalScanner = new ScannerStatusViewModel(
            shells is null ? null : new TrivyScanner(shells.ForLocal(() => SelectedDistribution.Name)),
            () => ScanLocalImages = true);
        _ = LocalScanner.DetectAsync();
        SetMachines(machines ?? []);
        _isLoading = false;
    }

    /// <summary>Raised when something that is stored was changed and is valid.</summary>
    public event EventHandler? Changed;

    public ObservableCollection<DistributionOption> Distributions { get; } = [];

    /// <summary>The machines the widget shows.</summary>
    public ObservableCollection<DockerMachineOptionViewModel> Machines { get; } = [];

    /// <summary>The machines of the shared list the widget does not show yet; offered by "+ Machine".</summary>
    public ObservableCollection<SshMachine> AvailableMachines { get; } = [];

    public ObservableCollection<IgnoredVulnerability> Ignored { get; } = [];

    public ScannerStatusViewModel LocalScanner { get; }

    public IReadOnlyList<ThresholdOption> Thresholds { get; } =
    [
        new(ReportThreshold.Critical, "Critical"),
        new(ReportThreshold.High, "High and critical"),
        new(ReportThreshold.Never, "Never"),
    ];

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

    public bool HasIgnored => Ignored.Count > 0;

    public string IgnoredLinkText => $"Ignored vulnerabilities ({Ignored.Count})";

    public string RefreshInfo =>
        "How often Docker is asked: containers every 2, 5 or 15 seconds, figures every 2, 3 or 10. Other machines are asked less often, every 10, 20 or 60 seconds, because every request opens an SSH connection. Other figures can be set in settings.json.";

    public string MachinesInfo =>
        "Machines are entered once, on the Machines page; \"+ Machine\" adds one of them here. Meerkat runs the docker CLI there over SSH, so the user it signs in with must be allowed to use Docker, which usually means being in the docker group. Without the permission to start and stop, a machine is only read. A scan runs on the machine itself and takes some of its processor time once a day.";

    public string ScannerInfo => $"Images are scanned with Trivy on the system they are on. {TrivyScanner.InstallDescription}";

    public async Task LoadDistributionsAsync(IWslDistributionCatalog catalog)
    {
        try
        {
            foreach (var name in await catalog.ListAsync(CancellationToken.None))
            {
                AddOption(name);
            }
        }
        catch (Exception)
        {
            // Without WSL the list simply stays limited to the default and the configured distribution.
        }
    }

    /// <summary>Takes over the shared machines as they are now, keeping what was chosen for those that stay.</summary>
    public void SetMachines(IReadOnlyList<SshMachine> machines)
    {
        var chosen = _isLoading ? _original.Machines.Where(use => use.ShowContainers).ToList() : [.. Machines.Select(option => option.ToUse())];
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

    public DockerWidgetSettings ToSettings() => new()
    {
        Distribution = SelectedDistribution.Name,
        Refresh = Refresh,
        CustomListIntervalSeconds = _original.CustomListIntervalSeconds,
        CustomStatsIntervalSeconds = _original.CustomStatsIntervalSeconds,
        CustomRemoteIntervalSeconds = _original.CustomRemoteIntervalSeconds,
        MaxLogLines = _original.MaxLogLines,
        Machines = [.. Machines.Select(option => option.ToUse())],
        CollapsedSources = [.. _original.CollapsedSources],
        CollapsedImageSources = [.. _original.CollapsedImageSources],
        Security = new DockerSecuritySettings
        {
            ScanLocalImages = ScanLocalImages,
            ReportFrom = SelectedThreshold.Threshold,

            // A time that is none is not stored; the one stored last stays until the field holds a time again.
            DailyScanTime = DockerSecuritySettings.TryParseTime(DailyScanTime, out var time)
                ? time.ToString("HH:mm", CultureInfo.InvariantCulture)
                : _original.Security.DailyScanTime,
            OnlyFixable = OnlyFixable,
            SkipOnBattery = SkipOnBattery,
            Ignored = [.. Ignored],
        },
    };

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(SelectedDistribution) or nameof(Refresh) or nameof(ScanLocalImages) or nameof(SelectedThreshold)
            or nameof(OnlyFixable) or nameof(SkipOnBattery))
        {
            RaiseChanged();
        }
    }

    [RelayCommand]
    private void ToggleIgnoredList() => IsIgnoredListOpen = !IsIgnoredListOpen;

    /// <summary>Lets the widget show one of the shared machines, read-only until the user decides otherwise.</summary>
    [RelayCommand]
    private void AddMachine(SshMachine machine)
    {
        IsPickerOpen = false;
        if (Machines.Any(option => option.Machine.Id == machine.Id))
        {
            return;
        }

        // Opened right away: whoever adds a machine will want to say what may be done there.
        Add(machine, new DockerMachineUse { MachineId = machine.Id }).IsOpen = true;
        RefreshAvailable();
        RaiseChanged();
    }

    /// <summary>Takes the machine off this widget; it stays on the shared list.</summary>
    [RelayCommand]
    private void RemoveMachine(DockerMachineOptionViewModel option)
    {
        option.PropertyChanged -= OnMachineChanged;
        Machines.Remove(option);
        RefreshAvailable();
        RaiseChanged();
    }

    [RelayCommand]
    private void RestoreIgnored(IgnoredVulnerability ignored) => Ignored.Remove(ignored);

    partial void OnSelectedDistributionChanged(DistributionOption value)
    {
        // Another distribution is another system as far as its programs go.
        _ = LocalScanner.DetectAsync();
    }

    partial void OnDailyScanTimeChanged(string value)
    {
        var isValid = DockerSecuritySettings.TryParseTime(value, out _);
        DailyScanTimeError = isValid ? null : "Enter hours and minutes, for example 12:30.";
        if (isValid)
        {
            RaiseChanged();
        }
    }

    private void OnMachineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DockerMachineOptionViewModel.AllowActions) or nameof(DockerMachineOptionViewModel.ScanImages))
        {
            RaiseChanged();
        }
    }

    private DockerMachineOptionViewModel Add(SshMachine machine, DockerMachineUse use)
    {
        var option = new DockerMachineOptionViewModel(machine, use, _shells, _checkDocker);
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

    private DistributionOption AddOption(string name)
    {
        var existing = Distributions.FirstOrDefault(option => option.Name == name);
        if (existing is not null)
        {
            return existing;
        }

        var option = new DistributionOption(name, name);
        Distributions.Add(option);
        return option;
    }
}
