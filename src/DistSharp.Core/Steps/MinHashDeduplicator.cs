using System.Text;
using System.Threading.Channels;
using DistSharp.Core.Models;
using DistSharp.Core.Pipeline;
using DistSharp.Core.Steps.Internal;

namespace DistSharp.Core.Steps;

/// <summary>
/// Drops rows whose Jaccard-similarity (estimated via MinHash) to any previously-seen row exceeds
/// <see cref="MinHashOptions.Threshold"/>. Comparison is performed against the field named
/// <see cref="MinHashOptions.Field"/>.
/// </summary>
public sealed class MinHashDeduplicator : IStep
{
    private readonly MinHashOptions options;
    private readonly uint[] hashSeeds;

    /// <summary>Initializes a new instance of the <see cref="MinHashDeduplicator"/> class.</summary>
    /// <param name="name">The step name.</param>
    /// <param name="options">Sampler configuration.</param>
    public MinHashDeduplicator(string name, MinHashOptions options)
    {
        this.Name = name;
        this.options = options;
        var random = new Random(options.Seed);
        this.hashSeeds = new uint[options.NumHashes];
        for (var i = 0; i < options.NumHashes; i++)
        {
            this.hashSeeds[i] = (uint)random.Next();
        }
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public async Task ExecuteAsync(ChannelReader<Row> input, ChannelWriter<Row> output, CancellationToken cancellationToken)
    {
        var signatures = new List<uint[]>();
        var matchThreshold = (int)Math.Ceiling(this.options.Threshold * this.options.NumHashes);

        await foreach (var row in input.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var text = row.Get<string>(this.options.Field) ?? string.Empty;
            var signature = this.ComputeSignature(text);

            var isDuplicate = false;
            foreach (var existing in signatures)
            {
                if (CountEqualSlots(signature, existing) >= matchThreshold)
                {
                    isDuplicate = true;
                    break;
                }
            }

            if (isDuplicate)
            {
                continue;
            }

            signatures.Add(signature);
            await output.WriteAsync(row, cancellationToken).ConfigureAwait(false);
        }
    }

    private static int CountEqualSlots(uint[] a, uint[] b)
    {
        var count = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i])
            {
                count++;
            }
        }

        return count;
    }

    private static List<string> Shingle(string text, int size)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<string>();
        }

        // Word-level shingles, lowercased and whitespace-normalised.
        var tokens = text
            .ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\r', '\n', '.', ',', ';', ':', '(', ')', '{', '}', '[', ']', '"', '\'', '`' }, StringSplitOptions.RemoveEmptyEntries);

        var result = new List<string>();
        if (tokens.Length < size)
        {
            // Short input: use the whole thing as one shingle so we still produce a signature.
            result.Add(string.Join(' ', tokens));
            return result;
        }

        for (var i = 0; i <= tokens.Length - size; i++)
        {
            result.Add(string.Join(' ', tokens, i, size));
        }

        return result;
    }

    private uint[] ComputeSignature(string text)
    {
        var shingles = Shingle(text, this.options.ShingleSize);
        var signature = new uint[this.options.NumHashes];
        for (var i = 0; i < signature.Length; i++)
        {
            signature[i] = uint.MaxValue;
        }

        if (shingles.Count == 0)
        {
            return signature;
        }

        foreach (var shingle in shingles)
        {
            var bytes = Encoding.UTF8.GetBytes(shingle);
            for (var i = 0; i < this.hashSeeds.Length; i++)
            {
                var h = MurmurHash3.Hash32(bytes, this.hashSeeds[i]);
                if (h < signature[i])
                {
                    signature[i] = h;
                }
            }
        }

        return signature;
    }
}
