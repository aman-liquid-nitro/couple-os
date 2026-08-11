using CoupleOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleOS.Infrastructure.Persistence;

public sealed class CoupleOsDbContext(DbContextOptions<CoupleOsDbContext> options) : DbContext(options)
{
    public DbSet<Memory> Memories => Set<Memory>();
    public DbSet<ShoppingItem> ShoppingItems => Set<ShoppingItem>();
    public DbSet<AiAction> AiActions => Set<AiAction>();

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

        b.Entity<ShoppingItem>(e =>
        {
            e.ToTable("shopping_items");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CoupleId).HasColumnName("couple_id");
            e.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            e.Property(x => x.Visibility).HasColumnName("visibility");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.NormalizedName).HasColumnName("normalized_name");
            e.Property(x => x.Quantity).HasColumnName("quantity");
            e.Property(x => x.AddedBy).HasColumnName("added_by");

            // status, recurring, created_at and the rest keep their database
            // defaults. Mapping a column just to restate its default invites the
            // two definitions to drift.
        });

        b.Entity<AiAction>(e =>
        {
            e.ToTable("ai_actions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CoupleId).HasColumnName("couple_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.ToolName).HasColumnName("tool_name");
            e.Property(x => x.Arguments).HasColumnName("arguments").HasColumnType("jsonb");
            e.Property(x => x.Outcome).HasColumnName("outcome");
            e.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
            e.Property(x => x.EntityType).HasColumnName("entity_type");
            e.Property(x => x.EntityId).HasColumnName("entity_id");
            e.Property(x => x.ErrorMessage).HasColumnName("error_message");
            e.Property(x => x.LatencyMs).HasColumnName("latency_ms");
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
