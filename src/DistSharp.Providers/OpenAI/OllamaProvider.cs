using Microsoft.Extensions.Logging;

namespace DistSharp.Providers.OpenAI;

/// <summary>Provider for a local Ollama server.</summary>
public sealed class OllamaProvider : OpenAICompatibleProvider
{
    /// <summary>Initializes a new instance of the <see cref="OllamaProvider"/> class.</summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="options">The provider options.</param>
    /// <param name="logger">The logger.</param>
    public OllamaProvider(HttpClient http, OllamaProviderOptions options, ILogger<OllamaProvider> logger)
        : base(http, options, logger)
    {
        if (string.IsNullOrEmpty(options.BaseUrl))
        {
            options.BaseUrl = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? "http://localhost:11434";
        }
    }

    /// <inheritdoc/>
    public override string ProviderName => "ollama";

    /// <inheritdoc/>
    protected override void ApplyAuthentication(HttpRequestMessage request)
    {
        // Ollama does not require authentication.
    }
}
