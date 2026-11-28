using Meerkat.Abstractions;

namespace Meerkat.Widgets.Docker.ViewModels;

/// <summary>What the Docker widget offers in the action search of the shell.</summary>
internal static class DockerActions
{
    public static IReadOnlyList<WidgetAction> Create(DockerWidgetViewModel page, IOverlayCommands commands)
    {
        var actions = new List<WidgetAction>
        {
            Control(page, ContainerOperation.Restart, "restart-container", "Restart container", _ => true),
            new()
            {
                Id = "show-logs",
                Title = "Show container logs",
                TargetPrompt = "Choose a container …",
                Targets = () => ListTargets(page, _ => true, changes: false),
                Run = (target, _) =>
                {
                    if (Find(page, target) is not var (source, container))
                    {
                        return Task.FromResult(Gone(target));
                    }

                    page.ShowContainersCommand.Execute(null);
                    source.ShowLogsCommand.Execute(container);
                    commands.ShowWidget(DockerWidget.WidgetId);
                    return Task.FromResult(WidgetActionResult.Done);
                },
            },
            Control(page, ContainerOperation.Stop, "stop-container", "Stop container", container => container.IsRunning),
            Control(page, ContainerOperation.Start, "start-container", "Start container", container => !container.IsRunning),
        };

        if (page.Security is { } security && security.Sources.Any(source => source.IsScanned))
        {
            actions.Add(new WidgetAction
            {
                Id = "scan-images",
                Title = "Scan images now",
                Run = (_, _) =>
                {
                    // A scan takes minutes; it is started here and followed on the page and in the status bar.
                    security.ScanNowCommand.Execute(null);
                    return Task.FromResult(WidgetActionResult.Success("The images are being scanned"));
                },
            });
        }

        return actions;
    }

    private static WidgetAction Control(DockerWidgetViewModel page, ContainerOperation operation, string id, string title, Func<ContainerRowViewModel, bool> applies) => new()
    {
        Id = id,
        Title = title,
        Hint = page.Sources.Any(source => source.IsRemote) ? "local and other machines" : null,
        TargetPrompt = "Choose a container …",
        Targets = () => ListTargets(page, applies, changes: true),

        // On this computer the user only disturbs themselves; on another machine others may be working.
        Confirm = target => Find(page, target) is var (source, container) && source.IsRemote
            ? new WidgetActionConfirmation(
                $"{title.Split(' ')[0]} {container.Name} on {source.Label}?",
                "This is another machine. Others who work there will notice it.",
                title.Split(' ')[0])
            : null,
        Run = (target, _) => Find(page, target) is var (source, container)
            ? source.ControlAsync(container, operation)
            : Task.FromResult(Gone(target)),
    };

    private static IReadOnlyList<WidgetActionTargetGroup> ListTargets(DockerWidgetViewModel page, Func<ContainerRowViewModel, bool> applies, bool changes) =>
    [
        .. page.Sources
            .Select(source => new WidgetActionTargetGroup(
                DescribeSource(source, changes),
                [
                    .. source.Containers.Where(applies).Select(container => new WidgetActionTarget(
                        $"{source.Id}/{container.Id}",
                        container.Name,
                        container.Status,
                        container.StatusKind switch
                        {
                            ContainerStatusKind.Running => WidgetSeverity.Ok,
                            ContainerStatusKind.Stopped => WidgetSeverity.Error,
                            _ => WidgetSeverity.Warning,
                        },
                        changes && !source.CanControl ? "not allowed" : null)),
                ]))
            .Where(group => group.Targets.Count > 0),
    ];

    private static string DescribeSource(ContainerSourceViewModel source, bool changes) =>
        !source.IsRemote ? $"{source.Label} · local"
        : !changes ? source.Label
        : source.CanControl ? $"{source.Label} · may be controlled"
        : $"{source.Label} · read only";

    private static (ContainerSourceViewModel Source, ContainerRowViewModel Container)? Find(DockerWidgetViewModel page, WidgetActionTarget? target)
    {
        foreach (var source in page.Sources)
        {
            if (source.Containers.FirstOrDefault(container => $"{source.Id}/{container.Id}" == target?.Id) is { } container)
            {
                return (source, container);
            }
        }

        return null;
    }

    private static WidgetActionResult Gone(WidgetActionTarget? target) =>
        WidgetActionResult.Failure($"{target?.Title ?? "The container"} is no longer there");
}