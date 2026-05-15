using DistSharp.Core.Models;

namespace DistSharp.Core.Prompts;

/// <summary>Builds prompts for bug-fix rows.</summary>
public static class BugFixPromptBuilder
{
    /// <summary>Builds the prompt for <paramref name="symbol"/>.</summary>
    /// <param name="symbol">The symbol that will be transformed into a buggy/fixed pair.</param>
    /// <returns>A <see cref="PromptResult"/> containing messages and field metadata.</returns>
    public static PromptResult Build(ExtractedSymbol symbol)
    {
        var userPrompt = $"Original method:\n\n```csharp\n{symbol.BodyText}\n```";
        var messages = new ChatMessage[]
        {
            new(ChatRole.System, "You are a senior C# developer. Introduce one subtle, realistic bug into the method below, then provide the corrected version and a one-sentence explanation of the bug. Output JSON with keys: `buggy_code`, `fixed_code`, `explanation`. Do not wrap the JSON in markdown."),
            new(ChatRole.User, userPrompt),
        };

        return new PromptResult
        {
            Messages = messages,
            PreparedFields = new Dictionary<string, object?>
            {
                ["instruction"] = "The following C# method contains a bug. Identify and fix it.",
            },
            ParseResponse = raw => JsonResponseHelper.ParseRequiredStringKeys(raw, "buggy_code", "fixed_code", "explanation"),
        };
    }
}
