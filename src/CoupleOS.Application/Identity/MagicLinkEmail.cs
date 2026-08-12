using CoupleOS.Domain.Enums;

namespace CoupleOS.Application.Identity;

/// <summary>
/// The text of the two emails this system sends.
///
/// Kept together and in Application rather than in a Razor view because of ADR
/// 0007's phishing note: links in email are a training hazard, so the message has
/// to state plainly that Couple OS will never ask for anything beyond clicking.
/// That sentence is a security control, and controls should not live somewhere
/// they can be reworded as copy.
/// </summary>
public static class MagicLinkEmail
{
    private const string NeverAsk =
        "Couple OS will never ask you for a password, a code, or any personal detail. " +
        "The only thing you ever need to do is click a link like the one above. " +
        "If a message claiming to be Couple OS asks for anything else, it is not us.";

    public static OutboundEmail ForSignIn(string to, string url, TimeSpan lifetime) => new(
        to,
        "Your Couple OS sign-in link",
        $"""
        Click to sign in:

        {url}

        The link works once and expires in {Describe(lifetime)}.

        If you did not ask to sign in, you can ignore this — someone typed your
        address and nothing has happened to your account.

        {NeverAsk}
        """);

    public static OutboundEmail ForCoupleInvite(string to, string url, string? invitedBy, TimeSpan lifetime) => new(
        to,
        "You have been invited to a Couple OS",
        $"""
        {invitedBy ?? "Your partner"} has invited you to share a Couple OS.

        Click to accept and sign in:

        {url}

        The link works once and expires in {Describe(lifetime)}.

        If you were not expecting this, you can ignore it. Nothing is shared with
        anyone until you accept.

        {NeverAsk}
        """);

    /// <summary>
    /// "15 minutes", not "00:15:00". The lifetime is configurable, so the sentence
    /// is generated rather than written, or the two would disagree the first time
    /// anyone changed it.
    /// </summary>
    private static string Describe(TimeSpan lifetime) => lifetime.TotalMinutes switch
    {
        < 1 => $"{lifetime.TotalSeconds:0} seconds",
        < 60 => $"{lifetime.TotalMinutes:0} minutes",
        _ => $"{lifetime.TotalHours:0.#} hours",
    };

    public static OutboundEmail For(
        MagicLinkPurpose purpose,
        string to,
        string url,
        string? invitedBy,
        TimeSpan lifetime) => purpose switch
        {
            MagicLinkPurpose.SignIn => ForSignIn(to, url, lifetime),
            MagicLinkPurpose.CoupleInvite => ForCoupleInvite(to, url, invitedBy, lifetime),
            _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "No email exists for this purpose."),
        };
}
