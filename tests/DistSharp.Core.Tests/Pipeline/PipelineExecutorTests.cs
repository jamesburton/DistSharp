using System.Threading.Channels;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;
using DistSharp.Core.Pipeline;
using FluentAssertions;
using Xunit;

namespace DistSharp.Core.Tests.Pipeline;

public sealed class PipelineExecutorTests
{
    [Fact]
    public async Task Linear_SourceOnly_WritesAllRowsToDatasetWriter()
    {
        var row1 = Row.Empty.With("id", 1);
        var row2 = Row.Empty.With("id", 2);
        var source = new SourceStep("source", row1, row2);
        var pipeline = new PipelineDefinition("test", new[] { new StepDefinition("source", source) });
        var writer = new CollectingWriter();

        await new PipelineExecutor().ExecuteAsync(pipeline, writer, CancellationToken.None);

        writer.Rows.Should().HaveCount(2);
        writer.Rows[0].Get<int>("id").Should().Be(1);
        writer.Rows[1].Get<int>("id").Should().Be(2);
    }

    [Fact]
    public async Task Linear_ThreeSteps_TransformationsAppliedInOrder()
    {
        var source = new SourceStep("source", Row.Empty.With("val", "original"));
        var tag1 = new TagStep("tag1", "step1", "done");
        var tag2 = new TagStep("tag2", "step2", "done");
        var steps = new StepDefinition[]
        {
            new StepDefinition("source", source),
            new StepDefinition("tag1", tag1, new[] { "source" }),
            new StepDefinition("tag2", tag2, new[] { "tag1" }),
        };
        var pipeline = new PipelineDefinition("test", steps);
        var writer = new CollectingWriter();

        await new PipelineExecutor().ExecuteAsync(pipeline, writer, CancellationToken.None);

        writer.Rows.Should().HaveCount(1);
        writer.Rows[0].Get<string>("val").Should().Be("original");
        writer.Rows[0].Get<string>("step1").Should().Be("done");
        writer.Rows[0].Get<string>("step2").Should().Be("done");
    }

    [Fact]
    public async Task Linear_EmptySource_WritesNoRows()
    {
        var source = new SourceStep("source"); // no rows
        var pipeline = new PipelineDefinition("test", new[] { new StepDefinition("source", source) });
        var writer = new CollectingWriter();

        await new PipelineExecutor().ExecuteAsync(pipeline, writer, CancellationToken.None);

        writer.Rows.Should().BeEmpty();
    }

    // Produces a fixed set of rows and ignores its input (source step behaviour).
    private sealed class SourceStep(string name, params Row[] rows) : IStep
    {
        public string Name { get; } = name;

        public async Task ExecuteAsync(ChannelReader<Row> input, ChannelWriter<Row> output, CancellationToken ct)
        {
            foreach (var row in rows)
            {
                await output.WriteAsync(row, ct);
            }
        }
    }

    // Passes every row through unchanged.
    private sealed class PassthroughStep(string name) : IStep
    {
        public string Name { get; } = name;

        public async Task ExecuteAsync(ChannelReader<Row> input, ChannelWriter<Row> output, CancellationToken ct)
        {
            await foreach (var row in input.ReadAllAsync(ct))
            {
                await output.WriteAsync(row, ct);
            }
        }
    }

    // Appends a field to every row it receives.
    private sealed class TagStep(string name, string field, string tag) : IStep
    {
        public string Name { get; } = name;

        public async Task ExecuteAsync(ChannelReader<Row> input, ChannelWriter<Row> output, CancellationToken ct)
        {
            await foreach (var row in input.ReadAllAsync(ct))
            {
                await output.WriteAsync(row.With(field, tag), ct);
            }
        }
    }

    // Collects every row written to it.
    private sealed class CollectingWriter : IDatasetWriter
    {
        public List<Row> Rows { get; } = new List<Row>();

        public Task WriteAsync(Row row, CancellationToken ct)
        {
            this.Rows.Add(row);
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
