using System.Text.Json.Serialization;
using Meerkat.Abstractions;

namespace Meerkat.Ssh;

/// <summary>
/// Another machine and how to sign in to it. Kept once for all widgets, which only say what they use
/// the machine for; that way a changed address or key is changed in one place.
/// </summary>
public sealed class SshMachine
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    /// <summary>A host name, an IP address or an alias from ~/.ssh/config.</summary>
    public string Address { get; set; } = string.Empty;

    public string? User { get; set; }

    public int? Port { get; set; }

    public SshAuthentication Authentication { get; set; }

    public string? KeyFile { get; set; }

    /// <summary>Under this key the password is kept in the secret store, never in the settings file.</summary>
    [JsonIgnore]
    public string SecretKey => $"machine/{Id}";

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Address : Name;

    /// <summary>The connection as one would type it for ssh: "user@host:port", with only the parts that are set.</summary>
    [JsonIgnore]
    public string Destination =>
        $"{(string.IsNullOrWhiteSpace(User) ? string.Empty : $"{User.Trim()}@")}{Address.Trim()}{(Port is { } port ? $":{port}" : string.Empty)}";

    public SshTarget ToTarget(string? password = null) => new(
        Address.Trim(),
        string.IsNullOrWhiteSpace(User) ? null : User.Trim(),
        Port,
        Authentication,
        string.IsNullOrWhiteSpace(KeyFile) ? null : KeyFile.Trim(),
        Authentication == SshAuthentication.Password ? password : null);

    /// <summary>Whether both describe the same connection, whatever they are called.</summary>
    public bool ConnectsLike(SshMachine other) =>
        string.Equals(Address.Trim(), other.Address.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(User?.Trim() ?? string.Empty, other.User?.Trim() ?? string.Empty, StringComparison.Ordinal)
        && Port == other.Port
        && Authentication == other.Authentication
        && string.Equals(KeyFile?.Trim() ?? string.Empty, other.KeyFile?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    public SshMachine Clone() => (SshMachine)MemberwiseClone();
}

public sealed class MachineSettings
{
    public List<SshMachine> Machines { get; set; } = [];
}

/// <summary>The machines the user entered, for every widget that reaches other machines over SSH.</summary>
public sealed class MachineRegistry
{
    public const string SettingsId = "machines";

    private readonly IWidgetSettingsStore _store;
    private readonly IWidgetSecretStore _secrets;
    private MachineSettings? _current;

    public MachineRegistry(IWidgetSettingsStore store, IWidgetSecretStore secrets)
    {
        _store = store;
        _secrets = secrets;

        // Not announced as a change: whoever uses the machines hears of the reload itself and reads them anew.
        store.Reloaded += (_, _) => _current = null;
    }

    /// <summary>Raised when a machine was added, changed or removed.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<SshMachine> Machines => Current.Machines;

    private MachineSettings Current => _current ??= _store.Load<MachineSettings>(SettingsId);

    public SshMachine? Find(string id) => Current.Machines.FirstOrDefault(machine => machine.Id == id);

    public bool HasPassword(SshMachine machine) => !string.IsNullOrEmpty(_secrets.Read(machine.SecretKey));

    /// <summary>The target to connect to, with the stored password where the machine signs in with one.</summary>
    public SshTarget GetTarget(SshMachine machine) =>
        machine.ToTarget(machine.Authentication == SshAuthentication.Password ? _secrets.Read(machine.SecretKey) : null);

    /// <summary>Adds the machine, or replaces the one with the same id.</summary>
    /// <param name="newPassword">Replaces the stored password; null or empty keeps it.</param>
    public void Save(SshMachine machine, string? newPassword = null)
    {
        if (machine.Authentication != SshAuthentication.Password)
        {
            _secrets.Delete(machine.SecretKey);
        }
        else if (!string.IsNullOrEmpty(newPassword))
        {
            _secrets.Write(machine.SecretKey, newPassword);
        }

        var machines = Current.Machines;
        var index = machines.FindIndex(existing => existing.Id == machine.Id);
        if (index >= 0)
        {
            machines[index] = machine;
        }
        else
        {
            machines.Add(machine);
        }

        Persist();
    }

    public void Remove(string id)
    {
        if (Find(id) is not { } machine)
        {
            return;
        }

        _secrets.Delete(machine.SecretKey);
        Current.Machines.Remove(machine);
        Persist();
    }

    /// <summary>
    /// Takes over a machine a widget used to keep for itself and returns the id it is known under from now
    /// on. A machine that connects the same way as one already known becomes that one, which is how the
    /// separate lists of the widgets grow together.
    /// </summary>
    /// <param name="previousSecretKey">Where the widget kept the machine's password.</param>
    public string Adopt(SshMachine machine, string previousSecretKey)
    {
        var password = _secrets.Read(previousSecretKey);
        _secrets.Delete(previousSecretKey);

        var known = Find(machine.Id) ?? Current.Machines.FirstOrDefault(existing => existing.ConnectsLike(machine));
        if (known is null)
        {
            Save(machine, password);
            return machine.Id;
        }

        if (!string.IsNullOrEmpty(password) && !HasPassword(known))
        {
            _secrets.Write(known.SecretKey, password);
        }

        return known.Id;
    }

    private void Persist()
    {
        _store.Save(SettingsId, Current);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
