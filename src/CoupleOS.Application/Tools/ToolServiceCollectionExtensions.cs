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
