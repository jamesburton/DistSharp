namespace DistSharp.Cli.Commands;

/// <summary>Options bound from the <c>export</c> command line.</summary>
public sealed class ExportCommandOptions
{
    /// <summary>Gets or sets the source dataset directory.</summary>
    public string DatasetDir { get; set; } = string.Empty;

    /// <summary>Gets or sets the target format.</summary>
    public string? Format { get; set; }

    /// <summary>Gets or sets the Hugging Face repository identifier.</summary>
    public string? HfRepo { get; set; }

    /// <summary>Gets or sets the Hugging Face API token.</summary>
    public string? HfToken { get; set; }

    /// <summary>Gets or sets the output directory.</summary>
    public string? OutDir { get; set; }

    /// <summary>Gets or sets the dataset split name.</summary>
    public string Split { get; set; } = "train";
}
