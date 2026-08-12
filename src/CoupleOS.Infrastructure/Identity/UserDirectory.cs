using CoupleOS.Application.Identity;
using CoupleOS.Domain.Entities;
using CoupleOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoupleOS.Infrastructure.Identity;

public sealed class UserDirectory(IdentityDbContext db) : IUserDirectory
{
    /// <summary>Unique violation. A duplicate email, or a second couple for one user.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>RAISE EXCEPTION from plpgsql — the two-member couple trigger.</summary>
    private const string RaisedException = "P0001";

    public Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalized = email.Trim().ToLowerInvariant();

        return db.Users
            .AsNoTracking()
            .Where(u => u.Email.ToLower() == normalized)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Guid> GetOrCreateUserAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalized = email.Trim();

        if (await FindUserIdByEmailAsync(normalized, cancellationToken) is { } existing)
        {
            return existing;
        }

        var user = new User
        {
            Email = normalized,
            DisplayName = DeriveDisplayName(normalized),
        };

        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return user.Id;
        }
        catch (DbUpdateException ex) when (IsPostgres(ex, UniqueViolation))
        {
            // Two links for the same new address, consumed at the same moment. The
            // unique index on lower(email) settles it and the loser reads the
            // winner's row rather than failing — both people clicked a valid link,
            // and both should end up signed in as the same person.
            db.ChangeTracker.Clear();

            return await FindUserIdByEmailAsync(normalized, cancellationToken)
                ?? throw new InvalidOperationException(
                    "users rejected an insert as a duplicate, but no row with that address exists. " +
                    "The unique index is on lower(email) — a change to it would produce exactly this.",
                    ex);
        }
    }

    /// <summary>
    /// The local part of the address, because <c>users.display_name</c> is NOT NULL
    /// and a magic link carries no name.
    ///
    /// This is a placeholder standing in for a profile screen that does not exist
    /// yet, and it will be visibly wrong for anyone whose address is not their
    /// name. Recorded in STATUS rather than hidden behind a better-looking guess.
    /// </summary>
    private static string DeriveDisplayName(string email)
    {
        var localPart = email.Split('@', 2)[0].Trim();

        return localPart.Length == 0 ? "Partner" : localPart;
    }

    public Task<Guid?> FindCoupleIdForUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        db.CoupleMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => (Guid?)m.CoupleId)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<Guid> CreateCoupleAsync(
        Guid userId,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        var couple = new Couple { DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim() };

        db.Couples.Add(couple);
        db.CoupleMembers.Add(new CoupleMember { CoupleId = couple.Id, UserId = userId });

        // One SaveChanges, so one transaction: a couple with no members would be
        // invisible to its own creator and unreachable by anyone.
        await db.SaveChangesAsync(cancellationToken);

        return couple.Id;
    }

    public async Task<JoinCoupleOutcome> JoinCoupleAsync(
        Guid userId,
        Guid coupleId,
        CancellationToken cancellationToken = default)
    {
        db.CoupleMembers.Add(new CoupleMember { CoupleId = coupleId, UserId = userId });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return JoinCoupleOutcome.Joined;
        }
        catch (DbUpdateException ex) when (IsPostgres(ex, UniqueViolation))
        {
            db.ChangeTracker.Clear();
            return JoinCoupleOutcome.AlreadyInACouple;
        }
        catch (DbUpdateException ex) when (IsPostgres(ex, RaisedException))
        {
            // couple_members_max_two is a DEFERRABLE INITIALLY DEFERRED constraint
            // trigger, so this arrives at COMMIT rather than at INSERT — which is
            // inside SaveChangesAsync, and therefore still here.
            db.ChangeTracker.Clear();
            return JoinCoupleOutcome.CoupleIsFull;
        }
    }

    private static bool IsPostgres(DbUpdateException exception, string sqlState) =>
        exception.InnerException is PostgresException postgres && postgres.SqlState == sqlState;
}
