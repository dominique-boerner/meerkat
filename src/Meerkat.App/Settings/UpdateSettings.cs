using System.Text.Json.Serialization;

namespace Meerkat.App.Settings;

[JsonConverter(typeof(JsonStringEnumConverter<UpdateChannel>))]
public enum UpdateChannel
{
    Stable,

    /// <summary>Also offers versions with a suffix such as 1.2.0-beta.1.</summary>
    Prerelease,
}

public sealed class UpdateSettings
{
    public bool CheckAutomatically { get; set; } = true;

    public bool DownloadInBackground { get; set; } = true;

    public UpdateChannel Channel { get; set; } = UpdateChannel.Stable;
}
