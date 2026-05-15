using DistSharp.Core.Configuration;

namespace DistSharp.Core.Abstractions;

/// <summary>Creates <see cref="IDatasetWriter"/> instances from output configuration.</summary>
public interface IDatasetWriterFactory
{
    /// <summary>Creates a writer for the format and path specified in <paramref name="config"/>.</summary>
    /// <param name="config">Output format and destination configuration.</param>
    /// <returns>A ready-to-use dataset writer.</returns>
    IDatasetWriter Create(OutputConfig config);
}
