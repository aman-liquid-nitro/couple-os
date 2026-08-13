using CoupleOS.Domain.Entities;

namespace CoupleOS.Application.Conversation;

/// <summary>
/// Reads and writes one partner's private thread.
///
/// Every method here is implicitly scoped to the caller: the session is found by
/// the couple scope's user id, and the row-level security policy on
/// conversation_messages scopes reads by the session they belong to. There is
/// deliberately no method that takes a user id, so there is no signature through
/// which one partner's thread could be asked for by the other.
/// </summary>
public interface IConversationStore
{
    /// <summary>
    /// The caller's open thread, created if they have never used the surface.
    ///
    /// Concurrency-safe by the same route <c>IDumpFileStore.GetOrCreateSharedAsync</c>
    /// takes: an insert with ON CONFLICT against
    /// <c>conversation_sessions_one_open_per_member</c> rather than a read
    /// followed by a write. One person with two tabs open is the race here, and a
    /// read-then-write passes every sequential test and loses that one.
    /// </summary>
    Task<ConversationSession> GetOrCreateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The thread's turns, oldest first.
    /// </summary>
    /// <param name="limit">
    /// How many of the most recent turns to return. A cap rather than the whole
    /// transcript, because this feeds both the screen and the model's context
    /// window: an unbounded history makes every message in a long thread cost
    /// more than the one before it, which is STATUS debt 8 wearing a different
    /// hat. The most recent <paramref name="limit"/> are taken and then ordered
    /// oldest-first, so the tail is what survives truncation — dropping the
    /// newest turns would be dropping the conversation.
    /// </param>
    Task<IReadOnlyList<ConversationMessage>> HistoryAsync(
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends one turn.
    ///
    /// One row per call and no batching, because the user's turn has to be
    /// durable before the model is called: that call takes seconds and can fail,
    /// and a thread that loses what the person typed because the reply never
    /// arrived is worse than a thread with an unanswered line in it.
    /// </summary>
    Task AppendAsync(ConversationMessage message, CancellationToken cancellationToken = default);
}
