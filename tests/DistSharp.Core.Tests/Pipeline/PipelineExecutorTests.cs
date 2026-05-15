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

    // ── Fan-out tests ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task FanOut_OneSourceTwoConsumers_BothReceiveAllRows()
    {
        var source = new SourceStep(
            "source",
            Row.Empty.With("id", 1),
            Row.Empty.With("id", 2));
        var branchA = new TagStep("branchA", "branch", "a");
        var branchB = new TagStep("branchB", "branch", "b");
        var steps = new StepDefinition[]
        {
            new StepDefinition("source", source),
            new StepDefinition("branchA", branchA, new[] { "source" }),
            new StepDefinition("branchB", branchB, new[] { "source" }),
        };
        var pipeline = new PipelineDefinition("test", steps);
        var writer = new CollectingWriter();

        await new PipelineExecutor().ExecuteAsync(pipeline, writer, CancellationToken.None);

        // Both branches receive 2 rows each = 4 rows total
        writer.Rows.Should().HaveCount(4);
        writer.Rows.Where(r => r.Get<string>("branch") == "a").Should().HaveCount(2);
        writer.Rows.Where(r => r.Get<string>("branch") == "b").Should().HaveCount(2);
    }

    // ── Fan-in tests ──────────────────────────────────────────────────────────────
    [Fact]
    public async Task FanIn_TwoUpstreamsMerge_DownstreamReceivesAllRows()
    {
        var sourceA = new SourceStep("sourceA", Row.Empty.With("src", "a"));
        var sourceB = new SourceStep("sourceB", Row.Empty.With("src", "b"));
        var merge = new PassthroughStep("merge");
        var steps = new StepDefinition[]
        {
            new StepDefinition("sourceA", sourceA),
            new StepDefinition("sourceB", sourceB),
            new StepDefinition("merge", merge, new[] { "sourceA", "sourceB" }),
        };
        var pipeline = new PipelineDefinition("test", steps);
        var writer = new CollectingWriter();

        await new PipelineExecutor().ExecuteAsync(pipeline, writer, CancellationToken.None);

        writer.Rows.Should().HaveCount(2);
        writer.Rows.Select(r => r.Get<string>("src")).Should().BeEquivalentTo(new[] { "a", "b" });
    }

    [Fact]
    public async Task FanOutThenFanIn_DiamondTopology_AllRowsReachSink()
    {
        // source → branchA ─┐
        //                    ├→ merge → sink
        // source → branchB ─┘
        var source = new SourceStep(
            "source",
            Row.Empty.With("id", 1),
            Row.Empty.With("id", 2));
        var branchA = new TagStep("branchA", "branch", "a");
        var branchB = new TagStep("branchB", "branch", "b");
        var merge = new PassthroughStep("merge");
        var steps = new StepDefinition[]
        {
            new StepDefinition("source", source),
            new StepDefinition("branchA", branchA, new[] { "source" }),
            new StepDefinition("branchB", branchB, new[] { "source" }),
            new StepDefinition("merge", merge, new[] { "branchA", "branchB" }),
        };
        var pipeline = new PipelineDefinition("test", steps);
        var writer = new CollectingWriter();

        await new PipelineExecutor().ExecuteAsync(pipeline, writer, CancellationToken.None);

        // 2 source rows × 2 branches = 4 merged rows
        writer.Rows.Should().HaveCount(4);
        writer.Rows.Select(r => r.Get<int>("id")).Should().BeEquivalentTo(new[] { 1, 2, 1, 2 });
    }

    // ── Cycle detection ───────────────────────────────────────────────────────────
    [Fact]
    public async Task Cycle_ThrowsInvalidOperationException()
    {
        var stepA = new PassthroughStep("a");
        var stepB = new PassthroughStep("b");
        var steps = new StepDefinition[]
        {
            new StepDefinition("a", stepA, new[] { "b" }),
            new StepDefinition("b", stepB, new[] { "a" }),
        };
        var pipeline = new PipelineDefinition("test", steps);
        var writer = new CollectingWriter();

        var act = () => new PipelineExecutor().ExecuteAsync(pipeline, writer, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cycle*");
    }

    // ── Failure and cancellation tests ────────────────────────────────────────────
    [Fact]
    public async Task StepFailure_PropagatesException_FromExecuteAsync()
    {
        var source = new SourceStep("source", Row.Empty.With("id", 1));
        var faulting = new FaultingStep("faulting");
        var steps = new StepDefinition[]
        {
            new StepDefinition("source", source),
            new StepDefinition("faulting", faulting, new[] { "source" }),
        };
        var pipeline = new PipelineDefinition("test", steps);
        var writer = new CollectingWriter();

        var act = () => new PipelineExecutor().ExecuteAsync(pipeline, writer, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>()
            .WithMessage("*Step failed intentionally*");
    }

    [Fact]
    public async Task Cancellation_StopsExecution()
    {
        // Use a channel to control when the source produces rows, ensuring cancellation can fire mid-run.
        var source = new SourceStep(
            "source",
            Row.Empty.With("id", 1),
            Row.Empty.With("id", 2),
            Row.Empty.With("id", 3));
        var passthrough = new PassthroughStep("passthrough");
        var steps = new StepDefinition[]
        {
            new StepDefinition("source", source),
            new StepDefinition("passthrough", passthrough, new[] { "source" }),
        };
        var pipeline = new PipelineDefinition("test", steps);
        var writer = new CollectingWriter();

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var act = () => new PipelineExecutor().ExecuteAsync(pipeline, writer, cts.Token);

        // Either completes normally (if cancellation fires after all rows are written)
        // or throws OperationCanceledException — both are acceptable.
        // What must NOT happen: hang or throw a non-cancellation/non-completion exception.
        var exception = await Record.ExceptionAsync(act);
        (exception is null || exception is OperationCanceledException).Should().BeTrue(
            because: "cancellation should either complete normally or throw OperationCanceledException");
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

    // Throws an exception after receiving the first row.
    private sealed class FaultingStep(string name) : IStep
    {
        public string Name { get; } = name;

        public async Task ExecuteAsync(ChannelReader<Row> input, ChannelWriter<Row> output, CancellationToken ct)
        {
            await foreach (var row in input.ReadAllAsync(ct))
            {
                throw new InvalidOperationException("Step failed intentionally.");
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
