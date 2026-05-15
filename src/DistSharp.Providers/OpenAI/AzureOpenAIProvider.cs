using System.Text.Json.Nodes;
using DistSharp.Core.Models;
using Microsoft.Extensions.Logging;

namespace DistSharp.Providers.OpenAI;

/// <summary>Provider for Azure OpenAI deployments.</summary>
public sealed class AzureOpenAIProvider : OpenAICompatibleProvider
{
    private const string ApiVersion = "2024-08-01-preview";

    /// <summary>Initializes a new instance of the <see cref="AzureOpenAIProvider"/> class.</summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="options">The provider options.</param>
    /// <param name="logger">The logger.</param>
    public AzureOpenAIProvider(HttpClient http, AzureOpenAIProviderOptions options, ILogger<AzureOpenAIProvider> logger)
        : base(http, options, logger)
    {
        if (string.IsNullOrEmpty(options.BaseUrl))
        {
            options.BaseUrl = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        }
    }

    /// <inheritdoc/>
    public override string ProviderName => "azure-openai";

    /// <inheritdoc/>
    protected override string BuildRequestUrl(LlmRequestOptions options)
    {
        var baseUrl = this.Options.BaseUrl?.TrimEnd('/') ?? throw new InvalidOperationException("Azure OpenAI: AZURE_OPENAI_ENDPOINT is not configured.");
        var deployment = options.Model ?? this.Options.DefaultModel ?? throw new InvalidOperationException("Azure OpenAI: model (deployment name) is not specified.");
        return $"{baseUrl}/openai/deployments/{deployment}/chat/completions?api-version={ApiVersion}";
    }

    /// <inheritdoc/>
    protected override JsonObject BuildRequestBody(IReadOnlyList<ChatMessage> messages, LlmRequestOptions options)
    {
        // Azure OpenAI uses deployment name in URL; do not include `model` in body.
        var body = base.BuildRequestBody(messages, options);
        body.Remove("model");
        return body;
    }

    /// <inheritdoc/>
    protected override void ApplyAuthentication(HttpRequestMessage request)
    {
        var key = this.Options.ApiKey ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Add("api-key", key);
        }
    }
}
