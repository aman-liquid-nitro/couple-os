using CoupleOS.Application.Tools;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CoupleOS.Infrastructure.Persistence;

/// <summary>
/// V0's lexical memory search: PostgreSQL full text, <c>pg_trgm</c> similarity, and
/// a ranking blend. ADR 0002 keeps the <c>embedding</c> column and leaves it empty
/// until there is a corpus worth indexing, so the signature TOOLS.md 7 describes is
/// final and only the <c>ORDER BY</c> below changes at M4.
///
/// <b>Raw SQL, on the couple-scoped context.</b> <c>ts_rank</c> and
/// <c>similarity</c> have no LINQ translation, and the ranking is the feature. It
/// runs on <see cref="CoupleOsDbContext"/> and therefore inside the caller's
/// transaction and under its <c>set_config</c> scope, which is what makes ADR
/// 0005's promise hold for a hand-written query as much as for a generated one: a
/// partner's private memory is not filtered out here, it is invisible here. Nothing
/// in this file mentions <c>visibility</c> or <c>owner_user_id</c>, and that absence
/// is the assertion — the day someone adds a predicate on either, they have moved
/// enforcement out of the database and into a string.
///
/// <b>Every parameter is a parameter.</b> The only value interpolated as text is
/// the type filter, and it is built from a closed CLR enum rather than from
/// anything the model wrote — see <see cref="Label"/>.
/// </summary>
public sealed class MemorySearch(CoupleOsDbContext dbContext) : IMemorySearch
{
    private readonly CoupleOsDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<IReadOnlyList<Memory>> SearchAsync(
        MemoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrWhiteSpace(query.Text))
        {
            return [];
        }

        // text[] rather than an array of the mapped enum, compared against
        // type::text. An enum array would need its PostgreSQL type name stated by
        // hand at the parameter, and getting that wrong fails at execution time
        // with a message about a type nobody wrote.
        var types = query.Types.Select(Label).ToArray();

        var rows = await _dbContext.Memories
            .FromSqlInterpolated($"""
                SELECT m.id, m.couple_id, m.owner_user_id, m.visibility, m.type, m.assertion,
                       m.source, m.content, m.subject_key, m.confidence, m.status,
                       m.superseded_by_id, m.expires_at, m.created_at
                FROM memories m
                WHERE m.couple_id = {query.CoupleId}
                  AND m.deleted_at IS NULL
                  AND m.status = 'active'

                  -- Expiry is enforced on read as well as written on create. ADR
                  -- 0006 says temporary context may not become permanent, and
                  -- nothing in V0 sweeps the table (STATUS debt 29) — so a musing
                  -- whose month is up has to stop being an answer here.
                  AND (m.expires_at IS NULL OR m.expires_at > now())

                  AND (cardinality({types}::text[]) = 0 OR m.type::text = ANY({types}::text[]))
                  AND ({query.Since}::timestamptz IS NULL OR m.created_at >= {query.Since}::timestamptz)

                  -- Two ways to match, deliberately. Full text catches "italian
                  -- food" against "Likes Italian food"; trigram catches the
                  -- misspelling and the partial word, and uses the GIN index the
                  -- schema already builds for it. similarity() itself is left to
                  -- the ranking, where it runs over the matches rather than the
                  -- table.
                  AND (m.search_tsv @@ websearch_to_tsquery('english', {query.Text})
                       OR m.content % {query.Text})

                ORDER BY
                  ts_rank(m.search_tsv, websearch_to_tsquery('english', {query.Text})) * 4
                  + similarity(m.content, {query.Text}) * 2

                  -- Recency as a decaying fraction rather than a tiebreak, so a
                  -- weak match from yesterday can outrank a weak match from March
                  -- but a strong one still wins. Denominated in months.
                  + 1.0 / (1.0 + EXTRACT(EPOCH FROM (now() - m.created_at)) / 2592000.0)

                  + m.importance
                  DESC,
                  m.created_at DESC
                LIMIT {query.Limit}
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return rows;
    }

    /// <summary>
    /// The database label for a <see cref="MemoryType"/>, by the same snake-case
    /// rule Npgsql's default name translator applies to the mapped enum.
    ///
    /// Derived rather than tabulated, so there is no second list of labels to
    /// disagree with the first — the failure <c>DumpEnumMappingTests</c> exists to
    /// catch, arriving here as a hand-written map that drifts. Asserted end to end
    /// by the memory search tests, which write a row through the enum mapping and
    /// find it through this string.
    /// </summary>
    internal static string Label(MemoryType type)
    {
        var name = type.ToString();
        var label = new System.Text.StringBuilder(name.Length + 4);

        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                label.Append('_');
            }

            label.Append(char.ToLowerInvariant(name[i]));
        }

        return label.ToString();
    }
}
