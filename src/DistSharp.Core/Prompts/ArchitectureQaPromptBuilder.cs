using DistSharp.Core.Models;

namespace DistSharp.Core.Prompts;

/// <summary>Builds prompts for architecture-QA rows.</summary>
public static class ArchitectureQaPromptBuilder
{
    /// <summary>Builds the prompt for <paramref name="symbol"/>.</summary>
    /// <param name="symbol">The type whose role in the codebase will be asked about.</param>
    /// <returns>A <see cref="PromptResult"/> containing messages and field metadata.</returns>
    public static PromptResult Build(ExtractedSymbol symbol)
    {
        var userPrompt =
            $"Given this type from `{symbol.Namespace}`:\n\n" +
            $"```csharp\n{symbol.BodyText}\n```\n\n" +
            "Generate one architectural question and its answer about this type's role in the codebase. " +
            "Output JSON with keys: `question`, `answer`. Do not wrap the JSON in markdown.";

        var messages = new ChatMessage[]
        {
            new(ChatRole.System, "You are a senior .NET architect explaining how a codebase fits together. Answer questions about design patterns, composition, and structure with grounded references to the actual code."),
            new(ChatRole.User, userPrompt),
        };

        return new PromptResult
        {
            Messages = messages,
            PreparedFields = new Dictionary<string, object?>(),
            ParseResponse = raw => JsonResponseHelper.ParseRequiredStringKeys(raw, "question", "answer"),
        };
    }
}
