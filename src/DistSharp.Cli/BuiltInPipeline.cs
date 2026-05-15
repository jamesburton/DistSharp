using DistSharp.Core.Configuration;

namespace DistSharp.Cli;

/// <summary>Builds a default <see cref="PipelineConfig"/> for the <c>generate</c> command from CLI flags.</summary>
internal static class BuiltInPipeline
{
    /// <summary>Creates a 3-step pipeline (extract → sample → llm) tuned for the given options.</summary>
    /// <param name="solutionPath">Path to the solution, project, or directory to analyse.</param>
    /// <param name="datasetType">The dataset type to generate.</param>
    /// <param name="maxRows">Maximum number of rows to generate.</param>
    /// <param name="outDir">Output directory for generated data.</param>
    /// <param name="outputFormat">Output file format (e.g. <c>jsonl</c>, <c>parquet</c>).</param>
    /// <param name="providerName">LLM provider name.</param>
    /// <param name="model">LLM model identifier, or <see langword="null"/> to use the provider default.</param>
    /// <param name="workers">Number of parallel LLM workers.</param>
    /// <param name="includeTests">Whether to include test projects.</param>
    /// <param name="includeGenerated">Whether to include generated files.</param>
    /// <param name="minComplexity">Minimum cyclomatic complexity threshold.</param>
    /// <param name="excludeNamespaces">Namespace prefixes to exclude.</param>
    /// <param name="seed">Random seed for reproducible sampling, or <see langword="null"/> for non-deterministic.</param>
    /// <param name="dryRun">When <see langword="true"/>, the LLM step is omitted (extraction and sampling only).</param>
    /// <returns>A <see cref="PipelineConfig"/> ready for <see cref="PipelineBuilder"/>.</returns>
    public static PipelineConfig Create(
        string solutionPath,
        string datasetType,
        int maxRows,
        string outDir,
        string outputFormat,
        string providerName,
        string? model,
        int workers,
        bool includeTests,
        bool includeGenerated,
        int minComplexity,
        IReadOnlyList<string> excludeNamespaces,
        int? seed,
        bool dryRun)
    {
        // For "mixed" we currently emit one LlmStep producing explanation rows; richer fan-out is a follow-up.
        var effectiveDatasetType = datasetType.Equals("mixed", StringComparison.OrdinalIgnoreCase) ? "explanation" : datasetType;

        var steps = new List<StepConfig>
        {
            new()
            {
                Name = "extract_symbols",
                Type = "RoslynSymbolExtractor",
                Config = new Dictionary<string, object?>(),
            },
            new()
            {
                Name = "sample",
                Type = "StratifiedSampler",
                DependsOn = new List<string> { "extract_symbols" },
                Config = new Dictionary<string, object?>
                {
                    ["max_rows"] = maxRows,
                    ["strategy"] = "complexity_weighted",
                    ["seed"] = seed,
                },
            },
        };

        if (!dryRun)
        {
            steps.Add(new StepConfig
            {
                Name = "generate",
                Type = "LlmStep",
                DependsOn = new List<string> { "sample" },
                Config = new Dictionary<string, object?>
                {
                    ["provider"] = providerName,
                    ["model"] = model,
                    ["dataset_type"] = effectiveDatasetType,
                    ["workers"] = workers,
                },
            });
        }

        return new PipelineConfig
        {
            Name = "distsharp-generate",
            Solution = new SolutionConfig
            {
                Path = solutionPath,
                IncludeTests = includeTests,
                IncludeGenerated = includeGenerated,
                MinComplexity = minComplexity,
                ExcludeNamespaces = excludeNamespaces.ToList(),
            },
            Steps = steps,
            Output = new OutputConfig
            {
                Dir = outDir,
                Format = outputFormat,
                WriteMetadata = true,
                CheckpointEvery = 1000,
            },
        };
    }
}
