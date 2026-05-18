using DistSharp.Core.Abstractions;
using DistSharp.Providers.Anthropic;
using DistSharp.Providers.Gemini;
using DistSharp.Providers.Onnx;
using DistSharp.Providers.OpenAI;
using Microsoft.Extensions.DependencyInjection;

namespace DistSharp.Providers;

/// <summary>Resolves an <see cref="ILlmProvider"/> by canonical name from the DI container.</summary>
public sealed class LlmProviderFactory : ILlmProviderFactory
{
    private readonly IServiceProvider services;

    /// <summary>Initializes a new instance of the <see cref="LlmProviderFactory"/> class.</summary>
    /// <param name="services">The service provider used to resolve concrete provider instances.</param>
    public LlmProviderFactory(IServiceProvider services)
    {
        this.services = services;
    }

    /// <inheritdoc/>
    public ILlmProvider Create(string providerName)
    {
        return providerName.ToLowerInvariant() switch
        {
            "openai" => this.services.GetRequiredService<OpenAIProvider>(),
            "anthropic" => this.services.GetRequiredService<AnthropicProvider>(),
            "azure-openai" or "azure" => this.services.GetRequiredService<AzureOpenAIProvider>(),
            "gemini" or "google" => this.services.GetRequiredService<GeminiProvider>(),
            "ollama" => this.services.GetRequiredService<OllamaProvider>(),
            "lmstudio" or "lm-studio" => this.services.GetRequiredService<LmStudioProvider>(),
            "openai-compatible" or "compatible" => this.services.GetRequiredService<OpenAICompatibleEndpointProvider>(),
            "onnx" => this.services.GetRequiredService<OnnxProvider>(),
            _ => throw new InvalidOperationException($"Unknown LLM provider: '{providerName}'. Supported: openai, anthropic, azure-openai, gemini, ollama, lmstudio, openai-compatible, onnx."),
        };
    }
}
