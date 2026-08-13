using CoupleOS.Application.Conversation;
using CoupleOS.Application.Security;
using CoupleOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleOS.Infrastructure.Persistence;

public sealed class ConversationStore(
    CoupleOsDbContext dbContext,
    ICoupleScopeAccessor scopeAccessor) : IConversationStore
{
    private readonly CoupleOsDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly ICoupleScopeAccessor _scopeAccessor = scopeAccessor
        ?? throw new ArgumentNullException(nameof(scopeAccessor));

    public async Task<ConversationSession> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        var scope = _scopeAccessor.Current;

        if (await FindOpenAsync(scope.CoupleId, scope.UserId, cancellationToken) is { } existing)
        {
            return existing;
        }

        // Raw SQL for ON CONFLICT, exactly as DumpFileStore does, and for the same
        // reason: the loser of a race should get "someone already made it, use
        // that" rather than a unique-violation exception. Here the two racers are
        // one person's two tabs.
        //
        // The WHERE clause is not decoration — the index is partial, and
        // PostgreSQL will not infer a partial index without its predicate.
        //
        // started_at is left to its column default so the thread's age is the
        // database's opinion rather than this process's clock.
        await _dbContext.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO conversation_sessions (couple_id, user_id)
             VALUES ({scope.CoupleId}, {scope.UserId})
             ON CONFLICT (couple_id, user_id) WHERE ended_at IS NULL DO NOTHING
             """,
            cancellationToken);

        // Null here would mean row-level security hid a row this transaction just
        // wrote, which the policy cannot do for a row carrying this couple and
        // this user. Saying so beats returning null and letting the caller find
        // out one dereference later.
        return await FindOpenAsync(scope.CoupleId, scope.UserId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"The private thread for user {scope.UserId} in couple {scope.CoupleId} is neither " +
                "readable nor creatable. The couple scope and the row's own scope disagree.");
    }

    public async Task<IReadOnlyList<ConversationMessage>> HistoryAsync(
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // Newest-first to apply the limit, then reversed in memory. Ordering
        // oldest-first and taking N would take the START of a long thread and hand
        // the model a conversation that stopped weeks ago.
        //
        // No filter on couple_id or user_id, deliberately: the policy scopes this
        // by the session's owner, and a WHERE clause here that looked like it was
        // doing the securing would be the kind of second line ADR 0005 says not to
        // add until the first is proven. rls-tests.sql section H is that proof.
        var newest = await _dbContext.ConversationMessages
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId)
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

        newest.Reverse();

        return newest;
    }

    public async Task AppendAsync(ConversationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // created_at left to the column default, so two turns written in the same
        // request are ordered by the database rather than by whether this process's
        // clock ticked between them. Id is a v7 uuid, which breaks the tie the
        // default's shared now() creates within one transaction.
        await _dbContext.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO conversation_messages
                 (id, session_id, couple_id, user_id, visibility, role, content)
             VALUES
                 ({message.Id}, {message.SessionId}, {message.CoupleId}, {message.UserId},
                  {message.Visibility}, {message.Role}, {message.Content})
             """,
            cancellationToken);
    }

    private Task<ConversationSession?> FindOpenAsync(
        Guid coupleId,
        Guid userId,
        CancellationToken cancellationToken) =>
        _dbContext.ConversationSessions
            .FirstOrDefaultAsync(
                s => s.CoupleId == coupleId && s.UserId == userId && s.EndedAt == null,
                cancellationToken);
}
