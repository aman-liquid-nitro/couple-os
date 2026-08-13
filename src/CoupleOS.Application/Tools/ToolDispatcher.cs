using System.Collections.Frozen;
using System.Text.Json;
using CoupleOS.Application.AI;

namespace CoupleOS.Application.Tools;

/// <summary>
/// TOOLS.md's pipeline: validate, authorize, confirmation gate, execute.
/// Auditing is layered on separately by <see cref="AuditingToolDispatcher"/>,
/// so this type has one reason to change and the audit rule cannot be forgotten
/// by a tool author.
///
/// Two of TOOLS.md's universal rules are enforced here rather than delegated to
/// each tool, because a rule that depends on every future implementer
/// remembering it is not a rule:
///
///   Rule 5 — couple_id and owner_user_id are never accepted as arguments.
///   Rule 2 — visibility is inherited from the capture surface, never chosen.
///
/// A model that could set any of those could write into another couple's data
/// or move a surprise into the shared scope. So a call carrying them is refused
/// outright, before the tool sees it, no matter what the tool would have done.
/// </summary>
public sealed class ToolDispatcher(IToolRegistry registry) : IToolDispatcher
{
    /// <summary>
    /// Names the model may never supply. These come from the session and the
    /// capture surface (ADR 0009).
    /// </summary>
    private static readonly FrozenSet<string> ForbiddenArguments =
        new[] { "couple_id", "owner_user_id", "user_id", "visibility" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly IToolRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public async Task<ToolResult> DispatchAsync(
        LlmToolCall call,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);

        var tool = _registry.Find(call.Name);
        if (tool is null)
        {
            // A hallucinated tool name is a validation failure, not a crash. The
            // block stays unprocessed and the report says why.
            return new ToolResult(call.Name, ToolOutcome.ValidationFailed,
                Errors: [$"No tool named '{call.Name}' is registered."]);
        }

        var structural = CheckArgumentShape(tool, call.Arguments);
        if (!structural.IsValid)
        {
            return new ToolResult(tool.Name, ToolOutcome.ValidationFailed, Errors: structural.Errors);
        }

        var validation = await tool.ValidateAsync(call.Arguments, cancellationToken);
        if (!validation.IsValid)
        {
            return new ToolResult(tool.Name, ToolOutcome.ValidationFailed, Errors: validation.Errors);
        }

        if (tool.Tier != ToolTier.None && !context.IsConfirmed)
        {
            return new ToolResult(tool.Name, ToolOutcome.ConfirmationRequired);
        }

        try
        {
            var execution = await tool.ExecuteAsync(new ToolInvocation(call.Arguments, context), cancellationToken);

            return new ToolResult(
                tool.Name,
                execution.Outcome,
                execution.EntityType,
                execution.EntityId,
                execution.Error is null ? null : [execution.Error],
                execution.Question,
                execution.Note,
                execution.Answer);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One tool throwing must not abandon the rest of a dump run. The
            // block is marked failed, the reason is recorded, and processing
            // continues.
            return new ToolResult(tool.Name, ToolOutcome.ExecutionFailed, Errors: [ex.Message]);
        }
    }

    /// <summary>
    /// Rejects arguments the model may never supply, and properties the tool's
    /// schema does not declare. TOOLS.md requires unknown properties to be
    /// rejected rather than ignored: silently dropping one means the model
    /// believed it set something that never took effect.
    /// </summary>
    private static ToolValidation CheckArgumentShape(ITool tool, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return ToolValidation.Invalid($"Arguments for '{tool.Name}' must be a JSON object.");
        }

        var declared = DeclaredProperties(tool.ParametersSchema);
        var errors = new List<string>();

        foreach (var property in arguments.EnumerateObject())
        {
            if (ForbiddenArguments.Contains(property.Name))
            {
                errors.Add(
                    $"'{property.Name}' may never be supplied by the model; it comes from the session " +
                    "and the capture surface (TOOLS.md rules 2 and 5).");
                continue;
            }

            if (!declared.Contains(property.Name))
            {
                errors.Add($"'{property.Name}' is not declared in the schema for '{tool.Name}'.");
            }
        }

        return errors.Count == 0 ? ToolValidation.Valid : new ToolValidation(false, errors);
    }

    private static FrozenSet<string> DeclaredProperties(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return FrozenSet<string>.Empty;
        }

        return properties.EnumerateObject()
            .Select(p => p.Name)
            .ToFrozenSet(StringComparer.Ordinal);
    }
}
