using System.Threading.Channels;
using DistSharp.Core.Models;

namespace DistSharp.Core.Pipeline;

/// <summary>
/// A single unit of work in a pipeline. Reads rows from <paramref name="input"/>, transforms or
/// filters them, and writes results to <paramref name="output"/>. Channel wiring is handled by
/// <see cref="IPipelineExecutor"/> — steps never create channels.
/// </summary>
public interface IStep
{
    /// <summary>Gets the unique name of this step within a pipeline.</summary>
    string Name { get; }

    /// <summary>Processes rows from <paramref name="input"/> and writes results to <paramref name="output"/>.</summary>
    /// <param name="input">The channel to read rows from. Completes when the upstream step is finished.</param>
    /// <param name="output">The channel to write result rows to.</param>
    /// <param name="cancellationToken">Token to cancel the step.</param>
    Task ExecuteAsync(
        ChannelReader<Row> input,
        ChannelWriter<Row> output,
        CancellationToken cancellationToken);
}
