using DistSharp.Core.Abstractions;
using DistSharp.Providers.Anthropic;
using DistSharp.Providers.Gemini;
using DistSharp.Providers.OpenAI;
using Microsoft.Extensions.DependencyInjection;

namespace DistSharp.Providers;

/// <summary>Extension methods to register DistSharp providers with <see cref="IServiceCollection"/>.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers all DistSharp LLM providers, their options classes, and the factory.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddDistSharpProviders(this IServiceCollection services)
    {
        // Options — defaults below match nominal use; environment-specific providers (Azure deployment
        // name, Ollama/LM Studio model tags, generic compatible endpoints) have no default because
        // the right value depends on the user's deployment.
        services.AddSingleton<OpenAIProviderOptions>(_ => new OpenAIProviderOptions { DefaultModel = "gpt-5.4" });
        services.AddSingleton<AzureOpenAIProviderOptions>(_ => new AzureOpenAIProviderOptions());
        services.AddSingleton<OpenAICompatibleEndpointOptions>(_ => new OpenAICompatibleEndpointOptions());
        services.AddSingleton<OllamaProviderOptions>(_ => new OllamaProviderOptions());
        services.AddSingleton<LmStudioProviderOptions>(_ => new LmStudioProviderOptions());
        services.AddSingleton<AnthropicProviderOptions>(_ => new AnthropicProviderOptions { DefaultModel = "claude-haiku-4-5" });
        services.AddSingleton<GeminiProviderOptions>(_ => new GeminiProviderOptions { DefaultModel = "gemini-2.5-flash" });

        // HttpClient per provider (each gets a typed client via AddHttpClient).
        services.AddHttpClient<OpenAIProvider>();
        services.AddHttpClient<AzureOpenAIProvider>();
        services.AddHttpClient<OpenAICompatibleEndpointProvider>();
        services.AddHttpClient<OllamaProvider>();
        services.AddHttpClient<LmStudioProvider>();
        services.AddHttpClient<AnthropicProvider>();
        services.AddHttpClient<GeminiProvider>();

        services.AddSingleton<ILlmProviderFactory, LlmProviderFactory>();

        return services;
    }
}
