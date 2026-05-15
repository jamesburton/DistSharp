using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace DistSharp.Providers.OpenAI;

/// <summary>Provider for OpenAI's Chat Completions API.</summary>
public sealed class OpenAIProvider : OpenAICompatibleProvider
{
    /// <summary>Initializes a new instance of the <see cref="OpenAIProvider"/> class.</summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="options">The provider options.</param>
    /// <param name="logger">The logger.</param>
    public OpenAIProvider(HttpClient http, OpenAIProviderOptions options, ILogger<OpenAIProvider> logger)
        : base(http, options, logger)
    {
        if (string.IsNullOrEmpty(options.BaseUrl))
        {
            options.BaseUrl = "https://api.openai.com";
        }
    }

    /// <inheritdoc/>
    public override string ProviderName => "openai";

    /// <inheritdoc/>
    protected override void ApplyAuthentication(HttpRequestMessage request)
    {
        var key = this.Options.ApiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }
}
