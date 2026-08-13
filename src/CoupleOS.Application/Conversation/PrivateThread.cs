using CoupleOS.Application.AI;
using CoupleOS.Application.Capture;
using CoupleOS.Application.Persistence;
using CoupleOS.Application.Security;
using CoupleOS.Application.Tools;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;

namespace CoupleOS.Application.Conversation;

/// <summary>
/// The private capture surface: one message in, one reply out.
/// </summary>
public interface IPrivateThread
{
    /// <summary>The caller's thread as it stands, oldest turn first.</summary>
    Task<IReadOnlyList<ConversationMessage>> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes one message through the pipeline and returns the exchange.
    ///
    /// Never throws for a model or a tool behaving badly, for the same reason
    /// <c>IBlockProcessor</c> does not: a person watching a spinner is owed a
    /// sentence, and an exception here becomes a 500 where the honest outcome is
    /// "I could not reach the model — your message is saved."
    /// </summary>
    Task<ConversationTurn> SayAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// ADR 0009's private half, and the mirror of <c>CaptureProcessor</c> rather than
/// a variant of it.
///
/// What the two share is the part that matters: one model provider, one tool
/// registry, one <see cref="IToolDispatcher"/>, and therefore one
/// validate → authorize → execute → audit path (ADR 0004). What differs is
/// everything about arrival and reporting. The shared surface batches a file into
/// blocks, parks what it cannot resolve and reports afterwards; this answers one
/// message at a time and asks its questions in the reply, because there is a
/// person present and no coordination cost to interrupting them.
///
/// The visibility every tool here runs under is
/// <see cref="Visibility.PrivateUser"/>, and it comes from
/// <see cref="CaptureSurface.PrivateThread"/> rather than from a literal in this
/// file. That indirection is the whole of ADR 0009's structural claim: there is
/// one mapping from surface to scope, and no code path where a model's opinion of
/// a sentence reaches it.
/// </summary>
public sealed class PrivateThread(
    IConversationStore conversations,
    ILlmProvider llmProvider,
    IToolRegistry toolRegistry,
    IToolDispatcher toolDispatcher,
    IScopedUnitOfWork unitOfWork,
    ICoupleScopeAccessor scopeAccessor) : IPrivateThread
{
    private readonly IConversationStore _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
    private readonly ILlmProvider _llmProvider = llmProvider ?? throw new ArgumentNullException(nameof(llmProvider));
    private readonly IToolRegistry _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
    private readonly IToolDispatcher _toolDispatcher = toolDispatcher ?? throw new ArgumentNullException(nameof(toolDispatcher));
    private readonly IScopedUnitOfWork _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    private readonly ICoupleScopeAccessor _scopeAccessor = scopeAccessor ?? throw new ArgumentNullException(nameof(scopeAccessor));

    /// <summary>
    /// Held in a constant so the role recorded in the audit trail cannot drift
    /// from the role actually requested.
    /// </summary>
    private const LlmRole Role = LlmRole.Fast;

    /// <summary>
    /// The surface, named once. Everything downstream derives the scope from it.
    /// </summary>
    private const CaptureSurface Surface = CaptureSurface.PrivateThread;

    public async Task<IReadOnlyList<ConversationMessage>> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await _unitOfWork.BeginAsync(cancellationToken);

        var session = await _conversations.GetOrCreateAsync(cancellationToken);
        var history = await _conversations.HistoryAsync(session.Id, ChatPrompt.HistoryTurns, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return history;
    }

    public async Task<ConversationTurn> SayAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var scope = _scopeAccessor.Current;

        // The person's turn is durable before the model is called, and that
        // ordering is deliberate. The call takes seconds and can fail; a thread
        // that loses what somebody typed because no reply arrived is worse than a
        // thread with an unanswered line sitting in it.
        var (session, history, said) = await RecordAsync(scope, text.Trim(), cancellationToken);

        LlmCompletion completion;

        try
        {
            // Outside any transaction, deliberately — the same reason
            // BlockProcessor does it: seconds of held connection and locked rows
            // for the one part of the work that touches no rows at all.
            completion = await AskAsync(history, said, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A reply, not an exception. The person is watching a spinner and is
            // owed a sentence; the message they typed is already saved, so the
            // honest thing is to say so and let them press send again.
            return await FailAsync(
                scope,
                session,
                said,
                $"I could not reach the model just now, so I have not acted on that — your message is " +
                $"saved. ({Describe(ex)})",
                cancellationToken);
        }

        return await ExecuteAsync(scope, session, said, completion, cancellationToken);
    }

    /// <summary>
    /// Opens the thread, reads what it can send as context, and writes the
    /// person's turn — one transaction, because a message recorded without the
    /// session it belongs to would be a message with no privacy.
    /// </summary>
    private async Task<(ConversationSession Session, IReadOnlyList<ConversationMessage> History, ConversationMessage Said)>
        RecordAsync(ICoupleScope scope, string text, CancellationToken cancellationToken)
    {
        await using var transaction = await _unitOfWork.BeginAsync(cancellationToken);

        var session = await _conversations.GetOrCreateAsync(cancellationToken);

        // Read before the new turn is written, so the history handed to the model
        // is the context and the new message is the question. Reading afterwards
        // would put the message in twice.
        var history = await _conversations.HistoryAsync(session.Id, ChatPrompt.HistoryTurns, cancellationToken);

        var said = Message(scope, session, MessageRole.User, text, authored: true);

        await _conversations.AppendAsync(said, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return (session, history, said);
    }

    private Task<LlmCompletion> AskAsync(
        IReadOnlyList<ConversationMessage> history,
        ConversationMessage said,
        CancellationToken cancellationToken) =>
        _llmProvider.CompleteAsync(
            new LlmRequest(
                Role,
                [
                    new LlmMessage(LlmMessageRole.System, ChatPrompt.System),
                    .. history.Select(m => new LlmMessage(Wire(m.Role), m.Content)),
                    new LlmMessage(LlmMessageRole.User, said.Content),
                ],
                [.. _toolRegistry.All.Select(t => new LlmTool(t.Name, t.Description, t.ParametersSchema))]),
            cancellationToken);

    private async Task<ConversationTurn> ExecuteAsync(
        ICoupleScope scope,
        ConversationSession session,
        ConversationMessage said,
        LlmCompletion completion,
        CancellationToken cancellationToken)
    {
        var context = new ToolExecutionContext
        {
            CoupleId = scope.CoupleId,
            UserId = scope.UserId,

            // From the surface, via the one mapping that exists (ADR 0009). Never
            // from the text, and never from this method's reading of it — "she
            // mentioned she likes that bag" and "she mentioned she wants to visit
            // her parents" are the same sentence shape with opposite answers, and
            // one of those mistakes cannot be taken back.
            Visibility = Surface.ToVisibility(),
        };

        // One completion, N calls, billed once. The counts ride on the first row
        // and are omitted from the rest, so summing the column gives the real
        // figure instead of N times it (SPEC.md 50).
        var attribution = LlmAttribution.From(completion.Usage, Role);
        var carriesTokens = true;

        var changes = new List<CaptureChange>();
        var questions = new List<string>();

        await using var transaction = await _unitOfWork.BeginAsync(cancellationToken);

        foreach (var call in completion.ToolCalls)
        {
            var callContext = context with
            {
                Attribution = carriesTokens ? attribution : attribution.WithoutTokens(),
            };

            carriesTokens = false;

            ToolResult result;

            try
            {
                result = await _toolDispatcher.DispatchAsync(call, callContext, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The dispatcher turns a tool's own failure into a result, so
                // reaching here means the infrastructure under it broke. Recorded
                // as a refusal of this call rather than allowed to take the turn
                // down: the other calls in the same message may well have worked.
                result = new ToolResult(call.Name, ToolOutcome.ExecutionFailed, Errors: [Describe(ex)]);
            }

            changes.Add(new CaptureChange(
                result.ToolName,
                result.Outcome,
                result.EntityType,
                result.EntityId,
                Describe(result, call),
                result.Asked));

            if (result.Asked)
            {
                questions.Add(result.Question!);
            }
        }

        var reply = Message(scope, session, MessageRole.Assistant, Compose(completion, changes, questions), authored: false);

        await _conversations.AppendAsync(reply, cancellationToken);

        // The rows the tools wrote, their audit entries, and the reply that
        // describes them commit together. A reply citing a row that was rolled
        // back would be worse than no reply.
        await transaction.CommitAsync(cancellationToken);

        return new ConversationTurn(said, reply, changes) { Usage = completion.Usage };
    }

    /// <summary>
    /// The assistant's turn when the model could not be reached, in a transaction
    /// of its own.
    ///
    /// Its own, because the failure being recorded is often the failure that rolled
    /// the previous transaction back — the same rule <c>BlockProcessor.FailAsync</c>
    /// follows, and for the same reason: a reply written inside the doomed
    /// transaction disappears along with the work it was describing.
    /// </summary>
    private async Task<ConversationTurn> FailAsync(
        ICoupleScope scope,
        ConversationSession session,
        ConversationMessage said,
        string reason,
        CancellationToken cancellationToken)
    {
        var reply = Message(scope, session, MessageRole.Assistant, reason, authored: false);

        await using var transaction = await _unitOfWork.BeginAsync(cancellationToken);
        await _conversations.AppendAsync(reply, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ConversationTurn(said, reply, []) { Failed = true };
    }

    /// <summary>
    /// What goes in the assistant's turn.
    ///
    /// The model's own words when it produced any, and a question it asked takes
    /// precedence over them: <c>request_clarification</c> stays in the catalogue
    /// on this surface, and a model that reaches for it here has asked a question
    /// that belongs in the reply rather than in a "needs your input" section this
    /// surface does not have. That is ADR 0009's in-band clarification, built out
    /// of the shared surface's parking tool rather than beside it.
    ///
    /// When the model said nothing at all, this stands in with a sentence — a
    /// blank reply reads as the system having ignored you, and rule 1 of
    /// ChatPrompt asks for prose precisely so this fallback is rare.
    ///
    /// The sentence deliberately does NOT restate the changes. Listing them was
    /// the first implementation and the browser said no: the page prints the
    /// change list beside every reply, so a fallback built out of the same strings
    /// showed the person "create shopping item: coffee" twice and looked like two
    /// coffees. The reply says how it went; the list says what it was.
    /// </summary>
    private static string Compose(
        LlmCompletion completion,
        IReadOnlyList<CaptureChange> changes,
        IReadOnlyList<string> questions)
    {
        if (questions.Count > 0)
        {
            return string.Join(" ", questions);
        }

        if (!string.IsNullOrWhiteSpace(completion.Content))
        {
            return completion.Content.Trim();
        }

        if (changes.Count == 0)
        {
            return "I read that and there was nothing for me to record.";
        }

        // Three sentences rather than one, because SPEC.md 46 forbids success
        // language over a failed action and "Done." over a refused call is
        // exactly that. Counted rather than assumed: a message can be half
        // applied, and that is the case worth having words for.
        var applied = changes.Count(c => c.Outcome == ToolOutcome.Success);

        return applied switch
        {
            0 => "None of that went through — the detail is below.",
            _ when applied == changes.Count => "Recorded — the detail is below.",
            _ => "Some of that went through and some did not — the detail is below.",
        };
    }

    private static ConversationMessage Message(
        ICoupleScope scope,
        ConversationSession session,
        MessageRole role,
        string content,
        bool authored) => new()
        {
            SessionId = session.Id,
            CoupleId = scope.CoupleId,

            // Null for the assistant, because nobody typed it. This does NOT make
            // the row public: who may read it is decided by the session it belongs
            // to. The policy that keyed on authorship instead is the one that let
            // the partner read these turns, which rls-tests.sql section H records.
            UserId = authored ? scope.UserId : null,

            Visibility = Surface.ToVisibility(),
            Role = role,
            Content = content,
        };

    /// <summary>
    /// The wire role for a stored turn.
    ///
    /// System and Tool are unwritten in V0 and are mapped rather than thrown on,
    /// because this runs over rows read back from the database: a value that
    /// arrived from a later version of the schema should degrade to context, not
    /// take the thread down on read.
    /// </summary>
    private static LlmMessageRole Wire(MessageRole role) => role switch
    {
        MessageRole.User => LlmMessageRole.User,
        MessageRole.Assistant => LlmMessageRole.Assistant,
        _ => LlmMessageRole.System,
    };

    /// <summary>
    /// The exception's own message, and never its stack trace. This string is
    /// written into the couple's transcript and rendered on a page.
    /// </summary>
    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>
    /// Plain language, including for failures — the same phrasing the shared
    /// surface's report uses, so the two surfaces describe one tool in one voice.
    /// </summary>
    private static string Describe(ToolResult result, LlmToolCall call)
    {
        if (result.Asked)
        {
            return result.Question!;
        }

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
}
