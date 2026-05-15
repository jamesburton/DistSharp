using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace DistSharp.Providers.OpenAI;

/// <summary>Provider for arbitrary OpenAI-compatible endpoints (Together AI, Fireworks, OpenRouter, etc.).</summary>
public sealed class OpenAICompatibleEndpointProvider : OpenAICompatibleProvider
{
    /// <summary>Initializes a new instance of the <see cref="OpenAICompatibleEndpointProvider"/> class.</summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="options">The provider options.</param>
    /// <param name="logger">The logger.</param>
    public OpenAICompatibleEndpointProvider(HttpClient http, OpenAICompatibleEndpointOptions options, ILogger<OpenAICompatibleEndpointProvider> logger)
        : base(http, options, logger)
    {
        if (string.IsNullOrEmpty(options.BaseUrl))
        {
            options.BaseUrl = Environment.GetEnvironmentVariable("OPENAI_COMPATIBLE_BASE_URL");
        }
    }

    /// <inheritdoc/>
    public override string ProviderName => "openai-compatible";

    /// <inheritdoc/>
    protected override void ApplyAuthentication(HttpRequestMessage request)
    {
        var key = this.Options.ApiKey ?? Environment.GetEnvironmentVariable("OPENAI_COMPATIBLE_API_KEY");
        if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }
}
