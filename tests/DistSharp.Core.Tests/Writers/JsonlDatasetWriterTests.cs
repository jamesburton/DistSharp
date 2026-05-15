using System.Text.Json;
using DistSharp.Core.Models;
using DistSharp.Core.Writers;
using FluentAssertions;
using Xunit;

namespace DistSharp.Core.Tests.Writers;

public sealed class JsonlDatasetWriterTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".jsonl");

    public void Dispose()
    {
        if (File.Exists(this.path))
        {
            File.Delete(this.path);
        }
    }

    [Fact]
    public async Task WritesOneJsonObjectPerLine()
    {
        await using (var writer = new JsonlDatasetWriter(this.path, writeMetadata: true))
        {
            await writer.WriteAsync(Row.Empty.With("instruction", "a").With("response", "1"), default);
            await writer.WriteAsync(Row.Empty.With("instruction", "b").With("response", "2"), default);
        }

        var lines = await File.ReadAllLinesAsync(this.path);
        lines.Should().HaveCount(2);
        JsonDocument.Parse(lines[0]).RootElement.GetProperty("instruction").GetString().Should().Be("a");
        JsonDocument.Parse(lines[1]).RootElement.GetProperty("instruction").GetString().Should().Be("b");
    }

    [Fact]
    public async Task FiltersOutNonCoreFields_WhenWriteMetadataFalse()
    {
        await using (var writer = new JsonlDatasetWriter(this.path, writeMetadata: false))
        {
            await writer.WriteAsync(
                Row.Empty.With("instruction", "x").With("response", "y").With("file_path", "z.cs").With("complexity", 5),
                default);
        }

        var json = JsonDocument.Parse(await File.ReadAllTextAsync(this.path)).RootElement;
        json.TryGetProperty("instruction", out _).Should().BeTrue();
        json.TryGetProperty("response", out _).Should().BeTrue();
        json.TryGetProperty("file_path", out _).Should().BeFalse();
        json.TryGetProperty("complexity", out _).Should().BeFalse();
    }

    [Fact]
    public async Task SkipsFieldsStartingWithUnderscore()
    {
        await using (var writer = new JsonlDatasetWriter(this.path, writeMetadata: true))
        {
            await writer.WriteAsync(Row.Empty.With("instruction", "a").With("_internal", "hidden"), default);
        }

        var json = JsonDocument.Parse(await File.ReadAllTextAsync(this.path)).RootElement;
        json.TryGetProperty("_internal", out _).Should().BeFalse();
    }
}
