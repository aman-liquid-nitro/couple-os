using CoupleOS.Application.Conversation;
using CoupleOS.Application.Persistence;
using CoupleOS.Application.Security;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;
using CoupleOS.Infrastructure.DependencyInjection;
using CoupleOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// The private thread against a real database, and mostly about one property:
/// a partner cannot reach it.
///
/// data/rls-tests.sql section H asserts the same thing directly in SQL, and this
/// asserts it through the path the application actually takes — EF Core, the
/// pooled connection, the scoped transaction. M0 exists because those two are not
/// the same claim: policies that hold in psql are the ones that were suspected of
/// not surviving connection pooling.
///
/// The assistant turns carry the weight. They are written with user_id null
/// because nobody types them, and they restate what the person just said, so a
/// policy keyed on authorship hands the partner a paraphrase of the surprise.
/// That policy was in the schema until this surface was built.
/// </summary>
public sealed class PrivateThreadIsolationTests : IClassFixture<RlsFixture>
{
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddCoupleOsInfrastructure(RlsFixture.AppConnectionString)
            .BuildServiceProvider();

    private static AsyncServiceScope BeginRequest(ServiceProvider provider, Guid couple, Guid user)
    {
        var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICoupleScopeSetter>().Set(new CoupleScope(couple, user));
        return scope;
    }

    private static ConversationMessage Message(
        Guid sessionId,
        Guid? userId,
        MessageRole role,
        string content) => new()
        {
            SessionId = sessionId,
            CoupleId = RlsFixture.Couple1,
            UserId = userId,
            Visibility = Visibility.PrivateUser,
            Role = role,
            Content = content,
        };

    [Fact]
    public async Task A_partner_cannot_read_the_other_thread_including_the_assistants_turns()
    {
        var marker = "necklace-" + Guid.NewGuid().ToString("N")[..8];

        await using var provider = BuildProvider();

        Guid sessionId;

        // Partner A's thread: what she typed, and an authorless reply that names
        // the thing she typed.
        await using (var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA))
        {
            var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
            var store = request.ServiceProvider.GetRequiredService<IConversationStore>();

            await using var transaction = await unitOfWork.BeginAsync();

            var session = await store.GetOrCreateAsync();
            sessionId = session.Id;

            await store.AppendAsync(Message(sessionId, RlsFixture.PartnerA, MessageRole.User, $"buying a {marker}"));
            await store.AppendAsync(Message(sessionId, userId: null, MessageRole.Assistant, $"Noted the {marker}."));

            await transaction.CommitAsync();
        }

