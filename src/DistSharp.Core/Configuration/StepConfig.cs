namespace DistSharp.Core.Configuration;

/// <summary>Configuration for a single step in a pipeline, bound from a <c>steps[]</c> entry in <c>distsharp.yaml</c>.</summary>
public sealed class StepConfig
{
    /// <summary>Gets or sets the unique step name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the step type identifier (e.g. <c>RoslynSymbolExtractor</c>, <c>LlmStep</c>).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the names of upstream steps this step depends on.</summary>
    public List<string> DependsOn { get; set; } = new List<string>();

    /// <summary>Gets or sets arbitrary step-specific configuration values.</summary>
    public Dictionary<string, object?> Config { get; set; } = new Dictionary<string, object?>();
}
