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
}

/// <summary>A tool call that has passed every gate and may now execute.</summary>
public sealed record ToolInvocation(JsonElement Arguments, ToolExecutionContext Context);

public sealed record ToolValidation(bool IsValid, IReadOnlyList<string> Errors)
{
    public static ToolValidation Valid { get; } = new(true, []);

    public static ToolValidation Invalid(params string[] errors) => new(false, errors);
}

/// <summary>What a tool did. Entity references let the change report cite real rows.</summary>
public sealed record ToolExecution(
    ToolOutcome Outcome,
    string? EntityType = null,
    Guid? EntityId = null,
    string? Error = null)
{
    public static ToolExecution Created(string entityType, Guid entityId) =>
        new(ToolOutcome.Success, entityType, entityId);

    public static ToolExecution Failed(string error) =>
        new(ToolOutcome.ExecutionFailed, Error: error);
}

/// <summary>The dispatcher's answer. Never an exception — a refusal is a result.</summary>
public sealed record ToolResult(
    string ToolName,
    ToolOutcome Outcome,
    string? EntityType = null,
    Guid? EntityId = null,
    IReadOnlyList<string>? Errors = null)
{
    public bool Succeeded => Outcome == ToolOutcome.Success;
}
