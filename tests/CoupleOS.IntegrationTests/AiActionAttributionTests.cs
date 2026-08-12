using System.Text.Json;
using CoupleOS.Application.AI;
using CoupleOS.Application.Security;
using CoupleOS.Application.Tools;
using CoupleOS.Domain.Enums;
using CoupleOS.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// That the attribution columns are actually written.
///
/// They existed in data/schema.sql from the first commit and nothing populated
/// them, which is the failure mode worth guarding: a column that is present and
/// always null reads as "this run used no model" rather than "nobody implemented
/// this". A schema test cannot catch that — the column exists either way — so it
/// takes a test that dispatches a call and reads the row back.
/// </summary>
[Collection(IdentityCollection.Name)]
public sealed class AiActionAttributionTests : IClassFixture<RlsFixture>, IAsyncLifetime
{
    private const string Tool = "create_shopping_item";

    public Task InitializeAsync() => ClearAsync();

    public Task DisposeAsync() => ClearAsync();

    private static async Task ClearAsync()
    {
        await using var conn = new NpgsqlConnection(RlsFixture.AdminConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ai_actions WHERE couple_id = @couple";
        cmd.Parameters.AddWithValue("couple", RlsFixture.Couple1);
        await cmd.ExecuteNonQueryAsync();
    }

    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddCoupleOsInfrastructure(RlsFixture.AppConnectionString)
            .AddCoupleOsTools()
            .BuildServiceProvider();

    private static ToolExecutionContext Context(LlmAttribution? attribution) => new()
    {
        CoupleId = RlsFixture.Couple1,
        UserId = RlsFixture.PartnerA,
        Visibility = Visibility.SharedCouple,
        Attribution = attribution,
    };

    private static LlmToolCall Call(string name) =>
        new(Tool, JsonDocument.Parse($$"""{"name":"{{name}}"}""").RootElement.Clone());

    private static async Task DispatchAsync(ServiceProvider provider, LlmToolCall call, ToolExecutionContext context)
    {
        await using var scope = provider.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<ICoupleScopeSetter>()
            .Set(new CoupleScope(context.CoupleId, context.UserId));

        var unitOfWork = scope.ServiceProvider
            .GetRequiredService<Application.Persistence.IScopedUnitOfWork>();

        await using var transaction = await unitOfWork.BeginAsync();

        await scope.ServiceProvider.GetRequiredService<IToolDispatcher>()
            .DispatchAsync(call, context);

        await transaction.CommitAsync();
    }

    private sealed record Row(string? Provider, string? Model, string? LlmRole, int? PromptTokens, int? CompletionTokens);

    private static async Task<List<Row>> ReadRowsAsync()
    {
        await using var conn = new NpgsqlConnection(RlsFixture.AdminConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT provider, model, llm_role, prompt_tokens, completion_tokens
            FROM ai_actions WHERE couple_id = @couple ORDER BY created_at
            """;
        cmd.Parameters.AddWithValue("couple", RlsFixture.Couple1);

        var rows = new List<Row>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new Row(
                await reader.IsDBNullAsync(0) ? null : reader.GetString(0),
                await reader.IsDBNullAsync(1) ? null : reader.GetString(1),
                await reader.IsDBNullAsync(2) ? null : reader.GetString(2),
                await reader.IsDBNullAsync(3) ? null : reader.GetInt32(3),
                await reader.IsDBNullAsync(4) ? null : reader.GetInt32(4)));
        }

        return rows;
    }

    [Fact]
    public async Task The_row_records_the_host_the_model_ran_on()
    {
        // ADR 0011's floor-versus-result distinction, and after ADR 0013 the host
        // is configuration — so the value configured today says nothing about what
        // answered then. "ollama" and "ollama-cloud" are the same software on
        // different machines, which is exactly why the provider name has to be
        // stored rather than inferred.
        await using var provider = BuildProvider();

        await DispatchAsync(
            provider,
            Call("detergent"),
            Context(new LlmAttribution("ollama-cloud", "gemma4:31b", LlmRole.Fast, 660, 42)));

        var row = Assert.Single(await ReadRowsAsync());

        Assert.Equal("ollama-cloud", row.Provider);
        Assert.Equal("gemma4:31b", row.Model);
        Assert.Equal("fast", row.LlmRole);
        Assert.Equal(660, row.PromptTokens);
        Assert.Equal(42, row.CompletionTokens);
    }

    [Fact]
    public async Task Summing_tokens_over_a_multi_call_completion_gives_the_real_figure()
    {
        // The database half of what CaptureProcessorAttributionTests pins in
        // memory: three rows from one completion, tokens on one of them, so the
        // cost query SPEC.md 50 asks for returns 660 rather than 1980.
        await using var provider = BuildProvider();

        var attribution = new LlmAttribution("ollama-cloud", "gemma4:31b", LlmRole.Fast, 660, 42);

        await DispatchAsync(provider, Call("detergent"), Context(attribution));
        await DispatchAsync(provider, Call("coffee"), Context(attribution.WithoutTokens()));
        await DispatchAsync(provider, Call("rice"), Context(attribution.WithoutTokens()));

        var rows = await ReadRowsAsync();

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal("gemma4:31b", r.Model));

        Assert.Equal(660, rows.Sum(r => r.PromptTokens ?? 0));
        Assert.Equal(42, rows.Sum(r => r.CompletionTokens ?? 0));
        Assert.Equal(1, rows.Count(r => r.PromptTokens is not null));
    }

    [Fact]
    public async Task A_call_no_model_proposed_leaves_the_columns_null()
    {
        // A scheduled job or a direct write has no model to name, and inventing a
        // provider for it would make the column lie in the other direction.
        await using var provider = BuildProvider();

        await DispatchAsync(provider, Call("detergent"), Context(attribution: null));

        var row = Assert.Single(await ReadRowsAsync());

        Assert.Null(row.Provider);
        Assert.Null(row.Model);
        Assert.Null(row.LlmRole);
        Assert.Null(row.PromptTokens);
    }

    [Fact]
    public async Task A_refused_call_is_still_attributed()
    {
        // TOOLS.md universal rule 3 records refusals too, and a refusal is when
        // knowing which model produced the argument matters most — it is the
        // evidence for whether a model or the schema is at fault.
        await using var provider = BuildProvider();

        var noName = new LlmToolCall(Tool, JsonDocument.Parse("""{"quantity":"2"}""").RootElement.Clone());

        await DispatchAsync(
            provider,
            noName,
            Context(new LlmAttribution("ollama", "qwen3.5:4b", LlmRole.Fast, 249, 8)));

        var row = Assert.Single(await ReadRowsAsync());

        Assert.Equal("ollama", row.Provider);
        Assert.Equal("qwen3.5:4b", row.Model);
        Assert.Equal(249, row.PromptTokens);
    }
}
