using System.Text.Json;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;

namespace DistSharp.Core.Checkpointing;

/// <summary>
/// Persists pipeline checkpoints as JSON files under <c>&lt;baseDir&gt;/&lt;pipelineId&gt;.checkpoint.json</c>.
/// </summary>
public sealed class FileCheckpointStore : ICheckpointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string baseDir;

    /// <summary>Initializes a new instance of the <see cref="FileCheckpointStore"/> class that writes to <paramref name="baseDir"/>.</summary>
    /// <param name="baseDir">The directory in which checkpoint files are stored. Created on first write.</param>
    public FileCheckpointStore(string baseDir) => this.baseDir = baseDir;

    /// <inheritdoc/>
    public async Task<PipelineCheckpoint?> LoadAsync(string pipelineId, CancellationToken cancellationToken)
    {
        var path = this.FilePath(pipelineId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PipelineCheckpoint>(stream, JsonOptions, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task SaveAsync(PipelineCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(this.baseDir);
        var path = this.FilePath(checkpoint.PipelineId);
        var tempPath = path + ".tmp";

        await using (var stream = File.Open(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, checkpoint, FileCheckpointStore.JsonOptions, cancellationToken);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private string FilePath(string pipelineId) =>
        Path.Combine(this.baseDir, $"{pipelineId}.checkpoint.json");
}
