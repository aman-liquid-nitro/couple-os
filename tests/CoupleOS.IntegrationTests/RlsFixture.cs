using Npgsql;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// Seeds the same fixtures data/rls-tests.sql uses, so the C# assertions and the
/// SQL ones describe the same world and can be compared directly.
///
/// Seeding runs as the admin role because a superuser bypasses RLS. Every
/// assertion runs as the application role, which does not. If a test ever
/// passes while connected as admin, it proves nothing at all.
/// </summary>
public sealed class RlsFixture
{
    public static readonly Guid Couple1 = Guid.Parse("c1111111-1111-1111-1111-111111111111");
    public static readonly Guid Couple2 = Guid.Parse("c2222222-2222-2222-2222-222222222222");
    public static readonly Guid PartnerA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid PartnerB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid StrangerC = Guid.Parse("33333333-3333-3333-3333-333333333333");

    /// <summary>
    /// A third couple, of two, that exists only so that tests which <i>write</i>
    /// memories do not perturb the tests which <i>count</i> them.
    ///
    /// The isolation assertions on <see cref="Couple1"/> are exact-count assertions
    /// — "sees the shared row and their own private one, and that is all" — and an
    /// exact count is the strong form of "only". <c>create_memory</c> arriving in M3
    /// broke both of them, because a tool pipeline test writing into Couple1 makes
    /// the count wrong without making the policy wrong. Weakening the count to
    /// accommodate the new rows would have been the easy fix and the wrong one, so
    /// this is STATUS debt 28's own remedy — a couple per concern — taken for the
    /// pair that actually collided rather than across the suite.
    /// </summary>
    public static readonly Guid Couple3 = Guid.Parse("c3333333-3333-3333-3333-333333333333");
    public static readonly Guid PartnerD = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid PartnerE = Guid.Parse("55555555-5555-5555-5555-555555555555");

    public static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("COUPLEOS_ADMIN_DB")
        ?? "Host=localhost;Port=5432;Database=coupleos;Username=postgres;Password=dev";

    public static string AppConnectionString =>
        Environment.GetEnvironmentVariable("COUPLEOS_APP_DB")
        ?? "Host=localhost;Port=5432;Database=coupleos;Username=app_user;Password=dev_app";

    private static bool _seeded;
    private static readonly Lock Gate = new();

    public RlsFixture()
    {
        lock (Gate)
        {
            if (_seeded) return;
            GuardAgainstPrivilegedTestRole();
            Seed();
            _seeded = true;
        }
    }

    /// <summary>
    /// Refuses to run if the application connection is privileged.
    ///
    /// Superusers, table owners and roles with BYPASSRLS ignore every policy in
    /// data/schema.sql. Tests run under such a role still execute, still read
    /// rows, and still report green for the two assertions that do not depend on
    /// isolation — while proving nothing about the thing they exist to prove.
    ///
    /// This happened: after pointing COUPLEOS_APP_DB at the admin role to verify
    /// the suite could fail, the variable outlived the experiment and a later run
    /// silently measured nothing. A test suite that cannot tell it is being lied
    /// to is not worth much, so it now refuses to start.
    /// </summary>
    private static void GuardAgainstPrivilegedTestRole()
    {
        using var conn = new NpgsqlConnection(AppConnectionString);

        try
        {
            conn.Open();
        }
        catch (NpgsqlException ex)
        {
            // Without this, an unreachable database produces one fifteen-frame
            // stack trace per test — nineteen of them — to communicate a single
            // fact that fits on one line. A test suite should tell you what to
            // do, not make you read Npgsql's call stack to work it out.
            // The Npgsql exception is deliberately NOT chained. xUnit prints the
            // whole inner chain once per test, so keeping it turns one known
            // diagnosis into sixteen copies of a stack trace that says nothing
            // the first line has not already said.
            throw new InvalidOperationException(
                "PostgreSQL is not reachable. Start the local stack:  docker compose up -d  " +
                $"(tried {Describe(AppConnectionString)}; {ex.InnerException?.Message ?? ex.Message})");
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT current_user, rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user";

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("Could not determine the current database role.");
        }

        var role = reader.GetString(0);
        var isSuperuser = reader.GetBoolean(1);
        var bypassesRls = reader.GetBoolean(2);

        if (isSuperuser || bypassesRls)
        {
            throw new InvalidOperationException(
                $"The row-level security tests are connected as '{role}', which bypasses RLS " +
                $"(superuser={isSuperuser}, bypassrls={bypassesRls}). Every isolation assertion " +
                "would be meaningless. Point COUPLEOS_APP_DB at the non-superuser application " +
                "role — in PowerShell: Remove-Item env:COUPLEOS_APP_DB");
        }
    }

