using DistSharp.Core.Models;

namespace DistSharp.Core.Prompts;

/// <summary>Builds prompts for code-completion rows.</summary>
public static class CompletionPromptBuilder
{
    /// <summary>Builds the prompt for <paramref name="symbol"/>.</summary>
    /// <param name="symbol">The symbol being split into prompt + completion.</param>
    /// <returns>A <see cref="PromptResult"/> containing messages and field metadata.</returns>
    public static PromptResult Build(ExtractedSymbol symbol)
    {
        var (prefix, _) = SplitAtMidpoint(symbol.BodyText);
        var promptText = $"Complete the following C# method body:\n\n```csharp\n{prefix}\n```";

        var messages = new ChatMessage[]
        {
            new(ChatRole.System, "You are a senior C# developer. Complete the partial method below following the patterns visible in the codebase. Output only the completion — no explanation, no surrounding code, no markdown fences."),
            new(ChatRole.User, promptText),
        };

        return new PromptResult
        {
            Messages = messages,
            PreparedFields = new Dictionary<string, object?>
            {
                ["prompt"] = promptText,
            },
            ParseResponse = raw => new Dictionary<string, object?>
            {
                ["completion"] = raw.Trim().TrimStart('`').TrimEnd('`'),
            },
        };
    }

    private static (string Prefix, string Suffix) SplitAtMidpoint(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return (string.Empty, string.Empty);
        }

        var midpoint = body.Length / 2;

        // Walk forward to the next newline so the split lands on a clean boundary.
        var split = body.IndexOf('\n', midpoint);
        if (split < 0 || split == body.Length - 1)
        {
            split = midpoint;
        }

        return (body[..split], body[split..]);
    }
}
