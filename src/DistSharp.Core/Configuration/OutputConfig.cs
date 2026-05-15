namespace DistSharp.Core.Configuration;

/// <summary>Output format and destination settings bound from the <c>output</c> section of <c>distsharp.yaml</c>.</summary>
public sealed class OutputConfig
{
    /// <summary>Gets or sets the output directory. Default: <c>./distsharp-out</c>.</summary>
    public string Dir { get; set; } = "./distsharp-out";

    /// <summary>Gets or sets the output format: <c>jsonl</c>, <c>parquet</c>, or <c>csv</c>. Default: <c>jsonl</c>.</summary>
    public string Format { get; set; } = "jsonl";

    /// <summary>Gets or sets how many rows to write between checkpoint saves. Default: 1000.</summary>
    public int CheckpointEvery { get; set; } = 1000;

    /// <summary>Gets or sets a value indicating whether symbol metadata is included as extra fields. Default: <see langword="true"/>.</summary>
    public bool WriteMetadata { get; set; } = true;
}
