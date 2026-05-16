using System.Text.Json;

namespace DistSharp.Core.Export;

/// <summary>Shared helpers for the format converters.</summary>
internal static class ConverterHelpers
{
    /// <summary>Returns the first key whose value is a non-empty JSON string, or <see langword="null"/>.</summary>
    /// <param name="row">The row to search.</param>
    /// <param name="keys">Keys to try in order.</param>
    /// <returns>The first non-empty string value found, or <see langword="null"/>.</returns>
    public static string? GetString(IReadOnlyDictionary<string, JsonElement> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!row.TryGetValue(key, out var value))
            {
                continue;
            }

            if (value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var s = value.GetString();
            if (!string.IsNullOrEmpty(s))
            {
                return s;
            }
        }

        return null;
    }
}
