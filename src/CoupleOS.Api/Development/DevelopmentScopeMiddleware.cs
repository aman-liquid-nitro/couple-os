using CoupleOS.Application.Security;

namespace CoupleOS.Api.Development;

/// <summary>
/// Stands in for authentication until M1.
///
/// Every request is treated as the seeded partner. This exists because the
/// alternative — pages running with no scope at all — would make every query
/// return nothing and look like a data bug rather than a missing feature, since
/// ADR 0005 fails closed.
///
/// Registered only in Development. If it ever runs elsewhere, every visitor is
/// the same person, so it refuses loudly rather than quietly carrying on.
/// </summary>
public sealed class DevelopmentScopeMiddleware(RequestDelegate next, IHostEnvironment environment)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    private readonly IHostEnvironment _environment = environment
        ?? throw new ArgumentNullException(nameof(environment));

    public async Task InvokeAsync(HttpContext context, ICoupleScopeSetter scopeSetter, IConfiguration configuration)
    {
        if (!_environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "DevelopmentScopeMiddleware is running outside Development. It authenticates " +
                "nobody and would make every visitor the same person.");
        }

        scopeSetter.Set(new CoupleScope(
            configuration.GetValue<Guid>("Development:CoupleId"),
            configuration.GetValue<Guid>("Development:UserId")));

        await _next(context);
    }
}
