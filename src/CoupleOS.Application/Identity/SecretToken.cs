using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace CoupleOS.Application.Identity;

/// <summary>
/// The one place a bearer secret is created or hashed. Magic links and session
/// cookies both use it, because they have the same requirement: a value that
/// cannot be guessed, and a stored form that cannot be turned back into the value.
///
/// ADR 0007 fixes the parameters — 32 bytes from a CSPRNG, base64url encoded,
/// SHA-256 at rest, plaintext only ever in the email or the cookie.
/// </summary>
public static class SecretToken
{
    /// <summary>
    /// 32 bytes, as ADR 0007 specifies. That is 256 bits of entropy, which puts
    /// guessing a live token out of reach regardless of how many are outstanding,
    /// so the 15-minute window is defence in depth rather than the thing keeping
    /// this safe.
    /// </summary>
    public const int ByteLength = 32;

    /// <summary>
    /// Base64url, so the value survives being pasted into a URL without escaping.
    /// A token mangled by encoding would fail to match its own hash and present
    /// as an expired link, which is indistinguishable from the real thing and
    /// therefore very hard to diagnose.
    /// </summary>
    public static string Create() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(ByteLength));

    /// <summary>
    /// SHA-256 of the token's UTF-8 bytes. Plain SHA-256 rather than a password
    /// hash on purpose: these are 256-bit random values, not human-chosen
    /// secrets, so there is no dictionary to slow down and no salt to add. A
    /// deliberate cost here would only slow down legitimate sign-in.
    /// </summary>
    public static byte[] Hash(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }
}
