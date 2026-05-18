using System.Text.Json;
using System.Text.Json.Nodes;

namespace DistSharp.Providers.Internal;

/// <summary>Extracts human-readable messages from provider error response bodies.</summary>
internal static class ProviderErrorParser
{
    /// <summary>Tries to extract a useful message from a provider error JSON body.</summary>
    /// <param name="raw">The raw response body.</param>
    /// <returns>A short, single-line message, or <see langword="null"/> if nothing extractable.</returns>
    public static string? ExtractMessage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            var root = JsonNode.Parse(raw);
            if (root is not JsonObject rootObj)
            {
                return ShortBody(raw);
            }

            // OpenAI / Azure OpenAI / OpenAI-compatible / Anthropic / Gemini all use
            //   { "error": { "message": "..." } }
            if (rootObj.TryGetPropertyValue("error", out var errorNode))
            {
                if (errorNode is JsonObject errorObj &&
                    errorObj.TryGetPropertyValue("message", out var messageNode) &&
                    messageNode is JsonValue messageVal &&
                    messageVal.TryGetValue<string>(out var message) &&
                    !string.IsNullOrWhiteSpace(message))
                {
                    return Trim(message);
                }

                // Ollama: { "error": "model 'foo' not found" }
                if (errorNode is JsonValue errorVal &&
                    errorVal.TryGetValue<string>(out var ollamaMessage) &&
                    !string.IsNullOrWhiteSpace(ollamaMessage))
                {
                    return Trim(ollamaMessage);
                }
            }

            // Some servers return a plain string at the root, or { "message": "..." } directly.
            if (rootObj.TryGetPropertyValue("message", out var topMessageNode) &&
                topMessageNode is JsonValue topMessageVal &&
                topMessageVal.TryGetValue<string>(out var topMessage) &&
                !string.IsNullOrWhiteSpace(topMessage))
            {
                return Trim(topMessage);
            }

            return ShortBody(raw);
        }
        catch (JsonException)
        {
            return ShortBody(raw);
        }
    }

    /// <summary>Returns <see langword="true"/> when the response indicates an invalid model name.</summary>
    /// <param name="statusCode">HTTP status code.</param>
    /// <param name="raw">The raw response body.</param>
    public static bool IsModelNotFound(int statusCode, string? raw)
    {
        if (statusCode != 404 && statusCode != 400)
        {
            return false;
        }

        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        // Most providers signal the error via a code field or by mentioning the model in the message.
        return raw.Contains("model_not_found", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("invalid_model", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("model not found", StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string message)
    {
        var first = message.IndexOf('\n');
        return first < 0 ? message.Trim() : message[..first].Trim();
    }

    private static string? ShortBody(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Length <= 200 ? raw.Trim() : raw[..200].Trim() + "…";
    }
}
