using DistSharp.Core.Abstractions;

namespace DistSharp.Core.Pipeline;

/// <summary>Executes a <see cref="PipelineDefinition"/>, routing rows through steps and into a dataset writer.</summary>
public interface IPipelineExecutor
{
    /// <summary>Runs all steps in <paramref name="pipeline"/> and writes output rows to <paramref name="writer"/>.</summary>
    /// <param name="pipeline">The pipeline to execute.</param>
    /// <param name="writer">The sink that receives completed rows from the final step(s).</param>
    /// <param name="cancellationToken">Token to cancel the run.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous execution.</returns>
    Task ExecuteAsync(
        PipelineDefinition pipeline,
        IDatasetWriter writer,
        CancellationToken cancellationToken);
}
