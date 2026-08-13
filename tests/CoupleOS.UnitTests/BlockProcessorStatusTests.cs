using CoupleOS.Application.AI;
using CoupleOS.Application.Capture;
using CoupleOS.Application.Tools;
using CoupleOS.Domain.Enums;
using Xunit;

using static CoupleOS.UnitTests.BlockProcessorFakes;

namespace CoupleOS.UnitTests;

/// <summary>
/// Every block ends in a status, and the status is the whole mechanism.
///
/// M2 exists because a line that produced no tool call left no trace: the report
/// described actions, so a line with no action had nothing to appear as. These
/// tests fix the inverse property — whatever happens to a block, including
/// nothing, it reaches a terminal status and the report can name it.
///
/// The distinctions are deliberate and each one is a different sentence to the
/// user: ignored means "read, and there was nothing to do", failed means "read,
/// and it did not work", and those must never be rendered as the same thing.
/// </summary>
public sealed class BlockProcessorStatusTests
{
    private static BlockProcessor Build(
        ILlmProvider provider,
        IToolDispatcher dispatcher,
        RecordingBlockStore store,
        CountingUnitOfWork unitOfWork) =>
        new(provider,
            new EmptyRegistry(),
            dispatcher,
            store,
            unitOfWork,
            new FixedScope(),
            TimeProvider.System);

    [Fact]
    public async Task A_block_the_model_had_nothing_to_say_about_is_ignored_not_forgotten()
    {
        // The M0 failure, reproduced as a unit: a line arrives, no tool matches
        // it, and the model narrates instead of calling. The old pipeline dropped
        // it silently.
        var store = new RecordingBlockStore();

        var processor = Build(
            new StubProvider(new LlmCompletion([], "That looks like an event, and I have no tool for it.", Usage())),
            new CapturingDispatcher(),
            store,
            new CountingUnitOfWork());

        var report = await processor.ProcessAsync(Block("dinner at Priya's parents on Saturday 8pm"));

        Assert.Equal(DumpBlockStatus.Ignored, report.Status);
        Assert.Equal("dinner at Priya's parents on Saturday 8pm", report.RawText);

        // The model's own words survive to the screen. Without them the report
        // says "nothing to do" and gives the user no way to tell a heading from
        // a note the system could not handle.
        Assert.Equal("That looks like an event, and I have no tool for it.", report.Note);

        var marked = Assert.Single(store.Marked);
        Assert.Equal(DumpBlockStatus.Ignored, marked.Status);
        Assert.NotNull(marked.ProcessedAt);
        Assert.Null(marked.ErrorMessage);
    }

    [Fact]
    public async Task A_block_whose_every_call_was_refused_is_failed_not_processed()
    {
        // SPEC.md 46: no success language over a failed action. "Processed" on a
        // block that wrote nothing is exactly that, in one word.
        var store = new RecordingBlockStore();

        var processor = Build(
            new StubProvider(new LlmCompletion([Call("create_shopping_item")], null, Usage())),
            new CapturingDispatcher(ToolOutcome.ValidationFailed),
            store,
            new CountingUnitOfWork());

        var report = await processor.ProcessAsync(Block());

        Assert.Equal(DumpBlockStatus.Failed, report.Status);

        var change = Assert.Single(report.Changes);
        Assert.Equal(ToolOutcome.ValidationFailed, change.Outcome);
        Assert.Contains("quantity must be positive", change.Description);

        var marked = Assert.Single(store.Marked);
        Assert.Equal(DumpBlockStatus.Failed, marked.Status);
        Assert.Contains("quantity must be positive", marked.ErrorMessage);
    }

