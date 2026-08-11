using CoupleOS.Domain.Enums;

namespace CoupleOS.Domain.Entities;

/// <summary>
/// Still partial. Grows one column at a time as each is needed and tested,
/// rather than all twenty-eight at once with none of them exercised.
/// </summary>
public sealed class Memory
{
    public Guid Id { get; init; }
    public Guid CoupleId { get; init; }
    public Guid? OwnerUserId { get; init; }
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// Mapped for reading. Note that the application does not decide this — the
    /// capture surface does (ADR 0009), and row-level security enforces it. A
    /// value here is a fact about the row, not an instruction.
    /// </summary>
    public Visibility Visibility { get; init; }
}
