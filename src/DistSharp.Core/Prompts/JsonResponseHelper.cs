using System.Text.Json;

namespace DistSharp.Core.Prompts;

/// <summary>Helpers for parsing JSON responses from LLM prompt outputs.</summary>
internal static class JsonResponseHelper
{
    /// <summary>Parses <paramref name="raw"/> as a JSON object and returns a dictionary with the requested string keys, or <see langword="null"/> on any failure.</summary>
    /// <param name="raw">The raw LLM response string.</param>
    /// <param name="requiredKeys">The keys that must be present as string values.</param>
    /// <returns>A dictionary of the required keys and their string values, or <see langword="null"/> if parsing fails.</returns>
    public static IReadOnlyDictionary<string, object?>? ParseRequiredStringKeys(string raw, params string[] requiredKeys)
    {
        try
        {
            var trimmed = StripCodeFences(raw);
            var json = JsonDocument.Parse(trimmed).RootElement;
            if (json.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var result = new Dictionary<string, object?>();
            foreach (var key in requiredKeys)
            {
                if (!json.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                result[key] = value.GetString();
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string StripCodeFences(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0)
            {
                trimmed = trimmed[(firstNewline + 1)..];
            }

            if (trimmed.EndsWith("```", StringComparison.Ordinal))
            {
                trimmed = trimmed[..^3];
            }
        }

        return trimmed.Trim();
    }
}
