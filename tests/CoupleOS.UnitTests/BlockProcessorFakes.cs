using System.Text.Json;
using CoupleOS.Application.AI;
using CoupleOS.Application.Capture;
using CoupleOS.Application.Persistence;
using CoupleOS.Application.Security;
using CoupleOS.Application.Tools;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;

namespace CoupleOS.UnitTests;

/// <summary>
/// The doubles BlockProcessor is tested against.
///
/// It was built to need no database and no GPU, and these are what that buys:
/// the status rules and the attribution rules are both decided in C#, so both
/// can be asserted in milliseconds without a model that might disagree with
/// itself twice in a row.
/// </summary>
internal static class BlockProcessorFakes
{
    public static readonly Guid Couple = Guid.Parse("c1111111-1111-1111-1111-111111111111");
    public static readonly Guid User = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static DumpBlock Block(
        string rawText = "we need detergent",
        Visibility visibility = Visibility.SharedCouple) => new()
    {
        DumpFileId = Guid.CreateVersion7(),
        CoupleId = Couple,
        Visibility = visibility,
        OwnerUserId = visibility == Visibility.PrivateUser ? User : null,
        ContentHash = [1, 2, 3],
        RawText = rawText,
    };

    public static LlmToolCall Call(string name) =>
        new(name, JsonDocument.Parse($$"""{"name":"{{name}}-arg"}""").RootElement.Clone());

    public static LlmUsage Usage(int prompt = 660, int completion = 42) =>
        new("ollama-cloud", "gemma4:31b", prompt, completion, TimeSpan.FromSeconds(2));

    public sealed class StubProvider(LlmCompletion completion) : ILlmProvider
    {
        public string Name => "stub";

        public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(completion);
    }

    /// <summary>A provider that cannot be reached, which is the common real failure.</summary>
    public sealed class ThrowingProvider(Exception failure) : ILlmProvider
    {
        public string Name => "stub";

        public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            Task.FromException<LlmCompletion>(failure);
    }

    public sealed class EmptyRegistry : IToolRegistry
    {
        public IReadOnlyList<ITool> All => [];

        public ITool? Find(string name) => null;
    }

    /// <summary>Records the context of every dispatch, which is what carries the attribution.</summary>
    public sealed class CapturingDispatcher(ToolOutcome outcome = ToolOutcome.Success) : IToolDispatcher
    {
        public List<ToolExecutionContext> Contexts { get; } = [];

        public Task<ToolResult> DispatchAsync(
            LlmToolCall call,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);

            return Task.FromResult(outcome == ToolOutcome.Success
                ? new ToolResult(call.Name, outcome, "shopping_item", Guid.CreateVersion7())
                : new ToolResult(call.Name, outcome, Errors: ["quantity must be positive"]));
        }
    }

    public sealed class ThrowingDispatcher(Exception failure) : IToolDispatcher
    {
        public Task<ToolResult> DispatchAsync(
            LlmToolCall call,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ToolResult>(failure);
    }

    /// <summary>Remembers the last state each block was marked with, and what it linked.</summary>
    public sealed class RecordingBlockStore : IDumpBlockStore
    {
        public List<DumpBlock> Marked { get; } = [];

        public List<BlockEntity> Linked { get; } = [];

        public Task<IReadOnlyList<DumpBlock>> AddNewAsync(
            DumpFile file,
            IReadOnlyList<SegmentedBlock> blocks,
            Guid runId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DumpBlock>>([]);

        public Task<IReadOnlyList<DumpBlock>> PendingAsync(
            Guid dumpFileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DumpBlock>>([]);

        public Task<IReadOnlyList<byte[]>> FailedHashesAsync(
            Guid dumpFileId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<byte[]>>([]);

        public Task MarkAsync(DumpBlock block, CancellationToken cancellationToken = default)
        {
            // Copied, not referenced. The processor mutates the block it was
            // handed, so holding the reference would make every recorded state
            // equal to the final one and the assertions meaningless.
            Marked.Add(new DumpBlock
            {
                Id = block.Id,
                DumpFileId = block.DumpFileId,
                CoupleId = block.CoupleId,
                Visibility = block.Visibility,
                ContentHash = block.ContentHash,
                RawText = block.RawText,
                Status = block.Status,
                ErrorMessage = block.ErrorMessage,
                Question = block.Question,
                ProcessedAt = block.ProcessedAt,
            });

            return Task.CompletedTask;
        }

        public Task LinkEntitiesAsync(
            Guid blockId,
            IReadOnlyList<BlockEntity> entities,
            CancellationToken cancellationToken = default)
        {
            Linked.AddRange(entities);

            return Task.CompletedTask;
        }
    }

    public sealed class NoopTransaction : ICoupleTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Counts transactions, because "one per block" is a claim worth checking.</summary>
    public sealed class CountingUnitOfWork : IScopedUnitOfWork
    {
        public int Began { get; private set; }

        public Task<ICoupleTransaction> BeginAsync(CancellationToken cancellationToken = default)
        {
            Began++;

            return Task.FromResult<ICoupleTransaction>(new NoopTransaction());
        }
    }

    public sealed class FixedScope : ICoupleScopeAccessor
    {
        public bool HasScope => true;

        public ICoupleScope Current => new CoupleScope(Couple, User);
    }
}
