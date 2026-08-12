using CoupleOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoupleOS.Api.Development;

/// <summary>
/// Creates the one couple the development scope refers to.
///
/// It needs no privileged connection: users, couples and couple_members are the
/// three tables deliberately left without row-level security, because they are
/// read before any session exists (data/schema.sql, ADR 0005). Seeding anything
/// else would require a real scope, which is the point.
/// </summary>
public static class DevelopmentSeeder
{
    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration)
    {
        var coupleId = configuration.GetValue<Guid>("Development:CoupleId");
        var userId = configuration.GetValue<Guid>("Development:UserId");
        var partnerId = configuration.GetValue<Guid>("Development:PartnerUserId");

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        // Idempotent. A restart must neither fail nor duplicate.
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO users (id, email, display_name) VALUES ({userId}, 'you@local', 'You') ON CONFLICT (id) DO NOTHING");

        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO users (id, email, display_name) VALUES ({partnerId}, 'partner@local', 'Partner') ON CONFLICT (id) DO NOTHING");

        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO couples (id, display_name) VALUES ({coupleId}, 'Development couple') ON CONFLICT (id) DO NOTHING");

        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO couple_members (couple_id, user_id) VALUES ({coupleId}, {userId}) ON CONFLICT (couple_id, user_id) DO NOTHING");

        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO couple_members (couple_id, user_id) VALUES ({coupleId}, {partnerId}) ON CONFLICT (couple_id, user_id) DO NOTHING");
    }
}
