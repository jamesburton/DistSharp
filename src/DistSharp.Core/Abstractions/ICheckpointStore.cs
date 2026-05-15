using DistSharp.Core.Models;

namespace DistSharp.Core.Abstractions;

/// <summary>Persists and retrieves pipeline checkpoints to support resumable runs.</summary>
public interface ICheckpointStore
{
    /// <summary>Loads the most recent checkpoint for <paramref name="pipelineId"/>, or <see langword="null"/> if none exists.</summary>
    /// <param name="pipelineId">The stable pipeline identifier.</param>
    /// <param name="cancellationToken">Token to cancel the load.</param>
    /// <returns>The checkpoint, or <see langword="null"/>.</returns>
    Task<PipelineCheckpoint?> LoadAsync(string pipelineId, CancellationToken cancellationToken);

    /// <summary>Saves <paramref name="checkpoint"/>, overwriting any previous checkpoint for the same pipeline.</summary>
    /// <param name="checkpoint">The checkpoint to persist.</param>
    /// <param name="cancellationToken">Token to cancel the save.</param>
    Task SaveAsync(PipelineCheckpoint checkpoint, CancellationToken cancellationToken);
}
