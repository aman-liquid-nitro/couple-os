using CoupleOS.Domain.Enums;

namespace CoupleOS.Domain.Entities;

/// <summary>
/// One attempted tool call. Written whether or not it succeeded — TOOLS.md
/// universal rule 3.
/// </summary>
public sealed class AiAction
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid CoupleId { get; init; }
    public required Guid UserId { get; init; }

    public required string ToolName { get; init; }

    /// <summary>Stored as jsonb so a bad call can be replayed and diffed later.</summary>
    public required string Arguments { get; init; }

    public required ActionOutcome Outcome { get; init; }

    /// <summary>
    /// What the tool reported back, as jsonb. Null when it reported nothing but
    /// the row it wrote.
    ///
    /// `Arguments` is the input and `Outcome` is the outcome; without this,
    /// V0_SCOPE's "input, output and outcome" was two of three, and
    /// `entity_id` is a pointer rather than a record — it says *which* shopping
    /// item, not what the tool said about it, and it is null for the two tools
    /// that create nothing (`search_memory`, `request_clarification`).
    ///
    /// This can carry the couple's own text into a second table. It is safe
    /// here and would not be everywhere: the `couple_scope` policy on
    /// `ai_actions` is `couple_id = app_current_couple() AND user_id =
    /// app_current_user()`, so a row is readable only by the person whose call
    /// it was — a private answer cannot reach the partner through it.
    /// </summary>
    public string? Result { get; init; }

    public string? IdempotencyKey { get; init; }
    public string? EntityType { get; init; }
    public Guid? EntityId { get; init; }
    public string? ErrorMessage { get; init; }
    public int? LatencyMs { get; init; }

    /// <summary>
    /// Which model proposed the call. Null for a call no model proposed.
    ///
    /// The provider name distinguishes hosts, not just vendors — `ollama` and
    /// `ollama-cloud` are the same software on different machines, and ADR 0011
    /// rests on telling a local floor from a hosted result (ADR 0013).
    /// </summary>
    public string? Provider { get; init; }

    public string? Model { get; init; }

    /// <summary>
    /// `fast` or `deep` (ADR 0003). Text rather than a mapped enum: the column is
    /// documented as `fast | deep | embed | local` and two of those four have no
    /// CLR counterpart yet, so a converter would have to invent members or throw
    /// on rows a later milestone writes legitimately.
    /// </summary>
    public string? LlmRole { get; init; }

    /// <summary>
    /// Written to exactly one row per completion, so <c>SUM</c> over these is the
    /// real figure. See <c>LlmAttribution</c> for why the alternative — the same
    /// counts on every row of a multi-call completion — silently overcounts.
    /// </summary>
    public int? PromptTokens { get; init; }

    public int? CompletionTokens { get; init; }

    // estimated_cost is deliberately unmapped. Ollama is free either way, so any
    // value would be a fabrication, and a fabricated cost is worse than a null
    // one: SPEC.md 50 wants a figure someone can act on. It gets mapped when a
    // metered provider ships, alongside the rate table that makes it meaningful.
}
