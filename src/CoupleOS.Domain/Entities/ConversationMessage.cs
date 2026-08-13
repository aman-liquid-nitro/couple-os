using CoupleOS.Domain.Enums;

namespace CoupleOS.Domain.Entities;

/// <summary>
/// One turn in a private thread, and the private surface's unit of input — what
/// a block is to shared.md.
///
/// It is deliberately NOT a dump_block. The two surfaces run through one tool
/// path (ADR 0004's validate → authorize → execute → audit), which is the
/// invariant ADR 0009 actually asks for; they do not share a storage shape,
/// because the shared file's shape is wrong for a conversation in one decisive
/// way. <c>dump_blocks_dedup</c> is UNIQUE (dump_file_id, content_hash) and there
/// is one private file per member, so routing chat through it would make the
/// second "ok" in a thread collide with the first and vanish. Repetition is
/// noise in a dump file and meaning in a conversation. <see cref="DumpFileKind"/>
/// records the same conclusion from the other side: its Private member is unused
/// because ADR 0009 chose chat as the private surface.
/// </summary>
public sealed class ConversationMessage
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>
    /// The thread this belongs to. NOT NULL in the schema, and the row-level
    /// security policy scopes reads by it — so a message with no session would be
    /// a message with no privacy, which is why the column can never become
    /// nullable.
    /// </summary>
    public required Guid SessionId { get; init; }

    public required Guid CoupleId { get; init; }

    /// <summary>
    /// Who typed it, and null for the assistant, because nobody did.
    ///
    /// Null does not mean "visible to everyone" — that reading is exactly the bug
    /// that was in the policy before this surface existed. Who may read this row
    /// is decided by <see cref="SessionId"/> alone.
    /// </summary>
    public Guid? UserId { get; init; }

    /// <summary>
    /// The scope of the records this turn produces, not of the turn itself.
    ///
    /// Always <see cref="Visibility.PrivateUser"/> here, taken from
    /// <c>CaptureSurface.PrivateThread</c> and never from the text (ADR 0009).
    /// The column does not gate reads: a message mislabelled shared_couple is
    /// still confined to its thread, which rls-tests.sql H6 asserts.
    /// </summary>
    public required Visibility Visibility { get; init; }

    public required MessageRole Role { get; init; }

    public required string Content { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    // detected_intent and intent_confidence are columns nothing writes yet. The
    // model's confidence in a classification is not something V0 acts on, and a
    // number recorded but never read is a number nobody has had to justify.
}
