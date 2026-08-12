using CoupleOS.Application.Capture;
using CoupleOS.Application.Persistence;
using CoupleOS.Application.Security;
using CoupleOS.Domain.Enums;
using CoupleOS.Infrastructure.DependencyInjection;
using CoupleOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// The same check <c>VisibilityMappingTests</c> makes, for the two enums M2
/// adds. Both rely on Npgsql's snake-case translator rather than an explicit
/// declaration, because [PgName] would drag Npgsql into the Domain project.
///
/// A mismatch would not throw at startup. <c>NeedsInput</c> mapping to a label
/// PostgreSQL does not have fails at the moment a block first needs a question
/// asked about it — which is the one path in M2 that exists to stop notes
/// disappearing silently.
/// </summary>
public sealed class DumpEnumMappingTests : IClassFixture<RlsFixture>
{
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddCoupleOsInfrastructure(RlsFixture.AppConnectionString)
            .BuildServiceProvider();

    [Fact]
    public async Task Both_enums_survive_a_round_trip_and_a_where_clause()
    {
        await using var provider = BuildProvider();
        await using var request = provider.CreateAsyncScope();
        request.ServiceProvider.GetRequiredService<ICoupleScopeSetter>()
            .Set(new CoupleScope(RlsFixture.Couple1, RlsFixture.PartnerA));

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var files = request.ServiceProvider.GetRequiredService<IDumpFileStore>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        var file = await files.GetOrCreateSharedAsync();

        // Reading is half of it. Filtering is where a wrong label actually bites:
        // the value has to survive translation into a WHERE clause, against a
        // PostgreSQL enum that will reject an unknown label outright.
        var byKind = await db.DumpFiles
            .Where(f => f.Kind == DumpFileKind.Shared)
            .Select(f => f.Id)
            .ToListAsync();

        Assert.Contains(file.Id, byKind);

        // Every dump_block_status label, asserted against the database's own list
        // rather than against a copy of it here — a copy would drift with the
        // schema and still pass.
        var labels = await db.Database
            .SqlQuery<string>($"SELECT unnest(enum_range(NULL::dump_block_status))::text AS \"Value\"")
            .ToListAsync();

        Assert.Equal(
            ["unprocessed", "processing", "processed", "needs_input", "ignored", "failed"],
            labels);

        // Same list, from the CLR side, in the same order. Order matters: these
        // are declaration-ordered in both places, and a member inserted in the
        // middle of one would silently re-label every value after it.
        Assert.Equal(
            labels,
            Enum.GetValues<DumpBlockStatus>().Select(ToLabel));

        var kindLabels = await db.Database
            .SqlQuery<string>($"SELECT unnest(enum_range(NULL::dump_file_kind))::text AS \"Value\"")
            .ToListAsync();

        Assert.Equal(["shared", "private"], kindLabels);
        Assert.Equal(kindLabels, Enum.GetValues<DumpFileKind>().Select(ToLabel));
    }

    /// <summary>
    /// Npgsql's default translator, spelled out. Written here rather than
    /// imported so that if the translator's behaviour ever changes, this test
    /// disagrees with it instead of changing along with it.
    /// </summary>
    private static string ToLabel<T>(T value) where T : struct, Enum
    {
        var name = value.ToString()!;
        var label = new System.Text.StringBuilder(name.Length + 4);

        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]))
            {
                if (i > 0)
                {
                    label.Append('_');
                }

                label.Append(char.ToLowerInvariant(name[i]));
            }
            else
            {
                label.Append(name[i]);
            }
        }

        return label.ToString();
    }
}
