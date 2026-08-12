using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;
using CoupleOS.Infrastructure.DependencyInjection;
using CoupleOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// Proves the CLR enum and the text stored in <c>auth_tokens.purpose</c> agree.
///
/// <c>purpose</c> is <c>text</c>, not a PostgreSQL enum, so nothing in the
/// database constrains it and nothing at build time notices a mismatch. The
/// consequence of one is not a display bug: an invitation link read as a sign-in
/// link loses its couple-join claim, and a sign-in link read as an invitation
/// carries a null couple. Both are authorization outcomes, so the labels get a
/// round-trip test rather than trust.
/// </summary>
public sealed class MagicLinkPurposeMappingTests : IClassFixture<RlsFixture>
{
    private const string Email = "purpose-mapping@x.com";

    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddCoupleOsInfrastructure(RlsFixture.AppConnectionString)
            .BuildServiceProvider();

    private static async Task ClearAsync()
    {
        await using var conn = new NpgsqlConnection(RlsFixture.AdminConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM auth_tokens WHERE email = @email";
        cmd.Parameters.AddWithValue("email", Email);
        await cmd.ExecuteNonQueryAsync();
    }

    private static AuthToken Token(MagicLinkPurpose purpose, byte hashSeed, Guid? coupleId) => new()
    {
        Email = Email,
        TokenHash = Enumerable.Repeat(hashSeed, 32).ToArray(),
        Purpose = purpose,
        CoupleId = coupleId,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
    };

    [Theory]
    [InlineData(MagicLinkPurpose.SignIn, "sign_in")]
    [InlineData(MagicLinkPurpose.CoupleInvite, "couple_invite")]
    public async Task Purpose_round_trips_through_the_exact_label_schema_sql_documents(
        MagicLinkPurpose purpose,
        string expectedLabel)
    {
        await ClearAsync();

        await using var provider = BuildProvider();

        var seed = (byte)(purpose == MagicLinkPurpose.SignIn ? 0xA1 : 0xB2);
        var coupleId = purpose == MagicLinkPurpose.CoupleInvite ? RlsFixture.Couple1 : (Guid?)null;

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            db.AuthTokens.Add(Token(purpose, seed, coupleId));
            await db.SaveChangesAsync();
        }

        // Read the raw text, not the mapped value. Asserting the enum against
        // itself would pass with both directions of the converter wrong.
        await using (var conn = new NpgsqlConnection(RlsFixture.AdminConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT purpose FROM auth_tokens WHERE email = @email";
            cmd.Parameters.AddWithValue("email", Email);
            Assert.Equal(expectedLabel, (string?)await cmd.ExecuteScalarAsync());
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var stored = await db.AuthTokens.SingleAsync(t => t.Email == Email);

            Assert.Equal(purpose, stored.Purpose);
            Assert.Equal(coupleId, stored.CoupleId);
        }

        await ClearAsync();
    }

    [Fact]
    public async Task A_label_this_build_does_not_recognise_throws_rather_than_reading_as_sign_in()
    {
        // The failure being prevented: a later milestone adds a purpose, an older
        // build reads the row, and a link meant for something else silently
        // becomes a sign-in link. Written straight to the column, because the
        // point is that nothing in the database stops this.
        await ClearAsync();

        await using (var conn = new NpgsqlConnection(RlsFixture.AdminConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO auth_tokens (email, token_hash, purpose, expires_at)
                VALUES (@email, @hash, 'password_reset', now() + interval '15 minutes')
                """;
            cmd.Parameters.AddWithValue("email", Email);
            cmd.Parameters.AddWithValue("hash", Enumerable.Repeat((byte)0xC3, 32).ToArray());
            await cmd.ExecuteNonQueryAsync();
        }

        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        var thrown = await Assert.ThrowsAnyAsync<Exception>(
            () => db.AuthTokens.SingleAsync(t => t.Email == Email));

        Assert.Contains("password_reset", Flatten(thrown), StringComparison.Ordinal);

        await ClearAsync();
    }

    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }
}
