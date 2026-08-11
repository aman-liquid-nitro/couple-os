namespace CoupleOS.Domain.Entities;

/// <summary>
/// Deliberately partial. This increment exists to answer one question — does
/// row-level security bind through EF Core and Npgsql — so it maps only the
/// columns that question needs. The full model, enums included, arrives once
/// the answer is known.
/// </summary>
public sealed class Memory
{
    public Guid Id { get; init; }
    public Guid CoupleId { get; init; }
    public Guid? OwnerUserId { get; init; }
    public string Content { get; init; } = string.Empty;
}
