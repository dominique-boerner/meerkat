namespace Meerkat.Abstractions;

/// <summary>
/// Stores secrets such as access tokens outside the settings file, protected by the operating system
/// for the current user.
/// </summary>
public interface IWidgetSecretStore
{
    /// <summary>Returns the secret stored under the key, or null if there is none.</summary>
    string? Read(string key);

    void Write(string key, string secret);

    void Delete(string key);
}
