using System.Security.Cryptography;
using System.Text;

namespace DistSharp.Core.Sync;

/// <summary>Pure-function helpers for computing stable row identity values.</summary>
public static class RowIdentity
{
    /// <summary>
    /// Computes a stable 16-hex-character row ID from the symbol's identity triplet.
    /// The ID is independent of the symbol body — only the key changes when the symbol is renamed or the prompt version changes.
    /// </summary>
    /// <param name="symbolFqn">Fully-qualified symbol name.</param>
    /// <param name="datasetType">Dataset type (e.g. <c>explanation</c>).</param>
    /// <param name="promptVersion">Prompt version string (e.g. <c>explanation@1</c>).</param>
    /// <returns>First 16 hex characters of SHA-256(<paramref name="symbolFqn"/> + "\n" + <paramref name="datasetType"/> + "\n" + <paramref name="promptVersion"/>).</returns>
    public static string ComputeRowId(string symbolFqn, string datasetType, string promptVersion)
    {
        var input = $"{symbolFqn}\n{datasetType}\n{promptVersion}";
        return HexPrefix(input, length: 16);
    }

    /// <summary>
    /// Computes a full SHA-256 hex string of the normalised symbol body.
    /// The body is assumed to already be normalised (whitespace-trimmed) by the caller.
    /// </summary>
    /// <param name="normalisedBody">The symbol body text.</param>
    /// <returns>Full lowercase hex SHA-256 of the body.</returns>
    public static string ComputeBodySha(string normalisedBody)
    {
        return FullHex(normalisedBody);
    }

    /// <summary>Returns the canonical prompt version string for a dataset type at revision 1.</summary>
    /// <param name="datasetType">Dataset type (e.g. <c>explanation</c>).</param>
    /// <returns>Version string in the form <c>{datasetType}@1</c>.</returns>
    public static string DefaultPromptVersion(string datasetType) => $"{datasetType}@1";

    private static string HexPrefix(string input, int length)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..length];
    }

    private static string FullHex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
