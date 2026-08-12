namespace CoupleOS.Api.Identity;

/// <summary>
/// The cookie's name and attributes, in one place so a page cannot set it with
/// one set of flags and clear it with another — a mismatch on Path or SameSite
/// leaves the old cookie in the browser and signing out appears to do nothing.
/// </summary>
public static class SessionCookie
{
    public const string Name = "coupleos_session";

    /// <summary>
    /// ADR 0007: httpOnly, Secure, SameSite=Lax.
    ///
    /// <para>httpOnly — no script reads it, so an injected script cannot exfiltrate
    /// a live session.</para>
    ///
    /// <para>Secure — set unconditionally, including in development. Browsers treat
    /// <c>http://localhost</c> as a secure context and send Secure cookies to it,
    /// so this needs no environment switch, and a switch is exactly the kind of
    /// thing that gets configured wrongly once and never noticed.</para>
    ///
    /// <para>SameSite=Lax rather than Strict — a magic link is a top-level
    /// navigation arriving from a mail client, and under Strict the cookie set on
    /// that response would be withheld on the redirect that follows it, so
    /// clicking a valid link would land back on the sign-in page.</para>
    /// </summary>
    public static CookieOptions Options(DateTimeOffset expiresAt) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        Path = "/",
        Expires = expiresAt,
    };

    /// <summary>
    /// Deletion has to repeat the same attributes. A browser matches a deletion
    /// against name, path and domain; get one wrong and the cookie survives.
    /// </summary>
    public static CookieOptions DeletionOptions() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        Path = "/",
    };
}
