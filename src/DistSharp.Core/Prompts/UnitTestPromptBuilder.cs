using DistSharp.Core.Models;

namespace DistSharp.Core.Prompts;

/// <summary>Builds prompts for unit-test rows.</summary>
public static class UnitTestPromptBuilder
{
    /// <summary>Builds the prompt for <paramref name="symbol"/>.</summary>
    /// <param name="symbol">The symbol a test will be written for.</param>
    /// <returns>A <see cref="PromptResult"/> containing messages and field metadata.</returns>
    public static PromptResult Build(ExtractedSymbol symbol)
    {
        var instruction = $"Write an xUnit unit test for `{symbol.FullyQualifiedName}` covering an interesting case or behaviour.";
        var userPrompt =
            $"Write a unit test for `{symbol.FullyQualifiedName}`:\n\n" +
            $"```csharp\n{symbol.SignatureText}\n```\n\n" +
            $"Full method body for context:\n```csharp\n{symbol.BodyText}\n```";

        var messages = new ChatMessage[]
        {
            new(ChatRole.System, "You are a senior C# developer. Write a single xUnit unit test for the method below, using NSubstitute for mocking dependencies. Focus on one specific behaviour or edge case. Output only the test method, including any required setup, inside an outer class declaration."),
            new(ChatRole.User, userPrompt),
        };

        return new PromptResult
        {
            Messages = messages,
            PreparedFields = new Dictionary<string, object?>
            {
                ["instruction"] = instruction,
                ["context"] = symbol.SignatureText,
            },
            ParseResponse = raw => new Dictionary<string, object?> { ["response"] = raw.Trim() },
        };
    }
}
