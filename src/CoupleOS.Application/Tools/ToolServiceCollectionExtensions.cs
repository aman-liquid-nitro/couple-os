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

        services.AddScoped<IToolRegistry>(sp => new ToolRegistry(sp.GetServices<ITool>()));

        services.AddScoped<IToolDispatcher>(sp => new AuditingToolDispatcher(
            new ToolDispatcher(sp.GetRequiredService<IToolRegistry>()),
            sp.GetRequiredService<IToolAuditSink>()));

        return services;
    }
}
