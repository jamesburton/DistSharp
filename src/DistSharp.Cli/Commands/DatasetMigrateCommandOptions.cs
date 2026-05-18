namespace DistSharp.Cli.Commands;

/// <summary>Options for the <c>dataset migrate</c> command.</summary>
public sealed class DatasetMigrateCommandOptions
{
    /// <summary>Gets or sets the dataset directory containing legacy JSONL files (no manifest yet).</summary>
    public required string DatasetDir { get; set; }

    /// <summary>Gets or sets the path to the solution to re-extract symbols from.</summary>
    public required string Solution { get; set; }

    /// <summary>Gets or sets the dataset type used when the legacy dataset was generated (e.g. <c>explanation</c>).</summary>
    public string DatasetType { get; set; } = "explanation";
}
