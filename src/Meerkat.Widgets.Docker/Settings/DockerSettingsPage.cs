using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meerkat.Abstractions;
using Meerkat.Ssh;
using Meerkat.Widgets.Docker.Containers;
using Meerkat.Widgets.Docker.Security;
using Meerkat.Widgets.Docker.ViewModels;
using Meerkat.Widgets.Docker.Views;

namespace Meerkat.Widgets.Docker.Settings;

public sealed class DockerSettingsPage(
    DockerSettingsSource settingsSource,
    IWslDistributionCatalog distributionCatalog,
    ISshClient ssh,
    IWidgetSecretStore secrets,
    IDockerShellFactory shells) : IWidgetSettingsPage
{
    private DockerSettingsPageViewModel? _viewModel;

    public FrameworkElement CreateView()
    {
        _viewModel = new DockerSettingsPageViewModel(settingsSource.Current, ssh, host => secrets.Read(host.SecretKey) is { Length: > 0 }, shells);
        _ = _viewModel.LoadDistributionsAsync(distributionCatalog);
        return new DockerSettingsView { DataContext = _viewModel };
    }

    public void Apply()
    {
        if (_viewModel is null)
        {
            return;
        }

        // Passwords go to the secret store first, so that the hosts can sign in as soon as they are announced.
        var settings = _viewModel.ToSettings();
        foreach (var (hostId, password) in _viewModel.PendingPasswords)
        {
            secrets.Write($"docker/{hostId}", password);
        }

        var kept = settings.RemoteHosts.Where(host => host.Authentication == SshAuthentication.Password).Select(host => host.SecretKey).ToHashSet();
        foreach (var previous in settingsSource.Current.RemoteHosts.Where(host => !kept.Contains(host.SecretKey)))
        {
            secrets.Delete(previous.SecretKey);
        }

        settingsSource.Update(settings);
    }
}

public sealed record DistributionOption(string? Name, string DisplayName);

public sealed record AuthenticationOption(SshAuthentication Authentication, string DisplayName);

public sealed record HostIntervalOption(double Seconds, string DisplayName);

public sealed record ThresholdOption(ReportThreshold Threshold, string DisplayName);

/// <summary>
/// A machine Docker runs on, as listed in the settings: this computer or another one. Tells whether the
/// machine has a scanner for images and offers to install one.
/// </summary>
public sealed partial class DockerSystemViewModel(DockerHost? host, IImageScanner? scanner, Action<DockerSystemViewModel>? installed = null) : ObservableObject
{
    public const string LocalName = "This computer";

    [ObservableProperty]
    private string _scannerText = string.Empty;

    [ObservableProperty]
    private bool _canInstall;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _error;

    /// <summary>Null for this computer.</summary>
    public DockerHost? Host => host;

    public bool IsLocal => host is null;

    public bool IsRemote => host is not null;

    public string Name => host?.DisplayName ?? LocalName;

    public string Address => host?.Address ?? string.Empty;

    public string AccessText => host switch
    {
        null => string.Empty,
        { AllowActions: true, ScanImages: true } => "start/stop allowed · scanned",
        { AllowActions: true } => "start/stop allowed",
        { ScanImages: true } => "read-only · scanned",
        _ => "read-only",
    };

