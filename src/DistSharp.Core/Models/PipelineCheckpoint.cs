namespace DistSharp.Core.Models;

/// <summary>A point-in-time snapshot of pipeline progress, used to resume interrupted runs.</summary>
/// <param name="PipelineId">Stable identifier for the pipeline run (typically derived from the output directory path).</param>
/// <param name="StepName">Name of the step at which the checkpoint was saved.</param>
/// <param name="RowsWritten">Number of rows successfully written to the dataset at this checkpoint.</param>
/// <param name="SavedAt">UTC timestamp when the checkpoint was saved.</param>
public sealed record PipelineCheckpoint(
    string PipelineId,
    string StepName,
    long RowsWritten,
    DateTimeOffset SavedAt);
