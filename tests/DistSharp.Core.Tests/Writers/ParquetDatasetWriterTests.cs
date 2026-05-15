using DistSharp.Core.Models;
using DistSharp.Core.Writers;
using FluentAssertions;
using Parquet;
using Xunit;

namespace DistSharp.Core.Tests.Writers;

public sealed class ParquetDatasetWriterTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".parquet");

    public void Dispose()
    {
        if (File.Exists(this.path))
        {
            File.Delete(this.path);
        }
    }

    [Fact]
    public async Task RoundtripsAllRows()
    {
        await using (var writer = new ParquetDatasetWriter(this.path, writeMetadata: true, rowGroupSize: 2))
        {
            await writer.WriteAsync(Row.Empty.With("instruction", "i1").With("response", "r1"), default);
            await writer.WriteAsync(Row.Empty.With("instruction", "i2").With("response", "r2"), default);
            await writer.WriteAsync(Row.Empty.With("instruction", "i3").With("response", "r3"), default);
        }

        using var reader = await ParquetReader.CreateAsync(this.path);
        reader.RowGroupCount.Should().BeGreaterThanOrEqualTo(2);

        var totalRows = 0;
        for (var rg = 0; rg < reader.RowGroupCount; rg++)
        {
            using var rgReader = reader.OpenRowGroupReader(rg);
            totalRows += (int)rgReader.RowCount;
        }

        totalRows.Should().Be(3);
    }
}
