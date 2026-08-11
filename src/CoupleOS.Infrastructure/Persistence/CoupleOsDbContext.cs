using CoupleOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleOS.Infrastructure.Persistence;

public sealed class CoupleOsDbContext(DbContextOptions<CoupleOsDbContext> options) : DbContext(options)
{
    public DbSet<Memory> Memories => Set<Memory>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Memory>(e =>
        {
            e.ToTable("memories");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CoupleId).HasColumnName("couple_id");
            e.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            e.Property(x => x.Content).HasColumnName("content");
            e.Property(x => x.Visibility).HasColumnName("visibility");
        });

        // NO global query filter on couple_id or visibility, and that is a
        // decision rather than an omission. ADR 0005 makes the database the
        // enforcement boundary. A query filter here would silently satisfy
        // every test whether or not the policy works, so RLS could rot
        // undetected until the first FromSql or projection bypassed EF.
        //
        // Application-level filtering returns as ADR 0005's "second line" once
        // these tests prove the first line holds — never before.
    }
}
