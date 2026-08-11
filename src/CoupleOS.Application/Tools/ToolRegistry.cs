using System.Collections.Frozen;

namespace CoupleOS.Application.Tools;

/// <summary>
/// Resolves tools by name. Adding a tool means registering a class, never
/// editing this type or the dispatcher — the open/closed part of ADR 0004.
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly FrozenDictionary<string, ITool> _byName;

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        All = [.. tools];

        var duplicates = All.GroupBy(t => t.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        // Two tools answering to one name would make which-one-ran depend on
        // registration order, and the audit trail would name the wrong code.
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate tool names registered: {string.Join(", ", duplicates)}.");
        }

        _byName = All.ToFrozenDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<ITool> All { get; }

    public ITool? Find(string name) => _byName.GetValueOrDefault(name);
}
