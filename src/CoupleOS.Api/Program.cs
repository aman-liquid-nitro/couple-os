using CoupleOS.Api.Development;
using CoupleOS.Api.Health;
using CoupleOS.Application;
using CoupleOS.AI.DependencyInjection;
using CoupleOS.Infrastructure.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// The connection string must name the non-superuser application role. A
// superuser connection bypasses every row-level security policy and silently
// voids ADR 0005 — the integration tests demonstrate exactly that failure.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Default is not configured. Copy .env.example to .env and run docker compose up -d.");

builder.Services.AddRazorPages();

// Container orchestration reads this, so it asserts something worth knowing:
// the database answers. See DatabaseHealthCheck for why a liveness-only probe
// was not enough.
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

builder.Services.AddCoupleOsInfrastructure(connectionString);
builder.Services.AddCoupleOsApplication();
builder.Services.AddOllamaProvider(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await DevelopmentSeeder.SeedAsync(app.Services, app.Configuration);
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

// Stands in for authentication until M1. Must run before any page, because a
// request without a scope reads nothing at all rather than failing visibly.
if (app.Environment.IsDevelopment())
{
    app.UseMiddleware<DevelopmentScopeMiddleware>();
}

app.MapRazorPages();

// Plain text, one word, no authentication: this is what the container's
// HEALTHCHECK calls, and it must stay callable before M1 adds sessions.
app.MapHealthChecks("/health");

app.Run();
