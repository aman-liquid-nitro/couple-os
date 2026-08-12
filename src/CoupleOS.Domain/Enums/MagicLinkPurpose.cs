namespace CoupleOS.Domain.Enums;

/// <summary>
/// What a magic link is for. Mirrors the documented values of the
/// <c>auth_tokens.purpose</c> column, which is <c>text</c> rather than a
/// PostgreSQL enum — so unlike <see cref="Visibility"/> there is no database
/// type to disagree with, and the mapping is an explicit converter checked by a
/// round-trip test.
///
/// ADR 0007: the invitation *is* a magic link carrying a couple-join claim, so
/// there is one authentication path rather than two.
/// </summary>
public enum MagicLinkPurpose
{
    /// <summary>Stored as <c>sign_in</c>. Signs in an existing user, or registers a new one.</summary>
    SignIn,

    /// <summary>
    /// Stored as <c>couple_invite</c>. Carries the couple to join in
    /// <c>auth_tokens.couple_id</c>, which is why that column is nullable.
    /// </summary>
    CoupleInvite,
}
