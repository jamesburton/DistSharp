namespace DistSharp.Core.Sync;

/// <summary>Pure-function diff between a stored manifest and the current set of symbols.</summary>
public static class DatasetDiff
{
    /// <summary>
    /// Compares <paramref name="remote"/> (the existing manifest) against <paramref name="current"/> (live symbols)
    /// and classifies each symbol+row into one of four categories.
    /// </summary>
    /// <param name="remote">The existing manifest, or <see langword="null"/> if no manifest exists yet.</param>
    /// <param name="current">The live symbols extracted from the solution.</param>
    /// <returns>A <see cref="DiffResult"/> describing what must be generated, regenerated, skipped, or dropped.</returns>
    public static DiffResult Diff(DatasetManifest? remote, IReadOnlyList<SymbolWithBody> current)
    {
        var newRows = new List<SymbolWithBody>();
        var staleRows = new List<(ManifestRow Existing, SymbolWithBody Current)>();
        var unchangedRows = new List<ManifestRow>();
        var orphanRows = new List<ManifestRow>();

        // Index the remote manifest by row_id for O(1) lookup.
        var remoteById = remote?.Rows
            .ToDictionary(r => r.Id, StringComparer.Ordinal)
            ?? new Dictionary<string, ManifestRow>(StringComparer.Ordinal);

        // Track which remote row IDs are matched by the current symbols.
        var matchedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var symbol in current)
        {
            var rowId = RowIdentity.ComputeRowId(symbol.SymbolFqn, symbol.DatasetType, symbol.PromptVersion);

            if (!remoteById.TryGetValue(rowId, out var existing))
            {
                // No remote row for this symbol — new.
                newRows.Add(symbol);
            }
            else
            {
                matchedIds.Add(rowId);

                if (existing.BodySha == symbol.BodySha)
                {
                    // Row exists and body hasn't changed — skip.
                    unchangedRows.Add(existing);
                }
                else
                {
                    // Row exists but body changed — regenerate.
                    staleRows.Add((existing, symbol));
                }
            }
        }

        // Any remote row not matched by current symbols is an orphan (symbol deleted/renamed).
        foreach (var row in remoteById.Values)
        {
            if (!matchedIds.Contains(row.Id))
            {
                orphanRows.Add(row);
            }
        }

        return new DiffResult(newRows, staleRows, unchangedRows, orphanRows);
    }
}
