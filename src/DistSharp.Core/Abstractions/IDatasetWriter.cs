using DistSharp.Core.Models;

namespace DistSharp.Core.Abstractions;

/// <summary>Incrementally writes rows to a dataset output (JSONL, Parquet, CSV, etc.).</summary>
public interface IDatasetWriter : IAsyncDisposable
{
    /// <summary>Writes a single <paramref name="row"/> to the output.</summary>
    /// <param name="row">The row to write.</param>
    /// <param name="cancellationToken">Token to cancel the write.</param>
    Task WriteAsync(Row row, CancellationToken cancellationToken);

    /// <summary>Flushes any buffered rows to the underlying storage.</summary>
    /// <param name="cancellationToken">Token to cancel the flush.</param>
    Task FlushAsync(CancellationToken cancellationToken);
}
