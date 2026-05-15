using DistSharp.Core.Models;

namespace DistSharp.Core.Prompts;

/// <summary>Resolves a prompt builder by dataset-type name and produces a <see cref="PromptResult"/>.</summary>
public static class PromptBuilderRegistry
{
    /// <summary>Gets the set of supported dataset-type names.</summary>
    public static IReadOnlyList<string> SupportedTypes { get; } = new[]
    {
        "explanation", "completion", "bug-fix", "unit-test", "docstring", "refactor", "architecture-qa",
    };

    /// <summary>Returns the prompt result for the given <paramref name="datasetType"/> and <paramref name="symbol"/>.</summary>
    /// <param name="datasetType">The dataset type identifier (e.g. <c>explanation</c>, <c>unit-test</c>).</param>
    /// <param name="symbol">The symbol being prompted about.</param>
    /// <returns>A <see cref="PromptResult"/> with messages and field metadata for the dataset type.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the dataset type is not recognised.</exception>
    public static PromptResult Build(string datasetType, ExtractedSymbol symbol)
    {
        return datasetType.ToLowerInvariant() switch
        {
            "explanation" => ExplanationPromptBuilder.Build(symbol),
            "completion" => CompletionPromptBuilder.Build(symbol),
            "bug-fix" or "bugfix" => BugFixPromptBuilder.Build(symbol),
            "unit-test" or "unittest" => UnitTestPromptBuilder.Build(symbol),
            "docstring" => DocstringPromptBuilder.Build(symbol),
            "refactor" => RefactorPromptBuilder.Build(symbol),
            "architecture-qa" or "architectureqa" => ArchitectureQaPromptBuilder.Build(symbol),
            _ => throw new InvalidOperationException($"Unknown dataset type: '{datasetType}'. Supported: explanation, completion, bug-fix, unit-test, docstring, refactor, architecture-qa."),
        };
    }
}
