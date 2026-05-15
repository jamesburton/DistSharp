using DistSharp.Core.Abstractions;
using DistSharp.Core.Configuration;

namespace DistSharp.Core.Writers;

/// <summary>Default implementation of <see cref="IDatasetWriterFactory"/> — creates a writer based on <see cref="OutputConfig.Format"/>.</summary>
public sealed class DatasetWriterFactory : IDatasetWriterFactory
{
    /// <inheritdoc/>
    public IDatasetWriter Create(OutputConfig config)
    {
        Directory.CreateDirectory(config.Dir);
        var fileBase = $"distsharp-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";

        return config.Format.ToLowerInvariant() switch
        {
            "jsonl" => new JsonlDatasetWriter(Path.Combine(config.Dir, fileBase + ".jsonl"), config.WriteMetadata),
            "csv" => new CsvDatasetWriter(Path.Combine(config.Dir, fileBase + ".csv"), config.WriteMetadata),
            "parquet" => new ParquetDatasetWriter(Path.Combine(config.Dir, fileBase + ".parquet"), config.WriteMetadata),
            _ => throw new InvalidOperationException($"Unsupported output format: '{config.Format}'. Supported: jsonl, csv, parquet."),
        };
    }
}