    /// <summary>Asks the machine whether it has a scanner. Does nothing where machines cannot be asked.</summary>
    public async Task DetectAsync()
    {
        if (scanner is null)
        {
            return;
        }

        IsBusy = true;
        CanInstall = false;
        Error = null;
        ScannerText = "checking …";
        try
        {
            var info = await scanner.DetectAsync(CancellationToken.None);
            ScannerText = info is null ? "no scanner" : $"{info.Name} {info.Version}";
            CanInstall = info is null;
        }
        catch (Exception exception) when (exception is ScannerException or DockerShellException or ArgumentException)
        {
            // Whether the machine can be reached is the business of the Docker page; here it only means "unknown".
            ScannerText = "scanner unknown";
            Error = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (scanner is null)
        {
            return;
        }

        IsBusy = true;
        CanInstall = false;
        Error = null;
        ScannerText = "installing …";
        try
        {
            var info = await scanner.InstallAsync(CancellationToken.None);
            ScannerText = $"{info.Name} {info.Version} installed";
            installed?.Invoke(this);
            OnPropertyChanged(nameof(AccessText));
        }
        catch (Exception exception) when (exception is ScannerException or DockerShellException or ArgumentException)
        {
            ScannerText = "no scanner";
            CanInstall = true;
            Error = exception is ScannerException failure ? $"{failure.Message} {failure.Detail}" : exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed partial class DockerSettingsPageViewModel : ObservableObject
{
    private readonly ISshClient? _ssh;
    private readonly IDockerShellFactory? _shells;
    private readonly Func<DockerHost, bool> _isPasswordStored;
    private DockerHost? _editing;

    // Not edited here, but part of the settings this page writes back as a whole.
    private readonly IReadOnlyList<string> _collapsedSources;

    [ObservableProperty]
    private DistributionOption _selectedDistribution;

    [ObservableProperty]
    private double _listIntervalSeconds;

    [ObservableProperty]
    private double _statsIntervalSeconds;

    [ObservableProperty]
    private int _maxLogLines;

    [ObservableProperty]
    private bool _isFormOpen;

    [ObservableProperty]
    private string _formTitle = string.Empty;

    [ObservableProperty]
    private string _hostName = string.Empty;

    [ObservableProperty]
    private string _hostAddress = string.Empty;

    [ObservableProperty]
    private string _hostUser = string.Empty;

    [ObservableProperty]
    private string _hostPort = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesKeyFile), nameof(UsesPassword))]
    private AuthenticationOption _selectedAuthentication;

    [ObservableProperty]
    private string _keyFile = string.Empty;

    /// <summary>A password typed into the form; empty keeps the stored one.</summary>
    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _hasStoredPassword;

    [ObservableProperty]
    private HostIntervalOption _selectedHostInterval;

    [ObservableProperty]
    private bool _allowActions;

    [ObservableProperty]
    private bool _scanHostImages;

    [ObservableProperty]
    private bool _scanLocalImages;

    [ObservableProperty]
    private ThresholdOption _selectedThreshold;

    /// <summary>Time of day of the daily scan, as typed: "HH:mm".</summary>
    [ObservableProperty]
    private string _dailyScanTime;

    [ObservableProperty]
    private bool _onlyFixable;

    [ObservableProperty]
    private bool _skipOnBattery;

    [ObservableProperty]
    private bool _isIgnoredListOpen;

    [ObservableProperty]
    private string? _formError;

    [ObservableProperty]
    private string? _formErrorDetail;

    [ObservableProperty]
    private bool _isChecking;

    /// <param name="ssh">Used to check a host before it is added; without it hosts cannot be added.</param>
    /// <param name="shells">Used to look for a scanner on the systems and to install one; without it neither is offered.</param>
    public DockerSettingsPageViewModel(
        DockerWidgetSettings settings,
        ISshClient? ssh = null,
        Func<DockerHost, bool>? hasStoredPassword = null,
        IDockerShellFactory? shells = null)
    {
        _ssh = ssh;
        _shells = shells;
        _isPasswordStored = hasStoredPassword ?? (_ => false);
        _collapsedSources = [.. settings.CollapsedSources];

        var defaultOption = new DistributionOption(null, "Default distribution");
        Distributions.Add(defaultOption);
        _selectedDistribution = settings.Distribution is { Length: > 0 } configured ? AddOption(configured) : defaultOption;
        _listIntervalSeconds = settings.ListIntervalSeconds;
        _statsIntervalSeconds = settings.StatsIntervalSeconds;
        _maxLogLines = settings.MaxLogLines;
        _selectedAuthentication = AuthenticationOptions[0];
        _selectedHostInterval = HostIntervals[1];
        foreach (var host in settings.RemoteHosts)
        {
            RemoteHosts.Add(host.Clone());
        }

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

        Ignored.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IgnoredLinkText));
        RemoteHosts.CollectionChanged += OnRemoteHostsChanged;
        Systems.Add(CreateSystem(null));
        foreach (var host in RemoteHosts)
        {
            Systems.Add(CreateSystem(host));
        }
    }

    /// <summary>This computer first, then the other machines: everything Docker is watched on.</summary>
    public ObservableCollection<DockerSystemViewModel> Systems { get; } = [];

    public ObservableCollection<IgnoredVulnerability> Ignored { get; } = [];

    public IReadOnlyList<ThresholdOption> Thresholds { get; } =
    [
        new(ReportThreshold.Critical, "Critical"),
        new(ReportThreshold.High, "High and critical"),
        new(ReportThreshold.Never, "Never"),
    ];

    public string IgnoredLinkText => $"Manage ignored vulnerabilities ({Ignored.Count})";

    public string InstallDescription => TrivyScanner.InstallDescription;

    public bool CanInstallScanners => _shells is not null;

    public ObservableCollection<DistributionOption> Distributions { get; } = [];

    public ObservableCollection<DockerHost> RemoteHosts { get; } = [];

    /// <summary>Passwords entered in this session, by host id; written to the secret store when the settings are saved.</summary>
    public Dictionary<string, string> PendingPasswords { get; } = [];

    public IReadOnlyList<AuthenticationOption> AuthenticationOptions { get; } =
    [
        new(SshAuthentication.Agent, "SSH agent or ~/.ssh/config"),
        new(SshAuthentication.KeyFile, "Key file"),
        new(SshAuthentication.Password, "Password"),
    ];

    public IReadOnlyList<HostIntervalOption> HostIntervals { get; } =
    [
        new(10, "every 10 s"),
        new(20, "every 20 s"),
        new(30, "every 30 s"),
        new(60, "every minute"),
        new(300, "every 5 min"),
    ];

    public bool CanAddHosts => _ssh is not null;

    public bool UsesKeyFile => SelectedAuthentication.Authentication == SshAuthentication.KeyFile;

    public bool UsesPassword => SelectedAuthentication.Authentication == SshAuthentication.Password;

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

    public DockerWidgetSettings ToSettings() => new()
    {
        Distribution = SelectedDistribution.Name,
        ListIntervalSeconds = ListIntervalSeconds,
        StatsIntervalSeconds = StatsIntervalSeconds,
        MaxLogLines = MaxLogLines,
        RemoteHosts = [.. RemoteHosts.Select(host => host.Clone())],
        CollapsedSources = [.. _collapsedSources],
        Security = new DockerSecuritySettings
        {
            ScanLocalImages = ScanLocalImages,
            ReportFrom = SelectedThreshold.Threshold,
            DailyScanTime = DockerSecuritySettings.TryParseTime(DailyScanTime, out var time)
                ? time.ToString("HH:mm", CultureInfo.InvariantCulture)
                : throw new InvalidOperationException("Enter the time of the daily scan as hours and minutes, for example 12:30."),
            OnlyFixable = OnlyFixable,
            SkipOnBattery = SkipOnBattery,
            Ignored = [.. Ignored],
        },
    };

    [RelayCommand]
    private void ToggleIgnoredList() => IsIgnoredListOpen = !IsIgnoredListOpen;

    [RelayCommand]
    private void RestoreIgnored(IgnoredVulnerability ignored) => Ignored.Remove(ignored);

    [RelayCommand]
    private void AddHost()
    {
        _editing = null;
        LoadForm(new DockerHost(), "Add host");
    }

    [RelayCommand]
    private void EditHost(DockerHost host)
    {
        _editing = host;
        LoadForm(host, $"Edit {host.DisplayName}");
    }

    [RelayCommand]
    private void RemoveHost(DockerHost host)
    {
        RemoteHosts.Remove(host);
        PendingPasswords.Remove(host.Id);
        if (_editing == host)
        {
            IsFormOpen = false;
        }
    }

    [RelayCommand]
    private void CancelForm() => IsFormOpen = false;

    /// <summary>
    /// Connects to the host once and asks its Docker daemon for its version. Only a host that answers is put
    /// on the list: this first connection is also what makes SSH remember the host's key.
    /// </summary>
    [RelayCommand]
    private async Task ConnectAndCheckAsync()
    {
        if (_ssh is null || TryBuildHost() is not { } host)
        {
            return;
        }

        var password = Password.Length > 0 ? Password : null;
        if (host.Authentication == SshAuthentication.Password && password is null && !HasStoredPassword)
        {
            SetError("Enter the password.");
            return;
        }

        IsChecking = true;
        try
        {
            // A stored password is not known to this form, so such a host is taken over without a new check.
            if (host.Authentication != SshAuthentication.Password || password is not null)
            {
                await new SshContainerProvider(_ssh, () => host.ToTarget(password)).CheckAsync(CancellationToken.None);
            }

            if (password is not null)
            {
                PendingPasswords[host.Id] = password;
            }

            if (_editing is not null && RemoteHosts.IndexOf(_editing) is >= 0 and var index)
            {
                RemoteHosts[index] = host;
            }
            else
            {
                RemoteHosts.Add(host);
            }

            IsFormOpen = false;
        }
        catch (Exception exception) when (exception is ContainerProviderException or ArgumentException or InvalidOperationException)
        {
            var failure = ContainerFailureViewModel.From(exception, host.DisplayName);
            SetError($"{failure.Title}. {failure.Message}", failure.Detail);
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void LoadForm(DockerHost host, string title)
    {
        FormTitle = title;
        HostName = host.Name;
        HostAddress = host.Address;
        HostUser = host.User ?? string.Empty;
        HostPort = host.Port?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        SelectedAuthentication = AuthenticationOptions.First(option => option.Authentication == host.Authentication);
        KeyFile = host.KeyFile ?? string.Empty;
        Password = string.Empty;
        HasStoredPassword = PendingPasswords.ContainsKey(host.Id) || _isPasswordStored(host);
        SelectedHostInterval = HostIntervals.FirstOrDefault(option => option.Seconds >= host.IntervalSeconds) ?? HostIntervals[^1];
        AllowActions = host.AllowActions;
        ScanHostImages = host.ScanImages;
        SetError(null);
        IsFormOpen = true;
    }

    private DockerHost? TryBuildHost()
    {
        int? port = null;
        if (HostPort.Trim().Length > 0)
        {
            if (!int.TryParse(HostPort.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                SetError("The port must be a number.");
                return null;
            }

            port = parsed;
        }

        var host = new DockerHost
        {
            Id = _editing?.Id ?? Guid.NewGuid().ToString("N"),
            Name = HostName.Trim(),
            Address = HostAddress.Trim(),
            User = HostUser.Trim().Length > 0 ? HostUser.Trim() : null,
            Port = port,
            Authentication = SelectedAuthentication.Authentication,
            KeyFile = UsesKeyFile && KeyFile.Trim().Length > 0 ? KeyFile.Trim() : null,
            IntervalSeconds = SelectedHostInterval.Seconds,
            AllowActions = AllowActions,
            ScanImages = ScanHostImages,
        };

        try
        {
            host.ToTarget(password: null).Validate();
        }
        catch (ArgumentException exception)
        {
            SetError(exception.Message);
            return null;
        }

        SetError(null);
        return host;
    }

    private DockerSystemViewModel CreateSystem(DockerHost? host)
    {
        IImageScanner? scanner = _shells is null ? null
            : host is null ? new TrivyScanner(_shells.ForLocal(() => SelectedDistribution.Name))
            : new TrivyScanner(_shells.ForHost(host, PendingPasswords.GetValueOrDefault(host.Id)));

        // A scanner the user just installed is meant to be used, so scanning is switched on with it.
        var system = new DockerSystemViewModel(host, scanner, installed =>
        {
            if (installed.Host is { } remote)
            {
                remote.ScanImages = true;
            }
            else
            {
                ScanLocalImages = true;
            }
        });
        _ = system.DetectAsync();
        return system;
    }

    private void OnRemoteHostsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Hosts that stay keep their entry, and with it what is known about their scanner.
        foreach (var gone in Systems.Where(system => system.Host is { } host && !RemoteHosts.Contains(host)).ToList())
        {
            Systems.Remove(gone);
        }

        for (var index = 0; index < RemoteHosts.Count; index++)
        {
            if (Systems.All(system => system.Host != RemoteHosts[index]))
            {
                Systems.Insert(Math.Min(index + 1, Systems.Count), CreateSystem(RemoteHosts[index]));
            }
        }
    }

    partial void OnSelectedDistributionChanged(DistributionOption value)
    {
        // Another distribution is another machine as far as its programs go.
        if (Systems.Count > 0)
        {
            _ = Systems[0].DetectAsync();
        }
    }

    private void SetError(string? message, string? detail = null)
    {
        FormError = message;
        FormErrorDetail = detail;
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
