using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CoupleOS.Infrastructure.Persistence;

/// <summary>
/// The five tables sign-in touches, and nothing else.
///
/// This is a second context rather than more DbSets on
/// <see cref="CoupleOsDbContext"/>, and the reason is structural. Every read and
/// write in that context goes through <c>IScopedUnitOfWork</c>, which opens a
/// transaction and sets <c>app.current_couple_id</c> before anything else runs.
/// Authentication happens *before* a couple scope exists — the request that
/// computes the scope has to read <c>sessions</c> and <c>couples</c> to know what
/// to set it to — so it cannot use that unit of work at all.
///
/// Given that, identity work needs its own connection handling either way. Making
/// it its own context costs a registration and buys an invariant the type system
/// enforces: code holding this context *cannot* query a couple-scoped table,
/// because no such DbSet exists on it. STATUS records that
/// <c>IScopedUnitOfWork</c> is "a seam by convention, not construction" and calls
/// that a debt; this is the same seam built the other way.
///
/// All five tables are deliberately without row-level security. That is asserted
/// by <c>RlsCoverageTests</c>, in both directions, because it is the premise this
/// entire context rests on: if <c>sessions</c> ever gained a policy, every query
/// here would return zero rows and sign-in would fail as a redirect loop with no
/// error anywhere.
/// </summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Couple> Couples => Set<Couple>();
    public DbSet<CoupleMember> CoupleMembers => Set<CoupleMember>();
    public DbSet<AuthToken> AuthTokens => Set<AuthToken>();
    public DbSet<Session> Sessions => Set<Session>();

    /// <summary>
    /// <c>auth_tokens.purpose</c> is <c>text</c>, not a PostgreSQL enum, so there
    /// is no database type to disagree with and no snake-case translator to rely
    /// on. The labels are spelled out here and checked by a round-trip test —
    /// a silent mismatch would make an invitation link behave as a sign-in link,
    /// which is a privilege change rather than a display bug.
    /// </summary>
    private static readonly ValueConverter<MagicLinkPurpose, string> PurposeConverter =
        new(purpose => ToLabel(purpose), label => FromLabel(label));

    private static string ToLabel(MagicLinkPurpose purpose) => purpose switch
    {
        MagicLinkPurpose.SignIn => "sign_in",
        MagicLinkPurpose.CoupleInvite => "couple_invite",
        _ => throw new ArgumentOutOfRangeException(
            nameof(purpose), purpose, "No auth_tokens.purpose label is defined for this value."),
    };

    /// <summary>
    /// Throws on an unrecognised label rather than falling back to
    /// <see cref="MagicLinkPurpose.SignIn"/>. A ternary with a default arm reads
    /// as harmless and is not: <c>purpose</c> is a free-text column, so any label
    /// this code has not been taught — a value from a later milestone, a typo in a
    /// hand-written row — would quietly become a sign-in link. Failing loudly on a
    /// row nobody can interpret is the same choice AiActionAuditSink makes.
    /// </summary>
    private static MagicLinkPurpose FromLabel(string label) => label switch
    {
        "sign_in" => MagicLinkPurpose.SignIn,
        "couple_invite" => MagicLinkPurpose.CoupleInvite,
        _ => throw new InvalidOperationException(
            $"auth_tokens.purpose holds '{label}', which this build does not recognise. " +
            "Add it to MagicLinkPurpose rather than letting it read as a sign-in link."),
    };

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Email).HasColumnName("email");
            e.Property(x => x.DisplayName).HasColumnName("display_name");

            // timezone, locale, preferences and the timestamps keep their
            // database defaults, as elsewhere in this codebase.
        });

        b.Entity<Couple>(e =>
        {
            e.ToTable("couples");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.DisplayName).HasColumnName("display_name");
        });

        b.Entity<CoupleMember>(e =>
        {
            e.ToTable("couple_members");
            e.HasKey(x => new { x.CoupleId, x.UserId });
            e.Property(x => x.CoupleId).HasColumnName("couple_id");
            e.Property(x => x.UserId).HasColumnName("user_id");

            // These relationships exist to fix insert ordering, not to provide
            // navigation — there are no navigation properties on these entities and
            // none are wanted.
            //
            // Creating a couple adds a couples row and a couple_members row in one
            // SaveChanges. Without a declared relationship EF has no reason to think
            // the two are connected, orders them arbitrarily, and picked members
            // first — so every attempt failed on couple_members_couple_id_fkey. The
            // database knew the order; the model had not been told it.
            e.HasOne<Couple>().WithMany().HasForeignKey(x => x.CoupleId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        });

        b.Entity<AuthToken>(e =>
        {
            e.ToTable("auth_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Email).HasColumnName("email");
            e.Property(x => x.TokenHash).HasColumnName("token_hash");
            e.Property(x => x.Purpose).HasColumnName("purpose").HasConversion(PurposeConverter);
            e.Property(x => x.CoupleId).HasColumnName("couple_id");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.ConsumedAt).HasColumnName("consumed_at");
            e.Property(x => x.CreatedIp).HasColumnName("created_ip");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        b.Entity<Session>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.TokenHash).HasColumnName("token_hash");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
            e.Property(x => x.UserAgent).HasColumnName("user_agent");
            e.Property(x => x.Ip).HasColumnName("ip");
        });
    }
}
