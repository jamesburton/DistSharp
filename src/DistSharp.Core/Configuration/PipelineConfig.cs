namespace DistSharp.Core.Configuration;

/// <summary>
/// Strongly-typed representation of <c>distsharp.yaml</c>. Bound via
/// <c>IOptions&lt;PipelineConfig&gt;</c> — all sources (YAML file, environment variables, CLI flags)
/// are merged before binding.
/// </summary>
public sealed class PipelineConfig
{
    /// <summary>Gets or sets the pipeline name used in output metadata and checkpoint IDs.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the config schema version.</summary>
    public string Version { get; set; } = "1";

    /// <summary>Gets or sets the solution analysis settings.</summary>
    public SolutionConfig Solution { get; set; } = new();

    /// <summary>Gets or sets the ordered list of step configurations.</summary>
    public List<StepConfig> Steps { get; set; } = new List<StepConfig>();

    /// <summary>Gets or sets the output format and destination settings.</summary>
    public OutputConfig Output { get; set; } = new();
}
