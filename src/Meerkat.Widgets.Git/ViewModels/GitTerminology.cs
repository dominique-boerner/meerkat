using Meerkat.Widgets.Git.Providers;

namespace Meerkat.Widgets.Git.ViewModels;

/// <summary>The vocabulary of a provider; the widget's layout is the same for all of them.</summary>
public sealed record GitTerminology(
    string ProviderName,
    string PipelinesTitle,
    string PipelineSingular,
    string ChangeRequestsAbbreviation,
    string ProjectNoun,
    string ProjectsNoun,
    string TokenScopeHint)
{
    public string CountProjects(int count) => $"{count} {(count == 1 ? ProjectNoun : ProjectsNoun)}";

    public static GitTerminology For(GitProviderKind kind) => kind switch
    {
        GitProviderKind.GitHub => new GitTerminology(
            "GitHub", "Workflow runs", "Workflow run", "PRs", "repository", "repositories", "scopes repo and workflow"),
        _ => new GitTerminology(
            "GitLab", "Pipelines", "Pipeline", "MRs", "project", "projects", "scope read_api; api to retry pipelines"),
    };
}

public static class RelativeTime
{
    /// <summary>Formats a duration as a single rounded-down unit: "40 s", "6 min", "2 h", "3 d".</summary>
    public static string Format(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration switch
        {
            { TotalMinutes: < 1 } => $"{(int)duration.TotalSeconds} s",
            { TotalHours: < 1 } => $"{(int)duration.TotalMinutes} min",
            { TotalDays: < 1 } => $"{(int)duration.TotalHours} h",
            _ => $"{(int)duration.TotalDays} d",
        };
    }
}
