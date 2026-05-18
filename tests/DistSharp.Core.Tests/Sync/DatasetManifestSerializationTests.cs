using DistSharp.Core.Sync;
using FluentAssertions;
using Xunit;

namespace DistSharp.Core.Tests.Sync;

public sealed class DatasetManifestSerializationTests
{
    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var manifest = new DatasetManifest
        {
            FormatVersion = 1,
            GeneratedAt = new DateTimeOffset(2026, 5, 18, 12, 34, 56, TimeSpan.Zero),
            Source = new ManifestSource
            {
                SolutionPath = "MyApp.sln",
                SolutionSha = "abc123",
                Commit = "main@a1b2c3d",
            },
            Rows =
            [
                new ManifestRow
                {
                    Id = "abcd1234abcd1234",
                    SymbolFqn = "MyApp.Services.Foo.Bar",
                    SymbolKind = "method",
                    DatasetType = "explanation",
                    BodySha = new string('a', 64),
                    PromptVersion = "explanation@1",
                    RowOffset = 42,
                },
            ],
        };

        var json = manifest.ToJson();
        var restored = DatasetManifest.FromJson(json)!;

        restored.FormatVersion.Should().Be(1);
        restored.GeneratedAt.Should().Be(manifest.GeneratedAt);
        restored.Source!.SolutionPath.Should().Be("MyApp.sln");
        restored.Source.SolutionSha.Should().Be("abc123");
        restored.Source.Commit.Should().Be("main@a1b2c3d");
        restored.Rows.Should().HaveCount(1);

        var row = restored.Rows[0];
        row.Id.Should().Be("abcd1234abcd1234");
        row.SymbolFqn.Should().Be("MyApp.Services.Foo.Bar");
        row.SymbolKind.Should().Be("method");
        row.DatasetType.Should().Be("explanation");
        row.BodySha.Should().Be(new string('a', 64));
        row.PromptVersion.Should().Be("explanation@1");
        row.RowOffset.Should().Be(42);
    }

    [Fact]
    public void FromJson_ReturnsNull_WhenInputIsNullOrWhitespace()
    {
        DatasetManifest.FromJson(null).Should().BeNull();
        DatasetManifest.FromJson(string.Empty).Should().BeNull();
        DatasetManifest.FromJson("   ").Should().BeNull();
    }

    [Fact]
    public void NullSource_IsOmittedFromJson()
    {
        var manifest = new DatasetManifest { FormatVersion = 1, GeneratedAt = DateTimeOffset.UtcNow };
        var json = manifest.ToJson();

        json.Should().NotContain("\"source\"");
    }

    [Fact]
    public async Task LoadAsync_ReturnsNull_WhenFileDoesNotExist()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "manifest.json");
        var result = await DatasetManifest.LoadAsync(missing, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var path = Path.Combine(dir, "manifest.json");

        try
        {
            var manifest = new DatasetManifest
            {
                FormatVersion = 1,
                GeneratedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            };

            await manifest.SaveAsync(path, CancellationToken.None);
            var loaded = await DatasetManifest.LoadAsync(path, CancellationToken.None);

            loaded.Should().NotBeNull();
            loaded!.FormatVersion.Should().Be(1);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
