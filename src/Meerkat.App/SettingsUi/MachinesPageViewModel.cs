using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Meerkat.Ssh;

namespace Meerkat.App.SettingsUi;

/// <param name="Alias">Null stands for entering the connection by hand.</param>
public sealed record ConfigHostOption(string? Alias, string DisplayName);

/// <summary>
/// The page on which other machines are entered: what they are called and how to sign in to them.
/// What a widget does with a machine is chosen on the page of that widget.
/// </summary>
public sealed partial class MachinesPageViewModel : ObservableObject
{
    private const string CheckCommand = "echo meerkat";

    private readonly MachineRegistry _registry;
    private readonly ISshClient _ssh;

    /// <summary>The machine being edited; null while a new one is entered.</summary>
    private SshMachine? _edited;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormTitle))]
    private bool _isFormOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormTitle))]
    private bool _isEditing;

    [ObservableProperty]
    private ConfigHostOption _selectedConfigHost;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormTitle))]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _hostname = string.Empty;

    [ObservableProperty]
    private string _user = string.Empty;

    [ObservableProperty]
    private string _port = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesAgent), nameof(UsesKeyFile), nameof(UsesPassword))]
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
    private string _formStatus = string.Empty;

    [ObservableProperty]
    private bool _isFormError;

    /// <summary>Set after a failed check, to offer keeping the machine anyway.</summary>
    [ObservableProperty]
    private bool _canSaveUnchecked;

    /// <param name="configAliases">Hosts from ~/.ssh/config, offered for taking over.</param>
    public MachinesPageViewModel(MachineRegistry registry, ISshClient ssh, IReadOnlyList<string> configAliases)
    {
        _registry = registry;
        _ssh = ssh;

        var manual = new ConfigHostOption(null, "manual");
        ConfigHosts = [.. configAliases.Select(alias => new ConfigHostOption(alias, alias)), manual];
        _selectedConfigHost = manual;

        // The list also changes without this page: widgets hand over machines they used to keep for themselves.
        WeakEventManager<MachineRegistry, EventArgs>.AddHandler(registry, nameof(MachineRegistry.Changed), (_, _) => Reload());
        Reload();
    }

    public ObservableCollection<SshMachine> Machines { get; } = [];

    /// <summary>Hosts from ~/.ssh/config, followed by the option to enter one by hand.</summary>
    public IReadOnlyList<ConfigHostOption> ConfigHosts { get; }

    public bool HasConfigHosts => ConfigHosts.Count > 1;

    public bool HasMachines => Machines.Count > 0;

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

    public string FormTitle => !IsEditing ? "Add machine" : $"Edit {(Name.Trim().Length > 0 ? Name.Trim() : "machine")}";

    public string MachinesInfo =>
        "Machines are reached over SSH with the OpenSSH client of Windows, so your ~/.ssh/config, the SSH agent and known_hosts apply as in a terminal. Enter a machine once; the Docker and Server pages then choose what to use it for.";

    public string SignInInfo =>
        "SSH agent: uses the keys from your agent or ~/.ssh/config. Key file: the key must need no passphrase, or its passphrase must be loaded in the agent. Password: kept in the Windows Credential Manager, never in the settings file; where the machine allows it, a key is the safer choice.";

    public string CheckInfo =>
        "Connects once to make sure the entries work. This first connection is also what makes SSH remember the key of the machine; afterwards Meerkat only connects while that key stays the same.";

    [RelayCommand]
    private void AddMachine()
    {
        _edited = null;
        LoadForm(new SshMachine(), isEditing: false);
    }

    [RelayCommand]
    private void EditMachine(SshMachine machine)
    {
        _edited = machine;
        LoadForm(machine, isEditing: true);
    }

    [RelayCommand]
    private void CancelForm() => IsFormOpen = false;

    [RelayCommand]
    private void RemoveMachine()
    {
        IsFormOpen = false;
        if (_edited is { } machine)
        {
            _edited = null;
            _registry.Remove(machine.Id);
        }
    }

    /// <summary>Connects once to verify the entries, then adds the machine or stores the changes.</summary>
    [RelayCommand]
    private async Task ConnectAndCheckAsync()
    {
        if (!TryBuildMachine(out var machine))
        {
            return;
        }

        IsFormError = false;
        CanSaveUnchecked = false;
        FormStatus = "Connecting …";
        try
        {
            // Entering a machine is the explicit decision to trust it, so its key is accepted on first contact.
            // A key that differs from a stored one is still refused.
            var password = Password.Length > 0 ? Password : null;
            var target = password is null && machine.Authentication == SshAuthentication.Password
                ? _registry.GetTarget(machine)
                : machine.ToTarget(password);
            var result = await _ssh.RunAsync(target, CheckCommand, HostKeyPolicy.AcceptNew, CancellationToken.None);
            if (result.ExitCode != 0)
            {
                var failure = SshFailure.Classify(result.Error);
                Fail(failure.Fingerprint is null ? failure.Message : $"{failure.Message} New fingerprint: {failure.Fingerprint}");
                return;
            }

            Commit(machine);
        }
        catch (Exception exception) when (exception is Win32Exception or ArgumentException or InvalidOperationException)
        {
            Fail(exception.Message);
        }
    }

    [RelayCommand]
    private void SaveUnchecked()
    {
        if (TryBuildMachine(out var machine))
        {
            Commit(machine);
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

    private void Reload()
    {
        Machines.Clear();
        foreach (var machine in _registry.Machines)
        {
            Machines.Add(machine);
        }

        OnPropertyChanged(nameof(HasMachines));
    }

    private void Select(bool isSelected, SshAuthentication authentication)
    {
        if (isSelected)
        {
            Authentication = authentication;
        }
    }

    private void LoadForm(SshMachine machine, bool isEditing)
    {
        SelectedConfigHost = ConfigHosts[^1];
        Name = machine.Name;
        Hostname = machine.Address;
        User = machine.User ?? string.Empty;
        Port = machine.Port?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        Authentication = machine.Authentication;
        KeyFile = machine.KeyFile ?? string.Empty;
        Password = string.Empty;
        HasStoredPassword = isEditing && _registry.HasPassword(machine);
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

    private bool TryBuildMachine(out SshMachine machine)
    {
        machine = new SshMachine
        {
            Id = _edited?.Id ?? Guid.NewGuid().ToString("N"),
            Address = Hostname.Trim(),
            User = string.IsNullOrWhiteSpace(User) ? null : User.Trim(),
            Authentication = Authentication,
            KeyFile = UsesKeyFile && !string.IsNullOrWhiteSpace(KeyFile) ? KeyFile.Trim() : null,
        };
        machine.Name = string.IsNullOrWhiteSpace(Name) ? machine.Address : Name.Trim();

        string? error = null;
        if (string.IsNullOrWhiteSpace(Port))
        {
            machine.Port = null;
        }
        else if (int.TryParse(Port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            machine.Port = port;
        }
        else
        {
            error = "The port must be a number.";
        }

        if (error is null && UsesPassword && Password.Length == 0 && !HasStoredPassword)
        {
            error = "Enter the password.";
        }

        if (error is null)
        {
            try
            {
                machine.ToTarget().Validate();
            }
            catch (ArgumentException exception)
            {
                error = exception.Message;
            }
        }

        IsFormError = error is not null;
        FormStatus = error ?? string.Empty;
        CanSaveUnchecked = false;
        return error is null;
    }

    private void Fail(string message)
    {
        IsFormError = true;
        CanSaveUnchecked = true;
        FormStatus = message;
    }

    private void Commit(SshMachine machine)
    {
        var password = UsesPassword && Password.Length > 0 ? Password : null;
        _edited = null;
        Password = string.Empty;
        FormStatus = string.Empty;
        IsFormOpen = false;
        _registry.Save(machine, password);
    }
}
