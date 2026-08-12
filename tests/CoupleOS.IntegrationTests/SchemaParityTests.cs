using CoupleOS.Infrastructure.DependencyInjection;
using CoupleOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// Asserts that the EF model and data/schema.sql agree.
///
/// ADR 0012 makes the SQL file the source of truth, so nothing at build time
/// notices when the two drift. This test is the only thing that does, which is
/// why it lives in the ordinary suite rather than somewhere optional.
/// </summary>
public sealed class SchemaParityTests : IClassFixture<RlsFixture>
{
    /// <summary>
    /// Tables the application inserts into. For these, every NOT NULL column
    /// without a database default must be mapped, or the insert fails at
    /// runtime with a constraint violation rather than at build time.
    /// </summary>
    private static readonly HashSet<string> WrittenTables =
        new(StringComparer.Ordinal)
        {
            "shopping_items", "ai_actions",

            // M1 identity. All five are inserted into during sign-in and
            // invitation, so every NOT NULL column without a default must be
            // mapped — the failure otherwise is a constraint violation at the
            // moment someone tries to sign in for the first time.
            "users", "couples", "couple_members", "auth_tokens", "sessions",
        };

    /// <summary>
    /// Read-only for now, so unmapped required columns are tolerated. This is
    /// recorded debt, not an oversight: memories.type and memories.content are
    /// NOT NULL with no default, and create_memory (M3) cannot ship until the
    /// entity carries them.
    /// </summary>
    private static readonly HashSet<string> ReadOnlyTables =
        new(StringComparer.Ordinal) { "memories" };

    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddCoupleOsInfrastructure(RlsFixture.AppConnectionString)
            .BuildServiceProvider();

    /// <summary>
    /// Both contexts, because parity has to hold across all of them. Checking
    /// only <see cref="CoupleOsDbContext"/> would have let every identity table
    /// drift from data/schema.sql unnoticed, which is exactly the failure ADR
    /// 0012 accepts this test as the mitigation for.
    /// </summary>
    private static List<IEntityType> AllMappedEntityTypes(ServiceProvider provider) =>
    [
        .. provider.GetRequiredService<CoupleOsDbContext>().Model.GetEntityTypes(),
        .. provider.GetRequiredService<IdentityDbContext>().Model.GetEntityTypes(),
    ];

    private sealed record Column(string Name, bool IsNullable, bool HasDefault);

    private static async Task<Dictionary<string, Column>> ReadColumnsAsync(string table)
    {
        await using var conn = new NpgsqlConnection(RlsFixture.AdminConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT column_name, is_nullable, (column_default IS NOT NULL) AS has_default
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table
            """;
        cmd.Parameters.AddWithValue("table", table);

        var columns = new Dictionary<string, Column>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns[reader.GetString(0)] = new Column(
                reader.GetString(0),
                reader.GetString(1) == "YES",
                reader.GetBoolean(2));
        }

        return columns;
    }

    [Fact]
    public async Task Every_mapped_table_and_column_exists_in_the_database()
    {
        await using var provider = BuildProvider();

        var problems = new List<string>();

        foreach (var entity in AllMappedEntityTypes(provider))
        {
            var table = entity.GetTableName();
            if (table is null)
            {
                continue;
            }

            var actual = await ReadColumnsAsync(table);
            if (actual.Count == 0)
            {
                problems.Add($"table '{table}' is mapped by {entity.ClrType.Name} but does not exist");
                continue;
            }

            foreach (var property in entity.GetProperties())
            {
                var column = property.GetColumnName();
                if (!actual.TryGetValue(column, out var dbColumn))
                {
                    problems.Add($"{table}.{column} is mapped by {entity.ClrType.Name}.{property.Name} but does not exist");
                    continue;
                }

                if (property.IsNullable && !dbColumn.IsNullable)
                {
                    problems.Add($"{table}.{column} is nullable in the model but NOT NULL in the database");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public async Task Tables_the_application_writes_to_map_every_required_column()
    {
        // The failure this prevents: a column added to data/schema.sql as NOT
        // NULL with no default, not added to the entity, and discovered only
        // when a user presses Process and the insert throws.
        await using var provider = BuildProvider();

        var problems = new List<string>();

        foreach (var entity in AllMappedEntityTypes(provider))
        {
            var table = entity.GetTableName();
            if (table is null || !WrittenTables.Contains(table))
            {
                continue;
            }

            var mapped = entity.GetProperties()
                .Select(p => p.GetColumnName())
                .ToHashSet(StringComparer.Ordinal);

            foreach (var column in await ReadColumnsAsync(table))
            {
                var required = !column.Value.IsNullable && !column.Value.HasDefault;

                if (required && !mapped.Contains(column.Key))
                {
                    problems.Add(
                        $"{table}.{column.Key} is NOT NULL with no default but is not mapped; " +
                        "inserts will fail at runtime");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public async Task Read_only_tables_are_declared_rather_than_forgotten()
    {
        // memories is mapped for reading only. Recording that here means the
        // day someone writes create_memory, this test tells them what is
        // missing instead of the database doing it in production.
        await using var provider = BuildProvider();

        var mappedTables = AllMappedEntityTypes(provider)
            .Select(e => e.GetTableName())
            .Where(t => t is not null)
            .Select(t => t!)
            .ToHashSet(StringComparer.Ordinal);

        var undeclared = mappedTables
            .Where(t => !WrittenTables.Contains(t) && !ReadOnlyTables.Contains(t))
            .ToList();

        Assert.True(
            undeclared.Count == 0,
            "These tables are mapped but declared neither writable nor read-only, so nothing " +
            "checks whether their required columns are covered: " + string.Join(", ", undeclared));
    }
}
