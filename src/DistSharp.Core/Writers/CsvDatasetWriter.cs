using System.Text;
using System.Text.Json;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;

namespace DistSharp.Core.Writers;

/// <summary>Writes rows as CSV (RFC 4180) to a <c>.csv</c> file. Header is taken from the first row.</summary>
public sealed class CsvDatasetWriter : IDatasetWriter
{
    private readonly StreamWriter writer;
    private readonly bool writeMetadata;
    private string[]? header;

    /// <summary>Initializes a new instance of the <see cref="CsvDatasetWriter"/> class.</summary>
    /// <param name="path">Path to the output file.</param>
    /// <param name="writeMetadata">If <see langword="false"/>, only core dataset fields are written.</param>
    public CsvDatasetWriter(string path, bool writeMetadata)
    {
        this.writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        this.writeMetadata = writeMetadata;
    }

    /// <inheritdoc/>
    public async Task WriteAsync(Row row, CancellationToken cancellationToken)
    {
        var dict = RowSerializer.ToDictionary(row, this.writeMetadata);
        if (this.header is null)
        {
            this.header = dict.Keys.ToArray();
            var headerLine = string.Join(',', this.header.Select(Escape));
            await this.writer.WriteLineAsync(headerLine.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        var values = new string[this.header.Length];
        for (var i = 0; i < this.header.Length; i++)
        {
            values[i] = dict.TryGetValue(this.header[i], out var v) ? Escape(Stringify(v)) : string.Empty;
        }

        var line = string.Join(',', values);
        await this.writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken cancellationToken) => this.writer.FlushAsync(cancellationToken);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await this.writer.FlushAsync().ConfigureAwait(false);
        await this.writer.DisposeAsync().ConfigureAwait(false);
    }

    private static string Stringify(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is string s)
        {
            return s;
        }

        if (value is bool b)
        {
            return b ? "true" : "false";
        }

        if (value.GetType().IsPrimitive || value is decimal)
        {
            return value.ToString() ?? string.Empty;
        }

        return JsonSerializer.Serialize(value);
    }

    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }
}
