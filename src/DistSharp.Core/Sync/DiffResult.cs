namespace DistSharp.Core.Sync;

/// <summary>The result of a diff between a stored manifest and the current symbol set.</summary>
/// <param name="NewRows">Symbols that have no corresponding manifest row — must be generated.</param>
/// <param name="StaleRows">Pairs of (existing manifest row, updated symbol) — must be regenerated.</param>
/// <param name="UnchangedRows">Manifest rows whose body SHA still matches — no LLM call needed.</param>
/// <param name="OrphanRows">Manifest rows whose symbol no longer exists in the solution — apply orphan policy.</param>
public sealed record DiffResult(
    IReadOnlyList<SymbolWithBody> NewRows,
    IReadOnlyList<(ManifestRow Existing, SymbolWithBody Current)> StaleRows,
    IReadOnlyList<ManifestRow> UnchangedRows,
    IReadOnlyList<ManifestRow> OrphanRows);
