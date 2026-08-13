using CoupleOS.Application.AI;
using CoupleOS.Application.Conversation;
using CoupleOS.Application.Tools;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;
using Xunit;

using static CoupleOS.UnitTests.BlockProcessorFakes;

namespace CoupleOS.UnitTests;

/// <summary>
/// The private surface, asserted the way the shared one is: against fakes, with
/// no database and no GPU, because the rules being checked are decided in C# and
/// a model asked to demonstrate them might disagree with itself twice in a row.
///
/// Two of these tests are about privacy and the rest are about not losing what
/// somebody typed. That ratio is deliberate — ADR 0009 calls the privacy failure
/// asymmetric and unrecoverable, and the message-loss failure merely bad.
/// </summary>
public sealed class PrivateThreadTests
{
    private static PrivateThread Build(
        ILlmProvider provider,
        IToolDispatcher dispatcher,
        RecordingConversationStore store,
        CountingUnitOfWork? unitOfWork = null) =>
        new(store,
            provider,
            new EmptyRegistry(),
            dispatcher,
            unitOfWork ?? new CountingUnitOfWork(),
            new FixedScope());

    private static LlmCompletion Says(string? content, params LlmToolCall[] calls) =>
        new([.. calls], content, Usage());

    [Fact]
    public async Task Every_tool_runs_as_private_user_and_the_surface_is_what_decides_that()
    {
        // ADR 0009's structural claim, on the surface where getting it wrong
        // cannot be taken back. The model is given no vocabulary for visibility
        // and the dispatcher would refuse it anyway; this asserts the value that
        // actually arrives.
        var dispatcher = new CapturingDispatcher();

        var thread = Build(
            new StubProvider(Says(null, Call("create_shopping_item"))),
            dispatcher,
            new RecordingConversationStore());

        await thread.SayAsync("get her the necklace before friday");

        var context = Assert.Single(dispatcher.Contexts);
        Assert.Equal(Visibility.PrivateUser, context.Visibility);
        Assert.Equal(BlockProcessorFakes.User, context.UserId);
    }

    [Fact]
    public async Task The_assistants_turn_is_authorless_and_that_does_not_make_it_public()
    {
        // user_id is null on the reply because nobody typed it. The policy that
        // read null as "everyone may see this" is the one that leaked the partner
        // thread — rls-tests.sql section H. Here the property being pinned is
        // narrower: the application writes the reply into the SAME session as the
        // message it answers, which is what the policy scopes by.
        var store = new RecordingConversationStore();

        var thread = Build(new StubProvider(Says("Noted.")), new CapturingDispatcher(), store);

        var turn = await thread.SayAsync("buying her a necklace");

        Assert.Equal(2, store.Appended.Count);

        var said = store.Appended[0];
        var reply = store.Appended[1];

        Assert.Equal(MessageRole.User, said.Role);
        Assert.Equal(BlockProcessorFakes.User, said.UserId);

        Assert.Equal(MessageRole.Assistant, reply.Role);
        Assert.Null(reply.UserId);

        Assert.Equal(said.SessionId, reply.SessionId);
        Assert.Equal(store.Session.Id, reply.SessionId);

        // Both halves carry the surface's scope, so the records they produce and
        // the turns themselves cannot disagree about who this belongs to.
        Assert.Equal(Visibility.PrivateUser, said.Visibility);
        Assert.Equal(Visibility.PrivateUser, reply.Visibility);

        Assert.Equal("Noted.", turn.Reply.Content);
    }

