using System.Globalization;
using Meerkat.Widgets.Git.Providers;

namespace Meerkat.Widgets.Git.ViewModels;

public enum GitFailureIcon
{
    Error,
    Key,
    Clock,
}

public sealed record GitFailureViewModel(
    string Title,
    string Message,
    GitFailureIcon Icon,
    bool CanRenewToken,
    bool CanOpenSettings)
{
    public static GitFailureViewModel NotConfigured(GitTerminology terminology) => new(
        $"Connect {terminology.ProviderName}",
        $"Add a personal access token in the settings to see your {terminology.PipelinesTitle.ToLowerInvariant()} and reviews.",
        GitFailureIcon.Key,
        CanRenewToken: false,
        CanOpenSettings: true);

    /// <param name="lastRefreshed">When data was last loaded successfully; null if never.</param>
    public static GitFailureViewModel From(Exception exception, GitTerminology terminology, DateTimeOffset? lastRefreshed)
    {
        var staleHint = lastRefreshed is { } time
            ? $" The last data from {FormatTime(time)} is still shown greyed out."
            : string.Empty;

        if (exception is not GitProviderException failure)
        {
            return new GitFailureViewModel(
                $"{terminology.ProviderName} request failed",
                exception.Message + staleHint,
                GitFailureIcon.Error,
                CanRenewToken: false,
                CanOpenSettings: true);
        }

        return failure.Kind switch
        {
            GitFailureKind.Unauthorized => new GitFailureViewModel(
                "Access token expired or invalid",
                failure.Message + staleHint,
                GitFailureIcon.Key,
                CanRenewToken: true,
                CanOpenSettings: true),
            GitFailureKind.RateLimited => new GitFailureViewModel(
                $"{terminology.ProviderName}: rate limit reached",
                failure.RetryAt is { } retryAt
                    ? $"Next refresh at {FormatTime(retryAt)}. The widget pauses until then."
                    : "The widget pauses and tries again later.",
                GitFailureIcon.Clock,
                CanRenewToken: false,
                CanOpenSettings: false),
            GitFailureKind.Unreachable => new GitFailureViewModel(
                $"{terminology.ProviderName} is not reachable",
                failure.Message + staleHint,
                GitFailureIcon.Error,
                CanRenewToken: false,
                CanOpenSettings: true),
            _ => new GitFailureViewModel(
                $"{terminology.ProviderName} request failed",
                failure.Message + staleHint,
                GitFailureIcon.Error,
                CanRenewToken: false,
                CanOpenSettings: true),
        };
    }

    private static string FormatTime(DateTimeOffset time) => time.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
}
