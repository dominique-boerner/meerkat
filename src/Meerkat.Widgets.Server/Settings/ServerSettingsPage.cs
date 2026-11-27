using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meerkat.Abstractions;
using Meerkat.Widgets.Server.Monitoring;
using Meerkat.Ssh;
using Meerkat.Widgets.Server.Views;

namespace Meerkat.Widgets.Server.Settings;

public sealed class ServerSettingsPage(ServerSettingsSource settingsSource, IServerProbe probe, IWidgetSecretStore secrets) : IWidgetSettingsPage
{
    private ServerSettingsPageViewModel? _viewModel;

    public FrameworkElement CreateView()
    {
        _viewModel = new ServerSettingsPageViewModel(
            settingsSource.Current,
            probe,
            SshConfigFile.ReadHostAliases(SshConfigFile.DefaultPath),
            host => !string.IsNullOrEmpty(secrets.Read(host.SecretKey)));
        return new ServerSettingsView { DataContext = _viewModel };
    }

    public void Apply()
    {
        if (_viewModel is null)
        {
            return;
        }

        // Passwords travel separately from the settings: they go to the secret store, never into the file.
        var settings = _viewModel.ToSettings();
        foreach (var (hostId, password) in _viewModel.PendingPasswords)
        {
            if (settings.Hosts.FirstOrDefault(host => host.Id == hostId) is { } host)
            {
                secrets.Write(host.SecretKey, password);
            }
        }

        var obsolete = settingsSource.Current.Hosts
            .Where(previous => settings.Hosts.FirstOrDefault(host => host.Id == previous.Id) is not { Authentication: SshAuthentication.Password });
        foreach (var host in obsolete)
        {
            secrets.Delete(host.SecretKey);
        }

        settingsSource.Update(settings);
    }
}

/// <param name="Seconds">Null stands for the widget's default interval.</param>
public sealed record IntervalOption(double? Seconds, string DisplayName)
{
    public static string Describe(double seconds) => seconds >= 60 && seconds % 60 == 0
        ? $"every {(seconds / 60).ToString("0", CultureInfo.CurrentCulture)} min"
        : $"every {seconds.ToString("0", CultureInfo.CurrentCulture)} s";
}

/// <param name="Alias">Null stands for entering the connection by hand.</param>
public sealed record ConfigHostOption(string? Alias, string DisplayName);

public sealed class HostRowViewModel(ServerHost host)
{
    public ServerHost Host { get; } = host;

    public string Name => Host.Name;

    public string Address => Host.Address;

    public bool IsPaused => Host.IsPaused;

    public string IntervalText => Host.IsPaused ? "paused"
        : Host.IntervalSeconds is { } seconds ? IntervalOption.Describe(seconds)
        : "Default";

    public string ServicesText => Host.Services.Count == 0 ? "–" : string.Join(", ", Host.Services);
}

public sealed partial class ServerSettingsPageViewModel : ObservableObject
{
    private static readonly double[] IntervalChoices = [15, 30, 60, 300, 900];

    private readonly IServerProbe _probe;
    private readonly Func<ServerHost, bool> _isPasswordStored;
    private readonly Dictionary<string, string> _pendingPasswords = [];

    /// <summary>The host being edited; null while a new one is entered.</summary>
    private ServerHost? _editedHost;

