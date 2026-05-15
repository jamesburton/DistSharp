using DistSharp.Core.Models;

namespace DistSharp.Core.Prompts;

/// <summary>Builds prompts for docstring rows.</summary>
public static class DocstringPromptBuilder
{
    /// <summary>Builds the prompt for <paramref name="symbol"/>.</summary>
    /// <param name="symbol">The symbol that needs documentation.</param>
    /// <returns>A <see cref="PromptResult"/> containing messages and field metadata.</returns>
    public static PromptResult Build(ExtractedSymbol symbol)
    {
        var instruction = "Write XML documentation comments for the following C# method.";
        var userPrompt = $"Write XML documentation comments for:\n\n```csharp\n{symbol.SignatureText}\n```";

        var messages = new ChatMessage[]
        {
            new(ChatRole.System, "You are a senior .NET engineer writing XML documentation comments. Output only valid `///` doc comments — no surrounding code, no markdown fences."),
            new(ChatRole.User, userPrompt),
        };

        return new PromptResult
        {
            Messages = messages,
            PreparedFields = new Dictionary<string, object?>
            {
                ["instruction"] = instruction,
                ["code"] = symbol.SignatureText,
            },
            ParseResponse = raw => new Dictionary<string, object?> { ["response"] = raw.Trim() },
        };
    }
}
