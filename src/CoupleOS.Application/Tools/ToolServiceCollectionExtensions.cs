using Microsoft.Extensions.DependencyInjection;

namespace CoupleOS.Application.Tools;

public static class ToolServiceCollectionExtensions
{
    /// <summary>
    /// Registers the tool pipeline and the V0 tool set.
    ///
    /// The dispatcher is registered as the auditing decorator wrapping the plain
    /// one, so nothing can obtain an unaudited dispatcher from the container.
    /// TOOLS.md makes auditing universal; composition is where that is made true.
    /// </summary>
    public static IServiceCollection AddCoupleOsTools(this IServiceCollection services)
    {
        // Adding a tool means adding a line here. Nothing else changes.
        services.AddScoped<ITool, CreateShoppingItemTool>();

        // M3. Two tools over one table: a reminder is a task whose due time is
        // required, which is one label and one check constraint rather than a
        // second entity (ARCHITECTURE.md §3).
        services.AddScoped<ITool, CreateTaskTool>();
        services.AddScoped<ITool, CreateReminderTool>();

        // The tool whose absence produced M2's founding defect: the dinner line in
        // the first real run had nothing to call, so it vanished from the report.
        services.AddScoped<ITool, CreateEventTool>();
        services.AddScoped<ITool, CreateExpenseTool>();

        // The last two, and the two that are not about doing anything: what the
        // couple knows, and asking it. search_memory is the only read-only tool in
        // the set and the only one whose result is a sentence rather than a row.
        services.AddScoped<ITool, CreateMemoryTool>();
        services.AddScoped<ITool, SearchMemoryTool>();

        // Registered alongside the writing tools rather than treated as part of
        // the pipeline, because to the model it is one of the options and that is
        // exactly the point: declining has to be as reachable as acting, or the
        // rule against guessing has no compliant branch (TOOLS.md 2a).
        services.AddScoped<ITool, RequestClarificationTool>();

        services.AddScoped<IToolRegistry>(sp => new ToolRegistry(sp.GetServices<ITool>()));

        services.AddScoped<IToolDispatcher>(sp => new AuditingToolDispatcher(
            new ToolDispatcher(sp.GetRequiredService<IToolRegistry>()),
            sp.GetRequiredService<IToolAuditSink>()));

        return services;
    }
}
