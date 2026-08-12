using System.Buffers.Text;
using CoupleOS.Application.Identity;
using Xunit;

namespace CoupleOS.UnitTests;

/// <summary>
/// The parameters ADR 0007 fixes, asserted rather than assumed. Every one of these
/// would still "work" if it were wrong — a 16-byte token signs people in fine, and
/// so does a token hashed with a truncated digest.
/// </summary>
public sealed class SecretTokenTests
{
    [Fact]
    public void A_token_carries_the_full_32_bytes_of_entropy()
    {
        // Base64url of 32 bytes is 43 characters unpadded. Asserting the decoded
        // length rather than the string length keeps this true if the encoding
        // changes.
        var decoded = Base64Url.DecodeFromChars(SecretToken.Create());

        Assert.Equal(SecretToken.ByteLength, decoded.Length);
        Assert.Equal(32, decoded.Length);
    }

    [Fact]
    public void Tokens_are_url_safe()
    {
        // '+', '/' and '=' would be mangled or re-encoded in a query string, and the
        // resulting token would fail to match its own hash — presenting as an
        // expired link, which is indistinguishable from the real thing.
        foreach (var token in Enumerable.Range(0, 200).Select(_ => SecretToken.Create()))
        {
            Assert.DoesNotContain('+', token);
            Assert.DoesNotContain('/', token);
            Assert.DoesNotContain('=', token);
        }
    }

    [Fact]
    public void Tokens_do_not_repeat()
    {
        // A weak generator is the one failure here that nothing downstream would
        // notice: a repeated token collides on auth_tokens_hash_key and looks like a
        // database error, or worse resolves to someone else's row.
        var tokens = Enumerable.Range(0, 1000).Select(_ => SecretToken.Create()).ToList();

        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Hashing_is_stable_and_the_full_digest()
    {
        var token = SecretToken.Create();

        var first = SecretToken.Hash(token);
        var second = SecretToken.Hash(token);

        Assert.Equal(first, second);
        Assert.Equal(32, first.Length);
    }

    [Fact]
    public void Different_tokens_hash_differently()
    {
        Assert.NotEqual(SecretToken.Hash(SecretToken.Create()), SecretToken.Hash(SecretToken.Create()));
    }

    [Fact]
    public void Hashing_is_case_sensitive()
    {
        // Base64url is case-significant, so a lookup that lower-cased the token
        // would match nothing. Worth pinning: email clients have been known to
        // rewrite URLs.
        var token = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_aB";

        Assert.NotEqual(SecretToken.Hash(token), SecretToken.Hash(token.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Hashing_nothing_is_refused(string? token)
    {
        // Otherwise an absent token hashes to the digest of the empty string, which
        // is a constant — and any row written with it would be openable by anyone
        // who sent no token at all.
        Assert.ThrowsAny<ArgumentException>(() => SecretToken.Hash(token!));
    }
}
