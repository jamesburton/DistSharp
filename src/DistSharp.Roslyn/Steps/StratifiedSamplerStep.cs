using System.Threading.Channels;
using DistSharp.Core.Models;
using DistSharp.Core.Pipeline;

namespace DistSharp.Roslyn.Steps;

/// <summary>Samples up to <c>MaxRows</c> rows using a uniform or complexity-weighted strategy.</summary>
public sealed class StratifiedSamplerStep : IStep
{
    private readonly StratifiedSamplerOptions options;

    /// <summary>Initializes a new instance of the <see cref="StratifiedSamplerStep"/> class.</summary>
    /// <param name="name">The step name in the pipeline.</param>
    /// <param name="options">Sampler configuration.</param>
    public StratifiedSamplerStep(string name, StratifiedSamplerOptions options)
    {
        this.Name = name;
        this.options = options;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public async Task ExecuteAsync(ChannelReader<Row> input, ChannelWriter<Row> output, CancellationToken cancellationToken)
    {
        var buffer = new List<Row>();
        await foreach (var row in input.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            buffer.Add(row);
        }

        if (this.options.MaxRows <= 0 || buffer.Count <= this.options.MaxRows)
        {
            foreach (var row in buffer)
            {
                await output.WriteAsync(row, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var random = this.options.Seed is int seed ? new Random(seed) : new Random();
        var sampled = this.options.Strategy == SamplingStrategy.Uniform
            ? UniformSample(buffer, this.options.MaxRows, random)
            : WeightedSample(buffer, this.options.MaxRows, random);

        foreach (var row in sampled)
        {
            await output.WriteAsync(row, cancellationToken).ConfigureAwait(false);
        }
    }

    private static IEnumerable<Row> UniformSample(IReadOnlyList<Row> rows, int count, Random random)
    {
        // Fisher–Yates partial shuffle: take first `count` after shuffling indices.
        var indices = Enumerable.Range(0, rows.Count).ToArray();
        for (var i = 0; i < count; i++)
        {
            var j = random.Next(i, indices.Length);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

        return indices.Take(count).OrderBy(i => i).Select(i => rows[i]);
    }

    private static IEnumerable<Row> WeightedSample(IReadOnlyList<Row> rows, int count, Random random)
    {
        // A-Res weighted reservoir sampling (Efraimidis-Spirakis).
        var keys = new (double Key, int Index)[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var weight = Math.Max(1, rows[i].Get<int>("complexity"));

            // key = u^(1/w), where u ~ Uniform(0, 1)
            var u = random.NextDouble();
            if (u <= 0)
            {
                u = double.Epsilon;
            }

            keys[i] = (Math.Pow(u, 1.0 / weight), i);
        }

        Array.Sort(keys, (a, b) => b.Key.CompareTo(a.Key));
        return keys.Take(count).Select(k => k.Index).OrderBy(i => i).Select(i => rows[i]);
    }
}
