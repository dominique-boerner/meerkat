using System.Text.Json.Serialization;

namespace Meerkat.Widgets.Git.Providers;

[JsonConverter(typeof(JsonStringEnumConverter<GitProviderKind>))]
public enum GitProviderKind
{
    GitLab,
    GitHub,
}

public enum PipelineStatus
{
    Success,
    Failed,
    Running,

    /// <summary>Queued, scheduled or waiting for a manual action.</summary>
    Waiting,

    /// <summary>Canceled, skipped or not started.</summary>
    Canceled,
}

/// <param name="Id">The identifier the provider's API expects: a numeric id on GitLab, "owner/name" on GitHub.</param>
public sealed record GitProject(string Id, string Name);

/// <summary>A pipeline on GitLab or a workflow run on GitHub.</summary>
public sealed record PipelineRun(
    GitProject Project,
    string Id,
    string Number,
    string Ref,
    string? WorkflowName,
    PipelineStatus Status,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    string WebUrl);

/// <summary>A stage on GitLab or a job on GitHub.</summary>
public sealed record PipelineStage(string Name, PipelineStatus Status, string? LogUrl);

/// <summary>A merge request on GitLab or a pull request on GitHub.</summary>
public sealed record ChangeRequest(
    string ProjectName,
    string Reference,
    string Title,
    string Author,
    DateTimeOffset UpdatedAt,
    string WebUrl);

public enum GitFailureKind
{
    Other,

    /// <summary>The access token is missing, expired or revoked.</summary>
    Unauthorized,
    RateLimited,
    Unreachable,
}

public sealed class GitProviderException(GitFailureKind kind, string message, DateTimeOffset? retryAt = null)
    : Exception(message)
{
    public GitFailureKind Kind { get; } = kind;

    /// <summary>For rate limits: when the provider accepts requests again.</summary>
    public DateTimeOffset? RetryAt { get; } = retryAt;
}
