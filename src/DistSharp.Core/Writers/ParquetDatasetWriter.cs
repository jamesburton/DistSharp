using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace DistSharp.Core.Writers;

/// <summary>Writes rows to a Parquet file, flushing row groups as the buffer fills.</summary>
public sealed class ParquetDatasetWriter : IDatasetWriter
{
    private const int DefaultRowGroupSize = 1000;

    private readonly string path;
    private readonly bool writeMetadata;
    private readonly int rowGroupSize;
    private readonly List<Dictionary<string, object?>> buffer = new();
    private ParquetSchema? schema;
    private FileStream? fileStream;
    private ParquetWriter? parquetWriter;

    /// <summary>Initializes a new instance of the <see cref="ParquetDatasetWriter"/> class.</summary>
    /// <param name="path">Path to the output file.</param>
    /// <param name="writeMetadata">If <see langword="false"/>, only core dataset fields are written.</param>
    /// <param name="rowGroupSize">Number of buffered rows per Parquet row group.</param>
    public ParquetDatasetWriter(string path, bool writeMetadata, int rowGroupSize = DefaultRowGroupSize)
    {
        this.path = path;
        this.writeMetadata = writeMetadata;
        this.rowGroupSize = rowGroupSize;
    }

    /// <inheritdoc/>
    public async Task WriteAsync(Row row, CancellationToken cancellationToken)
    {
        var dict = RowSerializer.ToDictionary(row, this.writeMetadata);
        this.buffer.Add(dict);

        if (this.buffer.Count >= this.rowGroupSize)
        {
            await this.FlushBufferAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken cancellationToken) =>
        this.buffer.Count > 0 ? this.FlushBufferAsync(cancellationToken) : Task.CompletedTask;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (this.buffer.Count > 0)
        {
            await this.FlushBufferAsync(CancellationToken.None).ConfigureAwait(false);
        }

        this.parquetWriter?.Dispose();
        if (this.fileStream is not null)
        {
            await this.fileStream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string? ToParquetString(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string s)
        {
            return s;
        }

        return System.Text.Json.JsonSerializer.Serialize(value);
    }

    private async Task EnsureWriterAsync(Dictionary<string, object?> firstRow, CancellationToken cancellationToken)
    {
        if (this.parquetWriter is not null)
        {
            return;
        }

        var fields = firstRow.Keys
            .Select(k => (Field)new DataField(k, typeof(string), isNullable: true))
            .ToArray();

        this.schema = new ParquetSchema(fields);
        this.fileStream = File.Create(this.path);
        this.parquetWriter = await ParquetWriter.CreateAsync(this.schema, this.fileStream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task FlushBufferAsync(CancellationToken cancellationToken)
    {
        await this.EnsureWriterAsync(this.buffer[0], cancellationToken).ConfigureAwait(false);

        using var rowGroup = this.parquetWriter!.CreateRowGroup();
        foreach (var field in this.schema!.DataFields)
        {
            var column = new string?[this.buffer.Count];
            for (var i = 0; i < this.buffer.Count; i++)
            {
                this.buffer[i].TryGetValue(field.Name, out var value);
                column[i] = ToParquetString(value);
            }

            await rowGroup.WriteColumnAsync(new DataColumn(field, column), cancellationToken).ConfigureAwait(false);
        }

        this.buffer.Clear();
    }
}
