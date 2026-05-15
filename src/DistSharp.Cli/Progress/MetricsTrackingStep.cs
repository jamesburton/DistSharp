using System.Threading.Channels;
using DistSharp.Core.Models;
using DistSharp.Core.Pipeline;

namespace DistSharp.Cli.Progress;

/// <summary>Decorates an <see cref="IStep"/> to track input/output row counts and step status in a <see cref="StepMetrics"/>.</summary>
public sealed class MetricsTrackingStep : IStep
{
    private readonly IStep inner;

    /// <summary>Initializes a new instance of the <see cref="MetricsTrackingStep"/> class.</summary>
    /// <param name="inner">The wrapped step.</param>
    public MetricsTrackingStep(IStep inner)
    {
        this.inner = inner;
        this.Metrics = new StepMetrics { Name = inner.Name };
    }

    /// <inheritdoc/>
    public string Name => this.inner.Name;

    /// <summary>Gets the metrics counters updated as the step runs.</summary>
    public StepMetrics Metrics { get; }

    /// <inheritdoc/>
    public async Task ExecuteAsync(ChannelReader<Row> input, ChannelWriter<Row> output, CancellationToken cancellationToken)
    {
        var trackedInput = new TrackingChannelReader<Row>(input, () => this.Metrics.IncrementRowsIn());
        var trackedOutput = new TrackingChannelWriter<Row>(output, () => this.Metrics.IncrementRowsOut());

        this.Metrics.Status = "running";
        try
        {
            await this.inner.ExecuteAsync(trackedInput, trackedOutput, cancellationToken).ConfigureAwait(false);
            this.Metrics.Status = "complete";
        }
        catch (OperationCanceledException)
        {
            this.Metrics.Status = "cancelled";
            throw;
        }
        catch (Exception ex)
        {
            this.Metrics.Status = "failed";
            this.Metrics.Error = ex.Message;
            throw;
        }
    }
}
