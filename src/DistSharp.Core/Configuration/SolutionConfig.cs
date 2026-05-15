namespace DistSharp.Core.Configuration;

/// <summary>Solution analysis settings bound from the <c>solution</c> section of <c>distsharp.yaml</c>.</summary>
public sealed class SolutionConfig
{
    /// <summary>Gets or sets the path to the <c>.sln</c>, <c>.csproj</c>, or directory to analyse.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether test projects are included. Default: <see langword="false"/>.</summary>
    public bool IncludeTests { get; set; }

    /// <summary>Gets or sets a value indicating whether auto-generated files are included. Default: <see langword="false"/>.</summary>
    public bool IncludeGenerated { get; set; }

    /// <summary>Gets or sets the minimum cyclomatic complexity. Default: 3.</summary>
    public int MinComplexity { get; set; } = 3;

    /// <summary>Gets or sets namespace prefixes to exclude.</summary>
    public List<string> ExcludeNamespaces { get; set; } = new List<string>();
}
