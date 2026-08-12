using CoupleOS.Application.Identity;
using CoupleOS.Application.Security;

namespace CoupleOS.Api.Identity;

/// <summary>
/// Resolves the session cookie, establishes the couple scope row-level security
/// reads, and sends everyone else to the sign-in page.
///
/// This replaces DevelopmentScopeMiddleware, which treated every visitor as the
/// same seeded partner. The reason that stand-in existed is the reason this
/// middleware redirects rather than continuing unauthenticated: ADR 0005 fails
/// closed, so a request with no scope reads nothing at all, and a page rendering
/// zero rows looks like a data bug rather than a missing sign-in. Nothing here
/// ever runs without a scope; it either has one or it never reaches a page.
/// </summary>
public sealed class SessionAuthenticationMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    /// <summary>
    /// Reachable without a session. Everything needed to get one, plus the
    /// container's health probe, which has no credentials to offer (STATUS debt 3).
    /// Static assets never reach here — UseStaticFiles short-circuits first.
    /// </summary>
    private static readonly string[] AnonymousPaths =
    [
        "/signin",
        "/auth/callback",
        "/health",
        "/error",
    ];

    /// <summary>
    /// Reachable when signed in but not yet in a couple. A freshly registered user
    /// is in this state, and so is someone whose invitation lost a race for the
    /// second seat in a couple.
    /// </summary>
    private static readonly string[] CouplelessPaths =
    [
        "/couple/create",
        "/signout",
    ];

    public async Task InvokeAsync(
        HttpContext context,
        CurrentSession currentSession,
        ISessionStore sessions,
        ICoupleScopeSetter scopeSetter,
        IdentityOptions options,
        TimeProvider clock)
    {
        var path = context.Request.Path;

        if (context.Request.Cookies.TryGetValue(SessionCookie.Name, out var token)
            && !string.IsNullOrWhiteSpace(token))
        {
            var resolved = await sessions.ResolveAsync(SecretToken.Hash(token), context.RequestAborted);

            if (resolved is null)
            {
                // Expired or revoked. Clearing it here means the browser stops
                // presenting a dead cookie on every subsequent request, and the
                // person is not stuck in a loop that looks like the site is broken.
                context.Response.Cookies.Delete(SessionCookie.Name, SessionCookie.DeletionOptions());
            }
            else
            {
                currentSession.User = resolved;

                if (resolved.CoupleId is { } coupleId)
                {
                    scopeSetter.Set(new CoupleScope(coupleId, resolved.UserId));
                }

                await RenewIfStaleAsync(resolved, sessions, options, clock, context);
            }
        }

        if (Matches(path, AnonymousPaths))
        {
            await _next(context);
            return;
        }

        if (!currentSession.IsSignedIn)
        {
            context.Response.Redirect(SignInWithReturnTo(context));
            return;
        }

        if (!currentSession.HasCouple && !Matches(path, CouplelessPaths))
        {
            context.Response.Redirect("/Couple/Create");
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// Extends the session, but only once per renewal interval. See
    /// <see cref="IdentityOptions.SessionRenewalInterval"/> for why this is not
    /// done on every request.
    /// </summary>
    private static async Task RenewIfStaleAsync(
        AuthenticatedUser resolved,
        ISessionStore sessions,
        IdentityOptions options,
        TimeProvider clock,
        HttpContext context)
    {
        var now = clock.GetUtcNow();

        if (now - resolved.LastSeenAt < options.SessionRenewalInterval)
        {
            return;
        }

        var expiresAt = now + options.SessionLifetime;

        await sessions.RenewAsync(resolved.SessionId, expiresAt, context.RequestAborted);

        context.Response.Cookies.Append(
            SessionCookie.Name,
            context.Request.Cookies[SessionCookie.Name]!,
            SessionCookie.Options(expiresAt));
    }

    private static string SignInWithReturnTo(HttpContext context)
    {
        // Only the path and query, and only from this request's own URL — never a
        // value a caller supplied. An open redirect on the sign-in page would let a
        // link that looks like ours land someone on a page that is not.
        var returnTo = context.Request.Path + context.Request.QueryString;

        return context.Request.Method == HttpMethods.Get && returnTo != "/"
            ? "/SignIn?returnTo=" + Uri.EscapeDataString(returnTo)
            : "/SignIn";
    }

    private static bool Matches(PathString path, string[] prefixes) =>
        prefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
}
