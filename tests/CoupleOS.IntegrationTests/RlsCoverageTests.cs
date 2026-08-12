using Npgsql;
using Xunit;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// Asserts which tables row-level security does and does not apply to.
///
/// Both directions are failures. A policy-bearing table that loses its policy
/// leaks across couples. An identity table that gains one breaks sign-in, because
/// the request that establishes <c>app.current_couple_id</c> has to read
/// <c>sessions</c> and <c>couples</c> to know what to set it to, and cannot be
/// filtered by the value it is computing — the failure is a login loop, and the
/// cause is invisible from the symptom.
///
/// data/schema.sql carried a comment naming three of the six unprotected tables.
/// Nothing checked it, so it went stale, and a reader following it would conclude
/// that <c>sessions</c> was protected when it never has been. This test is the
/// check that comment always needed.
/// </summary>
public sealed class RlsCoverageTests : IClassFixture<RlsFixture>
{
    /// <summary>
    /// Read before any session variable exists, so a fail-closed policy would
    /// make authentication impossible. expense_categories is global reference
    /// data with no couple content.
    /// </summary>
    private static readonly HashSet<string> DeliberatelyUnprotected =
        new(StringComparer.Ordinal)
        {
            "users", "couples", "couple_members", "auth_tokens", "sessions",
            "expense_categories",
        };

    /// <summary>
    /// Left behind in the database by data/rls-tests.sql and
    /// data/rls-concurrency.sh. Not part of the schema, and excluded by name
    /// rather than by pattern so that a real table called something similar
    /// cannot slip through.
    /// </summary>
    private static readonly HashSet<string> HarnessArtifacts =
        new(StringComparer.Ordinal) { "t_results", "probe" };

    private sealed record TableSecurity(string Name, bool Enabled, bool Forced);

    private static async Task<List<TableSecurity>> ReadTableSecurityAsync()
    {
        await using var conn = new NpgsqlConnection(RlsFixture.AdminConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind = 'r'
            ORDER BY c.relname
            """;

        var tables = new List<TableSecurity>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(new TableSecurity(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2)));
        }

        return tables;
    }

    [Fact]
    public async Task Every_unprotected_table_is_one_we_chose_to_leave_unprotected()
    {
        var tables = (await ReadTableSecurityAsync())
            .Where(t => !HarnessArtifacts.Contains(t.Name))
            .ToList();

        Assert.NotEmpty(tables);

        var unexpected = tables
            .Where(t => !t.Enabled && !DeliberatelyUnprotected.Contains(t.Name))
            .Select(t => t.Name)
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            "These tables have no row-level security and are not on the deliberate list in " +
            "data/schema.sql. Either add a policy or add them to the list with a reason: " +
            string.Join(", ", unexpected));
    }

    [Fact]
    public async Task Every_table_on_the_deliberate_list_still_exists_and_is_still_unprotected()
    {
        // The other direction. Adding a policy to sessions or couples breaks
        // sign-in in a way that looks like a redirect loop, so it should fail
        // here instead — with the reason attached.
        var tables = (await ReadTableSecurityAsync()).ToDictionary(t => t.Name, StringComparer.Ordinal);

        var problems = new List<string>();

        foreach (var name in DeliberatelyUnprotected)
        {
            if (!tables.TryGetValue(name, out var table))
            {
                problems.Add($"{name} is on the deliberate list but no longer exists");
                continue;
            }

            if (table.Enabled)
            {
                problems.Add(
                    $"{name} has gained row-level security, but it is read before any session " +
                    "variable exists — see the comment in data/schema.sql before removing this");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public async Task Row_level_security_is_forced_wherever_it_is_enabled()
    {
        // ENABLE exempts the table owner; FORCE does not. The application role is
        // not the owner, so ENABLE alone would still cover it today — but the
        // schema sets both everywhere, and a table with one and not the other is
        // a sign someone wrote the ALTER by hand and stopped early.
        var mismatched = (await ReadTableSecurityAsync())
            .Where(t => !HarnessArtifacts.Contains(t.Name))
            .Where(t => t.Enabled != t.Forced)
            .Select(t => $"{t.Name} (enabled={t.Enabled}, forced={t.Forced})")
            .ToList();

        Assert.True(
            mismatched.Count == 0,
            "Row-level security is enabled but not forced on: " + string.Join(", ", mismatched));
    }
}
