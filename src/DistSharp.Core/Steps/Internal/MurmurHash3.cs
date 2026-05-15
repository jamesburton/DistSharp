namespace DistSharp.Core.Steps.Internal;

/// <summary>32-bit MurmurHash3 implementation (Austin Appleby, public domain).</summary>
internal static class MurmurHash3
{
    /// <summary>Computes a 32-bit MurmurHash3 of <paramref name="data"/> with the given <paramref name="seed"/>.</summary>
    /// <param name="data">The data to hash.</param>
    /// <param name="seed">The seed value.</param>
    /// <returns>A 32-bit hash.</returns>
    public static uint Hash32(ReadOnlySpan<byte> data, uint seed)
    {
        const uint C1 = 0xcc9e2d51;
        const uint C2 = 0x1b873593;

        uint h1 = seed;
        var length = data.Length;
        var blockCount = length / 4;

        for (var i = 0; i < blockCount; i++)
        {
            var offset = i * 4;
            uint k1 = (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
            k1 *= C1;
            k1 = RotateLeft(k1, 15);
            k1 *= C2;

            h1 ^= k1;
            h1 = RotateLeft(h1, 13);
            h1 = (h1 * 5) + 0xe6546b64;
        }

        uint tail = 0;
        var tailStart = blockCount * 4;
        var remaining = length - tailStart;
        if (remaining >= 3)
        {
            tail ^= (uint)data[tailStart + 2] << 16;
        }

        if (remaining >= 2)
        {
            tail ^= (uint)data[tailStart + 1] << 8;
        }

        if (remaining >= 1)
        {
            tail ^= data[tailStart];
            tail *= C1;
            tail = RotateLeft(tail, 15);
            tail *= C2;
            h1 ^= tail;
        }

        h1 ^= (uint)length;
        h1 ^= h1 >> 16;
        h1 *= 0x85ebca6b;
        h1 ^= h1 >> 13;
        h1 *= 0xc2b2ae35;
        h1 ^= h1 >> 16;

        return h1;
    }

    private static uint RotateLeft(uint value, int bits) => (value << bits) | (value >> (32 - bits));
}
