namespace DistSharp.Cli.Commands;

/// <summary>Options bound from the <c>pipeline run</c> command line.</summary>
public sealed class PipelineRunCommandOptions
{
    /// <summary>Gets or sets the path to the YAML configuration file.</summary>
    public string ConfigPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the override output directory.</summary>
    public string? OutDir { get; set; }

    /// <summary>Gets or sets the override max rows.</summary>
    public int? MaxRows { get; set; }
}
