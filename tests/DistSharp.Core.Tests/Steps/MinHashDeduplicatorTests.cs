using System.Threading.Channels;
using DistSharp.Core.Models;
using DistSharp.Core.Steps;
using FluentAssertions;
using Xunit;

namespace DistSharp.Core.Tests.Steps;

public sealed class MinHashDeduplicatorTests
{
    [Fact]
    public async Task PassesThroughUniqueRows()
    {
        var rows = await RunAsync(
            new MinHashOptions { Threshold = 0.85, NumHashes = 64, ShingleSize = 2 },
            "the quick brown fox jumps",
            "totally unrelated phrase here today",
            "another distinct sentence to verify nothing collides");

        rows.Should().HaveCount(3);
    }

    [Fact]
    public async Task DropsExactDuplicates()
    {
        var rows = await RunAsync(
            new MinHashOptions { Threshold = 0.85, NumHashes = 64, ShingleSize = 2 },
            "the quick brown fox jumps over the lazy dog",
            "totally different content over here",
            "the quick brown fox jumps over the lazy dog");

        rows.Should().HaveCount(2);
        rows.Select(r => r.Get<int>("id")).Should().BeEquivalentTo(new[] { 0, 1 });
    }

    [Fact]
    public async Task DropsHighlySimilarRows()
    {
        var rows = await RunAsync(
            new MinHashOptions { Threshold = 0.7, NumHashes = 128, ShingleSize = 2 },
            "the quick brown fox jumps over the lazy dog every morning",
            "the quick brown fox jumps over the lazy dog every evening");

        rows.Should().HaveCount(1);
    }

    [Fact]
    public async Task KeepsDistinctShortStrings()
    {
        var rows = await RunAsync(
            new MinHashOptions { Threshold = 0.85, NumHashes = 64, ShingleSize = 2 },
            "alpha",
            "beta",
            "gamma");

        rows.Should().HaveCount(3);
    }

    private static async Task<List<Row>> RunAsync(MinHashOptions options, params string[] texts)
    {
        var input = Channel.CreateUnbounded<Row>();
        var output = Channel.CreateUnbounded<Row>();
        for (var i = 0; i < texts.Length; i++)
        {
            await input.Writer.WriteAsync(Row.Empty.With("id", i).With("response", texts[i]));
        }

        input.Writer.Complete();

        var dedup = new MinHashDeduplicator("dedup", options);
        await dedup.ExecuteAsync(input.Reader, output.Writer, CancellationToken.None);
        output.Writer.TryComplete();

        return await output.Reader.ReadAllAsync().ToListAsync();
    }
}