    /// <summary>Connection details minus the password, safe to put in a message.</summary>
    private static string Describe(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Password = null };
        return builder.ConnectionString;
    }

    private static void Seed()
    {
        using var conn = new NpgsqlConnection(AdminConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // memories is deleted for the write couple as well as the counted ones, and
        // there it matters more: a run leaves its own rows behind, so a suite that
        // did not clear them would search a table that grows every time it is run.
        cmd.CommandText = """
            DELETE FROM memories       WHERE couple_id IN ('c1111111-1111-1111-1111-111111111111','c2222222-2222-2222-2222-222222222222','c3333333-3333-3333-3333-333333333333');
            DELETE FROM ai_actions     WHERE couple_id IN ('c3333333-3333-3333-3333-333333333333');
            DELETE FROM couple_members WHERE couple_id IN ('c1111111-1111-1111-1111-111111111111','c2222222-2222-2222-2222-222222222222','c3333333-3333-3333-3333-333333333333');
            DELETE FROM couples        WHERE id       IN ('c1111111-1111-1111-1111-111111111111','c2222222-2222-2222-2222-222222222222','c3333333-3333-3333-3333-333333333333');
            DELETE FROM users          WHERE id       IN ('11111111-1111-1111-1111-111111111111','22222222-2222-2222-2222-222222222222','33333333-3333-3333-3333-333333333333','44444444-4444-4444-4444-444444444444','55555555-5555-5555-5555-555555555555');

            INSERT INTO users (id,email,display_name) VALUES
              ('11111111-1111-1111-1111-111111111111','a@x.com','Partner A'),
              ('22222222-2222-2222-2222-222222222222','b@x.com','Partner B'),
              ('33333333-3333-3333-3333-333333333333','c@x.com','Stranger C'),
              ('44444444-4444-4444-4444-444444444444','d@x.com','Partner D'),
              ('55555555-5555-5555-5555-555555555555','e@x.com','Partner E');

            INSERT INTO couples (id,display_name) VALUES
              ('c1111111-1111-1111-1111-111111111111','Couple One'),
              ('c2222222-2222-2222-2222-222222222222','Couple Two'),
              ('c3333333-3333-3333-3333-333333333333','Couple Three');

            INSERT INTO couple_members (couple_id,user_id) VALUES
              ('c1111111-1111-1111-1111-111111111111','11111111-1111-1111-1111-111111111111'),
              ('c1111111-1111-1111-1111-111111111111','22222222-2222-2222-2222-222222222222'),
              ('c2222222-2222-2222-2222-222222222222','33333333-3333-3333-3333-333333333333'),
              ('c3333333-3333-3333-3333-333333333333','44444444-4444-4444-4444-444444444444'),
              ('c3333333-3333-3333-3333-333333333333','55555555-5555-5555-5555-555555555555');

            INSERT INTO memories (couple_id,owner_user_id,visibility,type,content) VALUES
              ('c1111111-1111-1111-1111-111111111111', NULL,'shared_couple','semantic','C1 SHARED'),
              ('c1111111-1111-1111-1111-111111111111','11111111-1111-1111-1111-111111111111','private_user','episodic','C1 PRIVATE-A: necklace'),
              ('c1111111-1111-1111-1111-111111111111','22222222-2222-2222-2222-222222222222','private_user','episodic','C1 PRIVATE-B: surprise trip'),
              ('c2222222-2222-2222-2222-222222222222', NULL,'shared_couple','semantic','C2 SHARED');
            """;
        cmd.ExecuteNonQuery();
    }
}