    [Fact]
    public async Task One_success_among_refusals_still_counts_as_processed()
    {
        // The block did produce a record. Calling it failed would tell the user
        // to rewrite a line that already worked.
        var store = new RecordingBlockStore();

        var dispatcher = new AlternatingDispatcher();

        var processor = Build(
            new StubProvider(new LlmCompletion(
                [Call("create_shopping_item"), Call("create_shopping_item")], null, Usage())),
            dispatcher,
            store,
            new CountingUnitOfWork());

        var report = await processor.ProcessAsync(Block("detergent and something unparseable"));

        Assert.Equal(DumpBlockStatus.Processed, report.Status);
        Assert.Equal(2, report.Changes.Count);
        Assert.Single(report.Changes, c => c.Outcome == ToolOutcome.Success);

        // Only the row that was actually created is linked to the block.
        Assert.Single(store.Linked);
        Assert.Equal("created", store.Linked[0].Action);
    }

    [Fact]
    public async Task An_unreachable_model_fails_the_block_and_does_not_throw()
    {
        // ARCHITECTURE.md 6: one bad block does not discard the rest of a dump.
        // If this threw, the nineteen blocks after it would never run.
        var store = new RecordingBlockStore();

        var processor = Build(
            new ThrowingProvider(new HttpRequestException("No connection could be made (localhost:11434)")),
            new CapturingDispatcher(),
            store,
            new CountingUnitOfWork());

        var report = await processor.ProcessAsync(Block());

        Assert.Equal(DumpBlockStatus.Failed, report.Status);
        Assert.Contains("localhost:11434", report.Note);

        var marked = Assert.Single(store.Marked);
        Assert.Equal(DumpBlockStatus.Failed, marked.Status);
        Assert.Contains("HttpRequestException", marked.ErrorMessage);
    }

    [Fact]
    public async Task A_dispatch_that_throws_is_recorded_in_a_transaction_of_its_own()
    {
        // The failure being recorded is the failure that rolled the previous
        // transaction back. Writing the status inside it would lose the status
        // along with the work — so the second BeginAsync is the assertion.
        var store = new RecordingBlockStore();
        var unitOfWork = new CountingUnitOfWork();

        var processor = Build(
            new StubProvider(new LlmCompletion([Call("create_shopping_item")], null, Usage())),
            new ThrowingDispatcher(new InvalidOperationException("the connection died mid-insert")),
            store,
            unitOfWork);

        var report = await processor.ProcessAsync(Block());

        Assert.Equal(DumpBlockStatus.Failed, report.Status);
        Assert.Equal(2, unitOfWork.Began);

        var marked = Assert.Single(store.Marked);
        Assert.Equal(DumpBlockStatus.Failed, marked.Status);
        Assert.Contains("died mid-insert", marked.ErrorMessage);
    }

    [Fact]
    public async Task A_successful_block_takes_exactly_one_transaction()
    {
        var unitOfWork = new CountingUnitOfWork();

        var processor = Build(
            new StubProvider(new LlmCompletion([Call("create_shopping_item")], null, Usage())),
            new CapturingDispatcher(),
            new RecordingBlockStore(),
            unitOfWork);

        await processor.ProcessAsync(Block());

        Assert.Equal(1, unitOfWork.Began);
    }

    [Fact]
    public async Task The_visibility_a_tool_runs_under_comes_from_the_block()
    {
        // ADR 0009's structural claim. The block inherited this from the file,
        // which is the surface, and no line between here and the database gives
        // the model a way to influence it.
        var dispatcher = new CapturingDispatcher();

        var processor = Build(
            new StubProvider(new LlmCompletion([Call("create_shopping_item")], null, Usage())),
            dispatcher,
            new RecordingBlockStore(),
            new CountingUnitOfWork());

        await processor.ProcessAsync(Block("a gift idea", Visibility.PrivateUser));

        var context = Assert.Single(dispatcher.Contexts);
        Assert.Equal(Visibility.PrivateUser, context.Visibility);
    }

