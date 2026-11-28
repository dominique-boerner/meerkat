namespace Meerkat.Abstractions;

/// <summary>
/// Something a widget does on request. The shell offers the actions of all widgets in a search that
/// opens above any window, so that the user can act without opening the panel first.
/// </summary>
public sealed class WidgetAction
{
    /// <summary>Identifies the action within the widget; what the user did last is remembered under it.</summary>
    public required string Id { get; init; }

    /// <summary>What the action does, as a request: "Restart container". The shell marks actions that ask for a target.</summary>
    public required string Title { get; init; }

    /// <summary>A few words next to the title that tell what the action would meet right now, such as "2 open".</summary>
    public string? Hint { get; init; }

    /// <summary>
    /// What the action can be applied to, in groups; null for an action that needs nothing more. Read
    /// when the user chose the action, so it may reflect the state of that moment.
    /// </summary>
    public Func<IReadOnlyList<WidgetActionTargetGroup>>? Targets { get; init; }

    /// <summary>What the search field asks for while a target is chosen, such as "Choose a container".</summary>
    public string? TargetPrompt { get; init; }

    /// <summary>
    /// Asked before the action runs; returning null runs it at once. Ask for what cannot be undone
    /// and for what others would notice, not for everything.
    /// </summary>
    public Func<WidgetActionTarget?, WidgetActionConfirmation?>? Confirm { get; init; }

    /// <summary>Does it. Called on the UI thread with the chosen target, or null for an action without targets.</summary>
    public required Func<WidgetActionTarget?, CancellationToken, Task<WidgetActionResult>> Run { get; init; }

    /// <summary>
    /// Takes what the user typed when no action matched it, for an action that can make use of any
    /// text, such as saving it as a note. <see cref="TextTitle"/> says what will happen to the text.
    /// </summary>
    public Func<string, CancellationToken, Task<WidgetActionResult>>? RunWithText { get; init; }

    /// <summary>What <see cref="RunWithText"/> does with the text, as a request: "Save as quick note".</summary>
    public string? TextTitle { get; init; }
}

/// <summary>Targets that belong together, such as the containers of one machine.</summary>
public sealed record WidgetActionTargetGroup(string Title, IReadOnlyList<WidgetActionTarget> Targets);

/// <summary>Something an action can be applied to.</summary>
/// <param name="Id">Identifies the target within the action.</param>
/// <param name="Hint">A few words about its state, such as "running".</param>
/// <param name="Severity">Shown as a coloured dot in front of the title; null shows none.</param>
/// <param name="Unavailable">
/// Why the action cannot be applied to this target, such as "not allowed". The target is listed, so
/// that the user sees it was not forgotten, but cannot be chosen.
/// </param>
public sealed record WidgetActionTarget(string Id, string Title, string? Hint = null, WidgetSeverity? Severity = null, string? Unavailable = null);

/// <summary>What the user is asked before an action runs.</summary>
/// <param name="Question">What is about to happen, as a question: "Delete unused images?".</param>
/// <param name="Detail">What it means: what is lost, or who else is affected.</param>
/// <param name="ConfirmText">The word on the button that goes ahead, such as "Delete".</param>
public sealed record WidgetActionConfirmation(string Question, string Detail, string ConfirmText);

/// <summary>How an action ended.</summary>
/// <param name="Message">What to tell the user; null closes the search without a word, as after opening something.</param>
/// <param name="Detail">On failure, the reason.</param>
public sealed record WidgetActionResult(bool Succeeded, string? Message = null, string? Detail = null)
{
    /// <summary>The action did what it says and the result shows itself, as a page that opened.</summary>
    public static WidgetActionResult Done { get; } = new(true);

    public static WidgetActionResult Success(string message) => new(true, message);

    public static WidgetActionResult Failure(string message, string? detail = null) => new(false, message, detail);
}
