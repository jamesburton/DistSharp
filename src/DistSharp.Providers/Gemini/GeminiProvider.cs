using System.Text;
using System.Text.Json.Nodes;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;
using DistSharp.Providers.Internal;
using Microsoft.Extensions.Logging;

namespace DistSharp.Providers.Gemini;

/// <summary>Provider for the Google Gemini API.</summary>
public sealed class GeminiProvider : ILlmProvider
{
    private readonly HttpClient http;
    private readonly GeminiProviderOptions options;
    private readonly ILogger<GeminiProvider> logger;

    /// <summary>Initializes a new instance of the <see cref="GeminiProvider"/> class.</summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="options">Provider options.</param>
    /// <param name="logger">The logger.</param>
    public GeminiProvider(HttpClient http, GeminiProviderOptions options, ILogger<GeminiProvider> logger)
    {
        this.http = http;
        this.options = options;
        this.logger = logger;
        if (string.IsNullOrEmpty(options.BaseUrl))
        {
            options.BaseUrl = "https://generativelanguage.googleapis.com";
        }

        if (options.Timeout > TimeSpan.Zero)
        {
            this.http.Timeout = options.Timeout;
        }
    }

    /// <inheritdoc/>
    public string ProviderName => "gemini";

    /// <inheritdoc/>
    public async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, LlmRequestOptions options, CancellationToken cancellationToken)
    {
        var baseUrl = this.options.BaseUrl?.TrimEnd('/') ?? throw new InvalidOperationException("Gemini: BaseUrl is not configured.");
        var model = options.Model ?? this.options.DefaultModel ?? throw new InvalidOperationException("Gemini: model is not specified.");
        var key = this.options.ApiKey ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrEmpty(key))
        {
            throw new InvalidOperationException("Gemini: GEMINI_API_KEY is not set.");
        }

        var url = $"{baseUrl}/v1beta/models/{model}:generateContent?key={key}";
        var body = BuildRequestBody(messages, options);

        HttpRequestMessage Build()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            return req;
        }

        using var response = await HttpRetryHelper.SendWithRetryAsync(
            this.http, Build, this.options.MaxRetries, this.options.InitialRetryDelay, this.logger, cancellationToken).ConfigureAwait(false);

        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var apiMessage = ProviderErrorParser.ExtractMessage(raw) ?? response.ReasonPhrase ?? "(no message)";
            var hint = ProviderErrorParser.IsModelNotFound((int)response.StatusCode, raw)
                ? " — run 'distsharp models --provider gemini' to list available models."
                : string.Empty;
            throw new LlmProviderException(
                this.ProviderName,
                $"HTTP {(int)response.StatusCode} from Gemini: {apiMessage}{hint}",
                (int)response.StatusCode,
                raw);
        }

        var json = JsonNode.Parse(raw) ?? throw new LlmProviderException(this.ProviderName, "Empty body", (int)response.StatusCode, raw);
        var text = json["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>();
        if (text is null)
        {
            throw new LlmProviderException(this.ProviderName, "Response did not contain candidates[0].content.parts[0].text", (int)response.StatusCode, raw);
        }

        return text;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var baseUrl = this.options.BaseUrl?.TrimEnd('/') ?? throw new InvalidOperationException("Gemini: BaseUrl is not configured.");
        var key = this.options.ApiKey ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrEmpty(key))
        {
            throw new InvalidOperationException("Gemini: GEMINI_API_KEY is not set.");
        }

        var url = $"{baseUrl}/v1beta/models?key={key}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        using var response = await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var apiMessage = ProviderErrorParser.ExtractMessage(raw) ?? response.ReasonPhrase ?? "(no message)";
            throw new LlmProviderException(
                this.ProviderName,
                $"HTTP {(int)response.StatusCode} listing models from Gemini: {apiMessage}",
                (int)response.StatusCode,
                raw);
        }

        var json = JsonNode.Parse(raw);
        if (json?["models"] is not JsonArray models)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>(models.Count);
        foreach (var item in models)
        {
            var name = item?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            // Gemini returns names like "models/gemini-2.5-flash" — strip the prefix.
            const string prefix = "models/";
            var trimmed = name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
            result.Add(trimmed);
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static JsonObject BuildRequestBody(IReadOnlyList<ChatMessage> messages, LlmRequestOptions options)
    {
        var systemBuilder = new StringBuilder();
        var contents = new JsonArray();

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

            contents.Add(new JsonObject
            {
                ["role"] = m.Role == ChatRole.Assistant ? "model" : "user",
                ["parts"] = new JsonArray { new JsonObject { ["text"] = m.Content } },
            });
        }

        var generationConfig = new JsonObject();
        if (options.Temperature is { } t)
        {
            generationConfig["temperature"] = t;
        }

        if (options.MaxTokens is { } mt)
        {
            generationConfig["maxOutputTokens"] = mt;
        }

        var body = new JsonObject
        {
            ["contents"] = contents,
        };

        if (systemBuilder.Length > 0)
        {
            body["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = systemBuilder.ToString() } },
            };
        }

        if (generationConfig.Count > 0)
        {
            body["generationConfig"] = generationConfig;
        }

        return body;
    }
}
