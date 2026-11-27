namespace Meerkat.Widgets.Git.Providers;

/// <summary>
/// What the widget needs from a Git hosting service. GitLab and GitHub differ in vocabulary and API,
/// but both map onto pipelines with stages and change requests.
/// </summary>
public interface IGitProvider : IDisposable
{
    GitProviderKind Kind { get; }

    /// <summary>Page where the user can create or renew an access token.</summary>
    Uri TokenPageUrl { get; }

    Task<string> GetUserNameAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<GitProject>> ListProjectsAsync(CancellationToken cancellationToken);

    /// <summary>Returns the latest run per branch of the given projects, newest first.</summary>
    Task<IReadOnlyList<PipelineRun>> ListPipelinesAsync(
        IReadOnlyList<GitProject> projects,
        bool onlyMine,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PipelineStage>> GetStagesAsync(PipelineRun run, CancellationToken cancellationToken);

    Task<IReadOnlyList<ChangeRequest>> ListReviewRequestsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ChangeRequest>> ListMyChangeRequestsAsync(CancellationToken cancellationToken);

    Task RetryAsync(PipelineRun run, CancellationToken cancellationToken);
}

public interface IGitProviderFactory
{
    /// <summary>Returns null when no access token is available yet.</summary>
    IGitProvider? Create(Settings.GitWidgetSettings settings, string? token);
}
