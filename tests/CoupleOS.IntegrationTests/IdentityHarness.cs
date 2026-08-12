using CoupleOS.Application.Identity;
using CoupleOS.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// A clock the tests move by hand.
///
/// Written here rather than taking Microsoft.Extensions.TimeProvider.Testing,
/// because what these tests need from it is two methods, and the alternative to
/// ten lines is a package version to keep current.
/// </summary>
public sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Captures mail instead of sending it, and hands back the link that was in it.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    private readonly List<OutboundEmail> _sent = [];

    public IReadOnlyList<OutboundEmail> Sent => _sent;

    public OutboundEmail Last => _sent.Count > 0
        ? _sent[^1]
        : throw new InvalidOperationException("No mail was sent.");

    public Task SendAsync(OutboundEmail email, CancellationToken cancellationToken = default)
    {
        _sent.Add(email);
        return Task.CompletedTask;
    }

    public void Clear() => _sent.Clear();
}

/// <summary>
/// Produces a URL whose token can be read back out, so a test can follow a link the
/// way a person would rather than reaching past the email for the token.
/// </summary>
public sealed class TestUrlFactory : IMagicLinkUrlFactory
{
    public const string Prefix = "https://test.local/auth/callback?token=";

    public string CreateUrl(string token) => Prefix + Uri.EscapeDataString(token);

    public static string TokenFrom(OutboundEmail email)
    {
        var start = email.Body.IndexOf(Prefix, StringComparison.Ordinal);
        Assert.True(start >= 0, "The email body carried no magic link:\n" + email.Body);

        var afterPrefix = start + Prefix.Length;
        var end = email.Body.IndexOfAny([' ', '\r', '\n'], afterPrefix);
        var raw = end < 0 ? email.Body[afterPrefix..] : email.Body[afterPrefix..end];

        return Uri.UnescapeDataString(raw);
    }
}

/// <summary>
/// Builds an identity stack against the real database, and removes everything it
/// created afterwards.
///
/// Every address used by these tests ends in <see cref="Domain"/>, which is what
/// makes cleanup possible without truncating tables the rest of the suite and the
/// SQL harnesses share.
/// </summary>
public static class IdentityHarness
{
    public const string Domain = "@m1test.local";

    public static string Address(string localPart) => localPart + Domain;

    public sealed record Stack(
        ServiceProvider Provider,
        CapturingEmailSender Email,
        TestClock Clock,
        IdentityOptions Options) : IAsyncDisposable
    {
        public IMagicLinkService MagicLinks(IServiceScope scope) =>
            scope.ServiceProvider.GetRequiredService<IMagicLinkService>();

        public async ValueTask DisposeAsync() => await Provider.DisposeAsync();
    }

    public static Stack Build(IdentityOptions? options = null, DateTimeOffset? startAt = null)
    {
        var email = new CapturingEmailSender();
        var clock = new TestClock(startAt ?? DateTimeOffset.UtcNow);
        var resolved = options ?? new IdentityOptions();

        var provider = new ServiceCollection()
            .AddCoupleOsInfrastructure(RlsFixture.AppConnectionString)
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton(resolved)
            .AddSingleton<IEmailSender>(email)
            .AddSingleton<IMagicLinkUrlFactory, TestUrlFactory>()
            .AddScoped<IMagicLinkService, MagicLinkService>()
            .BuildServiceProvider();

        return new Stack(provider, email, clock, resolved);
    }

    /// <summary>
    /// Deletes every row these tests could have created. Couples are removed via
    /// their members rather than by name, because a couple created by a test has no
    /// distinguishing text on it — only the address of the person in it.
    /// </summary>
    public static async Task ClearAsync()
    {
        await using var conn = new NpgsqlConnection(RlsFixture.AdminConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            DELETE FROM auth_tokens WHERE lower(email) LIKE '%{Domain}';

            DELETE FROM sessions WHERE user_id IN (
                SELECT id FROM users WHERE lower(email) LIKE '%{Domain}');

            DELETE FROM couples WHERE id IN (
                SELECT couple_id FROM couple_members WHERE user_id IN (
                    SELECT id FROM users WHERE lower(email) LIKE '%{Domain}'));

            DELETE FROM users WHERE lower(email) LIKE '%{Domain}';
            """;

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Reads a token's stored state directly, for assertions about rows rather than about return values.</summary>
    public static async Task<(DateTimeOffset? ConsumedAt, int Count)> TokenStateAsync(string email)
    {
        await using var conn = new NpgsqlConnection(RlsFixture.AdminConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT max(consumed_at), count(*) FROM auth_tokens WHERE lower(email) = lower(@email)
            """;
        cmd.Parameters.AddWithValue("email", email);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();

        return (
            await reader.IsDBNullAsync(0) ? null : reader.GetFieldValue<DateTimeOffset>(0),
            reader.GetInt32(1));
    }
}
