using System.Text.Json;

namespace CoupleOS.Application.Tools;

/// <summary>
/// One thing the model is permitted to do (ADR 0004).
///
/// Validation and execution are separate members on purpose. The dispatcher
/// calls them in order and will not execute a call that failed validation, so
/// TOOLS.md's "validate → … → execute" is enforced by the pipeline rather than
/// by each tool remembering to check its own arguments first.
/// </summary>
public interface ITool
{
    /// <summary>Must match the name given to the model.</summary>
    string Name { get; }

    string Description { get; }

    /// <summary>
    /// JSON Schema shown to the model. Also used by the dispatcher to reject
    /// unknown properties, so this is a contract in both directions.
    /// </summary>
    JsonElement ParametersSchema { get; }

    ToolTier Tier { get; }

    bool IsIdempotent { get; }

    /// <summary>
    /// Tool-specific checks: required fields, ranges, parseable amounts.
    /// Structural checks — forbidden and unknown properties — are the
    /// dispatcher's job and have already run.
    /// </summary>
    ValueTask<ToolValidation> ValidateAsync(JsonElement arguments, CancellationToken cancellationToken = default);

    /// <summary>
    /// May assume validation passed and the context is trustworthy. Should not
    /// throw for expected failures — return an outcome instead, so the change
    /// report can explain what happened.
    /// </summary>
    Task<ToolExecution> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken = default);
}

public interface IToolRegistry
{
    IReadOnlyList<ITool> All { get; }

    ITool? Find(string name);
}
