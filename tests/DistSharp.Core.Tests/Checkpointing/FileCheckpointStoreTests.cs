using DistSharp.Core.Checkpointing;
using DistSharp.Core.Models;
using FluentAssertions;
using Xunit;

namespace DistSharp.Core.Tests.Checkpointing;

public sealed class FileCheckpointStoreTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(this.dir))
        {
            Directory.Delete(this.dir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ReturnsNull_WhenNoCheckpointExists()
    {
        var store = new FileCheckpointStore(this.dir);

        var result = await store.LoadAsync("run-1", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveThenLoad_RoundTrips_AllFields()
    {
        var store = new FileCheckpointStore(this.dir);
        var saved = new PipelineCheckpoint("run-1", "step-extract", 5000L, DateTimeOffset.UtcNow);

        await store.SaveAsync(saved, CancellationToken.None);
        var loaded = await store.LoadAsync("run-1", CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.PipelineId.Should().Be("run-1");
        loaded.StepName.Should().Be("step-extract");
        loaded.RowsWritten.Should().Be(5000L);
        loaded.SavedAt.Should().BeCloseTo(saved.SavedAt, TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task SaveAsync_Overwrites_PreviousCheckpoint()
    {
        var store = new FileCheckpointStore(this.dir);
        var first = new PipelineCheckpoint("run-1", "step-a", 100L, DateTimeOffset.UtcNow);
        var second = new PipelineCheckpoint("run-1", "step-b", 200L, DateTimeOffset.UtcNow);

        await store.SaveAsync(first, CancellationToken.None);
        await store.SaveAsync(second, CancellationToken.None);
        var loaded = await store.LoadAsync("run-1", CancellationToken.None);

        loaded!.RowsWritten.Should().Be(200L);
        loaded.StepName.Should().Be("step-b");
    }

    [Fact]
    public async Task LoadAsync_ReturnsNull_ForUnknownPipelineId()
    {
        var store = new FileCheckpointStore(this.dir);
        await store.SaveAsync(new PipelineCheckpoint("run-1", "step-a", 100L, DateTimeOffset.UtcNow), CancellationToken.None);

        var result = await store.LoadAsync("run-999", CancellationToken.None);

        result.Should().BeNull();
    }
}
