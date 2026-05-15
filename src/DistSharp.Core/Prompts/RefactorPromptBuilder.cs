using DistSharp.Core.Models;

namespace DistSharp.Core.Prompts;

/// <summary>Builds prompts for refactor rows.</summary>
public static class RefactorPromptBuilder
{
    /// <summary>Builds the prompt for <paramref name="symbol"/>.</summary>
    /// <param name="symbol">The high-complexity symbol to refactor.</param>
    /// <returns>A <see cref="PromptResult"/> containing messages and field metadata.</returns>
    public static PromptResult Build(ExtractedSymbol symbol)
    {
        var userPrompt = $"Refactor this method (current complexity: {symbol.Complexity}):\n\n```csharp\n{symbol.BodyText}\n```";
        var messages = new ChatMessage[]
        {
            new(ChatRole.System, "You are a senior C# developer refactoring complex code. Improve readability and reduce cyclomatic complexity without changing behaviour. Output JSON with keys: `refactored_code`, `explanation`. Do not wrap the JSON in markdown."),
            new(ChatRole.User, userPrompt),
        };

        return new PromptResult
        {
            Messages = messages,
            PreparedFields = new Dictionary<string, object?>
            {
                ["instruction"] = "Refactor the following C# method to improve readability and reduce cyclomatic complexity.",
                ["original_code"] = symbol.BodyText,
            },
            ParseResponse = raw => JsonResponseHelper.ParseRequiredStringKeys(raw, "refactored_code", "explanation"),
        };
    }
}
