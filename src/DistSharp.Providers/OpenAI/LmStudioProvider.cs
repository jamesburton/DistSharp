using Microsoft.Extensions.Logging;

namespace DistSharp.Providers.OpenAI;

/// <summary>Provider for a local LM Studio server.</summary>
public sealed class LmStudioProvider : OpenAICompatibleProvider
{
    /// <summary>Initializes a new instance of the <see cref="LmStudioProvider"/> class.</summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="options">The provider options.</param>
    /// <param name="logger">The logger.</param>
    public LmStudioProvider(HttpClient http, LmStudioProviderOptions options, ILogger<LmStudioProvider> logger)
        : base(http, options, logger)
    {
        if (string.IsNullOrEmpty(options.BaseUrl))
        {
            options.BaseUrl = Environment.GetEnvironmentVariable("LMSTUDIO_BASE_URL") ?? "http://localhost:1234";
        }
    }

    /// <inheritdoc/>
    public override string ProviderName => "lmstudio";

    /// <inheritdoc/>
    protected override void ApplyAuthentication(HttpRequestMessage request)
    {
        // LM Studio does not require authentication by default.
    }
}
