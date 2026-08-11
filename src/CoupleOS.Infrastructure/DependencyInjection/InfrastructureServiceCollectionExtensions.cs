using CoupleOS.Application.Persistence;
using CoupleOS.Application.Security;
using CoupleOS.Infrastructure.Persistence;
using CoupleOS.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoupleOS.Infrastructure.DependencyInjection;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers persistence and the couple-scoping seam.
    /// </summary>
    /// <param name="connectionString">
    /// Must name the non-superuser application role. A superuser connection
    /// bypasses every row-level security policy and silently voids ADR 0005;
    /// the integration tests demonstrate exactly that failure.
    /// </param>
    public static IServiceCollection AddCoupleOsInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<CoupleOsDbContext>(options => options.UseNpgsql(connectionString));

        // One instance per request, two interfaces onto it.
        services.AddScoped<CoupleScopeHolder>();
        services.AddScoped<ICoupleScopeAccessor>(sp => sp.GetRequiredService<CoupleScopeHolder>());
        services.AddScoped<ICoupleScopeSetter>(sp => sp.GetRequiredService<CoupleScopeHolder>());

        services.AddScoped<IScopedUnitOfWork, CoupleScopedUnitOfWork>();

        return services;
    }
}
