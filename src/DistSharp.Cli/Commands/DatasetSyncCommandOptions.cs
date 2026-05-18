using DistSharp.Core.Sync;

namespace DistSharp.Cli.Commands;

/// <summary>Options for the <c>dataset sync</c> command.</summary>
public sealed class DatasetSyncCommandOptions
{
    /// <summary>Gets or sets the dataset directory to sync.</summary>
    public required string DatasetDir { get; set; }

    /// <summary>Gets or sets the path to the solution to analyse.</summary>
    public required string Solution { get; set; }

    /// <summary>Gets or sets the dataset type (e.g. <c>explanation</c>).</summary>
    public string DatasetType { get; set; } = "explanation";

    /// <summary>Gets or sets the orphan policy.</summary>
    public OrphanPolicy OrphanPolicy { get; set; } = OrphanPolicy.Drop;

    /// <summary>Gets or sets a value indicating whether to show the plan without making LLM calls or writing files.</summary>
    public bool DryRun { get; set; }

    /// <summary>Gets or sets the LLM provider name (e.g. <c>openai</c>, <c>anthropic</c>).</summary>
    public string Provider { get; set; } = "openai";
}
