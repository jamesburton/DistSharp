using System.Text;
using System.Text.Json.Nodes;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;
using DistSharp.Providers.Internal;
using Microsoft.Extensions.Logging;

namespace DistSharp.Providers.OpenAI;

/// <summary>
/// Base class for providers that speak the OpenAI Chat Completions protocol.
/// Subclasses customise base URL, authentication, and (rarely) request shape.
/// </summary>
public abstract class OpenAICompatibleProvider : ILlmProvider
{
    /// <summary>Initializes a new instance of the <see cref="OpenAICompatibleProvider"/> class.</summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="options">The provider options.</param>
    /// <param name="logger">The logger.</param>
    protected OpenAICompatibleProvider(HttpClient http, LlmProviderOptions options, ILogger logger)
    {
        this.Http = http;
        this.Options = options;
        this.Logger = logger;
        if (options.Timeout > TimeSpan.Zero)
        {
            this.Http.Timeout = options.Timeout;
        }
    }

    /// <inheritdoc/>
    public abstract string ProviderName { get; }

    /// <summary>Gets the shared <see cref="HttpClient"/> used for HTTP requests.</summary>
    protected HttpClient Http { get; }

    /// <summary>Gets the provider's options.</summary>
    protected LlmProviderOptions Options { get; }

    /// <summary>Gets the logger.</summary>
    protected ILogger Logger { get; }

    /// <summary>Gets the path used for chat completion requests, relative to the base URL.</summary>
    protected virtual string ChatCompletionsPath => "/v1/chat/completions";

    /// <inheritdoc/>
    public async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, LlmRequestOptions options, CancellationToken cancellationToken)
    {
        var body = this.BuildRequestBody(messages, options);
        var url = this.BuildRequestUrl(options);

        HttpRequestMessage Build()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            this.ApplyAuthentication(req);
            return req;
        }

        using var response = await HttpRetryHelper.SendWithRetryAsync(
            this.Http, Build, this.Options.MaxRetries, this.Options.InitialRetryDelay, this.Logger, cancellationToken).ConfigureAwait(false);

        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new LlmProviderException(
                this.ProviderName,
                $"HTTP {(int)response.StatusCode} from {this.ProviderName}: {response.ReasonPhrase}",
                (int)response.StatusCode,
                raw);
        }

        var json = JsonNode.Parse(raw);
        if (json is null)
        {
            throw new LlmProviderException(this.ProviderName, "Provider returned empty body", (int)response.StatusCode, raw);
        }

        return this.ExtractContent(json);
    }

    /// <summary>Maps <see cref="ChatRole"/> to the OpenAI role string.</summary>
    /// <param name="role">The chat role to convert.</param>
    /// <returns>The OpenAI role string, e.g. <c>"user"</c>.</returns>
    protected static string RoleToString(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    /// <summary>Adds authentication headers (or query string) to <paramref name="request"/>.</summary>
    /// <param name="request">The outgoing HTTP request to authenticate.</param>
    protected abstract void ApplyAuthentication(HttpRequestMessage request);

    /// <summary>Builds the request URL. Most providers concatenate <see cref="LlmProviderOptions.BaseUrl"/> and <see cref="ChatCompletionsPath"/>.</summary>
    /// <param name="options">Per-request options that may influence the URL (e.g. Azure deployment name).</param>
    /// <returns>The fully-qualified URL string for the chat completions endpoint.</returns>
    protected virtual string BuildRequestUrl(LlmRequestOptions options)
    {
        var baseUrl = this.Options.BaseUrl?.TrimEnd('/') ?? throw new InvalidOperationException($"{this.ProviderName}: BaseUrl is not configured.");
        return baseUrl + this.ChatCompletionsPath;
    }

    /// <summary>Builds the JSON request body. Override to customise the schema.</summary>
    /// <param name="messages">The conversation turns to include in the request.</param>
    /// <param name="options">Per-request options such as model and temperature.</param>
    /// <returns>A <see cref="JsonObject"/> representing the request body.</returns>
    protected virtual JsonObject BuildRequestBody(IReadOnlyList<ChatMessage> messages, LlmRequestOptions options)
    {
        var msgs = new JsonArray();
        foreach (var m in messages)
        {
            msgs.Add(new JsonObject
            {
                ["role"] = RoleToString(m.Role),
                ["content"] = m.Content,
            });
        }

        var body = new JsonObject
        {
            ["model"] = options.Model ?? this.Options.DefaultModel ?? throw new InvalidOperationException($"{this.ProviderName}: model is not specified."),
            ["messages"] = msgs,
        };

        if (options.Temperature is { } t)
        {
            body["temperature"] = t;
        }

        if (options.MaxTokens is { } mt)
        {
            body["max_tokens"] = mt;
        }

        return body;
    }

    /// <summary>Extracts the completion text from the response JSON. Default: <c>choices[0].message.content</c>.</summary>
    /// <param name="response">The parsed JSON response from the provider.</param>
    /// <returns>The completion text.</returns>
    protected virtual string ExtractContent(JsonNode response)
    {
        var content = response["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        if (content is null)
        {
            throw new LlmProviderException(this.ProviderName, "Response did not contain choices[0].message.content", null, response.ToJsonString());
        }

        return content;
    }
}
