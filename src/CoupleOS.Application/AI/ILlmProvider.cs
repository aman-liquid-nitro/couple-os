namespace CoupleOS.Application.AI;

/// <summary>
/// The single point at which this system talks to a language model (ADR 0003).
///
/// Deliberately narrow. It takes messages and tools, and returns tool calls and
/// token counts. It does not persist, retry across providers, or decide what to
/// do with a result — those belong to callers, and folding them in here would
/// give this type several reasons to change.
///
/// No streaming in V0. The dump-processing loop consumes whole tool calls, and a
/// half-parsed call is unusable; the chat surface can gain streaming when it is
/// built rather than being speculated about now.
/// </summary>
public interface ILlmProvider
{
    string Name { get; }

    Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default);
}