    [ObservableProperty]
    private IntervalOption _defaultInterval;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormTitle))]
    private bool _isFormOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormTitle))]
    private bool _isEditing;

    [ObservableProperty]
    private ConfigHostOption _selectedConfigHost;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _hostname = string.Empty;

    [ObservableProperty]
    private string _user = string.Empty;

    [ObservableProperty]
    private string _port = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesAgent))]
    [NotifyPropertyChangedFor(nameof(UsesKeyFile))]
    [NotifyPropertyChangedFor(nameof(UsesPassword))]
    private SshAuthentication _authentication;

    [ObservableProperty]
    private string _keyFile = string.Empty;

    /// <summary>A newly entered password; empty keeps the stored one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStoredPasswordPlaceholder))]
    private string _password = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStoredPasswordPlaceholder))]
    private bool _hasStoredPassword;

    [ObservableProperty]
    private string _servicesText = string.Empty;

    [ObservableProperty]
    private IntervalOption _selectedInterval;

    [ObservableProperty]
    private double _diskWarningPercent = 85;

    [ObservableProperty]
    private double _diskErrorPercent = 95;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private string _formStatus = string.Empty;

    [ObservableProperty]
    private bool _isFormError;

    /// <summary>Set after a failed check, to offer adding the host anyway.</summary>
    [ObservableProperty]
    private bool _canSaveUnchecked;

    [ObservableProperty]
    private string _lastCheckResult = string.Empty;

    /// <param name="hasStoredPassword">Tells whether a password is already stored for a host.</param>
    public ServerSettingsPageViewModel(
        ServerWidgetSettings settings,
        IServerProbe probe,
        IReadOnlyList<string> configAliases,
        Func<ServerHost, bool> hasStoredPassword)
    {
        _probe = probe;
        _isPasswordStored = hasStoredPassword;

        DefaultIntervals = [.. IntervalChoices.Select(seconds => new IntervalOption(seconds, IntervalOption.Describe(seconds)))];
        _defaultInterval = DefaultIntervals.FirstOrDefault(option => option.Seconds == settings.DefaultIntervalSeconds)
            ?? AddOption(DefaultIntervals, settings.DefaultIntervalSeconds);

        HostIntervals = [new IntervalOption(null, "Default"), .. IntervalChoices.Select(seconds => new IntervalOption(seconds, IntervalOption.Describe(seconds)))];
        _selectedInterval = HostIntervals[0];

        var manual = new ConfigHostOption(null, "manual");
        ConfigHosts = [.. configAliases.Select(alias => new ConfigHostOption(alias, alias)), manual];
        _selectedConfigHost = manual;

        foreach (var host in settings.Hosts)
        {
            Hosts.Add(new HostRowViewModel(host.Clone()));
        }
    }

    public List<IntervalOption> DefaultIntervals { get; }

    public List<IntervalOption> HostIntervals { get; }

    /// <summary>Hosts from ~/.ssh/config, followed by the option to enter one by hand.</summary>
    public IReadOnlyList<ConfigHostOption> ConfigHosts { get; }

    public bool HasConfigHosts => ConfigHosts.Count > 1;

    public ObservableCollection<HostRowViewModel> Hosts { get; } = [];

    /// <summary>Passwords entered in this session by host id; written to the secret store when the settings are saved.</summary>
    public IReadOnlyDictionary<string, string> PendingPasswords => _pendingPasswords;

    // One property per radio button. Each only reacts to being selected; deselecting follows from the others.
    public bool UsesAgent
    {
        get => Authentication == SshAuthentication.Agent;
        set => Select(value, SshAuthentication.Agent);
    }

    public bool UsesKeyFile
    {
        get => Authentication == SshAuthentication.KeyFile;
        set => Select(value, SshAuthentication.KeyFile);
    }

    public bool UsesPassword
    {
        get => Authentication == SshAuthentication.Password;
        set => Select(value, SshAuthentication.Password);
    }

    /// <summary>The stored password is never read back into the page; dots stand in for it.</summary>
    public bool ShowStoredPasswordPlaceholder => HasStoredPassword && Password.Length == 0;

    public string FormTitle => IsEditing ? "Edit host" : "Add host";

    public ServerWidgetSettings ToSettings() => new()
    {
        DefaultIntervalSeconds = DefaultInterval.Seconds ?? 30,
        Hosts = [.. Hosts.Select(row => row.Host)],
    };

    [RelayCommand]
    private void AddHost()
    {
        _editedHost = null;
        LoadForm(new ServerHost(), isEditing: false);
    }

    [RelayCommand]
    private void EditHost(HostRowViewModel row)
    {
        _editedHost = row.Host;
        LoadForm(row.Host, isEditing: true);
    }

    [RelayCommand]
    private void CancelForm() => IsFormOpen = false;

    [RelayCommand]
    private void RemoveHost()
    {
        if (Hosts.FirstOrDefault(row => row.Host == _editedHost) is { } row)
        {
            Hosts.Remove(row);
            _pendingPasswords.Remove(row.Host.Id);
        }

        IsFormOpen = false;
    }

    /// <summary>Connects once to verify the entries, then adds the host or applies the changes.</summary>
    [RelayCommand]
    private async Task ConnectAndCheckAsync()
    {
        if (!TryBuildHost(out var host))
        {
            return;
        }

        IsFormError = false;
        CanSaveUnchecked = false;
        FormStatus = "Connecting …";
        try
        {
            // Adding a host is the explicit decision to trust it, so its key is accepted on first contact.
            // A key that differs from a stored one is still refused.
            var password = Password.Length > 0 ? Password : _pendingPasswords.GetValueOrDefault(host.Id);
            var snapshot = await _probe.ProbeAsync(host, HostKeyPolicy.AcceptNew, CancellationToken.None, password);
            var inactive = snapshot.Services.Where(service => !service.IsActive).Select(service => service.Name).ToList();
            Commit(host);
            LastCheckResult = inactive.Count == 0
                ? $"{host.Name}: connected ({snapshot.OsName})."
                : $"{host.Name}: connected ({snapshot.OsName}). Not active: {string.Join(", ", inactive)}.";
        }
        catch (Exception exception)
        {
            IsFormError = true;
            CanSaveUnchecked = true;
            FormStatus = exception.Message;
        }
    }

    [RelayCommand]
    private void SaveUnchecked()
    {
        if (TryBuildHost(out var host))
        {
            Commit(host);
        }
    }

    partial void OnSelectedConfigHostChanged(ConfigHostOption value)
    {
        // An alias carries its own user, port and key in ~/.ssh/config, so those fields stay empty.
        if (value.Alias is { } alias)
        {
            Name = alias;
            Hostname = alias;
            User = string.Empty;
            Port = string.Empty;
            Authentication = SshAuthentication.Agent;
        }
    }

    private void Select(bool isSelected, SshAuthentication authentication)
    {
        if (isSelected)
        {
            Authentication = authentication;
        }
    }

    private void LoadForm(ServerHost host, bool isEditing)
    {
        SelectedConfigHost = ConfigHosts[^1];
        Name = host.Name;
        Hostname = host.Address;
        User = host.User ?? string.Empty;
        Port = host.Port?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        Authentication = host.Authentication;
        KeyFile = host.KeyFile ?? string.Empty;
        Password = string.Empty;
        HasStoredPassword = isEditing && (_pendingPasswords.ContainsKey(host.Id) || _isPasswordStored(host));
        ServicesText = string.Join(", ", host.Services);
        SelectedInterval = HostIntervals.FirstOrDefault(option => option.Seconds == host.IntervalSeconds)
            ?? AddOption(HostIntervals, host.IntervalSeconds ?? 30);
        DiskWarningPercent = host.DiskWarningPercent;
        DiskErrorPercent = host.DiskErrorPercent;
        IsPaused = host.IsPaused;
        FormStatus = string.Empty;
        IsFormError = false;
        CanSaveUnchecked = false;
        IsEditing = isEditing;
        IsFormOpen = true;

        // The form's controls may have been created while it was hidden; announce the selection once more
        // so that exactly one sign-in method shows as selected.
        OnPropertyChanged(nameof(UsesAgent));
        OnPropertyChanged(nameof(UsesKeyFile));
        OnPropertyChanged(nameof(UsesPassword));
    }

    private bool TryBuildHost(out ServerHost host)
    {
        host = new ServerHost
        {
            Id = _editedHost?.Id ?? Guid.NewGuid().ToString("N"),
            Address = Hostname.Trim(),
            User = string.IsNullOrWhiteSpace(User) ? null : User.Trim(),
            Authentication = Authentication,
            KeyFile = UsesKeyFile && !string.IsNullOrWhiteSpace(KeyFile) ? KeyFile.Trim() : null,
            Services = [.. ServicesText.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            IntervalSeconds = SelectedInterval.Seconds,
            DiskWarningPercent = DiskWarningPercent,
            DiskErrorPercent = DiskErrorPercent,
            IsPaused = IsPaused,
        };
        host.Name = string.IsNullOrWhiteSpace(Name) ? host.Address : Name.Trim();

        string? error = null;
        if (string.IsNullOrWhiteSpace(Port))
        {
            host.Port = null;
        }
        else if (int.TryParse(Port, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            host.Port = port;
        }
        else
        {
            error = "The port must be a number.";
        }

        if (error is null && host.DiskWarningPercent > host.DiskErrorPercent)
        {
            error = "The yellow disk threshold must not be above the red one.";
        }

        if (error is null && UsesPassword && Password.Length == 0 && !HasStoredPassword)
        {
            error = "Enter the password.";
        }

        if (error is null)
        {
            try
            {
                host.ToTarget().Validate();
                ServerProbe.BuildScript(host.Services);
            }
            catch (Exception exception) when (exception is ArgumentException or ProbeException)
            {
                error = exception.Message;
            }
        }

        IsFormError = error is not null;
        FormStatus = error ?? string.Empty;
        CanSaveUnchecked = false;
        return error is null;
    }

    private void Commit(ServerHost host)
    {
        if (UsesPassword && Password.Length > 0)
        {
            _pendingPasswords[host.Id] = Password;
        }
        else if (!UsesPassword)
        {
            _pendingPasswords.Remove(host.Id);
        }

        var index = Hosts.ToList().FindIndex(row => row.Host == _editedHost);
        if (index >= 0)
        {
            Hosts[index] = new HostRowViewModel(host);
        }
        else
        {
            Hosts.Add(new HostRowViewModel(host));
        }

        _editedHost = null;
        Password = string.Empty;
        FormStatus = string.Empty;
        LastCheckResult = string.Empty;
        IsFormOpen = false;
    }

    private static IntervalOption AddOption(List<IntervalOption> options, double seconds)
    {
        var option = new IntervalOption(seconds, IntervalOption.Describe(seconds));
        options.Add(option);
        return option;
    }
}
