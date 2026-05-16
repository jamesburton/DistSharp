using DistSharp.Core.Export;
using FluentAssertions;
using Xunit;

namespace DistSharp.Core.Tests.Export;

public sealed class JsonlDatasetReaderTests : IDisposable
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
    public async Task ReadsAllValidRows()
    {
        var content = "{\"a\":1}\n{\"a\":2}\n\n{\"a\":3}\n";
        await File.WriteAllTextAsync(this.path, content);

        var reader = new JsonlDatasetReader();
        var rows = new List<Dictionary<string, System.Text.Json.JsonElement>>();
        await foreach (var row in reader.ReadAsync(this.path))
        {
            rows.Add(row);
        }

        rows.Should().HaveCount(3);
        rows[0]["a"].GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task SkipsMalformedLines()
    {
        var content = "{\"a\":1}\nthis is not json\n{\"a\":3}\n";
        await File.WriteAllTextAsync(this.path, content);

        var reader = new JsonlDatasetReader();
        var rows = new List<Dictionary<string, System.Text.Json.JsonElement>>();
        await foreach (var row in reader.ReadAsync(this.path))
        {
            rows.Add(row);
        }

        rows.Should().HaveCount(2);
    }
}
