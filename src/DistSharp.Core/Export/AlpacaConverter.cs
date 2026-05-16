using System.Text.Json;

namespace DistSharp.Core.Export;

/// <summary>Converts a DistSharp row into Alpaca format (<c>instruction</c> / <c>input</c> / <c>output</c>).</summary>
public static class AlpacaConverter
{
    /// <summary>Converts <paramref name="row"/> to an Alpaca-format dictionary.</summary>
    /// <param name="row">A row read from a DistSharp JSONL dataset.</param>
    /// <returns>A dictionary with keys <c>instruction</c>, <c>input</c>, and <c>output</c>.</returns>
    public static Dictionary<string, object?> Convert(IReadOnlyDictionary<string, JsonElement> row)
    {
        var instruction = ConverterHelpers.GetString(row, "instruction") ?? string.Empty;
        var input = ConverterHelpers.GetString(row, "input", "context", "code", "original_code") ?? string.Empty;
        var output = ConverterHelpers.GetString(row, "output", "response", "answer", "completion") ?? string.Empty;

        if (string.IsNullOrEmpty(output))
        {
            var fixedCode = ConverterHelpers.GetString(row, "fixed_code");
            var explanation = ConverterHelpers.GetString(row, "explanation");
            if (fixedCode is not null)
            {
                output = explanation is null ? fixedCode : $"{fixedCode}\n\n{explanation}";
            }
        }

        if (string.IsNullOrEmpty(output))
        {
            var refactored = ConverterHelpers.GetString(row, "refactored_code");
            var explanation = ConverterHelpers.GetString(row, "explanation");
            if (refactored is not null)
            {
                output = explanation is null ? refactored : $"{refactored}\n\n{explanation}";
            }
        }

        if (string.IsNullOrEmpty(instruction))
        {
            var question = ConverterHelpers.GetString(row, "question");
            if (question is not null)
            {
                instruction = question;
            }
        }

        return new Dictionary<string, object?>
        {
            ["instruction"] = instruction,
            ["input"] = input,
            ["output"] = output,
        };
    }
}
