using Npgsql;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// A hundred and four memories for one couple, so that <c>search_memory</c> can be
/// asked a question with more than one plausible answer.
///
/// This is STATUS debt 47's remedy. Every other memory test writes one or two rows
/// carrying a token nothing else contains, which asserts that the query matches and
/// says nothing about the ordering — and the ordering is the feature, since
/// <c>MemorySearch</c> blends four terms and the model is handed the top few rows as
/// though they were the answer.
///
/// <b>Its own couple, by the same rule as <see cref="RlsFixture.Couple3"/>.</b> A
/// hundred rows dropped into a couple whose other tests count rows would break them,
/// and the exact-count assertion is the one worth protecting.
///
/// <b>Fixed ages, not fixed dates.</b> Rows are seeded relative to <c>now()</c>, so a
/// recency assertion made today still means the same thing next March. This is the
/// mistake STATUS debt 21 records in the eval set, avoided rather than repeated.
///
/// <b>The corpus is deliberately clustered.</b> Ten rows about food, nine about the
/// car, eight about money — because a ranking is only wrong in the presence of
/// neighbours, and a corpus of a hundred unrelated sentences would be a hundred
/// copies of the tests that already exist. Where a test names a row, the words that
/// select it appear in a known number of rows and the comment says how many.
/// </summary>
public sealed class MemoryCorpusFixture
{
    public static readonly Guid Couple4 = Guid.Parse("c4444444-4444-4444-4444-444444444444");
    public static readonly Guid PartnerF = Guid.Parse("66666666-6666-6666-6666-666666666666");
    public static readonly Guid PartnerG = Guid.Parse("77777777-7777-7777-7777-777777777777");

    /// <summary>
    /// The floor V0_SCOPE's done-checklist names. Asserted at seed time rather than
    /// trusted, because the checklist item is the corpus <i>size</i> and a corpus
    /// that quietly shrank to eighty rows would still pass every test below.
    /// </summary>
    public const int MinimumCorpusSize = 100;

    private static bool _seeded;
    private static readonly Lock Gate = new();

    public MemoryCorpusFixture()
    {
        lock (Gate)
        {
            if (_seeded) return;
            Seed();
            _seeded = true;
        }
    }

