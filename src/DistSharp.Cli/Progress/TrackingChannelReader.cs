using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace DistSharp.Cli.Progress;

/// <summary>Wraps a <see cref="ChannelReader{T}"/> and increments a counter for each successful read.</summary>
/// <typeparam name="T">The item type.</typeparam>
internal sealed class TrackingChannelReader<T> : ChannelReader<T>
{
    private readonly ChannelReader<T> inner;
    private readonly Action onRead;

    internal TrackingChannelReader(ChannelReader<T> inner, Action onRead)
    {
        this.inner = inner;
        this.onRead = onRead;
    }

    /// <inheritdoc/>
    public override Task Completion => this.inner.Completion;

    /// <inheritdoc/>
    public override bool CanCount => this.inner.CanCount;

    /// <inheritdoc/>
    public override int Count => this.inner.Count;

    /// <inheritdoc/>
    public override bool TryRead([MaybeNullWhen(false)] out T item)
    {
        if (this.inner.TryRead(out item))
        {
            this.onRead();
            return true;
        }

        item = default;
        return false;
    }

    /// <inheritdoc/>
    public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        => this.inner.WaitToReadAsync(cancellationToken);

    /// <inheritdoc/>
    public override async ValueTask<T> ReadAsync(CancellationToken cancellationToken = default)
    {
        var item = await this.inner.ReadAsync(cancellationToken).ConfigureAwait(false);
        this.onRead();
        return item;
    }
}
