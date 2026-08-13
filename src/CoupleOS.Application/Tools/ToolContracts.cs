using System.Text.Json;
using CoupleOS.Domain.Enums;

namespace CoupleOS.Application.Tools;

/// <summary>ADR 0008's confirmation tiers.</summary>
public enum ToolTier
{
    /// <summary>Executes immediately. Cheap and reversible.</summary>
    None,

    /// <summary>Requires the user to confirm before executing.</summary>
    Confirm,

    /// <summary>Requires explicit authorisation beyond confirmation.</summary>
    Authorize,
}

/// <summary>
/// Mirrors the action_outcome enum in data/schema.sql, with one addition.
///
/// ConfirmationRequired has NO counterpart in the database enum, which lists
/// success, validation_failed, unauthorized, execution_failed and
/// cancelled_by_user. Every V0 tool is tier None so nothing hits it yet, but a
/// migration is owed before the first Confirm-tier tool ships — see the note in
/// IToolAuditSink.
/// </summary>
public enum ToolOutcome
{
    Success,
    ValidationFailed,
    Unauthorized,
    ConfirmationRequired,
    ExecutionFailed,
    CancelledByUser,
}

/// <summary>
/// Everything a tool is allowed to know about who is calling.
///
/// These values come from the session and the capture surface, never from the
/// model (TOOLS.md universal rules 2 and 5). A model that could set them could
/// write into another couple's data, or move a surprise into the shared scope.
/// </summary>
public sealed record ToolExecutionContext
{
    public required Guid CoupleId { get; init; }
    public required Guid UserId { get; init; }

    /// <summary>Determined by the capture surface (ADR 0009), never inferred from text.</summary>
    public required Visibility Visibility { get; init; }

    /// <summary>hash(message_id, tool_name, canonical_args). A retry must not double-create.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Set when the user has confirmed a Confirm-tier call.</summary>
    public bool IsConfirmed { get; init; }

    /// <summary>
    /// Which model proposed this call, for the audit trail. Null when a call did
    /// not come from a model at all — a future scheduled job or a direct API
    /// write is not attributable to one, and inventing a provider name for it
    /// would be worse than a null.
    ///
    /// A tool never reads this: it rides on the context because the context is
    /// what already reaches the audit sink, and adding a parameter to
    /// <c>IToolDispatcher</c> would make every caller carry something only the
    /// sink uses.
    /// </summary>
    public AI.LlmAttribution? Attribution { get; init; }
}

/// <summary>A tool call that has passed every gate and may now execute.</summary>
public sealed record ToolInvocation(JsonElement Arguments, ToolExecutionContext Context);

public sealed record ToolValidation(bool IsValid, IReadOnlyList<string> Errors)
{
    public static ToolValidation Valid { get; } = new(true, []);

    public static ToolValidation Invalid(params string[] errors) => new(false, errors);
}

/// <summary>What a tool did. Entity references let the change report cite real rows.</summary>
/// <param name="Question">
/// What the caller has to answer before this input can be acted on. Set only by
/// <see cref="RequestClarificationTool"/>, and the reason the pipeline can park a
/// block without knowing that tool by name — a capability in the result rather
/// than a string comparison on the tool's own.
/// </param>
/// <param name="Note">
/// Something the person should know about a call that succeeded — today, which
/// date an expression resolved to and what was filled in to get there.
///
/// Separate from <paramref name="Error"/> because it is not a failure, and
/// separate from <paramref name="Question"/> because it needs no answer. The rule
/// it exists for is <c>DateExpressionResolver</c>'s: completing an expression is
/// allowed and completing it silently is not, and a note that stops at the tool
/// boundary is silent as far as the couple is concerned.
/// </param>
/// <param name="Answer">
/// What a read-only tool found, already phrased for a person.
///
/// The third thing a successful call can be, after a row and a question, and the
/// channel <c>search_memory</c> could not ship without: a search's whole output is
/// what it found, so returning it as a <paramref name="Note"/> would file the
/// answer as a footnote to a change that did not happen.
///
/// Phrased in the tool rather than handed back as rows, deliberately. There is no
/// second model call in V0 — one completion, then execution — so a model's prose
/// is written *before* any search runs, and letting it narrate results it never saw
/// is how a system states a memory the couple does not have. So the answer is
/// rendered where the rows are, and it outranks the model's own words on the
/// surface that shows a reply.
/// </param>
public sealed record ToolExecution(
    ToolOutcome Outcome,
    string? EntityType = null,
    Guid? EntityId = null,
    string? Error = null,
    string? Question = null,
    string? Note = null,
    string? Answer = null)
{
    public static ToolExecution Created(string entityType, Guid entityId, string? note = null) =>
        new(ToolOutcome.Success, entityType, entityId, Note: note);

    public static ToolExecution Failed(string error) =>
        new(ToolOutcome.ExecutionFailed, Error: error);

    /// <summary>
    /// The call was right and there was nothing to write.
    ///
    /// Today: a memory the couple already holds, word for word, on the same
    /// subject (SPEC.md §44). A failure would be wrong — nothing went wrong — and
    /// <see cref="Created"/> would be worse, because it would report a row and
    /// count an entity for a statement that changed nothing.
    /// </summary>
    public static ToolExecution Unchanged(string note) =>
        new(ToolOutcome.Success, Note: note);

    /// <summary>Succeeded at looking, and wrote nothing.</summary>
    public static ToolExecution Found(string answer) =>
        new(ToolOutcome.Success, Answer: answer);

    /// <summary>
    /// Succeeded at asking, and wrote nothing.
    ///
    /// The outcome is Success because the call did what it was for: TOOLS.md 2a
    /// makes this the only legal way to decline, so recording it as a failure
    /// would put an error beside the one thing the model got right. Nothing was
    /// created, which is why no entity comes with it.
    /// </summary>
    public static ToolExecution Asks(string question) =>
        new(ToolOutcome.Success, Question: question);
}

/// <summary>The dispatcher's answer. Never an exception — a refusal is a result.</summary>
public sealed record ToolResult(
    string ToolName,
    ToolOutcome Outcome,
    string? EntityType = null,
    Guid? EntityId = null,
    IReadOnlyList<string>? Errors = null,
    string? Question = null,
    string? Note = null,
    string? Answer = null)
{
    public bool Succeeded => Outcome == ToolOutcome.Success;

    /// <summary>
    /// True when this call asked rather than wrote. Read by the block pipeline to
    /// park the block, and by the report to keep a question out of the list of
    /// things that were added.
    /// </summary>
    public bool Asked => Question is { Length: > 0 };

    /// <summary>
    /// True when this call looked rather than wrote. Read by the private thread,
    /// where an answer takes precedence over the model's own prose — the prose was
    /// composed before the search ran.
    /// </summary>
    public bool Found => Answer is { Length: > 0 };
}
