using System.Net;
using System.Text;
using CoupleOS.AI.DependencyInjection;
using CoupleOS.Application.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CoupleOS.AITests;

/// <summary>
/// One provider serves both a local Ollama and the hosted service, and the only
/// difference is a base address and a bearer token (ADR 0013). These tests pin
/// the two things that difference has to get right.
///
/// No network: a capturing handler stands in for the endpoint, because what is
/// being tested is the request this code builds, not a model's answer. The
/// model's answer is <c>OllamaLlmProviderTests</c>'s job and needs a real one.
/// </summary>
public sealed class OllamaHostSelectionTests
{
    private const string Key = "test-key-not-a-real-credential";

    /// <summary>Answers every request with a minimal valid /api/chat body and remembers what it was asked.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"model":"stub","message":{"role":"assistant","content":"","tool_calls":[]}}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }

    private static (ILlmProvider Provider, CapturingHandler Handler) Build(string? apiKey)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Llm:Ollama:BaseUrl"] = apiKey is null ? "http://localhost:11434" : "https://ollama.com",
            ["Llm:Ollama:FastModel"] = "stub",
            ["Llm:Ollama:DeepModel"] = "stub",
            ["Llm:Ollama:NumCtx"] = "4096",
            ["Llm:Ollama:Think"] = "false",
        };

        if (apiKey is not null)
        {
            settings["Llm:Ollama:ApiKey"] = apiKey;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var handler = new CapturingHandler();

        // A second AddHttpClient for the same typed client adds configuration to
        // the same named client rather than replacing it, which is how the real
        // registration's header logic stays under test while the transport is swapped.
        var provider = new ServiceCollection()
            .AddOllamaProvider(configuration)
            .AddHttpClient<ILlmProvider, CoupleOS.AI.Ollama.OllamaLlmProvider>()
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .Services
            .BuildServiceProvider()
            .GetRequiredService<ILlmProvider>();

        return (provider, handler);
    }

    private static LlmRequest Request() => new(
        LlmRole.Fast,
        [new LlmMessage(LlmMessageRole.User, "ping")],
        []);

    [Fact]
    public async Task A_local_instance_is_called_with_no_credential()
    {
        // An empty bearer token is a header that says "unauthenticated" while
        // looking like it says something. Local Ollama accepts it either way,
        // which is exactly why the mistake would go unnoticed.
        var (provider, handler) = Build(apiKey: null);

        await provider.CompleteAsync(Request());

        Assert.NotNull(handler.LastRequest);
        Assert.Null(handler.LastRequest.Headers.Authorization);
    }

    [Fact]
    public async Task The_hosted_service_is_called_with_a_bearer_token()
    {
        var (provider, handler) = Build(Key);

        await provider.CompleteAsync(Request());

        Assert.NotNull(handler.LastRequest);
        Assert.NotNull(handler.LastRequest.Headers.Authorization);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization.Scheme);
        Assert.Equal(Key, handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task Both_hosts_are_reached_on_the_same_endpoint()
    {
        // The reason this is one provider and not two: the hosted service speaks
        // the identical /api/chat with the identical body. If that ever stops
        // being true, this is where it shows up.
        var (local, localHandler) = Build(apiKey: null);
        var (hosted, hostedHandler) = Build(Key);

        await local.CompleteAsync(Request());
        await hosted.CompleteAsync(Request());

        Assert.Equal("/api/chat", localHandler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("/api/chat", hostedHandler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("localhost", localHandler.LastRequest.RequestUri.Host);
        Assert.Equal("ollama.com", hostedHandler.LastRequest.RequestUri.Host);
    }

    [Theory]
    [InlineData(null, "ollama")]
    [InlineData(Key, "ollama-cloud")]
    public async Task The_audit_trail_records_which_host_answered(string? apiKey, string expected)
    {
        // ADR 0011: a local model's output is a floor, not a result. That
        // distinction is only recoverable later if the provider name says which
        // one ran — LlmUsage.Provider is what reaches ai_actions.
        var (provider, _) = Build(apiKey);

        Assert.Equal(expected, provider.Name);

        var completion = await provider.CompleteAsync(Request());
        Assert.Equal(expected, completion.Usage.Provider);
    }
}
