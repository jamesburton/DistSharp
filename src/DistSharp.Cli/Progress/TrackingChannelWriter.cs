using System.Threading.Channels;

namespace DistSharp.Cli.Progress;

/// <summary>Wraps a <see cref="ChannelWriter{T}"/> and increments a counter for each successful write.</summary>
/// <typeparam name="T">The item type.</typeparam>
internal sealed class TrackingChannelWriter<T> : ChannelWriter<T>
{
    private readonly ChannelWriter<T> inner;
    private readonly Action onWrite;

    internal TrackingChannelWriter(ChannelWriter<T> inner, Action onWrite)
    {
        this.inner = inner;
        this.onWrite = onWrite;
    }

    /// <inheritdoc/>
    public override bool TryComplete(Exception? error = null) => this.inner.TryComplete(error);

    /// <inheritdoc/>
    public override bool TryWrite(T item)
    {
        if (this.inner.TryWrite(item))
        {
            this.onWrite();
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
        => this.inner.WaitToWriteAsync(cancellationToken);

    /// <inheritdoc/>
    public override async ValueTask WriteAsync(T item, CancellationToken cancellationToken = default)
    {
        await this.inner.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        this.onWrite();
    }
}