        // Partner B, same couple, same database, same pooled connections.
        await using (var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerB))
        {
            var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
            var store = request.ServiceProvider.GetRequiredService<IConversationStore>();
            var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

            await using var transaction = await unitOfWork.BeginAsync();

            // Asking for the session by id — the id is not a secret, and treating
            // it as one would be the wrong kind of defence.
            Assert.Empty(await store.HistoryAsync(sessionId, 20));

            // And directly, in case a future HistoryAsync grows a filter that
            // happens to do the securing. The policy is what has to hold.
            Assert.Empty(await db.ConversationMessages
                .Where(m => m.Content.Contains(marker))
                .ToListAsync());

            Assert.Empty(await db.ConversationSessions.Where(s => s.Id == sessionId).ToListAsync());

            // The regression itself, stated as the query that used to succeed.
            Assert.Empty(await db.ConversationMessages.Where(m => m.UserId == null).ToListAsync());

            await transaction.CommitAsync();
        }
    }

    [Fact]
    public async Task Each_partner_gets_their_own_thread_and_the_same_one_every_time()
    {
        await using var provider = BuildProvider();

        var first = await OpenAsync(provider, RlsFixture.PartnerA);
        var again = await OpenAsync(provider, RlsFixture.PartnerA);
        var partner = await OpenAsync(provider, RlsFixture.PartnerB);

        // Idempotent: the second visit does not create a second thread, which is
        // what conversation_sessions_one_open_per_member and the ON CONFLICT
        // insert are between them for.
        Assert.Equal(first, again);

        // And one per member, so the two partners are never appending to one
        // transcript.
        Assert.NotEqual(first, partner);
    }

    [Fact]
    public async Task Two_tabs_opening_the_thread_at_once_still_produce_one_thread()
    {
        // The single-user race, which is the one a read-then-write would lose. M1
        // learned this on magic links: the sequential test passes under both
        // designs and only the contended one says which was chosen.
        await using var provider = BuildProvider();

        var opens = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => OpenAsync(provider, RlsFixture.PartnerA)));

        Assert.Single(opens.Distinct());
    }

    [Fact]
    public async Task The_message_role_enum_survives_a_round_trip_and_a_where_clause()
    {
        // The same check DumpEnumMappingTests makes for M2's other two enums.
        // MessageRole relies on Npgsql's snake-case translator rather than
        // [PgName], and a mismatch would not throw at startup — it would throw the
        // first time somebody sent a message.
        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var store = request.ServiceProvider.GetRequiredService<IConversationStore>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        var session = await store.GetOrCreateAsync();
        var marker = "role-" + Guid.NewGuid().ToString("N")[..8];

        await store.AppendAsync(Message(session.Id, RlsFixture.PartnerA, MessageRole.User, marker));
        await store.AppendAsync(Message(session.Id, userId: null, MessageRole.Assistant, $"re: {marker}"));

        // Filtering is where a wrong label bites: the value has to survive
        // translation into a WHERE clause against a PostgreSQL enum that rejects
        // an unknown label outright.
        var assistant = await db.ConversationMessages
            .Where(m => m.SessionId == session.Id && m.Role == MessageRole.Assistant)
            .Select(m => m.Content)
            .ToListAsync();

        Assert.Contains($"re: {marker}", assistant);

        var labels = await db.Database
            .SqlQuery<string>($"SELECT unnest(enum_range(NULL::message_role))::text AS \"Value\"")
            .ToListAsync();

        Assert.Equal(["user", "assistant", "system", "tool"], labels);

        // Same list, same order, from the CLR side. Order matters: both are
        // declaration-ordered, and a member inserted in the middle of one would
        // silently re-label every value after it.
        Assert.Equal(labels, Enum.GetValues<MessageRole>().Select(r => r.ToString().ToLowerInvariant()));

        await transaction.CommitAsync();
    }

    [Fact]
    public async Task History_returns_the_newest_turns_and_returns_them_oldest_first()
    {
        // The cap protects the context window, so it has to drop the START of a
        // long thread rather than the end — a model handed the first twenty turns
        // of a fifty-turn conversation is answering a question nobody just asked.
        await using var provider = BuildProvider();
        // StrangerC is in Couple2, so this runs in a thread of its own and cannot
        // be perturbed by whatever the other tests in this class left behind.
        await using var request = BeginRequest(provider, RlsFixture.Couple2, RlsFixture.StrangerC);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var store = request.ServiceProvider.GetRequiredService<IConversationStore>();

        await using var transaction = await unitOfWork.BeginAsync();

        var session = await store.GetOrCreateAsync();
        var run = Guid.NewGuid().ToString("N")[..8];

        for (var i = 0; i < 5; i++)
        {
            await store.AppendAsync(new ConversationMessage
            {
                SessionId = session.Id,
                CoupleId = RlsFixture.Couple2,
                UserId = RlsFixture.StrangerC,
                Visibility = Visibility.PrivateUser,
                Role = MessageRole.User,
                Content = $"{run}-{i}",
            });
        }

        var last3 = await store.HistoryAsync(session.Id, 3);

        Assert.Equal([$"{run}-2", $"{run}-3", $"{run}-4"], last3.Select(m => m.Content));

        await transaction.CommitAsync();
    }

    private static async Task<Guid> OpenAsync(ServiceProvider provider, Guid user)
    {
        await using var request = BeginRequest(provider, RlsFixture.Couple1, user);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var store = request.ServiceProvider.GetRequiredService<IConversationStore>();

        await using var transaction = await unitOfWork.BeginAsync();
        var session = await store.GetOrCreateAsync();
        await transaction.CommitAsync();

        return session.Id;
    }
}
