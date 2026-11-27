using CommunityToolkit.Mvvm.ComponentModel;
using Meerkat.Widgets.Git.Providers;

namespace Meerkat.Widgets.Git.ViewModels;

public sealed partial class PipelineRowViewModel : ObservableObject
{
    private readonly GitTerminology _terminology;
    private readonly PipelineStage? _failedStage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    private string _timeText = string.Empty;

    public PipelineRowViewModel(PipelineRun run, IReadOnlyList<PipelineStage> stages, GitTerminology terminology, DateTimeOffset now)
    {
        Run = run;
        Stages = stages;
        _terminology = terminology;
        _failedStage = stages.FirstOrDefault(stage => stage.Status == PipelineStatus.Failed);
        UpdateTime(now);
    }

    public PipelineRun Run { get; }

    public IReadOnlyList<PipelineStage> Stages { get; }

    public PipelineStatus Status => Run.Status;

    public bool IsFailed => Status == PipelineStatus.Failed;

    public bool IsNotFailed => !IsFailed;

    /// <summary>The last path segment; the group or owner is the same for most rows and only adds noise.</summary>
    public string ProjectName => Run.Project.Name[(Run.Project.Name.LastIndexOf('/') + 1)..];

    public string RefText => string.IsNullOrEmpty(Run.WorkflowName) ? Run.Ref : $"{Run.Ref} · {Run.WorkflowName}";

    public string StatusText
    {
        get
        {
            switch (Status)
            {
                case PipelineStatus.Success:
                    return "successful";
                case PipelineStatus.Failed:
                    return "failed";
                case PipelineStatus.Running when Stages.Count > 0:
                    return $"running · {Stages.Count(stage => stage.Status == PipelineStatus.Success)}/{Stages.Count}";
                case PipelineStatus.Running:
                    return "running";
                case PipelineStatus.Waiting:
                    var waitingStage = Stages.FirstOrDefault(stage => stage.Status == PipelineStatus.Waiting);
                    return waitingStage is null ? "waiting" : $"waiting · {waitingStage.Name}";
                default:
                    return "canceled";
            }
        }
    }

    public string DetailText =>
        $"{(_failedStage is null ? "Failed" : $"Failed in {_failedStage.Name}")} · {_terminology.PipelineSingular} {Run.Number} · {TimeText}";

    /// <summary>The log of the failed job if the provider named one, otherwise the pipeline page.</summary>
    public string JobLogUrl => string.IsNullOrEmpty(_failedStage?.LogUrl) ? Run.WebUrl : _failedStage.LogUrl;

    public string WebUrl => Run.WebUrl;

    public void UpdateTime(DateTimeOffset now) =>
        TimeText = Status == PipelineStatus.Running
            ? $"for {RelativeTime.Format(now - (Run.StartedAt ?? Run.UpdatedAt))}"
            : $"{RelativeTime.Format(now - Run.UpdatedAt)} ago";
}

public sealed partial class ChangeRequestRowViewModel(ChangeRequest changeRequest, DateTimeOffset now) : ObservableObject
{
    [ObservableProperty]
    private string _timeText = $"{RelativeTime.Format(now - changeRequest.UpdatedAt)} ago";

    public string Reference => changeRequest.Reference;

    public string Title => changeRequest.Title;

    public string ProjectName => changeRequest.ProjectName;

    public string Author => changeRequest.Author;

    public string WebUrl => changeRequest.WebUrl;

    public void UpdateTime(DateTimeOffset now) => TimeText = $"{RelativeTime.Format(now - changeRequest.UpdatedAt)} ago";
}

public enum GitTab
{
    Pipelines,
    ReviewRequests,
    MyChangeRequests,
}

public sealed partial class GitTabViewModel(GitTab tab) : ObservableObject
{
    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Highlights the count, used for reviews that are waiting for the user.</summary>
    [ObservableProperty]
    private bool _needsAttention;

    public GitTab Tab { get; } = tab;
}
