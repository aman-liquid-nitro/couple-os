using System.Text.Json;
using CoupleOS.Application.Tools;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CoupleOS.UnitTests;

/// <summary>
/// Two properties of the tool catalogue as a whole, asserted over the real
/// registration rather than over a list written here.
///
/// The first is M3's own exit criterion: no tool schema anywhere in the codebase
/// offers the model a word for whose data it is writing. The dispatcher refuses
/// those arguments whatever a schema says, and that is the second line — this is
/// the first, and it is the one that scales, because a tool written next year gets
/// checked without anybody remembering to add it here.
///
/// The second is that every tool in the assembly is actually registered. An
/// unregistered tool is not a compile error and not a test failure anywhere else:
/// it is a tool the model is never told about, so the notes it was written for go
/// to a model with nothing legal to do about them and come back as "read, nothing
/// to do" — the exact silence M2 exists to remove, reintroduced by omission.
/// </summary>
public sealed class ToolCatalogueTests
{
    /// <summary>
    /// The same names <c>ToolDispatcher</c> refuses. Repeated rather than exposed
    /// from it: a schema that started offering one of these and a dispatcher that
    /// stopped refusing it are two separate failures, and a shared constant would
    /// let the second one hide the first.
    /// </summary>
    private static readonly string[] Forbidden = ["couple_id", "owner_user_id", "user_id", "visibility"];

    /// <summary>
    /// The real registration, with the persistence seams faked. Resolving
    /// <c>ITool</c> from the container is what makes this a test of what ships
    /// rather than of a list of types someone kept up to date.
    /// </summary>
    internal static List<ITool> Registered()
    {
        var services = new ServiceCollection();

        services.AddScoped<IShoppingItemWriter>(_ => new NoOpShoppingItemWriter());
        services.AddScoped<ITaskWriter>(_ => new RecordingTaskWriter());
        services.AddScoped<IEventWriter>(_ => new NoOpEventWriter());
        services.AddScoped<IExpenseWriter>(_ => new NoOpExpenseWriter());
        services.AddScoped<IExpenseCategoryLookup>(_ => new NoOpCategoryLookup());
        services.AddScoped<CoupleOS.Application.Time.ICoupleClock>(_ => new FixedCoupleClock());
        services.AddScoped<IPartnerLookup>(_ => new StubPartnerLookup(Guid.NewGuid()));
        services.AddScoped<IMemoryWriter>(_ => new NoOpMemoryWriter());
        services.AddScoped<IMemorySearch>(_ => new NoOpMemorySearch());

        services.AddCoupleOsTools();

        using var provider = services.BuildServiceProvider();

        return [.. provider.GetServices<ITool>()];
    }

    [Fact]
    public void No_tool_schema_offers_the_model_a_word_for_whose_data_it_is_writing()
    {
        var offences = new List<string>();

        foreach (var tool in Registered())
        {
            foreach (var property in Properties(tool.ParametersSchema))
            {
                if (Forbidden.Contains(property, StringComparer.OrdinalIgnoreCase))
                {
                    offences.Add($"{tool.Name} declares '{property}'");
                }
            }
        }

        Assert.True(offences.Count == 0, string.Join("; ", offences));
    }

    [Fact]
    public void Every_tool_in_the_assembly_is_registered()
    {
        var registered = Registered().Select(t => t.GetType()).ToHashSet();

        var declared = typeof(ITool).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(ITool).IsAssignableFrom(t))
            .ToList();

        var missing = declared.Where(t => !registered.Contains(t)).Select(t => t.Name).ToList();

        Assert.True(
            missing.Count == 0,
            "These tools exist but are never offered to a model, so the notes they were written for " +
            "come back as 'nothing to do': " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_tool_declares_an_object_schema_with_at_least_one_property()
    {
        // The dispatcher reads "properties" to reject undeclared arguments, and a
        // schema without one declares nothing — so every argument would be
        // undeclared and every call would be refused. A tool that cannot be called
        // is worse than one that is missing, because it is in the prompt.
        foreach (var tool in Registered())
        {
            Assert.Equal(JsonValueKind.Object, tool.ParametersSchema.ValueKind);
            Assert.NotEmpty(Properties(tool.ParametersSchema));
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), $"{tool.Name} has no description");
        }
    }

    private static List<string> Properties(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties)
            ? [.. properties.EnumerateObject().Select(p => p.Name)]
            : [];

    private sealed class NoOpMemoryWriter : IMemoryWriter
    {
        public Task<IReadOnlyList<CoupleOS.Domain.Entities.Memory>> FindBySubjectAsync(
            Guid coupleId,
            CoupleOS.Domain.Enums.MemoryType type,
            string subjectKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CoupleOS.Domain.Entities.Memory>>([]);

        public Task AddAsync(
            CoupleOS.Domain.Entities.Memory memory,
            IReadOnlyList<CoupleOS.Domain.Entities.Memory> superseded,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpMemorySearch : IMemorySearch
    {
        public Task<IReadOnlyList<CoupleOS.Domain.Entities.Memory>> SearchAsync(
            MemoryQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CoupleOS.Domain.Entities.Memory>>([]);
    }

    private sealed class NoOpExpenseWriter : IExpenseWriter
    {
        public Task AddAsync(
            CoupleOS.Domain.Entities.Expense expense,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpCategoryLookup : IExpenseCategoryLookup
    {
        public Task<Guid?> FindAsync(Guid coupleId, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(null);
    }

    private sealed class NoOpEventWriter : IEventWriter
    {
        public Task AddAsync(
            CoupleOS.Domain.Entities.CalendarEvent calendarEvent,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpShoppingItemWriter : IShoppingItemWriter
    {
        public Task AddAsync(
            CoupleOS.Domain.Entities.ShoppingItem item,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