    /// <summary>
    /// (type, content, importance, months old, months until expiry).
    ///
    /// Ages are spread across two years so that recency is a spectrum rather than
    /// new-or-old. Importance sits at the column's own default of 0.50 unless a test
    /// depends on it, so that the rows carrying a deliberate value are the only ones
    /// that could explain a result.
    /// </summary>
    private static readonly (string Type, string Content, decimal Importance, int MonthsOld, int? ExpiresInMonths)[] Corpus =
    [
        // ── Food and eating out (11) ────────────────────────────────────────────
        // "anniversary dinner" matches exactly three of these, and they are the
        // three that Anniversary_dinner_picks_the_focused_recent_memory ranks.
        ("episodic",   "Anniversary dinner at the rooftop place was perfect",                                                                                     0.80m,  2, null),
        ("episodic",   "We were talking about what to do for the anniversary and whether dinner out is worth it given how much we spent last year on the trip, but nothing was decided", 0.40m, 11, null),
        ("episodic",   "Booked the anniversary dinner and cancelled it the same week",                                                                            0.50m, 14, null),
        ("preference", "She does not like coriander in anything",                                                                                                 0.70m, 19, null),
        ("preference", "He will eat almost anything except mushrooms",                                                                                            0.60m, 18, null),
        ("preference", "We both prefer the Thai place on the main road to the one in the mall",                                                                   0.50m,  7, null),
        ("episodic",   "The Italian restaurant near the park was disappointing the second time",                                                                  0.40m,  5, null),
        ("semantic",   "Sunday breakfast is dosa, and it has been for years",                                                                                     0.60m, 22, null),
        ("episodic",   "We tried the new Korean place and it was too spicy for her",                                                                              0.30m,  4, null),
        ("decision",   "We decided to stop ordering in on weeknights",                                                                                            0.60m,  9, null),
        ("commitment", "He said he would learn to make proper biryani this year",                                                                                 0.40m, 10, null),

        // ── The car (9) ─────────────────────────────────────────────────────────
        // "insurance" matches exactly two, and they differ only in length. That
        // pair is the whole of A_short_empty_memory_outranks_a_long_precise_one.
        ("semantic",   "Insurance sorted",                                                                                                                        0.50m,  6, null),
        ("decision",   "The car insurance renewal is due in March and we agreed to switch providers because the current one raised the premium after the claim",   0.50m,  6, null),
        ("episodic",   "The car made a grinding noise on the way back from her parents",                                                                          0.40m,  3, null),
        ("semantic",   "The service centre we use is the one off the ring road, not the dealership",                                                              0.50m, 15, null),
        ("episodic",   "We got the tyres replaced before the monsoon",                                                                                            0.30m,  8, null),
        ("decision",   "We decided to keep the old car another year rather than upgrade",                                                                         0.70m, 12, null),
        ("commitment", "She said she would sort out the parking sticker renewal",                                                                                 0.40m,  1, null),
        ("episodic",   "The wing mirror got clipped in the basement and neither of us saw who did it",                                                            0.30m, 16, null),
        ("plan",       "Wash the car before the trip",                                                                                                            0.20m,  2, null),

        // ── Travel (11) ─────────────────────────────────────────────────────────
        ("episodic",   "We visited Munnar in July and stayed at the place with the tea estate view",                                                              0.70m, 13, null),
        ("plan",       "We want to do a long weekend in Goa before the rains",                                                                                    0.60m,  3, null),
        ("decision",   "We decided against the Europe trip this year because of the house deposit",                                                               0.80m,  7, null),
        ("episodic",   "The flight back from Delhi was delayed six hours and we missed the booking",                                                              0.40m, 20, null),
        ("preference", "She prefers hill stations, he prefers the coast",                                                                                         0.60m, 17, null),
        ("semantic",   "Her passport expires next year and mine the year after",                                                                                  0.70m,  5, null),
        ("plan",       "Book the flights early for the December trip, prices climb after October",                                                                0.50m,  4, null),
        ("episodic",   "The homestay in Coorg was better than the resort and half the price",                                                                     0.50m, 21, null),
        ("commitment", "He promised her parents we would visit before the end of the year",                                                                       0.70m,  6, null),
        ("event",      "Our first trip together was to Pondicherry",                                                                                              0.60m, 23, null),
        ("plan",       "Renew the travel insurance before booking anything",                                                                                      0.40m,  2, null),

        // ── House, shopping, chores (14) ────────────────────────────────────────
        // "detergent" matches exactly one row, and the misspelling of it matches
        // none — which is A_misspelling_finds_nothing_once_the_memory_has_a_sentence.
        ("episodic",   "We are out of detergent",                                                                                                                 0.30m,  1, null),
        // Two rows, the same sentence, twenty months apart — and the older one is
        // twice as important, deliberately. Recency_outweighs_a_doubled_importance
        // needs the newer row to win *against* something, because the ORDER BY ends
        // in created_at DESC and a pair that differed only in age would come out
        // newest-first with the recency term deleted entirely. That version of this
        // fixture existed, and the test passed while measuring the tiebreak.
        ("plan",       "The balcony plants need repotting",                                                                                                       0.30m,  1, null),
        ("plan",       "The balcony plants need repotting",                                                                                                       0.60m, 20, null),
        ("semantic",   "The geyser takes about fifteen minutes and the switch is behind the door",                                                                0.40m, 18, null),
        ("episodic",   "The washing machine drawer jammed again and he cleared it with a knife",                                                                  0.20m,  4, null),
        ("decision",   "We decided to get the sofa reupholstered rather than replace it",                                                                         0.50m, 10, null),
        ("plan",       "Get the water tank cleaned before summer",                                                                                                0.40m,  6, null),
        ("semantic",   "The spare keys are with the neighbour on the third floor",                                                                                0.80m, 14, null),
        ("commitment", "She said she would call the plumber about the leak in the guest bathroom",                                                                0.50m,  2, null),
        ("episodic",   "The power went out for most of a day in April and the food in the freezer went",                                                          0.30m, 15, null),
        ("preference", "He likes the flat cold at night, she does not",                                                                                           0.50m, 19, null),
        ("plan",       "Replace the bathroom mirror, the backing has gone",                                                                                       0.30m,  8, null),
        ("semantic",   "Rent is due on the fifth and the landlord prefers a transfer",                                                                            0.70m, 22, null),
        ("decision",   "We agreed not to keep anything on the dining table",                                                                                      0.30m, 11, null),

        // ── Money (10) ──────────────────────────────────────────────────────────
        ("decision",   "We agreed to keep a joint account for household spending only",                                                                           0.80m, 16, null),
        ("semantic",   "The house deposit target is what we are saving towards",                                                                                  0.90m,  9, null),
        ("episodic",   "The dinner in December came to more than we expected and neither of us minded",                                                           0.30m,  8, null),
        ("preference", "She would rather track spending weekly than monthly",                                                                                     0.40m, 12, null),
        ("decision",   "We decided to stop the second streaming subscription",                                                                                    0.30m,  5, null),
        ("commitment", "He said he would sort out the tax filing before the deadline",                                                                            0.70m,  3, null),
        ("semantic",   "The electricity bill is usually around two thousand in summer",                                                                           0.40m, 13, null),
        ("episodic",   "We split the furniture cost unevenly and agreed to settle it later",                                                                      0.50m, 21, null),
        ("plan",       "Review the insurance and the SIPs together once a year",                                                                                  0.60m,  7, null),
        ("decision",   "We decided the wedding gift budget for the year",                                                                                         0.40m, 10, null),

        // ── People and family (12) ──────────────────────────────────────────────
        ("semantic",   "Her mother does not eat onion or garlic",                                                                                                 0.70m, 20, null),
        ("semantic",   "His brother is allergic to peanuts, seriously so",                                                                                        0.90m, 18, null),
        ("event",      "Her parents anniversary is in the first week of September",                                                                               0.80m, 17, null),
        ("episodic",   "We had her cousins over and it went better than either of us expected",                                                                   0.30m,  6, null),
        ("preference", "He finds long family calls difficult and would rather visit",                                                                             0.50m, 11, null),
        ("commitment", "She promised her sister help with the move in August",                                                                                    0.60m,  4, null),
        ("semantic",   "The neighbours are away most weekends",                                                                                                   0.20m,  9, null),
        ("episodic",   "His father was in hospital briefly in February and is fine now",                                                                          0.70m, 14, null),
        ("event",      "We met at a wedding neither of us wanted to attend",                                                                                      0.80m, 23, null),
        ("preference", "She would rather host than be hosted",                                                                                                    0.40m, 15, null),
        ("semantic",   "Her friend from college is the one who recommended the clinic",                                                                           0.30m, 12, null),
        ("commitment", "We said we would go to his cousin's housewarming",                                                                                        0.40m,  2, null),

        // ── Health (8) ──────────────────────────────────────────────────────────
        ("semantic",   "She is lactose intolerant, mildly",                                                                                                       0.80m, 19, null),
        ("episodic",   "He pulled something in his back lifting the mattress",                                                                                    0.40m,  5, null),
        ("commitment", "We said we would both do the annual bloodwork this year",                                                                                 0.60m,  8, null),
        ("plan",       "Book the dentist for both of us, it has been two years",                                                                                  0.50m,  1, null),
        ("preference", "He will not take the stairs past the fourth floor",                                                                                       0.20m, 16, null),
        ("semantic",   "The clinic near the market opens at seven, the other at nine",                                                                            0.40m, 10, null),
        ("episodic",   "The fever in January turned out to be nothing",                                                                                           0.20m, 13, null),
        ("decision",   "We decided to walk in the evenings rather than pay for the gym",                                                                          0.50m,  7, null),

        // ── Work (8) ────────────────────────────────────────────────────────────
        ("semantic",   "Her review cycle is in the first quarter, his is in the third",                                                                           0.60m, 15, null),
        ("episodic",   "He travelled three weeks in a row in November and it was too much",                                                                       0.50m, 12, null),
        ("decision",   "We decided she would take the offer even though it means the longer commute",                                                             0.90m,  6, null),
        ("preference", "He does not want to talk about work before coffee",                                                                                       0.40m, 17, null),
        ("commitment", "She said she would stop taking calls after nine",                                                                                         0.50m,  3, null),
        ("semantic",   "His office is closed the last week of December",                                                                                          0.50m,  9, null),
        ("plan",       "Sort out the home desk before her team visit",                                                                                            0.30m,  2, null),
        ("episodic",   "The team offsite clashed with her birthday and she was fine about it",                                                                     0.30m, 21, null),

        // ── Gifts, occasions, the two of them (11) ──────────────────────────────
        ("event",      "Our anniversary is the fourteenth of March",                                                                                              0.90m, 22, null),
        ("preference", "She likes practical gifts and dislikes surprises in public",                                                                              0.70m, 18, null),
        ("episodic",   "The watch he gave her for her birthday needed the strap shortened",                                                                        0.30m, 11, null),
        ("semantic",   "Her ring size is small and she has told him twice",                                                                                       0.60m, 16, null),
        ("preference", "He would rather have a good meal than a present",                                                                                         0.50m, 14, null),
        ("plan",       "Something for her parents at Diwali, not sweets again",                                                                                   0.40m,  5, null),
        ("episodic",   "The concert tickets were her idea and the better evening for it",                                                                          0.40m,  9, null),
        ("event",      "We moved into this flat in the second week of June",                                                                                      0.70m, 20, null),
        ("commitment", "He said he would plan the whole day for the anniversary this year",                                                                       0.60m,  1, null),
        ("preference", "Neither of us wants a party",                                                                                                             0.50m, 13, null),
        ("episodic",   "The photo from Pondicherry is the one she wants framed",                                                                                  0.40m,  7, null),

        // ── Ongoing and temporary (10) ──────────────────────────────────────────
        ("temporary_context", "She is on antibiotics this week and off coffee",                                                                                   0.30m,  0, 1),
        ("temporary_context", "He is thinking about the standing desk but has not decided",                                                                       0.20m,  0, 2),
        ("temporary_context", "We are trying the earlier bedtime for a month",                                                                                    0.20m,  0, 1),
        ("temporary_context", "The lift is out until the end of the month",                                                                                       0.30m,  0, 1),
        ("plan",              "Finish the balcony before the guests in November",                                                                                 0.40m,  3, null),
        ("plan",              "Look at schools in the area eventually, no hurry",                                                                                 0.30m, 10, null),
        ("commitment", "We said we would take a photo together every month this year",                                                                            0.30m, 11, null),
        ("semantic",   "The building water supply is cut on Tuesday mornings",                                                                                    0.40m, 19, null),
        ("episodic",   "The argument about the holiday was really about the money",                                                                               0.60m, 15, null),
        ("decision",   "We agreed to say things the same week rather than let them sit",                                                                          0.80m, 12, null),
    ];

