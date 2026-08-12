using System.Net.Http.Headers;
using CoupleOS.AI.Ollama;
using CoupleOS.Application.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoupleOS.AI.DependencyInjection;

public static class AiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Ollama provider behind <see cref="ILlmProvider"/>.
    ///
    /// Options are validated on start rather than on first use: a mistyped
    /// context window should stop the application immediately, not surface
    /// hours later as a dump file whose tail was silently truncated.
    /// </summary>
    public static IServiceCollection AddOllamaProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<OllamaOptions>()
            .Bind(configuration.GetSection(OllamaOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<ILlmProvider, OllamaLlmProvider>((provider, client) =>
        {
            var options = provider.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<OllamaOptions>>().Value;

            client.BaseAddress = options.BaseUrl;

            // A local 4B model answers a block in about two seconds, but a cold
            // load costs six. The default HttpClient timeout would turn the
            // first request after an idle period into a spurious failure.
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

            // Set only when a key is configured. A local instance rejects nothing
            // but needs nothing either, and sending an empty bearer token to it
            // would be a header that means "unauthenticated" spelled as if it
            // meant something (ADR 0013).
            if (options.IsHosted)
            {
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", options.ApiKey);
            }
        });

        return services;
    }
}