    [Fact]
    public async Task What_the_person_typed_survives_a_model_that_cannot_be_reached()
    {
        // The ordering that makes this true is the point: the user's turn commits
        // before the call, so a provider that is down costs a reply and not a
        // message. Losing what somebody typed because no answer came back is the
        // worse of the two failures.
        var store = new RecordingConversationStore();

        var thread = Build(
            new ThrowingProvider(new HttpRequestException("No connection could be made (localhost:11434)")),
            new CapturingDispatcher(),
            store);

        var turn = await thread.SayAsync("buying her a necklace");

        Assert.True(turn.Failed);
        Assert.Equal("buying her a necklace", store.Appended[0].Content);

        // And it says so, in the thread, rather than throwing a 500 at somebody
        // watching a spinner.
        Assert.Equal(2, store.Appended.Count);
        Assert.Equal(MessageRole.Assistant, store.Appended[1].Role);
        Assert.Contains("localhost:11434", turn.Reply.Content);
        Assert.Contains("your message is saved", turn.Reply.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(turn.Changes);
    }

    [Fact]
    public async Task A_failed_turn_is_recorded_in_a_transaction_of_its_own()
    {
        // Same rule as BlockProcessor.FailAsync, same reason: the failure being
        // recorded is often the failure that rolled the previous transaction back,
        // so the second BeginAsync is the assertion.
        var unitOfWork = new CountingUnitOfWork();

        var thread = Build(
            new ThrowingProvider(new HttpRequestException("down")),
            new CapturingDispatcher(),
            new RecordingConversationStore(),
            unitOfWork);

        await thread.SayAsync("hello");

        Assert.Equal(2, unitOfWork.Began);
    }

    [Fact]
    public async Task A_question_is_asked_in_the_reply_rather_than_parked()
    {
        // ADR 0009's in-band clarification, built out of the shared surface's
        // parking tool rather than beside it. There is nowhere to park here and
        // nobody to park for: the person is reading this now.
        var store = new RecordingConversationStore();

        var thread = Build(
            new StubProvider(Says(null, Call("request_clarification"))),
            new AskingDispatcher(),
            store);

        var turn = await thread.SayAsync("remind me to book the dentist");

        Assert.Equal("\"book the dentist\" — when?", turn.Reply.Content);
        Assert.Equal("\"book the dentist\" — when?", store.Appended[1].Content);

        // The change is still recorded — it was a tool call and it is audited —
        // and it is flagged so the screen does not print the question twice.
        var change = Assert.Single(turn.Changes);
        Assert.True(change.IsQuestion);
    }

    [Fact]
    public async Task A_question_outranks_the_models_own_prose()
    {
        // A model that both asks and chatters would otherwise bury the question
        // under the chatter, and the question is the only part the person can act
        // on.
        var thread = Build(
            new StubProvider(Says("Sure, I can help with that!", Call("request_clarification"))),
            new AskingDispatcher(),
            new RecordingConversationStore());

        var turn = await thread.SayAsync("remind me to book the dentist");

        Assert.Equal("\"book the dentist\" — when?", turn.Reply.Content);
    }

    [Fact]
    public async Task What_was_recorded_is_kept_separate_from_what_the_model_claimed()
    {
        // The injection-003 shape, on a surface where prose is allowed. A model
        // can write "added that" without calling anything, and the only defence a
        // screen has is holding both accounts at once — so the turn carries the
        // prose and the changes as two fields, and never folds one into the other.
        var thread = Build(
            new StubProvider(Says("Added it, and I also booked your flights.", Call("create_shopping_item"))),
            new CapturingDispatcher(),
            new RecordingConversationStore());

        var turn = await thread.SayAsync("we need detergent");

        Assert.Equal("Added it, and I also booked your flights.", turn.Reply.Content);

        var change = Assert.Single(turn.Changes);
        Assert.Equal("create_shopping_item", change.ToolName);
        Assert.Equal(ToolOutcome.Success, change.Outcome);
        Assert.False(change.IsQuestion);
    }

    [Fact]
    public async Task A_model_that_says_nothing_gets_a_sentence_and_not_the_change_list_again()
    {
        // A blank reply reads as the system having ignored you. ChatPrompt rule 1
        // asks for prose so this fallback is rare, not so it is unreachable.
        //
        // And it must not be built out of the change descriptions. That was the
        // first implementation, and in the browser it printed "create shopping
        // item: coffee" as the reply with the identical line listed underneath —
        // one call that read as two items.
        var thread = Build(
            new StubProvider(Says(null, Call("create_shopping_item"))),
            new CapturingDispatcher(),
            new RecordingConversationStore());

        var turn = await thread.SayAsync("we need detergent");

        Assert.Equal("Recorded — the detail is below.", turn.Reply.Content);

        var change = Assert.Single(turn.Changes);
        Assert.DoesNotContain(change.Description, turn.Reply.Content);
    }

    [Fact]
    public async Task A_silent_model_whose_calls_were_all_refused_does_not_get_success_language()
    {
        // SPEC.md 46, in the fallback. "Recorded" over a message that recorded
        // nothing is the sentence the whole rule exists to forbid.
        var thread = Build(
            new StubProvider(Says(null, Call("create_shopping_item"))),
            new CapturingDispatcher(ToolOutcome.ValidationFailed),
            new RecordingConversationStore());

        var turn = await thread.SayAsync("we need something");

        Assert.Equal("None of that went through — the detail is below.", turn.Reply.Content);
    }

    [Fact]
    public async Task A_silent_model_that_half_succeeded_says_half()
    {
        var thread = Build(
            new StubProvider(Says(null, Call("create_shopping_item"), Call("create_shopping_item"))),
            new HalfDispatcher(),
            new RecordingConversationStore());

        var turn = await thread.SayAsync("detergent and something unparseable");

        Assert.Equal("Some of that went through and some did not — the detail is below.", turn.Reply.Content);
    }

    [Fact]
    public async Task A_model_that_does_nothing_at_all_says_so_in_words()
    {
        var thread = Build(
            new StubProvider(Says(null)),
            new CapturingDispatcher(),
            new RecordingConversationStore());

        var turn = await thread.SayAsync("just thinking out loud");

        Assert.Equal("I read that and there was nothing for me to record.", turn.Reply.Content);
        Assert.Empty(turn.Changes);
    }

    [Fact]
    public async Task A_dispatch_that_throws_is_a_refused_call_and_not_a_lost_turn()
    {
        // The dispatcher turns a tool's own failure into a result, so reaching the
        // catch means the infrastructure under it broke. The other calls in the
        // same message may well have worked, and the person is still owed a reply.
        var store = new RecordingConversationStore();

        var thread = Build(
            new StubProvider(Says(null, Call("create_shopping_item"))),
            new ThrowingDispatcher(new InvalidOperationException("the connection died mid-insert")),
            store);

        var turn = await thread.SayAsync("we need detergent");

        var change = Assert.Single(turn.Changes);
        Assert.Equal(ToolOutcome.ExecutionFailed, change.Outcome);
        Assert.Contains("died mid-insert", change.Description);

        Assert.Equal(2, store.Appended.Count);
    }

    [Fact]
    public async Task The_model_is_given_the_thread_oldest_first_with_the_new_message_last()
    {
        // A conversation handed to a model in the wrong order is a different
        // conversation. And the history is read BEFORE the new turn is written, so
        // the message cannot arrive twice — once as context and once as the
        // question.
        var store = new RecordingConversationStore();
        store.Seed(MessageRole.User, "i want to get her something");
        store.Seed(MessageRole.Assistant, "What is the occasion?");

        var provider = new RecordingProvider(Says("A necklace sounds good."));

        var thread = Build(provider, new CapturingDispatcher(), store);

        await thread.SayAsync("her birthday");

        var request = Assert.Single(provider.Requests);

        Assert.Equal(
            [
                (LlmMessageRole.System, ChatPrompt.System),
                (LlmMessageRole.User, "i want to get her something"),
                (LlmMessageRole.Assistant, "What is the occasion?"),
                (LlmMessageRole.User, "her birthday"),
            ],
            request.Messages.Select(m => (m.Role, m.Content)));
    }

    [Fact]
    public async Task Reading_the_thread_does_not_write_to_it()
    {
        // Opening the page must not append anything. GetOrCreate creating the
        // session on first visit is fine; a turn appearing because somebody looked
        // is not.
        var store = new RecordingConversationStore();
        store.Seed(MessageRole.User, "hello");

        var thread = Build(new StubProvider(Says("hi")), new CapturingDispatcher(), store);

        var history = await thread.ReadAsync();

        Assert.Single(history);
        Assert.Empty(store.Appended);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task An_empty_message_is_refused_before_anything_is_written(string? text)
    {
        var store = new RecordingConversationStore();
        var thread = Build(new StubProvider(Says("hi")), new CapturingDispatcher(), store);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => thread.SayAsync(text!));

        Assert.Empty(store.Appended);
    }

    /// <summary>One thread, in memory, remembering the order things were written in.</summary>
    private sealed class RecordingConversationStore : IConversationStore
    {
        private readonly List<ConversationMessage> _history = [];

        public ConversationSession Session { get; } = new()
        {
            Id = Guid.CreateVersion7(),
            CoupleId = BlockProcessorFakes.Couple,
            UserId = BlockProcessorFakes.User,
        };

        /// <summary>Only what this run appended, so the seeded history is not counted.</summary>
        public List<ConversationMessage> Appended { get; } = [];

        public void Seed(MessageRole role, string content) => _history.Add(new ConversationMessage
        {
            SessionId = Session.Id,
            CoupleId = BlockProcessorFakes.Couple,
            UserId = role == MessageRole.User ? BlockProcessorFakes.User : null,
            Visibility = Visibility.PrivateUser,
            Role = role,
            Content = content,
        });

        public Task<ConversationSession> GetOrCreateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Session);

        public Task<IReadOnlyList<ConversationMessage>> HistoryAsync(
            Guid sessionId,
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationMessage>>(
                [.. _history.TakeLast(limit)]);

        public Task AppendAsync(ConversationMessage message, CancellationToken cancellationToken = default)
        {
            Appended.Add(message);
            _history.Add(message);

            return Task.CompletedTask;
        }
    }

    /// <summary>Remembers the request, which is how prompt and history order get asserted.</summary>
    private sealed class RecordingProvider(LlmCompletion completion) : ILlmProvider
    {
        public List<LlmRequest> Requests { get; } = [];

        public string Name => "stub";

        public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);

            return Task.FromResult(completion);
        }
    }

    /// <summary>Succeeds once, then refuses — the half-applied message.</summary>
    private sealed class HalfDispatcher : IToolDispatcher
    {
        private int _calls;

        public Task<ToolResult> DispatchAsync(
            LlmToolCall call,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_calls++ == 0
                ? new ToolResult(call.Name, ToolOutcome.Success, "shopping_item", Guid.CreateVersion7())
                : new ToolResult(call.Name, ToolOutcome.ValidationFailed, Errors: ["not a shopping item"]));
    }

    /// <summary>Asks rather than writes — the shape only request_clarification produces.</summary>
    private sealed class AskingDispatcher : IToolDispatcher
    {
        public Task<ToolResult> DispatchAsync(
            LlmToolCall call,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolResult(
                call.Name,
                ToolOutcome.Success,
                Question: "\"book the dentist\" — when?"));
    }
}
