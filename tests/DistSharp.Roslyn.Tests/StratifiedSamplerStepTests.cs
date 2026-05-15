using System.Threading.Channels;
using DistSharp.Core.Models;
using DistSharp.Roslyn.Steps;
using FluentAssertions;
using Xunit;

namespace DistSharp.Roslyn.Tests;

public sealed class StratifiedSamplerStepTests
{
    [Fact]
    public async Task PassesThrough_WhenInputCountIsAtOrBelowMaxRows()
    {
        var input = Channel.CreateUnbounded<Row>();
        var output = Channel.CreateUnbounded<Row>();
        for (var i = 0; i < 5; i++)
        {
            await input.Writer.WriteAsync(Row.Empty.With("id", i).With("complexity", 1));
        }

        input.Writer.Complete();

        var step = new StratifiedSamplerStep("sample", new StratifiedSamplerOptions { MaxRows = 10, Seed = 42 });
        await step.ExecuteAsync(input.Reader, output.Writer, CancellationToken.None);
        output.Writer.TryComplete();

        var rows = new List<Row>();
        await foreach (var r in output.Reader.ReadAllAsync())
        {
            rows.Add(r);
        }

        rows.Should().HaveCount(5);
    }

    [Fact]
    public async Task Uniform_SamplesExactlyMaxRows_WhenInputExceedsLimit()
    {
        var input = Channel.CreateUnbounded<Row>();
        var output = Channel.CreateUnbounded<Row>();
        for (var i = 0; i < 100; i++)
        {
            await input.Writer.WriteAsync(Row.Empty.With("id", i).With("complexity", 1));
        }

        input.Writer.Complete();

        var step = new StratifiedSamplerStep("sample", new StratifiedSamplerOptions { MaxRows = 25, Strategy = SamplingStrategy.Uniform, Seed = 42 });
        await step.ExecuteAsync(input.Reader, output.Writer, CancellationToken.None);
        output.Writer.TryComplete();

        var rows = new List<Row>();
        await foreach (var r in output.Reader.ReadAllAsync())
        {
            rows.Add(r);
        }

        rows.Should().HaveCount(25);
    }

    [Fact]
    public async Task ComplexityWeighted_BiasesTowardHigherComplexityRows()
    {
        var input = Channel.CreateUnbounded<Row>();
        var output = Channel.CreateUnbounded<Row>();

        // Mix: 80 rows complexity=1, 20 rows complexity=10
        for (var i = 0; i < 80; i++)
        {
            await input.Writer.WriteAsync(Row.Empty.With("id", i).With("complexity", 1));
        }

        for (var i = 80; i < 100; i++)
        {
            await input.Writer.WriteAsync(Row.Empty.With("id", i).With("complexity", 10));
        }

        input.Writer.Complete();

        var step = new StratifiedSamplerStep("sample", new StratifiedSamplerOptions { MaxRows = 30, Strategy = SamplingStrategy.ComplexityWeighted, Seed = 42 });
        await step.ExecuteAsync(input.Reader, output.Writer, CancellationToken.None);
        output.Writer.TryComplete();

        var rows = new List<Row>();
        await foreach (var r in output.Reader.ReadAllAsync())
        {
            rows.Add(r);
        }

        rows.Should().HaveCount(30);
        var highComplexityCount = rows.Count(r => r.Get<int>("complexity") == 10);

        // With weights 10 vs 1 (10x ratio) and 20 high vs 80 low (200 vs 80 total weight, ~71% expected),
        // expect well over half of sampled rows to be high-complexity.
        highComplexityCount.Should().BeGreaterThan(15);
    }

    [Fact]
    public async Task Sampling_IsDeterministic_WithSameSeed()
    {
        async Task<List<int>> RunAsync()
        {
            var input = Channel.CreateUnbounded<Row>();
            var output = Channel.CreateUnbounded<Row>();
            for (var i = 0; i < 100; i++)
            {
                await input.Writer.WriteAsync(Row.Empty.With("id", i).With("complexity", i + 1));
            }

            input.Writer.Complete();

            var step = new StratifiedSamplerStep("sample", new StratifiedSamplerOptions { MaxRows = 20, Strategy = SamplingStrategy.Uniform, Seed = 123 });
            await step.ExecuteAsync(input.Reader, output.Writer, CancellationToken.None);
            output.Writer.TryComplete();

            var ids = new List<int>();
            await foreach (var r in output.Reader.ReadAllAsync())
            {
                ids.Add(r.Get<int>("id"));
            }

            return ids;
        }

        var run1 = await RunAsync();
        var run2 = await RunAsync();

        run1.Should().BeEquivalentTo(run2, options => options.WithStrictOrdering());
    }
}