    private static void Seed()
    {
        using var conn = new NpgsqlConnection(RlsFixture.AdminConnectionString);
        conn.Open();

        using (var reset = conn.CreateCommand())
        {
            // Deleted and re-seeded per run for the reason RlsFixture states: a suite
            // that appended would search a table that grew every time it ran, and the
            // ranking assertions would drift with it.
            reset.CommandText = $"""
                DELETE FROM memories       WHERE couple_id = '{Couple4}';
                DELETE FROM ai_actions     WHERE couple_id = '{Couple4}';
                DELETE FROM couple_members WHERE couple_id = '{Couple4}';
                DELETE FROM couples        WHERE id        = '{Couple4}';
                DELETE FROM users          WHERE id       IN ('{PartnerF}','{PartnerG}');

                INSERT INTO users (id,email,display_name) VALUES
                  ('{PartnerF}','f@x.com','Partner F'),
                  ('{PartnerG}','g@x.com','Partner G');

                INSERT INTO couples (id,display_name) VALUES ('{Couple4}','Couple Four');

                INSERT INTO couple_members (couple_id,user_id) VALUES
                  ('{Couple4}','{PartnerF}'),
                  ('{Couple4}','{PartnerG}');
                """;
            reset.ExecuteNonQuery();
        }

        // One statement, arrays unnested, so a hundred rows are one round trip and
        // every value is a parameter rather than interpolated text.
        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO memories
                    (couple_id, owner_user_id, visibility, type, content, importance, created_at, expires_at)
                SELECT @couple, NULL, 'shared_couple', c.type::memory_type, c.content, c.importance,
                       now() - make_interval(months => c.months_old),
                       CASE WHEN c.expires_in IS NULL THEN NULL
                            ELSE now() + make_interval(months => c.expires_in) END
                FROM unnest(@types::text[], @contents::text[], @importances::numeric[],
                            @months_old::int[], @expires_in::int[])
                     AS c(type, content, importance, months_old, expires_in)
                """;

            insert.Parameters.AddWithValue("couple", Couple4);
            insert.Parameters.AddWithValue("types", Corpus.Select(r => r.Type).ToArray());
            insert.Parameters.AddWithValue("contents", Corpus.Select(r => r.Content).ToArray());
            insert.Parameters.AddWithValue("importances", Corpus.Select(r => r.Importance).ToArray());
            insert.Parameters.AddWithValue("months_old", Corpus.Select(r => r.MonthsOld).ToArray());
            // int?[] rather than object[]: Npgsql infers integer[] from the element
            // type and writes the nulls itself. An object[] carrying DBNull has no
            // element type to infer from and is refused before it reaches the server.
            insert.Parameters.AddWithValue("expires_in", Corpus.Select(r => r.ExpiresInMonths).ToArray());

            var written = insert.ExecuteNonQuery();

            if (written < MinimumCorpusSize)
            {
                throw new InvalidOperationException(
                    $"The memory corpus seeded {written} rows and V0_SCOPE's checklist asks for at least " +
                    $"{MinimumCorpusSize}. The ranking assertions would still pass on a smaller one, which " +
                    "is exactly why this is checked here rather than assumed.");
            }
        }
    }
}
