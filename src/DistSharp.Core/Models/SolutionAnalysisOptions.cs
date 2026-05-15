namespace DistSharp.Core.Models;

/// <summary>Options that control how <c>ISolutionAnalyzer</c> processes a solution.</summary>
public sealed class SolutionAnalysisOptions
{
    /// <summary>Gets or sets a value indicating whether test projects are included. Default: <see langword="false"/>.</summary>
    public bool IncludeTests { get; set; }

    /// <summary>Gets or sets a value indicating whether auto-generated (<c>*.g.cs</c>) files are included. Default: <see langword="false"/>.</summary>
    public bool IncludeGenerated { get; set; }

    /// <summary>Gets or sets the minimum cyclomatic complexity for a method to be included. Default: 3.</summary>
    public int MinComplexity { get; set; } = 3;

    /// <summary>Gets or sets namespace prefixes to exclude from analysis.</summary>
    public IReadOnlyList<string> ExcludeNamespaces { get; set; } = Array.Empty<string>();
}
