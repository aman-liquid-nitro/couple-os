using CoupleOS.Application.AI;

namespace CoupleOS.Application.Tools;

/// <summary>
/// The only route from a model's output to a database write (ADR 0004).
/// Returns an outcome for every call, including refusals. It does not throw for
/// a bad call, because a refusal is information the change report must show.
/// </summary>
public interface IToolDispatcher
{
    Task<ToolResult> DispatchAsync(
        LlmToolCall call,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default);
}
