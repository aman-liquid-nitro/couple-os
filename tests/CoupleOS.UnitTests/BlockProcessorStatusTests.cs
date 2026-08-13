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
}
