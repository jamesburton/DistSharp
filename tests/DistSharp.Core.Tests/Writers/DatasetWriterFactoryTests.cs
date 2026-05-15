using DistSharp.Core.Abstractions;
using DistSharp.Core.Configuration;
using DistSharp.Core.Writers;
using FluentAssertions;
using Xunit;

namespace DistSharp.Core.Tests.Writers;

public sealed class DatasetWriterFactoryTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public void Dispose()
    {
        if (Directory.Exists(this.dir))
        {
            Directory.Delete(this.dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("jsonl", typeof(JsonlDatasetWriter))]
    [InlineData("JSONL", typeof(JsonlDatasetWriter))]
    [InlineData("csv", typeof(CsvDatasetWriter))]
    [InlineData("parquet", typeof(ParquetDatasetWriter))]
    public async Task Create_ReturnsExpectedWriterType(string format, Type expected)
    {
        var factory = new DatasetWriterFactory();
        var config = new OutputConfig { Dir = this.dir, Format = format };

        await using IDatasetWriter writer = factory.Create(config);

        writer.Should().BeOfType(expected);
    }

    [Fact]
    public void Create_ThrowsInvalidOperation_ForUnknownFormat()
    {
        var factory = new DatasetWriterFactory();
        var config = new OutputConfig { Dir = this.dir, Format = "xml" };

        var act = () => factory.Create(config);

        act.Should().Throw<InvalidOperationException>().WithMessage("*xml*");
    }
}
