using CoupleOS.Application.AI;
using CoupleOS.Application.Persistence;
using CoupleOS.Application.Security;
using CoupleOS.Application.Tools;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;

namespace CoupleOS.Application.Capture;

/// <summary>
/// Takes one block from text to rows, and leaves it in a terminal status either
/// way.
/// </summary>
public interface IBlockProcessor
{
    /// <summary>
    /// Never throws for a model or a tool behaving badly. A block that fails is
    /// a block with status = 'failed' and a reason, because ARCHITECTURE.md 6
    /// requires the rest of the run to continue — an exception here would take
    /// the other nineteen blocks with it.
    /// </summary>
    Task<BlockReport> ProcessAsync(DumpBlock block, CancellationToken cancellationToken = default);
}

public sealed class BlockProcessor(
    ILlmProvider llmProvider,
    IToolRegistry toolRegistry,
    IToolDispatcher toolDispatcher,
    IDumpBlockStore blocks,
    IScopedUnitOfWork unitOfWork,
    ICoupleScopeAccessor scopeAccessor,
    TimeProvider clock) : IBlockProcessor
{
    private readonly ILlmProvider _llmProvider = llmProvider ?? throw new ArgumentNullException(nameof(llmProvider));
    private readonly IToolRegistry _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
    private readonly IToolDispatcher _toolDispatcher = toolDispatcher ?? throw new ArgumentNullException(nameof(toolDispatcher));
    private readonly IDumpBlockStore _blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));
    private readonly IScopedUnitOfWork _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    private readonly ICoupleScopeAccessor _scopeAccessor = scopeAccessor ?? throw new ArgumentNullException(nameof(scopeAccessor));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public async Task<BlockReport> ProcessAsync(DumpBlock block, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(block);

        LlmCompletion completion;

        try
        {
            // Outside any transaction, deliberately. This call takes seconds
            // against a hosted model, and a transaction held open across it is a
            // connection out of the pool and a row locked for the duration —
            // for the one part of the work that touches no rows at all.
            completion = await AskAsync(block, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The model is unreachable, or answered something unparseable. That
            // is this block's failure, not the run's.
            return await FailAsync(block, Describe(ex), cancellationToken);
        }

        if (completion.ToolCalls.Count == 0)
        {
            // Read, and deliberately nothing to do — a heading, a note to self,
            // or a thought no registered tool covers. Under ADR 0004 the model
            // cannot write except through a tool, so "no call" is the whole of
            // what happened, and saying so is the entire point of this milestone.
            return await SettleAsync(
                block,
                DumpBlockStatus.Ignored,
                changes: [],
                note: completion.Content,
                entities: [],
                usage: completion.Usage,
                cancellationToken);
        }

        try
        {
            return await ExecuteAsync(block, completion, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await FailAsync(block, Describe(ex), cancellationToken);
        }
    }

    private Task<LlmCompletion> AskAsync(DumpBlock block, CancellationToken cancellationToken) =>
        _llmProvider.CompleteAsync(
            new LlmRequest(
                Role,
                [
                    new LlmMessage(LlmMessageRole.System, CapturePrompt.System),

                    // The block's text, not the file's. One call per block is what
                    // makes a per-block status honest: with one call for the whole
                    // file there is no way to know which line a refusal belonged
                    // to, and the report would be back to describing actions.
                    new LlmMessage(LlmMessageRole.User, block.RawText),
                ],
                [.. _toolRegistry.All.Select(t => new LlmTool(t.Name, t.Description, t.ParametersSchema))]),
            cancellationToken);

    /// <summary>
    /// Held in a constant so the role recorded in the audit trail cannot drift
    /// from the role actually requested.
    /// </summary>
    private const LlmRole Role = LlmRole.Fast;

    private async Task<BlockReport> ExecuteAsync(
        DumpBlock block,
        LlmCompletion completion,
        CancellationToken cancellationToken)
    {
        var scope = _scopeAccessor.Current;

        var context = new ToolExecutionContext
        {
            CoupleId = scope.CoupleId,
            UserId = scope.UserId,

            // Taken from the block, which inherited it from the file, which is
            // the surface. Never from the text and never from this method's
            // opinion of it (ADR 0009). The private thread reaches this line
            // with PrivateUser and needs no other change.
            Visibility = block.Visibility,
        };

        var changes = new List<CaptureChange>();
        var entities = new List<BlockEntity>();

        // One completion, N calls, billed once. The counts go on the first row
        // and are omitted from the rest, so summing the column over a couple
        // gives the real figure instead of N times it (SPEC.md 50).
        var attribution = LlmAttribution.From(completion.Usage, Role);
        var carriesTokens = true;

        await using var transaction = await _unitOfWork.BeginAsync(cancellationToken);

        foreach (var call in completion.ToolCalls)
        {
            var callContext = context with
            {
                Attribution = carriesTokens ? attribution : attribution.WithoutTokens(),
            };

            carriesTokens = false;

            var result = await _toolDispatcher.DispatchAsync(call, callContext, cancellationToken);

            changes.Add(new CaptureChange(
                result.ToolName,
                result.Outcome,
                result.EntityType,
                result.EntityId,
                Describe(result, call)));

            if (result.Succeeded && result is { EntityType: { } type, EntityId: { } id })
            {
                entities.Add(new BlockEntity(type, id, "created"));
            }
        }

        // Refused by every call it made is a failure of this block, not a
        // success with footnotes. SPEC.md 46 forbids success language over a
        // failed action, and "processed" over a block that wrote nothing is
        // exactly that language.
        var status = changes.Any(c => c.Outcome == ToolOutcome.Success)
            ? DumpBlockStatus.Processed
            : DumpBlockStatus.Failed;

        var note = status == DumpBlockStatus.Failed
            ? string.Join("; ", changes.Select(c => c.Description))
            : completion.Content;

        block.Status = status;
        block.ErrorMessage = status == DumpBlockStatus.Failed ? Truncate(note) : null;
        block.ProcessedAt = _clock.GetUtcNow();

        await _blocks.MarkAsync(block, cancellationToken);
        await _blocks.LinkEntitiesAsync(block.Id, entities, cancellationToken);

        // The rows, their audit entries, the block's status and the links between
        // them commit together. A report citing a row that was rolled back would
        // be worse than no report.
        await transaction.CommitAsync(cancellationToken);

        return new BlockReport(block.Id, block.RawText, status, changes, note)
        {
            Usage = completion.Usage,
        };
    }

    /// <summary>
    /// Records a terminal status that produced no tool calls, in its own
    /// transaction.
    /// </summary>
    private async Task<BlockReport> SettleAsync(
        DumpBlock block,
        DumpBlockStatus status,
        IReadOnlyList<CaptureChange> changes,
        string? note,
        IReadOnlyList<BlockEntity> entities,
        LlmUsage? usage,
        CancellationToken cancellationToken)
    {
        block.Status = status;
        block.ProcessedAt = _clock.GetUtcNow();

        await using var transaction = await _unitOfWork.BeginAsync(cancellationToken);

        await _blocks.MarkAsync(block, cancellationToken);

        if (entities.Count > 0)
        {
            await _blocks.LinkEntitiesAsync(block.Id, entities, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new BlockReport(block.Id, block.RawText, status, changes, note) { Usage = usage };
    }

    /// <summary>
    /// Marks a block failed in a transaction of its own.
    ///
    /// It has to be a new one: the failure being recorded is, often enough, the
    /// failure that rolled the previous transaction back, and a status written
    /// inside it would disappear along with the work it was describing. This is
    /// why intake is atomic and processing is not — the note in CaptureIntake
    /// said a transaction per block would follow, and this is it.
    /// </summary>
    private async Task<BlockReport> FailAsync(DumpBlock block, string reason, CancellationToken cancellationToken)
    {
        block.Status = DumpBlockStatus.Failed;
        block.ErrorMessage = Truncate(reason);
        block.ProcessedAt = _clock.GetUtcNow();

        await using var transaction = await _unitOfWork.BeginAsync(cancellationToken);
        await _blocks.MarkAsync(block, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new BlockReport(block.Id, block.RawText, DumpBlockStatus.Failed, [], reason);
    }

    /// <summary>
    /// The exception's own message, and never its stack trace. This string is
    /// rendered on a page and stored in a column both partners can read.
    /// </summary>
    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>
    /// Plain language, including for failures. SPEC.md 46 forbids success
    /// language over a failed action, and a refusal the user cannot understand
    /// is indistinguishable from the system quietly losing their note.
    /// </summary>
    private static string Describe(ToolResult result, LlmToolCall call)
    {
        if (result.Succeeded)
        {
            return $"{Humanise(result.ToolName)}: {Summarise(call)}";
        }

        var reason = result.Errors is { Count: > 0 }
            ? string.Join("; ", result.Errors)
            : result.Outcome.ToString();

        return $"{Humanise(result.ToolName)} was not applied — {reason}";
    }

    private static string Humanise(string toolName) => toolName.Replace('_', ' ');

    private static string Summarise(LlmToolCall call)
    {
        // The first string argument is nearly always the human-meaningful one:
        // an item's name, a task's title, an event's title.
        foreach (var property in call.Arguments.EnumerateObject())
        {
            if (property.Value.ValueKind == System.Text.Json.JsonValueKind.String &&
                property.Value.GetString() is { Length: > 0 } value)
            {
                return value;
            }
        }

        return call.Arguments.GetRawText();
    }

    /// <summary>
    /// error_message is text and takes anything, but a model's raw output can be
    /// thousands of characters and this column is read by a person on a phone.
    /// </summary>
    private static string? Truncate(string? value) =>
        value is { Length: > 500 } ? value[..497] + "…" : value;
}
