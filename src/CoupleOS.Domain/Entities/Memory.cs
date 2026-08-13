using CoupleOS.Domain.Enums;

namespace CoupleOS.Domain.Entities;

/// <summary>
/// What the couple knows. ARCHITECTURE.md §117 calls this the richest entity in
/// the schema and ADR 0006 is why: every column below except the content itself
/// exists to stop a guess from hardening into a fact, or a fact from quietly
/// outliving its truth.
///
/// Writable as of M3 (STATUS debt 2, paid). The columns still absent are absent on
/// purpose:
///
/// <c>importance</c> keeps its database default of 0.50 and is not mapped, because
/// nothing in V0 decides it and a mapped <c>decimal</c> would insert the CLR's zero
/// over the schema's default — the same trap <c>source</c> sets, which is why that
/// one is <c>required</c> rather than defaulted. Search still ranks by it; a raw
/// query may order by a column this model does not carry.
///
/// <c>embedding</c> exists in the table and stays null (ADR 0002: V0 search is
/// lexical). <c>source_message_id</c>, <c>confirmed_at</c> and
/// <c>visibility_changed_at</c> wait on the flows that would write them —
/// confirming an inferred memory, and <c>share_memory</c>.
///
/// <c>search_tsv</c> is <c>GENERATED ALWAYS</c> and must never be mapped: EF would
/// try to write it and PostgreSQL would refuse the insert.
/// </summary>
public sealed class Memory
{
    /// <summary>
    /// Generated here rather than by the column's <c>gen_random_uuid()</c>, as every
    /// other written entity does — a v7 uuid is time-ordered, and superseding needs
    /// the new row's id before the insert in order to point the old row at it.
    /// </summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid CoupleId { get; init; }
    public Guid? OwnerUserId { get; init; }
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// Mapped for reading. Note that the application does not decide this — the
    /// capture surface does (ADR 0009), and row-level security enforces it. A
    /// value here is a fact about the row, not an instruction.
    /// </summary>
    public Visibility Visibility { get; init; }

    /// <summary>NOT NULL with no default. The column M3 could not ship without.</summary>
    public required MemoryType Type { get; init; }

    /// <summary>
    /// Where the claim came from. <c>Inferred</c> is capped at 0.7 confidence by
    /// the tool, because a model may not certify its own guess (ADR 0006).
    /// </summary>
    public required MemoryAssertion Assertion { get; init; }

    /// <summary>
    /// Which capture surface produced this. <c>required</c> rather than defaulted
    /// because the CLR default for this enum is <c>user_input</c> and the
    /// database's is <c>chat</c>: an omission here would not inherit the schema's
    /// answer, it would silently contradict it.
    /// </summary>
    public required DataSource Source { get; init; }

    /// <summary>
    /// <c>required</c> for the reason <see cref="Source"/> is: the CLR default for
    /// a <c>decimal</c> is zero and the column's default is 1.00, so a memory
    /// created by omission would arrive certain of nothing.
    /// </summary>
    public required decimal Confidence { get; init; }

    /// <summary>
    /// The canonical subject a contradiction is detected against (ADR 0006):
    /// <c>self:cuisine</c>, <c>decision:car</c>. Null means this memory has no
    /// subject to supersede on, which is most episodic memories — two dinners in
    /// Munnar are two memories, not a correction.
    /// </summary>
    public string? SubjectKey { get; init; }

    /// <summary>
    /// When this stops being true. Required for <c>temporary_context</c> by
    /// <c>memories_temp_context_expires</c>, and null for everything else —
    /// nothing else ages out, which is STATUS debt 29.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Settable, unlike everything above, because superseding is the one operation
    /// that writes an <i>existing</i> memory. <see cref="MemoryStatus.Superseded"/>
    /// is only ever written together with <see cref="SupersededById"/>.
    /// </summary>
    public MemoryStatus Status { get; set; }

    /// <summary>The memory that replaced this one. Its history, not its deletion.</summary>
    public Guid? SupersededById { get; set; }

    /// <summary>
    /// Left to the database's <c>now()</c> and read back. Search needs it twice —
    /// to rank recency, and to answer <c>since_expression</c> — and a memory
    /// rendered without a date is a memory a person cannot judge the age of.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }
}
