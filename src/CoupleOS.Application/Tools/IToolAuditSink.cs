using System.Text.Json;

namespace CoupleOS.Application.Tools;

/// <summary>
/// What lands in ai_actions. Recorded for every call including refusals —
/// TOOLS.md universal rule 3: "a rejected call is as interesting as a
/// successful one when debugging trust".
/// </summary>
public sealed record ToolAuditEntry
{
    public required string ToolName { get; init; }
    public required JsonElement Arguments { get; init; }
    public required ToolOutcome Outcome { get; init; }
    public required ToolExecutionContext Context { get; init; }
    public required TimeSpan Duration { get; init; }

    public string? EntityType { get; init; }
    public Guid? EntityId { get; init; }
    public string? Error { get; init; }

    /// <summary>
    /// What the tool reported back, as a JSON object, or null when it reported
    /// nothing but the row it wrote. Lands in <c>ai_actions.result</c>.
    /// </summary>
    public string? Result { get; init; }
}

/// <summary>
/// Persists the audit trail.
///
/// NOTE, owed before the first Confirm-tier tool ships: the action_outcome enum
/// in data/schema.sql has no 'confirmation_required' label. Every V0 tool is
/// tier None so nothing reaches it, but an implementation must not quietly map
/// that outcome onto some other label — that would make the audit trail lie
/// about what happened.
/// </summary>
public interface IToolAuditSink
{
    Task RecordAsync(ToolAuditEntry entry, CancellationToken cancellationToken = default);
}
