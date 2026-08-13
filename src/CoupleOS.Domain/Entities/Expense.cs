using CoupleOS.Domain.Enums;

namespace CoupleOS.Domain.Entities;

/// <summary>
/// A row in <c>expenses</c>. SPEC.md §14.
///
/// <c>decimal</c> and never <c>double</c>, matching <c>numeric(14,2)</c>:
/// SPEC.md §56.7 requires the arithmetic to be deterministic, and a float total
/// disagrees with itself depending on the order rows were added.
/// </summary>
public sealed class Expense
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid CoupleId { get; init; }

    /// <summary>Null unless <see cref="Visibility"/> is <c>private_user</c> — <c>expenses_owner_required_when_private</c>. A gift is the case this exists for (SPEC.md §19).</summary>
    public Guid? OwnerUserId { get; init; }

    public required Visibility Visibility { get; init; }

    /// <summary>Positive, and the database says so: <c>CHECK (amount > 0)</c>.</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217, three characters, because the column is <c>char(3)</c>.</summary>
    public required string Currency { get; init; }

    /// <summary>
    /// Resolved from a name against <c>expense_categories</c>, or null when the
    /// name matched nothing. Null is a legitimate answer: an expense with no
    /// category is still an expense, and inventing a category row from a model's
    /// word would put the couple's spending under a heading nobody chose.
    /// </summary>
    public Guid? CategoryId { get; init; }

    public string? Description { get; init; }

    public string? Merchant { get; init; }

    /// <summary>
    /// Who paid, or null when the note did not say. SPEC.md §14 is explicit that an
    /// ambiguous payer is not defaulted to the speaker.
    /// </summary>
    public Guid? PaidBy { get; init; }

    public required bool IsShared { get; init; }

    /// <summary>
    /// A <c>date</c>, not a timestamp, and the couple's date rather than the
    /// server's. The column defaults to <c>CURRENT_DATE</c>, which is the database
    /// server's idea of today — for a couple in Asia/Kolkata spending money at 3am
    /// that is yesterday. So this is always written.
    /// </summary>
    public required DateOnly OccurredOn { get; init; }
}

/// <summary>
/// A row in <c>expense_categories</c>, read-only.
///
/// Twelve system categories are seeded with a null <c>couple_id</c>; a couple may
/// have its own later. Nothing in V0 writes this table, which is why the entity
/// carries only what a lookup needs.
/// </summary>
public sealed class ExpenseCategory
{
    public Guid Id { get; init; }

    /// <summary>Null for a system category, shared by every couple.</summary>
    public Guid? CoupleId { get; init; }

    public required string Name { get; init; }
}
