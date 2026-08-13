namespace CoupleOS.Domain.Entities;

/// <summary>
/// One partner's private thread, and the unit privacy is enforced on.
///
/// The row-level security policy on conversation_messages scopes by session
/// rather than by who authored a message, because the assistant's turns have no
/// author: they are written with <c>user_id</c> null, and they restate what the
/// person just said. A policy keyed on authorship let them through to the
/// partner — see section H of data/rls-tests.sql, which exists because that was
/// the state of the schema before this surface was built.
///
/// So the session is not bookkeeping. It is the thing that makes a private
/// conversation private, and every message has to belong to exactly one.
/// </summary>
public sealed class ConversationSession
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid CoupleId { get; init; }

    /// <summary>
    /// Whose thread this is. NOT NULL in the schema, unlike the owner column on
    /// couple-scoped tables, because there is no such thing as a shared session
    /// in ADR 0009's design — the shared surface is a file.
    /// </summary>
    public required Guid UserId { get; init; }

    /// <summary>
    /// Unwritten in V0. Naming a thread is a feature of having more than one, and
    /// V0 has one per partner (see <c>IConversationStore.GetOrCreateAsync</c>).
    /// </summary>
    public string? Title { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// Null for the whole of V0. Nothing ends a thread, and the column is what a
    /// future "start a new conversation" button writes rather than deleting the
    /// old one.
    /// </summary>
    public DateTimeOffset? EndedAt { get; init; }
}
