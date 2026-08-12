using System.Text.Json;

namespace CoupleOS.Application.AI;

/// <summary>
/// Which model tier to use. ADR 0003 routes by role rather than by model name so
/// that swapping providers is configuration. On the current dev machine both
/// roles resolve to the same model — 8 GB of VRAM cannot hold two (ADR 0011) —
/// but the distinction stays in the contract because it costs nothing and the
/// roles diverge again the moment a second machine has more memory.
/// </summary>
public enum LlmRole
{
    Fast,
    Deep,
}

public enum LlmMessageRole
{
    System,
    User,
    Assistant,
}

public sealed record LlmMessage(LlmMessageRole Role, string Content);

/// <summary>
/// A tool the model may call. The schema is passed as data rather than derived
/// by reflection: ADR 0004 makes the tool layer the sole write path, so what the
/// model is offered must be reviewable in one place, not assembled from
/// attributes scattered across handler classes.
/// </summary>
public sealed record LlmTool(string Name, string Description, JsonElement ParametersSchema);

public sealed record LlmToolCall(string Name, JsonElement Arguments);

/// <summary>
/// Recorded against every call for SPEC.md 49 and 50's per-couple cost tracking.
/// Returned rather than written here: writing the audit row is the caller's
/// responsibility, and a provider that also persisted would have two reasons to
/// change.
/// </summary>
public sealed record LlmUsage(
    string Provider,
    string Model,
    int PromptTokens,
    int CompletionTokens,
    TimeSpan Duration);

public sealed record LlmCompletion(
    IReadOnlyList<LlmToolCall> ToolCalls,
    string? Content,
    LlmUsage Usage);

/// <summary>
/// Which model produced a tool call, carried into the audit trail.
///
/// ADR 0011 says a local model's output is "a floor, not a result". That
/// distinction is only recoverable months later if the row says which it was, and
/// ADR 0013 made the host a configuration value — so the configured value today
/// is no evidence of what ran then.
///
/// <para><b>Tokens are nullable on purpose, and that is the whole design.</b> A
/// completion produces N tool calls but is billed once. Copying its token counts
/// onto all N rows would make <c>SUM(prompt_tokens)</c> overcount by a factor of
/// N — and SPEC.md 50 wants per-couple cost, which is exactly a SUM. So the
/// counts are written to one row per completion and left null on the rest:
/// additive by construction rather than by a caveat nobody reads.</para>
/// </summary>
public sealed record LlmAttribution(
    string Provider,
    string Model,
    LlmRole Role,
    int? PromptTokens,
    int? CompletionTokens)
{
    /// <summary>Full attribution, including the completion's token counts. Use once per completion.</summary>
    public static LlmAttribution From(LlmUsage usage, LlmRole role)
    {
        ArgumentNullException.ThrowIfNull(usage);

        return new LlmAttribution(usage.Provider, usage.Model, role, usage.PromptTokens, usage.CompletionTokens);
    }

    /// <summary>
    /// Same provenance, no counts — for the second and later calls of one
    /// completion. Provider, model and role stay because they are true of every
    /// call and summing them is meaningless anyway.
    /// </summary>
    public LlmAttribution WithoutTokens() => this with { PromptTokens = null, CompletionTokens = null };
}

public sealed record LlmRequest(
    LlmRole Role,
    IReadOnlyList<LlmMessage> Messages,
    IReadOnlyList<LlmTool> Tools);
