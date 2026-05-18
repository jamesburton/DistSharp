using System.Text.Json;
using System.Text.Json.Serialization;

namespace DistSharp.Core.Sync;

/// <summary>The on-disk manifest that tracks row identities for a DistSharp dataset directory.</summary>
public sealed class DatasetManifest
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Gets or sets the manifest format version. Currently always <c>1</c>.</summary>
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; set; }

    /// <summary>Gets or sets the UTC timestamp at which this manifest was generated.</summary>
    [JsonPropertyName("generated_at")]
    public DateTimeOffset GeneratedAt { get; set; }

    /// <summary>Gets or sets the source-solution metadata.</summary>
    [JsonPropertyName("source")]
    public ManifestSource? Source { get; set; }

    /// <summary>Gets or sets the ordered list of row index entries.</summary>
    [JsonPropertyName("rows")]
    public List<ManifestRow> Rows { get; set; } = new();

    /// <summary>Deserializes a <see cref="DatasetManifest"/> from the JSON text in <paramref name="json"/>.</summary>
    /// <param name="json">UTF-8 JSON text.</param>
    /// <returns>The deserialized manifest, or <see langword="null"/> if the input is null or whitespace.</returns>
    public static DatasetManifest? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<DatasetManifest>(json, JsonOpts);
    }

    /// <summary>Reads and deserializes a manifest from <paramref name="path"/>.</summary>
    /// <param name="path">Path to the <c>manifest.json</c> file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The manifest, or <see langword="null"/> if the file does not exist.</returns>
    public static async Task<DatasetManifest?> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return FromJson(json);
    }

    /// <summary>Serializes this manifest to pretty-printed JSON.</summary>
    /// <returns>JSON string representation of this manifest.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    /// <summary>Writes this manifest to <paramref name="path"/>, creating parent directories as needed.</summary>
    /// <param name="path">Destination path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> that completes when the file is written.</returns>
    public async Task SaveAsync(string path, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(path, this.ToJson(), cancellationToken).ConfigureAwait(false);
    }
}
