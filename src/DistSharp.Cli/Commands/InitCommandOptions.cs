namespace DistSharp.Cli.Commands;

/// <summary>Options bound from the <c>init</c> command line.</summary>
public sealed class InitCommandOptions
{
    /// <summary>Gets or sets the pipeline name written into the template.</summary>
    public string PipelineName { get; set; } = "distsharp-pipeline";

    /// <summary>Gets or sets the template identifier.</summary>
    public string Template { get; set; } = "dotnet-mixed";

    /// <summary>Gets or sets the output path.</summary>
    public string OutputPath { get; set; } = "./distsharp.yaml";
}
