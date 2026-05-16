using System.Text.Json;

namespace DistSharp.Core.Export;

/// <summary>Converts a DistSharp row into ShareGPT format (<c>conversations</c> array).</summary>
public static class ShareGptConverter
{
    /// <summary>Converts <paramref name="row"/> to a ShareGPT-format dictionary.</summary>
    /// <param name="row">A row read from a DistSharp JSONL dataset.</param>
    /// <returns>A dictionary with a <c>conversations</c> array containing human and gpt turns.</returns>
    public static Dictionary<string, object?> Convert(IReadOnlyDictionary<string, JsonElement> row)
    {
        var instruction = ConverterHelpers.GetString(row, "instruction", "question") ?? string.Empty;
        var input = ConverterHelpers.GetString(row, "input", "context", "code", "original_code");
        var human = string.IsNullOrEmpty(input) ? instruction : $"{instruction}\n\n{input}";

        var gpt = ConverterHelpers.GetString(row, "output", "response", "answer", "completion") ?? string.Empty;
        if (string.IsNullOrEmpty(gpt))
        {
            var fixedCode = ConverterHelpers.GetString(row, "fixed_code");
            var explanation = ConverterHelpers.GetString(row, "explanation");
            if (fixedCode is not null)
            {
                gpt = explanation is null ? fixedCode : $"{fixedCode}\n\n{explanation}";
            }
        }

        if (string.IsNullOrEmpty(gpt))
        {
            var refactored = ConverterHelpers.GetString(row, "refactored_code");
            var explanation = ConverterHelpers.GetString(row, "explanation");
            if (refactored is not null)
            {
                gpt = explanation is null ? refactored : $"{refactored}\n\n{explanation}";
            }
        }

        return new Dictionary<string, object?>
        {
            ["conversations"] = new[]
            {
                new Dictionary<string, object?> { ["from"] = "human", ["value"] = human },
                new Dictionary<string, object?> { ["from"] = "gpt", ["value"] = gpt },
            },
        };
    }
}
