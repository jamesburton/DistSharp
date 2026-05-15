namespace DistSharp.Roslyn.Steps;

/// <summary>Configuration for <see cref="RoslynSymbolExtractorStep"/>.</summary>
public sealed class RoslynSymbolExtractorOptions
{
    /// <summary>Gets or sets the solution, project, or directory path to analyse.</summary>
    public string SolutionPath { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether test projects are included.</summary>
    public bool IncludeTests { get; set; }

    /// <summary>Gets or sets a value indicating whether auto-generated files are included.</summary>
    public bool IncludeGenerated { get; set; }

    /// <summary>Gets or sets the minimum cyclomatic complexity for methods. Default: 3.</summary>
    public int MinComplexity { get; set; } = 3;

    /// <summary>Gets or sets namespace prefixes to exclude.</summary>
    public IReadOnlyList<string> ExcludeNamespaces { get; set; } = new List<string>();

    /// <summary>Gets or sets the symbol kinds to emit. Default: <c>method</c>, <c>property</c>, <c>class</c>, <c>interface</c>.</summary>
    public IReadOnlyList<string> SymbolKinds { get; set; } = new List<string> { "method", "property", "class", "interface" };

    /// <summary>Gets or sets the maximum body lines to keep on each symbol. 0 means unlimited. Default: 0.</summary>
    public int MaxBodyLines { get; set; }
}
