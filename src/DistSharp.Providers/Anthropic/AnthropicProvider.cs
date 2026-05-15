using System.Text;
using System.Text.Json.Nodes;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;
using DistSharp.Providers.Internal;
using Microsoft.Extensions.Logging;

namespace DistSharp.Providers.Anthropic;

/// <summary>Provider for the Anthropic Messages API.</summary>
public sealed class AnthropicProvider : ILlmProvider
{
    private const string AnthropicVersionHeader = "2023-06-01";

    private readonly HttpClient http;
    private readonly AnthropicProviderOptions options;
    private readonly ILogger<AnthropicProvider> logger;

    /// <summary>Initializes a new instance of the <see cref="AnthropicProvider"/> class.</summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="options">Provider options.</param>
    /// <param name="logger">The logger.</param>
    public AnthropicProvider(HttpClient http, AnthropicProviderOptions options, ILogger<AnthropicProvider> logger)
    {
        this.http = http;
        this.options = options;
        this.logger = logger;
        if (string.IsNullOrEmpty(options.BaseUrl))
        {
            options.BaseUrl = "https://api.anthropic.com";
        }

        if (options.Timeout > TimeSpan.Zero)
        {
            this.http.Timeout = options.Timeout;
        }
    }

    /// <inheritdoc/>
    public string ProviderName => "anthropic";

    /// <inheritdoc/>
    public async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, LlmRequestOptions options, CancellationToken cancellationToken)
    {
        var baseUrl = this.options.BaseUrl?.TrimEnd('/') ?? throw new InvalidOperationException("Anthropic: BaseUrl is not configured.");
        var url = baseUrl + "/v1/messages";
        var body = this.BuildRequestBody(messages, options);

        HttpRequestMessage Build()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            var key = this.options.ApiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            if (!string.IsNullOrEmpty(key))
            {
                req.Headers.Add("x-api-key", key);
            }

            req.Headers.Add("anthropic-version", AnthropicVersionHeader);
            return req;
        }

        using var response = await HttpRetryHelper.SendWithRetryAsync(
            this.http, Build, this.options.MaxRetries, this.options.InitialRetryDelay, this.logger, cancellationToken).ConfigureAwait(false);

        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new LlmProviderException(
                this.ProviderName,
                $"HTTP {(int)response.StatusCode} from Anthropic: {response.ReasonPhrase}",
                (int)response.StatusCode,
                raw);
        }

        var json = JsonNode.Parse(raw) ?? throw new LlmProviderException(this.ProviderName, "Empty body", (int)response.StatusCode, raw);
        var text = json["content"]?[0]?["text"]?.GetValue<string>();
        if (text is null)
        {
            throw new LlmProviderException(this.ProviderName, "Response did not contain content[0].text", (int)response.StatusCode, raw);
        }

        return text;
    }

    private JsonObject BuildRequestBody(IReadOnlyList<ChatMessage> messages, LlmRequestOptions options)
    {
        // Anthropic separates system messages into a top-level `system` field.
        var systemBuilder = new StringBuilder();
        var conversation = new JsonArray();

        foreach (var m in messages)
        {
            if (m.Role == ChatRole.System)
            {
                if (systemBuilder.Length > 0)
                {
                    systemBuilder.AppendLine();
                }

                systemBuilder.Append(m.Content);
                continue;
            }

            conversation.Add(new JsonObject
            {
                ["role"] = m.Role == ChatRole.Assistant ? "assistant" : "user",
                ["content"] = m.Content,
            });
        }

        var body = new JsonObject
        {
            ["model"] = options.Model ?? this.options.DefaultModel ?? throw new InvalidOperationException("Anthropic: model is not specified."),
            ["messages"] = conversation,
            ["max_tokens"] = options.MaxTokens ?? 4096,
        };

        if (systemBuilder.Length > 0)
        {
            body["system"] = systemBuilder.ToString();
        }

        if (options.Temperature is { } t)
        {
            body["temperature"] = t;
        }

        return body;
    }
}
