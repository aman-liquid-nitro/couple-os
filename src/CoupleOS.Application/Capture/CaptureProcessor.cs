using CoupleOS.Application.AI;
using CoupleOS.Application.Persistence;
using CoupleOS.Application.Security;
using CoupleOS.Application.Tools;

namespace CoupleOS.Application.Capture;

/// <summary>
/// Joins the two halves of the system: a model that proposes, and a tool layer
/// that disposes.
///
/// It orchestrates and nothing else. It does not talk to a model directly, does
/// not know what a tool does, does not open connections and does not render.
/// Each of those belongs to something it depends on, which is what lets this be
/// unit-tested against a fake provider without a database or a GPU.
/// </summary>
public sealed class CaptureProcessor(
    ILlmProvider llmProvider,
    IToolRegistry toolRegistry,
    IToolDispatcher toolDispatcher,
    IScopedUnitOfWork unitOfWork,
    ICoupleScopeAccessor scopeAccessor) : ICaptureProcessor
{
    private readonly ILlmProvider _llmProvider = llmProvider ?? throw new ArgumentNullException(nameof(llmProvider));
    private readonly IToolRegistry _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
    private readonly IToolDispatcher _toolDispatcher = toolDispatcher ?? throw new ArgumentNullException(nameof(toolDispatcher));
    private readonly IScopedUnitOfWork _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    private readonly ICoupleScopeAccessor _scopeAccessor = scopeAccessor ?? throw new ArgumentNullException(nameof(scopeAccessor));

    public async Task<CaptureReport> ProcessAsync(
        string text,
        CaptureSurface surface,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return CaptureReport.Empty;
        }

        var scope = _scopeAccessor.Current;

        var completion = await _llmProvider.CompleteAsync(
            new LlmRequest(
                LlmRole.Fast,
                [
                    new LlmMessage(LlmMessageRole.System, CapturePrompt.System),
                    new LlmMessage(LlmMessageRole.User, text),
                ],
                [.. _toolRegistry.All.Select(t => new LlmTool(t.Name, t.Description, t.ParametersSchema))]),
            cancellationToken);

        if (completion.ToolCalls.Count == 0)
        {
            // Nothing actionable, or the model narrated instead of calling.
            // Either way nothing was written, and saying so beats silence.
            return new CaptureReport([], [], completion.Content, completion.Usage);
        }

        var context = new ToolExecutionContext
        {
            CoupleId = scope.CoupleId,
            UserId = scope.UserId,

            // Derived from the surface, never from the text (ADR 0009).
            Visibility = surface.ToVisibility(),
        };

        var applied = new List<CaptureChange>();
        var refused = new List<CaptureChange>();

        // One transaction for the whole run: the rows and their audit entries
        // commit together, or a change report could cite something that was
        // rolled back.
        await using var transaction = await _unitOfWork.BeginAsync(cancellationToken);

        foreach (var call in completion.ToolCalls)
        {
            var result = await _toolDispatcher.DispatchAsync(call, context, cancellationToken);

            var change = new CaptureChange(
                result.ToolName,
                result.Outcome,
                result.EntityType,
                result.EntityId,
                Describe(result, call));

            if (result.Succeeded)
            {
                applied.Add(change);
            }
            else
            {
                refused.Add(change);
            }
        }

        await transaction.CommitAsync(cancellationToken);

        return new CaptureReport(applied, refused, completion.Content, completion.Usage);
    }

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
}