    [Fact]
    public async Task A_block_the_model_asked_about_parks_rather_than_failing()
    {
        // The gap request_clarification was built to close. Before it, a note
        // with a required value missing left the model no compliant action —
        // rule 1 forbids inventing one, ADR 0004 forbids writing without a tool —
        // so it said nothing, the block was Ignored, and the archive filed it as
        // "read, nothing to do". Nobody was ever asked the question.
        var store = new RecordingBlockStore();

        var processor = Build(
            new StubProvider(new LlmCompletion([Call("request_clarification")], null, Usage())),
            new AskingDispatcher(),
            store,
            new CountingUnitOfWork());

        var report = await processor.ProcessAsync(Block("remind me to book the dentist"));

        Assert.Equal(DumpBlockStatus.NeedsInput, report.Status);

        var marked = Assert.Single(store.Marked);
        Assert.Equal(DumpBlockStatus.NeedsInput, marked.Status);

        // The schema's dump_blocks_question_when_needs_input check refuses one
        // without the other, so an unfilled question here is a write that fails
        // at the database and takes the whole block with it.
        Assert.Equal("\"book the dentist\" — when?", marked.Question);

        // Not an error. A question is the tool working, and error_message is what
        // the report renders as a refusal.
        Assert.Null(marked.ErrorMessage);
        Assert.Empty(store.Linked);
    }

    [Fact]
    public async Task An_open_question_outranks_a_success_in_the_same_block()
    {
        // "buy detergent, and dinner was 2400" — one line, one thing done, one
        // thing still to answer. Processed would archive the whole line and take
        // the question out of the inbox with it, which is the one place either
        // partner would have seen it.
        var store = new RecordingBlockStore();

        var processor = Build(
            new StubProvider(new LlmCompletion(
                [Call("create_shopping_item"), Call("request_clarification")], null, Usage())),
            new AskingDispatcher(succeedFirst: true),
            store,
            new CountingUnitOfWork());

        var report = await processor.ProcessAsync(Block("detergent, and dinner was 2400"));

        Assert.Equal(DumpBlockStatus.NeedsInput, report.Status);

        // The row that was created stays created and stays linked. Parking is
        // about what the file says is outstanding, not about undoing work.
        Assert.Single(store.Linked);

        // Both calls are reported, and only the one that wrote something counts
        // as applied — entities_created is the length of that sequence.
        Assert.Equal(2, report.Changes.Count);

        var applied = new CaptureReport([report], 1, 0, null).Applied.ToList();
        Assert.Single(applied);
        Assert.Equal("create_shopping_item", applied[0].ToolName);
    }

    [Fact]
    public async Task The_question_reaches_the_report_in_the_words_it_was_asked_in()
    {
        // Not "request clarification: ..." — the tool's own name is a mechanism
        // the reader has no reason to know, sitting in front of the only part of
        // the line addressed to them.
        var processor = Build(
            new StubProvider(new LlmCompletion([Call("request_clarification")], null, Usage())),
            new AskingDispatcher(),
            new RecordingBlockStore(),
            new CountingUnitOfWork());

        var report = await processor.ProcessAsync(Block("remind me to book the dentist"));

        var change = Assert.Single(report.Changes);
        Assert.Equal("\"book the dentist\" — when?", change.Description);
        Assert.True(change.IsQuestion);
        Assert.Equal(ToolOutcome.Success, change.Outcome);
    }

    /// <summary>Succeeds once, then refuses — the mixed outcome the status rule turns on.</summary>
    private sealed class AlternatingDispatcher : IToolDispatcher
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

    /// <summary>
    /// Answers a question rather than writing a row — a success carrying a
    /// Question and no entity, which is the shape only request_clarification
    /// produces.
    /// </summary>
    /// <param name="succeedFirst">
    /// Writes a row on the first call before asking on the second, so the
    /// precedence rule has both outcomes in one block to choose between.
    /// </param>
    private sealed class AskingDispatcher(bool succeedFirst = false) : IToolDispatcher
    {
        private bool _written;

        public Task<ToolResult> DispatchAsync(
            LlmToolCall call,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            if (succeedFirst && !_written)
            {
                _written = true;

                return Task.FromResult(
                    new ToolResult(call.Name, ToolOutcome.Success, "shopping_item", Guid.CreateVersion7()));
            }

            return Task.FromResult(new ToolResult(
                call.Name,
                ToolOutcome.Success,
                Question: "\"book the dentist\" — when?"));
        }
    }
}
