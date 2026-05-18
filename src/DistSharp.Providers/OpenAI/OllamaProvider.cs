using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace DistSharp.Providers.OpenAI;

/// <summary>
/// Provider for an Ollama server — local (default <c>http://localhost:11434</c>) or
/// cloud (set <c>OLLAMA_BASE_URL</c> to the cloud endpoint and provide an API key
/// via <c>OLLAMA_API_KEY</c>).
/// </summary>
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

        if (string.IsNullOrEmpty(options.ApiKey))
        {
            // Optional: only set when running against Ollama Cloud / a gateway that requires auth.
            options.ApiKey = Environment.GetEnvironmentVariable("OLLAMA_API_KEY");
        }
    }

    /// <inheritdoc/>
    public override string ProviderName => "ollama";

    /// <inheritdoc/>
    protected override void ApplyAuthentication(HttpRequestMessage request)
    {
        // Local Ollama is unauthenticated; cloud / gateway deployments use a bearer token.
        if (!string.IsNullOrEmpty(this.Options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", this.Options.ApiKey);
        }
    }
}
