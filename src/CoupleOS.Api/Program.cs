using CoupleOS.Api.Identity;
using CoupleOS.Api.Health;
using CoupleOS.Application;
using CoupleOS.Application.Identity;
using CoupleOS.AI.DependencyInjection;
using CoupleOS.Infrastructure.DependencyInjection;
using CoupleOS.Infrastructure.Identity;

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

// ADR 0007's parameters. Registered after AddCoupleOsApplication, which uses
// TryAdd for its defaults, so a value in configuration wins and an absent
// section leaves the ADR's numbers in place.
builder.Services.AddSingleton(
    builder.Configuration.GetSection("Identity").Get<IdentityOptions>() ?? new IdentityOptions());

builder.Services.AddCoupleOsMail(
    builder.Configuration.GetSection("Smtp").Get<SmtpOptions>() ?? new SmtpOptions(),
    builder.Environment.IsDevelopment());

builder.Services.AddScoped<CurrentSession>();
builder.Services.AddScoped<IMagicLinkUrlFactory, MagicLinkUrlFactory>();
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

// Real sessions as of M1. Runs after UseStaticFiles so assets never pay for it,
// and before any page because ADR 0005 fails closed: a request reaching a page
// without a couple scope would render zero rows and look like a data bug rather
// than a missing sign-in. This middleware either establishes a scope or redirects.
app.UseMiddleware<SessionAuthenticationMiddleware>();

app.MapRazorPages();

// Plain text, one word, no authentication: this is what the container's
// HEALTHCHECK calls, and it has no credentials to offer.
app.MapHealthChecks("/health");

app.Run();
