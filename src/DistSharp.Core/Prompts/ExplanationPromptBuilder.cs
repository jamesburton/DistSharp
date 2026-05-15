using DistSharp.Core.Models;

namespace DistSharp.Core.Prompts;

/// <summary>Builds prompts for code-explanation rows.</summary>
public static class ExplanationPromptBuilder
{
    /// <summary>Builds the prompt for <paramref name="symbol"/>.</summary>
    /// <param name="symbol">The symbol being explained.</param>
    /// <returns>A <see cref="PromptResult"/> containing messages and field metadata.</returns>
    public static PromptResult Build(ExtractedSymbol symbol)
    {
        var instruction = $"Explain what the {symbol.Kind} `{symbol.FullyQualifiedName}` does. Provide your answer in 2–4 sentences focused on intent and behaviour, not line-by-line.";
        var context = $"```csharp\n{symbol.BodyText}\n```";
        var messages = new ChatMessage[]
        {
            new(ChatRole.System, "You are a senior .NET engineer explaining code to a capable colleague. Be precise, reference type names, and mention important edge cases."),
            new(ChatRole.User, instruction + "\n\nCode:\n" + context),
        };

        return new PromptResult
        {
            Messages = messages,
            PreparedFields = new Dictionary<string, object?>
            {
                ["instruction"] = instruction,
                ["context"] = symbol.BodyText,
            },
            ParseResponse = raw => new Dictionary<string, object?> { ["response"] = raw.Trim() },
        };
    }
}
