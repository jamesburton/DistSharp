using System.Text;
using System.Text.Json;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;

namespace DistSharp.Core.Writers;

/// <summary>Writes rows as one JSON object per line to a <c>.jsonl</c> file.</summary>
public sealed class JsonlDatasetWriter : IDatasetWriter
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly StreamWriter writer;
    private readonly bool writeMetadata;

    /// <summary>Initializes a new instance of the <see cref="JsonlDatasetWriter"/> class.</summary>
    /// <param name="path">Path to the output file.</param>
    /// <param name="writeMetadata">If <see langword="false"/>, only core dataset fields are written.</param>
    public JsonlDatasetWriter(string path, bool writeMetadata)
    {
        this.writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        this.writeMetadata = writeMetadata;
    }

    /// <inheritdoc/>
    public async Task WriteAsync(Row row, CancellationToken cancellationToken)
    {
        var dict = RowSerializer.ToDictionary(row, this.writeMetadata);
        var json = JsonSerializer.Serialize(dict, JsonOpts);
        await this.writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken cancellationToken) => this.writer.FlushAsync(cancellationToken);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await this.writer.FlushAsync().ConfigureAwait(false);
        await this.writer.DisposeAsync().ConfigureAwait(false);
    }
}
