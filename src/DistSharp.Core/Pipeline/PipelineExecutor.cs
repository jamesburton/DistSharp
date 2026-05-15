using System.Threading.Channels;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;

namespace DistSharp.Core.Pipeline;

/// <summary>
/// Executes a <see cref="PipelineDefinition"/> by wiring steps together via
/// <see cref="System.Threading.Channels"/>. Supports linear pipelines, fan-out
/// (one step → many), and fan-in (many steps → one).
/// </summary>
public sealed class PipelineExecutor : IPipelineExecutor
{
    private const int ChannelCapacity = 1000;

    /// <inheritdoc/>
    public async Task ExecuteAsync(PipelineDefinition pipeline, IDatasetWriter writer, CancellationToken cancellationToken)
    {
        var steps = TopologicalSort(pipeline.Steps);
        var count = steps.Count;

        // consumers[i] = indices of steps that list steps[i] as a dependency
        var consumers = Enumerable.Range(0, count)
            .Select(i => steps
                .Select((s, j) => (s, j))
                .Where(x => x.s.DependsOn.Contains(steps[i].Name))
                .Select(x => x.j)
                .ToArray())
            .ToArray();

        // Each step writes to its own output channel.
        var outputChannels = Enumerable.Range(0, count)
            .Select(_ => Channel.CreateBounded<Row>(ChannelCapacity))
            .ToArray();

        // Each step reads from its own input channel.
        // Source steps (no DependsOn) get a pre-completed empty channel.
        // Fan-in steps (multiple DependsOn) allow multiple concurrent writers.
        var inputChannels = steps
            .Select(s =>
            {
                if (s.DependsOn.Count == 0)
                {
                    var empty = Channel.CreateBounded<Row>(1);
                    empty.Writer.Complete();
                    return empty;
                }

                return Channel.CreateBounded<Row>(new BoundedChannelOptions(ChannelCapacity)
                {
                    SingleWriter = s.DependsOn.Count == 1,
                });
            })
            .ToArray();

        // fanInCounters[i] = number of upstream distributor tasks still writing to inputChannels[i].
        // When this reaches 0, the distributor completing it calls TryComplete on inputChannels[i].Writer.
        var fanInCounters = steps.Select(s => s.DependsOn.Count).ToArray();

        // Step tasks: run each step, then complete its output channel.
        var stepTasks = steps.Select((s, i) =>
            s.Step.ExecuteAsync(inputChannels[i].Reader, outputChannels[i].Writer, cancellationToken)
                .ContinueWith(
                    t => outputChannels[i].Writer.TryComplete(t.IsFaulted ? t.Exception : null),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default))
            .ToArray();

        // Distributor tasks: for each step, read its output and forward to consumers (or the writer).
        var distributorTasks = steps.Select((_, i) =>
            consumers[i].Length == 0
                ? DrainToWriterAsync(outputChannels[i].Reader, writer, cancellationToken)
                : DistributeAsync(outputChannels[i].Reader, consumers[i], inputChannels, fanInCounters, cancellationToken))
            .ToArray();

        await Task.WhenAll(stepTasks.Concat(distributorTasks));
        await writer.FlushAsync(cancellationToken);
    }

    // Reads every row from source and forwards a copy to each consumer's input channel.
    // When source is exhausted, decrements the fan-in counter for each consumer and completes
    // their input channel writer if the counter reaches zero (all upstreams done).
    private static async Task DistributeAsync(
        ChannelReader<Row> source,
        int[] consumerIndices,
        Channel<Row>[] inputChannels,
        int[] fanInCounters,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var row in source.ReadAllAsync(cancellationToken))
            {
                foreach (var idx in consumerIndices)
                {
                    await inputChannels[idx].Writer.WriteAsync(row, cancellationToken);
                }
            }
        }
        finally
        {
            foreach (var idx in consumerIndices)
            {
                if (Interlocked.Decrement(ref fanInCounters[idx]) == 0)
                {
                    inputChannels[idx].Writer.TryComplete();
                }
            }
        }
    }

    private static async Task DrainToWriterAsync(
        ChannelReader<Row> source,
        IDatasetWriter writer,
        CancellationToken cancellationToken)
    {
        await foreach (var row in source.ReadAllAsync(cancellationToken))
        {
            await writer.WriteAsync(row, cancellationToken);
        }
    }

    private static List<StepDefinition> TopologicalSort(IReadOnlyList<StepDefinition> steps)
    {
        var sorted = new List<StepDefinition>(steps.Count);
        var permanent = new HashSet<string>(steps.Count);
        var temporary = new HashSet<string>();
        var byName = steps.ToDictionary(s => s.Name);

        void Visit(StepDefinition step)
        {
            if (permanent.Contains(step.Name))
            {
                return;
            }

            if (!temporary.Add(step.Name))
            {
                throw new InvalidOperationException($"Pipeline has a cycle involving step '{step.Name}'.");
            }

            foreach (var dep in step.DependsOn)
            {
                Visit(byName[dep]);
            }

            temporary.Remove(step.Name);
            permanent.Add(step.Name);
            sorted.Add(step);
        }

        foreach (var step in steps)
        {
            Visit(step);
        }

        return sorted;
    }
}
