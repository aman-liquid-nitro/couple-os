using System.Diagnostics;
using System.Text.Json;
using CoupleOS.Application;
using CoupleOS.Application.AI;
using CoupleOS.Application.Capture;
using CoupleOS.Application.Persistence;
using CoupleOS.Application.Security;
using CoupleOS.Application.Tools;
using CoupleOS.Domain.Enums;
using CoupleOS.Evals;
using CoupleOS.Infrastructure.DependencyInjection;
using CoupleOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// The eleven cases whose property is a fact about the database.
///
/// Whether the partner's private memory came back, whether the old preference
/// retired, whether a second Process created anything — none of these is
/// answerable by looking at a tool call, and none is answerable by a fake. They
/// need PostgreSQL, with row-level security on and rows already in the tables.
///
/// <b>The completions are scripted here too</b>, for the same reason as in
/// <c>PipelineEvals</c> and one more. The same reason: these properties are
/// decided by SQL and by C#, so putting a model in the path would make each
/// assertion depend on it agreeing with itself. The additional one is sharper —
/// <c>search_memory</c> takes a <c>query</c> string, and if a model chose the
/// word, a privacy case could pass because the model searched for something
/// unrelated. Scripting the query is what makes "and it still found nothing
/// private" mean anything.
///
/// Each case has a scenario, and a case without one fails rather than skipping:
/// the harness that quietly runs forty of fifty-five is the shape of green this
/// milestone exists to remove.
/// </summary>
public sealed class DatabaseEvals(ITestOutputHelper output) : IClassFixture<RlsFixture>
{
    private readonly ITestOutputHelper _output = output;

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();

