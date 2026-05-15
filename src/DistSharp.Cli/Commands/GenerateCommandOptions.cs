namespace DistSharp.Cli.Commands;

/// <summary>Options bound from the <c>generate</c> command line.</summary>
public sealed class GenerateCommandOptions
{
    /// <summary>Gets or sets the solution/project/directory path to analyse.</summary>
    public string Solution { get; set; } = string.Empty;

    /// <summary>Gets or sets the output directory.</summary>
    public string OutDir { get; set; } = "./distsharp-out";

    /// <summary>Gets or sets the maximum total rows to generate.</summary>
    public int MaxRows { get; set; } = 50_000;

    /// <summary>Gets or sets the dataset type.</summary>
    public string DatasetType { get; set; } = "mixed";

    /// <summary>Gets or sets the output format.</summary>
    public string Format { get; set; } = "jsonl";

    /// <summary>Gets or sets the LLM provider name.</summary>
    public string Provider { get; set; } = "openai";

    /// <summary>Gets or sets the LLM model identifier.</summary>
    public string? Model { get; set; }

    /// <summary>Gets or sets a value indicating whether test projects are included.</summary>
    public bool IncludeTests { get; set; }

    /// <summary>Gets or sets a value indicating whether generated files are included.</summary>
    public bool IncludeGenerated { get; set; }

    /// <summary>Gets or sets the minimum cyclomatic complexity.</summary>
    public int MinComplexity { get; set; } = 3;

    /// <summary>Gets or sets namespace prefixes to exclude.</summary>
    public IReadOnlyList<string> ExcludeNamespaces { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the worker count.</summary>
    public int Workers { get; set; } = 4;

    /// <summary>Gets or sets the random seed.</summary>
    public int? Seed { get; set; }

    /// <summary>Gets or sets a value indicating whether the run is a dry run (no LLM calls).</summary>
    public bool DryRun { get; set; }
}
