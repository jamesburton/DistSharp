using System.Text.Json.Nodes;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;
using DistSharp.Providers.Internal;
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
    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var baseUrl = this.Options.BaseUrl?.TrimEnd('/') ?? throw new InvalidOperationException("Azure OpenAI: AZURE_OPENAI_ENDPOINT is not configured.");
        var url = $"{baseUrl}/openai/models?api-version={ApiVersion}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        this.ApplyAuthentication(request);

        using var response = await this.Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var apiMessage = ProviderErrorParser.ExtractMessage(raw) ?? response.ReasonPhrase ?? "(no message)";
            throw new LlmProviderException(
                this.ProviderName,
                $"HTTP {(int)response.StatusCode} listing models from Azure OpenAI: {apiMessage}",
                (int)response.StatusCode,
                raw);
        }

        var json = JsonNode.Parse(raw);
        if (json?["data"] is not JsonArray data)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>(data.Count);
        foreach (var item in data)
        {
            var id = item?["id"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id))
            {
                result.Add(id);
            }
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

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