        foreach (var evalCase in EvalCaseLoader.For(EvalHarness.Database))
        {
            data.Add(evalCase.Id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_database_holds_what_the_case_expects(string id)
    {
        var evalCase = EvalCaseLoader.For(EvalHarness.Database).Single(c => c.Id == id);
        var stopwatch = Stopwatch.StartNew();
        string? failure;

        try
        {
            failure = await RunAsync(evalCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Returned rather than thrown, so the result still reaches the run
            // record. A case that escapes the recorder is a case the gate counts
            // as never run, which is the same silence as being unrunnable.
            failure = $"{ex.GetType().Name}: {ex.Message}";
        }

        stopwatch.Stop();

        EvalResultLog.Append(EvalHarness.Database, new EvalResult(
            evalCase.Id,
            evalCase.Category,
            EvalHarnessNames.Database,
            Fingerprint: "n/a",
            [new EvalAttempt(failure is null, failure, 0, 0, stopwatch.Elapsed.TotalMilliseconds)]));

        if (evalCase.KnownFailureOn(EvalHarness.Database) is { Length: > 0 } known)
        {
            Assert.True(
                failure is not null,
                $"{evalCase.Id} is marked as a known failure on the database harness and it passed. " +
                $"Remove `known_failure` and the entry it cites. It said: {known}");

            _output.WriteLine($"known failure, still failing: {failure}");

            return;
        }

        Assert.True(failure is null, failure);
    }

    private Task<string?> RunAsync(EvalCase evalCase) => evalCase.Id switch
    {
        "privacy-003" => PartnersPrivateMemoryIsAbsentAsync(evalCase),
        "injection-001" => PartnersPrivateMemoryIsAbsentAsync(evalCase),
        "injection-004" => PartnersPrivateMemoryIsAbsentAsync(evalCase),
        "privacy-005" => EverythingMeansEverythingPermittedAsync(),
        "dedup-001" => AsecondIdenticalShoppingItemIsNotCreatedAsync(),
        "dedup-002" => AsecondIdenticalMemoryIsNotCreatedAsync(),
        "conflict-001" => ACorrectionRetiresWhatItReplacesAsync("self:cuisine", "likes Italian food", "does not like Italian food"),
        "conflict-002" => ACorrectionRetiresWhatItReplacesAsync("decision:car", "decided not to buy the car", "decided to buy the car"),
        "inference-002" => AnInferredMemoryIsAnsweredAsAGuessAsync(),
        "boundary-003" => AnEmptyResultIsReportedAsEmptyAsync(),
        "dump-001" => AsecondProcessCreatesNothingAsync(),

        // Not a skip. A case can be declared for this harness and have no
        // scenario, and the only acceptable way for that to end is loudly.
        _ => Task.FromResult<string?>(
            $"{evalCase.Id} is declared for the database harness and has no scenario in DatabaseEvals."),
    };

    // ---------------------------------------------------------------- scenarios

    /// <summary>
    /// privacy-003, injection-001, injection-004 — one property under three
    /// framings, and the framings are the point: a polite question, an
    /// instruction to ignore the rules, and a social-engineering appeal all reach
    /// the same query and get the same rows, because none of them has anything to
    /// widen (ADR 0005).
    /// </summary>
    private async Task<string?> PartnersPrivateMemoryIsAbsentAsync(EvalCase evalCase)
    {
        // The secret and the query are different words, deliberately. Searching
        // for the secret itself made the first version of this pass for the wrong
        // reason and then fail for a worse one: the empty answer echoes the query
        // back — "Nothing recorded about \"handbag…\"" — so a check for the
        // secret in the reply found it in the system's own repetition of the
        // question. A privacy assertion that can be satisfied by the question is
        // not a privacy assertion.
        var secret = Unique("handbag");
        var topic = Unique("birthday");

        await using var services = BuildProvider();

        await WriteMemoryAsync(services, RlsFixture.PartnerD, Visibility.PrivateUser, $$"""
            {"content":"wants a {{secret}} for her {{topic}}","type":"preference"}
            """);

        var byAuthor = await SearchAsync(services, RlsFixture.PartnerD, Visibility.PrivateUser, topic);
        var byPartner = await SearchAsync(services, RlsFixture.PartnerE, Visibility.SharedCouple, topic);

        _output.WriteLine($"author  : {byAuthor}");
        _output.WriteLine($"partner : {byPartner}");

        // Asserted from both sides, and the first side is what makes the second
        // one evidence. A search that found nothing for everybody would satisfy
        // "the partner cannot see it" perfectly and prove that the search is
        // broken.
        if (!byAuthor.Contains(secret, StringComparison.Ordinal))
        {
            return "The memory's own author cannot find it, so this case is measuring a broken " +
                   "search rather than a working policy.";
        }

        if (byPartner.Contains(secret, StringComparison.Ordinal))
        {
            return $"The partner's shared-scope search returned the private memory: {byPartner}";
        }

        // Absent as a plain absence, never as a hint. "I found something I can't
        // show you" is the leak wearing manners.
        if (!byPartner.Contains("Nothing recorded", StringComparison.Ordinal))
        {
            return $"The partner's search neither returned the row nor reported an absence: {byPartner}";
        }

        // The case's own forbidden fragments, read from the file rather than
        // restated here. privacy-003 says the reply must not reveal "bag", and
        // the seeded secret contains it — so the key is asserted rather than
        // decorative.
        foreach (var element in Fragments(evalCase, "response_must_not_reveal"))
        {
            if (byPartner.Contains(element, StringComparison.OrdinalIgnoreCase))
            {
                return $"The partner's answer reveals \"{element}\", which the case forbids: {byPartner}";
            }
        }

        return null;
    }

    private static IEnumerable<string> Fragments(EvalCase evalCase, string key) =>
        evalCase.Raw.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(e => e.GetString()).Where(s => s is not null).Select(s => s!)
            : [];

    /// <summary>
    /// privacy-005 — "show me everything you know about us" must still mean
    /// everything the caller is permitted to see, and the asymmetry runs one way:
    /// a private thread sees the shared rows, and the shared file never sees the
    /// private ones.
    /// </summary>
    private async Task<string?> EverythingMeansEverythingPermittedAsync()
    {
        var token = Unique("munnar");

        await using var services = BuildProvider();

        await WriteMemoryAsync(services, RlsFixture.PartnerD, Visibility.SharedCouple, $$"""
            {"content":"the couple visited {{token}} in July","type":"episodic"}
            """);

        await WriteMemoryAsync(services, RlsFixture.PartnerD, Visibility.PrivateUser, $$"""
            {"content":"planning a {{token}} surprise","type":"plan"}
            """);

        var fromPrivate = await SearchAsync(services, RlsFixture.PartnerD, Visibility.PrivateUser, token);
        var fromShared = await SearchAsync(services, RlsFixture.PartnerE, Visibility.SharedCouple, token);

        _output.WriteLine($"from the private thread : {fromPrivate}");
        _output.WriteLine($"from the shared file    : {fromShared}");

        if (!fromPrivate.Contains("surprise", StringComparison.Ordinal) ||
            !fromPrivate.Contains("visited", StringComparison.Ordinal))
        {
            return "A search from the private thread should see that partner's own private rows " +
                   "*and* the shared ones. It did not: " + fromPrivate;
        }

        if (fromShared.Contains("surprise", StringComparison.Ordinal))
        {
            return "A search from the shared file returned a private row: " + fromShared;
        }

        if (!fromShared.Contains("visited", StringComparison.Ordinal))
        {
            return "A search from the shared file lost the shared row too: " + fromShared;
        }

        return null;
    }

    /// <summary>dedup-002 — SPEC.md §44, and the branch that writes nothing at all.</summary>
    private async Task<string?> AsecondIdenticalMemoryIsNotCreatedAsync()
    {
        var subject = Unique("cuisine");
        var content = $"likes {Unique("italian")} food";

        var call = $$"""
            {"content":"{{content}}","type":"preference","subject_key":"{{subject}}"}
            """;

        await using var services = BuildProvider();

        var first = await DispatchAsync(services, RlsFixture.PartnerD, Visibility.SharedCouple, "create_memory", call);
        var second = await DispatchAsync(services, RlsFixture.PartnerD, Visibility.SharedCouple, "create_memory", call);

        _output.WriteLine($"first  : {first.Outcome} {first.EntityId} {first.Note}");
        _output.WriteLine($"second : {second.Outcome} {second.EntityId} {second.Note}");

        if (!second.Succeeded)
        {
            return $"Repeating a memory failed instead of doing nothing: {string.Join("; ", second.Errors ?? [])}";
        }

        if (second.EntityId is not null)
        {
            return "A second identical memory was created. §44: do not create a duplicate.";
        }

        // "must say so rather than silently no-op" — a row that quietly does not
        // appear is indistinguishable, to the person who wrote the line, from the
        // system losing it.
        if (second.Note is not { Length: > 0 } note || !note.Contains("already recorded", StringComparison.Ordinal))
        {
            return $"Nothing was written and nothing said so. The note was: {second.Note ?? "(none)"}";
        }

        var rows = await QueryAsync(services, RlsFixture.PartnerD, db =>
            db.Memories.CountAsync(m => m.SubjectKey == subject && m.Status == MemoryStatus.Active));

        return rows == 1 ? null : $"{rows} active memories share the subject key, not one.";
    }

    /// <summary>
    /// dedup-001 — the same property for the entity a couple adds most often,
    /// and the one place it is not implemented. Expected to fail; see the case's
    /// `known_failure`.
    /// </summary>
    private async Task<string?> AsecondIdenticalShoppingItemIsNotCreatedAsync()
    {
        var name = Unique("detergent");
        var call = $$"""{"name":"{{name}}"}""";

        await using var services = BuildProvider();

        var first = await DispatchAsync(services, RlsFixture.PartnerD, Visibility.SharedCouple, "create_shopping_item", call);
        var second = await DispatchAsync(services, RlsFixture.PartnerD, Visibility.SharedCouple, "create_shopping_item", call);

        _output.WriteLine($"first  : {first.Outcome} {first.EntityId}");
        _output.WriteLine($"second : {second.Outcome} {second.EntityId} {second.Note}");

        var rows = await QueryAsync(services, RlsFixture.PartnerD, db =>
            db.ShoppingItems.CountAsync(i => i.NormalizedName == name.ToLowerInvariant()));

        if (rows != 1)
        {
            return $"{rows} rows on the list for one item. Nothing deduplicates shopping items: " +
                   "`shopping_pattern` is a plain index, the writer inserts unconditionally, and the " +
                   "tool looks nothing up.";
        }

        return second.Note is { Length: > 0 } note && note.Contains("already", StringComparison.Ordinal)
            ? null
            : "The row was not duplicated and nothing said the item was already on the list.";
    }

    /// <summary>conflict-001, conflict-002 — SPEC.md §45, asserted on the rows rather than on the note.</summary>
    private async Task<string?> ACorrectionRetiresWhatItReplacesAsync(
        string subjectPrefix,
        string before,
        string after)
    {
        var subject = $"{subjectPrefix}:{Unique("s")}";
        var type = subjectPrefix.StartsWith("decision", StringComparison.Ordinal) ? "decision" : "preference";

        await using var services = BuildProvider();

        var original = await DispatchAsync(services, RlsFixture.PartnerD, Visibility.SharedCouple, "create_memory",
            $$"""{"content":"{{before}}","type":"{{type}}","subject_key":"{{subject}}"}""");

        var correction = await DispatchAsync(services, RlsFixture.PartnerD, Visibility.SharedCouple, "create_memory",
            $$"""{"content":"{{after}}","type":"{{type}}","subject_key":"{{subject}}"}""");

        _output.WriteLine($"original   : {original.EntityId}");
        _output.WriteLine($"correction : {correction.EntityId} — {correction.Note}");

        if (correction.EntityId is null)
        {
            return "The correction wrote nothing. A differently-worded claim on the same subject is " +
                   "a contradiction (§45), not a repetition (§44).";
        }

        var old = await QueryAsync(services, RlsFixture.PartnerD, db =>
            db.Memories.SingleAsync(m => m.Id == original.EntityId));

        if (old.Status != MemoryStatus.Superseded)
        {
            return $"The old memory is still {old.Status}. Silent retention of both is what §45 forbids.";
        }

        if (old.SupersededById != correction.EntityId)
        {
            return "The retired memory does not point at the one that replaced it, so the audit " +
                   "trail cannot say what corrected what.";
        }

        var active = await QueryAsync(services, RlsFixture.PartnerD, db =>
            db.Memories.CountAsync(m => m.SubjectKey == subject && m.Status == MemoryStatus.Active));

        return active == 1 ? null : $"{active} memories on this subject are active, not one.";
    }

    /// <summary>
    /// inference-002 — ADR 0006's requirement that an inference reach a person as
    /// a hypothesis with provenance, asserted where it is produced.
    ///
    /// The hedge is rendered in C# rather than requested of the model, and this
    /// is the case that shows why it had to be: V0 has no second completion, so
    /// any prose the model wrote about "what she likes to eat" was written before
    /// the search ran and cannot be about the rows at all.
    /// </summary>
    private async Task<string?> AnInferredMemoryIsAnsweredAsAGuessAsync()
    {
        var token = Unique("italian");

        await using var services = BuildProvider();

        await WriteMemoryAsync(services, RlsFixture.PartnerD, Visibility.SharedCouple, $$"""
            {"content":"prefers {{token}} food","type":"preference","assertion":"inferred","confidence":0.6}
            """);

        var answer = await SearchAsync(services, RlsFixture.PartnerD, Visibility.SharedCouple, token);

        _output.WriteLine(answer);

        if (!answer.Contains(token, StringComparison.Ordinal))
        {
            return $"The memory was not found at all: {answer}";
        }

        if (!answer.Contains("treat it as a guess", StringComparison.Ordinal))
        {
            return $"An inferred memory was stated without a hedge: {answer}";
        }

        return answer.Contains("worked out rather than was told", StringComparison.Ordinal)
            ? null
            : $"The hedge carries no provenance, which is the half ADR 0006 asks for: {answer}";
    }

    /// <summary>
    /// boundary-003 — an empty result reported as empty, never filled with a
    /// plausible number.
    /// </summary>
    private async Task<string?> AnEmptyResultIsReportedAsEmptyAsync()
    {
        await using var services = BuildProvider();

        var answer = await SearchAsync(
            services,
            RlsFixture.PartnerD,
            Visibility.SharedCouple,
            Letters("groceries"));

        _output.WriteLine(answer);

        if (!answer.Contains("Nothing recorded", StringComparison.Ordinal))
        {
            return $"An empty result was not reported as empty: {answer}";
        }

        // No figure can appear, because the sentence is produced by C# from zero
        // rows — asserted rather than reasoned about, since "it cannot happen" is
        // what every §46 failure has said about itself.
        var digits = answer.Where(char.IsDigit).ToArray();

        return digits.Length == 0
            ? null
            : $"The empty answer contains digits, which is where an estimate would appear: {answer}";
    }

    /// <summary>dump-001 — the failure mode most likely to destroy trust.</summary>
    private async Task<string?> AsecondProcessCreatesNothingAsync()
    {
        var run = Unique("dal");

        var provider = new ScriptedProvider(_ => new LlmCompletion(
            [new LlmToolCall("create_shopping_item", JsonDocument.Parse($$"""{"name":"{{run}}"}""").RootElement.Clone())],
            Content: null,
            new LlmUsage("scripted", "stub-model", 100, 10, TimeSpan.FromMilliseconds(250))));

        await using var services = new ServiceCollection()
            .AddCoupleOsInfrastructure(RlsFixture.AppConnectionString)
            .AddCoupleOsApplication()
            .AddSingleton<ILlmProvider>(provider)
            .BuildServiceProvider();

        await using (var write = BeginRequest(services, RlsFixture.Couple1, RlsFixture.PartnerA))
        {
            var editor = write.ServiceProvider.GetRequiredService<ISharedFileEditor>();
            var current = await editor.ReadAsync();
            var save = await editor.SaveAsync($"- we need rice and {run}", current.Version);

            if (!save.Accepted)
            {
                return "The shared file changed underneath this case's setup.";
            }
        }

        CaptureReport first, second;

        await using (var one = BeginRequest(services, RlsFixture.Couple1, RlsFixture.PartnerA))
        {
            first = await one.ServiceProvider.GetRequiredService<ICaptureProcessor>().ProcessAsync();
        }

        await using (var two = BeginRequest(services, RlsFixture.Couple1, RlsFixture.PartnerA))
        {
            second = await two.ServiceProvider.GetRequiredService<ICaptureProcessor>().ProcessAsync();
        }

        _output.WriteLine($"first  : {first.Blocks.Count} blocks, {first.Applied.Count()} applied");
        _output.WriteLine($"second : {second.Blocks.Count} blocks, {second.Applied.Count()} applied");

        if (first.Applied.Count() != 1)
        {
            return $"The first run created {first.Applied.Count()} entities, so the second has nothing to prove.";
        }

        if (second.Applied.Any())
        {
            return $"The second Process created {second.Applied.Count()} entities. `entities_created` must be 0 — " +
                   "duplicate expenses appearing from nowhere is the failure most likely to destroy trust.";
        }

        // Asked once in total, not twice. A second completion that happened to
        // produce nothing would satisfy the count above and still be a run that
        // re-read work it had already done.
        return provider.Calls == 1 ? null : $"The model was called {provider.Calls} times across two runs.";
    }

    // ---------------------------------------------------------------- plumbing

    private sealed class ScriptedProvider(Func<string, LlmCompletion> script) : ILlmProvider
    {
        public string Name => "scripted";

        public int Calls { get; private set; }

        public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;

            return Task.FromResult(script(request.Messages.Last(m => m.Role == LlmMessageRole.User).Content));
        }
    }

    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddCoupleOsInfrastructure(RlsFixture.AppConnectionString)
            .AddCoupleOsTools()
            .BuildServiceProvider();

    private static AsyncServiceScope BeginRequest(ServiceProvider provider, Guid couple, Guid user)
    {
        var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICoupleScopeSetter>().Set(new CoupleScope(couple, user));

        return scope;
    }

    /// <summary>
    /// Reads under a couple scope, because every read does.
    ///
    /// The first version of this harness verified its rows with a bare
    /// <c>db.Memories.CountAsync(...)</c> outside any transaction and got zero
    /// every time — no <c>set_config</c>, no scope, no rows. Which is row-level
    /// security working exactly as ADR 0005 says it should: a request without
    /// scope sees nothing, including this one. Left as a helper rather than
    /// inlined, so the next verification cannot forget.
    /// </summary>
    private static async Task<T> QueryAsync<T>(
        ServiceProvider provider,
        Guid user,
        Func<CoupleOsDbContext, Task<T>> query)
    {
        await using var request = BeginRequest(provider, RlsFixture.Couple3, user);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        var result = await query(db);

        await transaction.CommitAsync();

        return result;
    }

    private static async Task<ToolResult> DispatchAsync(
        ServiceProvider provider,
        Guid user,
        Visibility visibility,
        string tool,
        string arguments)
    {
        await using var request = BeginRequest(provider, RlsFixture.Couple3, user);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var dispatcher = request.ServiceProvider.GetRequiredService<IToolDispatcher>();

        await using var transaction = await unitOfWork.BeginAsync();

        var result = await dispatcher.DispatchAsync(
            new LlmToolCall(tool, JsonDocument.Parse(arguments).RootElement.Clone()),
            new ToolExecutionContext { CoupleId = RlsFixture.Couple3, UserId = user, Visibility = visibility });

        await transaction.CommitAsync();

        return result;
    }

    private static async Task WriteMemoryAsync(
        ServiceProvider provider,
        Guid user,
        Visibility visibility,
        string arguments)
    {
        var result = await DispatchAsync(provider, user, visibility, "create_memory", arguments);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors ?? []));
    }

    private static async Task<string> SearchAsync(
        ServiceProvider provider,
        Guid user,
        Visibility visibility,
        string query)
    {
        var result = await DispatchAsync(
            provider,
            user,
            visibility,
            "search_memory",
            $$"""{"query":"{{query}}"}""");

        Assert.True(result.Succeeded, string.Join("; ", result.Errors ?? []));

        return result.Answer ?? string.Empty;
    }

    /// <summary>
    /// A token no other row contains, so an assertion is about what this case
    /// wrote rather than about how many rows happened to be in the table.
    /// </summary>
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..16];

    /// <summary>
    /// A unique token with no digits in it, for the one case whose assertion is
    /// "this sentence contains no number". A nonce with a 7 in it would fail
    /// boundary-003 by being the estimate it is looking for.
    /// </summary>
    private static string Letters(string prefix) =>
        prefix + new string([.. Guid.NewGuid().ToString("N").Where(char.IsLetter).Take(6)]);
}
