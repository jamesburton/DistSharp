using System.Text.Json.Serialization;

namespace DistSharp.Core.Sync;

/// <summary>A single row entry in the manifest index.</summary>
public sealed class ManifestRow
{
    /// <summary>Gets or sets the stable 16-hex row identifier.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    /// <summary>Gets or sets the fully-qualified symbol name.</summary>
    [JsonPropertyName("symbol_fqn")]
    public required string SymbolFqn { get; set; }

    /// <summary>Gets or sets the symbol kind (e.g. <c>method</c>, <c>class</c>).</summary>
    [JsonPropertyName("symbol_kind")]
    public required string SymbolKind { get; set; }

    /// <summary>Gets or sets the dataset type (e.g. <c>explanation</c>).</summary>
    [JsonPropertyName("dataset_type")]
    public required string DatasetType { get; set; }

    /// <summary>Gets or sets the SHA-256 of the symbol body at generation time.</summary>
    [JsonPropertyName("body_sha")]
    public required string BodySha { get; set; }

    /// <summary>Gets or sets the prompt version (e.g. <c>explanation@1</c>).</summary>
    [JsonPropertyName("prompt_version")]
    public required string PromptVersion { get; set; }

    /// <summary>Gets or sets the 0-based line offset of this row in the data file.</summary>
    [JsonPropertyName("row_offset")]
    public int RowOffset { get; set; }
}
