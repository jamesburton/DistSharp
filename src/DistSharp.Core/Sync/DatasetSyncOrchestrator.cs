using System.Text;
using System.Text.Json;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;
using DistSharp.Core.Prompts;
using Microsoft.Extensions.Logging;

namespace DistSharp.Core.Sync;

/// <summary>
/// Orchestrates a local sync: compares the current solution against an existing dataset manifest,
/// regenerates only new or stale rows, applies the orphan policy, and rewrites the manifest.
/// Transport (pull/push) is out of scope for Phase 1 — see Phase 2 for HF/git transports.
/// </summary>
public sealed class DatasetSyncOrchestrator
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly ISolutionAnalyzer analyzer;
    private readonly ILlmProvider llmProvider;
    private readonly ILogger<DatasetSyncOrchestrator> logger;

    /// <summary>Initializes a new instance of the <see cref="DatasetSyncOrchestrator"/> class.</summary>
    /// <param name="analyzer">Roslyn solution analyser used to extract current symbols.</param>
    /// <param name="llmProvider">LLM provider for generating new and stale rows.</param>
    /// <param name="logger">Logger.</param>
    public DatasetSyncOrchestrator(
        ISolutionAnalyzer analyzer,
        ILlmProvider llmProvider,
        ILogger<DatasetSyncOrchestrator> logger)
    {
        this.analyzer = analyzer;
        this.llmProvider = llmProvider;
        this.logger = logger;
    }

    /// <summary>
    /// Runs the local sync plan and returns a <see cref="SyncPlan"/> describing what was done (or would be done).
    /// </summary>
    /// <param name="datasetDir">
    /// Root directory of the dataset. Must contain a <c>data/</c> sub-directory with at least one <c>.jsonl</c> file,
    /// or the sync will generate a fresh dataset. The <c>_distsharp/manifest.json</c> is read and rewritten here.
    /// </param>
    /// <param name="solutionPath">Path to the solution (<c>.sln</c>, <c>.csproj</c>, or directory) to analyse.</param>
    /// <param name="datasetType">Dataset type driving prompt selection (e.g. <c>explanation</c>).</param>
    /// <param name="orphanPolicy">What to do with rows whose symbols no longer exist in the solution.</param>
    /// <param name="dryRun">When <see langword="true"/>, no LLM calls are made and no files are written.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="SyncPlan"/> summarising new, stale, unchanged, and orphaned row counts.</returns>
    public async Task<SyncPlan> SyncAsync(
        string datasetDir,
        string solutionPath,
        string datasetType,
        OrphanPolicy orphanPolicy,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(datasetDir, "_distsharp", "manifest.json");
        var dataDir = Path.Combine(datasetDir, "data");
        var dataFile = Path.Combine(dataDir, "train.jsonl");

        var existing = await DatasetManifest.LoadAsync(manifestPath, cancellationToken).ConfigureAwait(false);

        // Extract symbols and build SymbolWithBody for each.
        var analysisOptions = new SolutionAnalysisOptions { MinComplexity = 1 };
        var allSymbols = new List<ExtractedSymbol>();
        await foreach (var sym in this.analyzer.AnalyzeAsync(solutionPath, analysisOptions, cancellationToken).ConfigureAwait(false))
        {
            allSymbols.Add(sym);
        }

        var promptVersion = RowIdentity.DefaultPromptVersion(datasetType);
        var current = allSymbols
            .Select(s => new SymbolWithBody(
                s.FullyQualifiedName,
                s.Kind,
                datasetType,
                promptVersion,
                RowIdentity.ComputeBodySha(s.BodyText)))
            .ToList();

        var diff = DatasetDiff.Diff(existing, current);
        var plan = new SyncPlan(diff.NewRows.Count, diff.StaleRows.Count, diff.UnchangedRows.Count, diff.OrphanRows.Count);

        this.logger.LogInformation(
            "Sync plan: {New} new, {Stale} stale, {Unchanged} unchanged, {Orphan} orphaned",
            plan.NewCount,
            plan.StaleCount,
            plan.UnchangedCount,
            plan.OrphanCount);

        if (dryRun)
        {
            return plan;
        }

        // Build symbol lookup by FQN for fast access when generating.
        var symbolByFqn = allSymbols.ToDictionary(s => s.FullyQualifiedName, StringComparer.Ordinal);

        // Load existing JSONL rows (by row_id) so we can preserve unchanged rows.
        var existingRowsByOffset = await LoadExistingRowsAsync(dataFile, existing, cancellationToken).ConfigureAwait(false);

        // Generate new rows.
        var generatedRows = new List<(ManifestRow Manifest, Dictionary<string, object?> Fields)>();

        foreach (var sym in diff.NewRows)
        {
            if (!symbolByFqn.TryGetValue(sym.SymbolFqn, out var extractedSymbol))
            {
                continue;
            }

            var fields = await this.GenerateRowAsync(extractedSymbol, datasetType, cancellationToken).ConfigureAwait(false);
            if (fields is null)
            {
                continue;
            }

            var rowId = RowIdentity.ComputeRowId(sym.SymbolFqn, sym.DatasetType, sym.PromptVersion);
            generatedRows.Add((new ManifestRow
            {
                Id = rowId,
                SymbolFqn = sym.SymbolFqn,
                SymbolKind = sym.SymbolKind,
                DatasetType = sym.DatasetType,
                BodySha = sym.BodySha,
                PromptVersion = sym.PromptVersion,
                RowOffset = 0, // will be reassigned
            }, fields));
        }

        // Regenerate stale rows.
        var staleReplacements = new Dictionary<string, (ManifestRow Manifest, Dictionary<string, object?> Fields)>(StringComparer.Ordinal);
        foreach (var (staleRow, sym) in diff.StaleRows)
        {
            if (!symbolByFqn.TryGetValue(sym.SymbolFqn, out var extractedSymbol))
            {
                continue;
            }

            var fields = await this.GenerateRowAsync(extractedSymbol, datasetType, cancellationToken).ConfigureAwait(false);
            if (fields is null)
            {
                continue;
            }

            var updatedManifestRow = new ManifestRow
            {
                Id = staleRow.Id,
                SymbolFqn = staleRow.SymbolFqn,
                SymbolKind = staleRow.SymbolKind,
                DatasetType = staleRow.DatasetType,
                BodySha = sym.BodySha,
                PromptVersion = staleRow.PromptVersion,
                RowOffset = 0, // will be reassigned
            };
            staleReplacements[staleRow.Id] = (updatedManifestRow, fields);
        }

        // Determine which orphan row IDs to drop from the active dataset.
        var orphanIds = new HashSet<string>(diff.OrphanRows.Select(r => r.Id), StringComparer.Ordinal);

        await this.WriteDatasetAsync(
            dataFile,
            dataDir,
            existing,
            existingRowsByOffset,
            diff.UnchangedRows,
            generatedRows,
            staleReplacements,
            orphanIds,
            orphanPolicy,
            datasetDir,
            cancellationToken).ConfigureAwait(false);

        return plan;
    }

    private static async Task<Dictionary<string, List<Dictionary<string, object?>>>> LoadExistingRowsAsync(
        string dataFile,
        DatasetManifest? existing,
        CancellationToken cancellationToken)
    {
        // Key: row_id → raw field dict loaded from JSONL at the row's recorded offset.
        var result = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal);

        if (!File.Exists(dataFile) || existing is null)
        {
            return result;
        }

        // Build offset → row_id index from the manifest.
        var rowIdByOffset = existing.Rows.ToDictionary(r => r.RowOffset, r => r.Id);

        var lines = await File.ReadAllLinesAsync(dataFile, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!rowIdByOffset.TryGetValue(i, out var rowId))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    fields[prop.Name] = prop.Value.Clone();
                }

                if (!result.TryGetValue(rowId, out var list))
                {
                    list = new List<Dictionary<string, object?>>();
                    result[rowId] = list;
                }

                list.Add(fields);
            }
            catch (JsonException)
            {
                // Skip malformed lines — they will be missing from the rewritten file.
            }
        }

        return result;
    }

    private async Task<Dictionary<string, object?>?> GenerateRowAsync(
        ExtractedSymbol symbol,
        string datasetType,
        CancellationToken cancellationToken)
    {
        try
        {
            var prompt = PromptBuilderRegistry.Build(datasetType, symbol);
            var raw = await this.llmProvider.CompleteAsync(
                prompt.Messages,
                new LlmRequestOptions(),
                cancellationToken).ConfigureAwait(false);

            var parsed = prompt.ParseResponse(raw);
            if (parsed is null)
            {
                this.logger.LogDebug("SyncOrchestrator: response did not parse for {Symbol}; skipping.", symbol.FullyQualifiedName);
                return null;
            }

            var fields = new Dictionary<string, object?>(prompt.PreparedFields.Count + parsed.Count, StringComparer.Ordinal);
            foreach (var kv in prompt.PreparedFields)
            {
                fields[kv.Key] = kv.Value;
            }

            foreach (var kv in parsed)
            {
                fields[kv.Key] = kv.Value;
            }

            return fields;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "SyncOrchestrator: LLM call failed for {Symbol}; skipping.", symbol.FullyQualifiedName);
            return null;
        }
    }

    private async Task WriteDatasetAsync(
        string dataFile,
        string dataDir,
        DatasetManifest? existing,
        Dictionary<string, List<Dictionary<string, object?>>> existingRowsByOffset,
        IReadOnlyList<ManifestRow> unchangedRows,
        List<(ManifestRow Manifest, Dictionary<string, object?> Fields)> newRows,
        Dictionary<string, (ManifestRow Manifest, Dictionary<string, object?> Fields)> staleReplacements,
        HashSet<string> orphanIds,
        OrphanPolicy orphanPolicy,
        string datasetDir,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dataDir);

        // Collect archived orphan rows before rewriting.
        if (orphanPolicy == OrphanPolicy.Archive && existing is not null)
        {
            await this.ArchiveOrphansAsync(
                datasetDir,
                existing,
                existingRowsByOffset,
                orphanIds,
                cancellationToken).ConfigureAwait(false);
        }

        // Build the new ordered row list: unchanged (preserving order) + new rows appended.
        // Stale rows replace unchanged at the same logical position via staleReplacements.
        var outputRows = new List<(ManifestRow Manifest, Dictionary<string, object?> Fields)>();

        // Determine which row IDs to include from the existing dataset.
        var allExistingRows = existing?.Rows ?? new List<ManifestRow>();
        foreach (var row in allExistingRows)
        {
            if (orphanIds.Contains(row.Id) && orphanPolicy != OrphanPolicy.Keep)
            {
                // Drop or archive — exclude from active dataset.
                continue;
            }

            if (staleReplacements.TryGetValue(row.Id, out var replacement))
            {
                outputRows.Add(replacement);
                continue;
            }

            // Unchanged or kept orphan — restore from loaded lines.
            if (existingRowsByOffset.TryGetValue(row.Id, out var fieldsList) && fieldsList.Count > 0)
            {
                outputRows.Add((row, fieldsList[0]));
            }
        }

        // Append newly generated rows.
        outputRows.AddRange(newRows);

        // Rewrite the JSONL file.
        var sb = new StringBuilder();
        for (var i = 0; i < outputRows.Count; i++)
        {
            var (manifestRow, fields) = outputRows[i];
            manifestRow.RowOffset = i;
            sb.AppendLine(JsonSerializer.Serialize(fields, JsonOpts));
        }

        await File.WriteAllTextAsync(dataFile, sb.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        // Write updated manifest.
        var headSha = GitHelper.TryGetHeadSha(dataFile);
        var branch = GitHelper.TryGetBranch(dataFile);
        string? commitRef = headSha is not null
            ? (branch is not null ? $"{branch}@{headSha[..Math.Min(7, headSha.Length)]}" : headSha[..Math.Min(7, headSha.Length)])
            : null;

        var manifest = new DatasetManifest
        {
            FormatVersion = 1,
            GeneratedAt = DateTimeOffset.UtcNow,
            Source = new ManifestSource
            {
                SolutionPath = existing?.Source?.SolutionPath,
                SolutionSha = headSha,
                Commit = commitRef,
            },
            Rows = outputRows.Select(r => r.Manifest).ToList(),
        };

        var manifestPath = Path.Combine(datasetDir, "_distsharp", "manifest.json");
        await manifest.SaveAsync(manifestPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task ArchiveOrphansAsync(
        string datasetDir,
        DatasetManifest existing,
        Dictionary<string, List<Dictionary<string, object?>>> existingRowsByOffset,
        HashSet<string> orphanIds,
        CancellationToken cancellationToken)
    {
        var archiveDir = Path.Combine(datasetDir, "_distsharp", "archive");
        Directory.CreateDirectory(archiveDir);

        var archivePath = Path.Combine(archiveDir, $"{DateTimeOffset.UtcNow:yyyyMMdd}.jsonl");

        var sb = new StringBuilder();
        foreach (var row in existing.Rows)
        {
            if (!orphanIds.Contains(row.Id))
            {
                continue;
            }

            if (existingRowsByOffset.TryGetValue(row.Id, out var fieldsList) && fieldsList.Count > 0)
            {
                sb.AppendLine(JsonSerializer.Serialize(fieldsList[0], JsonOpts));
            }
        }

        if (sb.Length > 0)
        {
            // Append to any existing archive for today.
            await File.AppendAllTextAsync(archivePath, sb.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            this.logger.LogInformation("SyncOrchestrator: archived {Count} orphan row(s) to {Path}", orphanIds.Count, archivePath);
        }
    }
}
