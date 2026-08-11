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

    public string? IdempotencyKey { get; init; }
    public string? EntityType { get; init; }
    public Guid? EntityId { get; init; }
    public string? ErrorMessage { get; init; }
    public int? LatencyMs { get; init; }
}
