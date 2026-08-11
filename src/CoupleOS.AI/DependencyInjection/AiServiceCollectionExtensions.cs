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
        });

        return services;
    }
}
