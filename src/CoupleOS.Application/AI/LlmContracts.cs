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

public sealed record LlmRequest(
    LlmRole Role,
    IReadOnlyList<LlmMessage> Messages,
    IReadOnlyList<LlmTool> Tools);
