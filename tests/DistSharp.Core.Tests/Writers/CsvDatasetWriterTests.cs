using DistSharp.Core.Models;
using DistSharp.Core.Writers;
using FluentAssertions;
using Xunit;

namespace DistSharp.Core.Tests.Writers;

public sealed class CsvDatasetWriterTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".csv");

    public void Dispose()
    {
        if (File.Exists(this.path))
        {
            File.Delete(this.path);
        }
    }

    [Fact]
    public async Task WritesHeaderAndRows()
    {
        await using (var writer = new CsvDatasetWriter(this.path, writeMetadata: true))
        {
            await writer.WriteAsync(Row.Empty.With("instruction", "a").With("response", "1"), default);
            await writer.WriteAsync(Row.Empty.With("instruction", "b").With("response", "2"), default);
        }

        var lines = await File.ReadAllLinesAsync(this.path);
        lines.Should().HaveCount(3);
        lines[0].Should().Contain("instruction");
        lines[0].Should().Contain("response");
    }

    [Fact]
    public async Task QuotesValuesContainingDelimiters()
    {
        await using (var writer = new CsvDatasetWriter(this.path, writeMetadata: true))
        {
            await writer.WriteAsync(Row.Empty.With("text", "has, comma"), default);
            await writer.WriteAsync(Row.Empty.With("text", "has \"quote\""), default);
        }

        var lines = await File.ReadAllLinesAsync(this.path);
        lines[1].Should().Be("\"has, comma\"");
        lines[2].Should().Be("\"has \"\"quote\"\"\"");
    }
}
