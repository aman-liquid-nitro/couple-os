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
        conn.Open();

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

    private static void Seed()
    {
        using var conn = new NpgsqlConnection(AdminConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM memories       WHERE couple_id IN ('c1111111-1111-1111-1111-111111111111','c2222222-2222-2222-2222-222222222222');
            DELETE FROM couple_members WHERE couple_id IN ('c1111111-1111-1111-1111-111111111111','c2222222-2222-2222-2222-222222222222');
            DELETE FROM couples        WHERE id       IN ('c1111111-1111-1111-1111-111111111111','c2222222-2222-2222-2222-222222222222');
            DELETE FROM users          WHERE id       IN ('11111111-1111-1111-1111-111111111111','22222222-2222-2222-2222-222222222222','33333333-3333-3333-3333-333333333333');

            INSERT INTO users (id,email,display_name) VALUES
              ('11111111-1111-1111-1111-111111111111','a@x.com','Partner A'),
              ('22222222-2222-2222-2222-222222222222','b@x.com','Partner B'),
              ('33333333-3333-3333-3333-333333333333','c@x.com','Stranger C');

            INSERT INTO couples (id,display_name) VALUES
              ('c1111111-1111-1111-1111-111111111111','Couple One'),
              ('c2222222-2222-2222-2222-222222222222','Couple Two');

            INSERT INTO couple_members (couple_id,user_id) VALUES
              ('c1111111-1111-1111-1111-111111111111','11111111-1111-1111-1111-111111111111'),
              ('c1111111-1111-1111-1111-111111111111','22222222-2222-2222-2222-222222222222'),
              ('c2222222-2222-2222-2222-222222222222','33333333-3333-3333-3333-333333333333');

            INSERT INTO memories (couple_id,owner_user_id,visibility,type,content) VALUES
              ('c1111111-1111-1111-1111-111111111111', NULL,'shared_couple','semantic','C1 SHARED'),
              ('c1111111-1111-1111-1111-111111111111','11111111-1111-1111-1111-111111111111','private_user','episodic','C1 PRIVATE-A: necklace'),
              ('c1111111-1111-1111-1111-111111111111','22222222-2222-2222-2222-222222222222','private_user','episodic','C1 PRIVATE-B: surprise trip'),
              ('c2222222-2222-2222-2222-222222222222', NULL,'shared_couple','semantic','C2 SHARED');
            """;
        cmd.ExecuteNonQuery();
    }
}
