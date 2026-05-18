using DistSharp.Core.Abstractions;
using DistSharp.Core.Models;
using DistSharp.Core.Sync;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace DistSharp.Cli.Commands;

/// <summary>
/// Handles the <c>dataset migrate</c> command — seeds <c>_distsharp/manifest.json</c> for an
/// existing dataset directory that was generated before sync support was added.
/// No LLM calls are made: this command only re-establishes row identity for existing rows.
/// </summary>
public sealed class DatasetMigrateCommandHandler
{
    private readonly ISolutionAnalyzer analyzer;
    private readonly IAnsiConsole console;
    private readonly ILogger<DatasetMigrateCommandHandler> logger;

    /// <summary>Initializes a new instance of the <see cref="DatasetMigrateCommandHandler"/> class.</summary>
    /// <param name="analyzer">The solution analyser used to re-extract symbols.</param>
    /// <param name="console">The Spectre console for output.</param>
    /// <param name="logger">Logger for warnings and diagnostics.</param>
    public DatasetMigrateCommandHandler(
        ISolutionAnalyzer analyzer,
        IAnsiConsole console,
        ILogger<DatasetMigrateCommandHandler> logger)
    {
        this.analyzer = analyzer;
        this.console = console;
        this.logger = logger;
    }

    /// <summary>Runs the dataset migrate command.</summary>
    /// <param name="options">The parsed command options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Process exit code: 0 on success, 1 on failure, 2 on bad arguments.</returns>
    public async Task<int> InvokeAsync(DatasetMigrateCommandOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(options.DatasetDir) || !Directory.Exists(options.DatasetDir))
        {
            this.console.MarkupLine($"[red]Dataset directory not found: {options.DatasetDir}[/]");
            return 2;
        }

        if (string.IsNullOrEmpty(options.Solution))
        {
            this.console.MarkupLine("[red]Error: --solution is required.[/]");
            return 2;
        }

        var manifestPath = Path.Combine(options.DatasetDir, "_distsharp", "manifest.json");
        if (File.Exists(manifestPath))
        {
            this.console.MarkupLine("[yellow]Warning: manifest.json already exists. Delete it first if you want to re-seed.[/]");
            return 2;
        }

        try
        {
            // Extract current symbols from solution (no LLM calls).
            var analysisOptions = new SolutionAnalysisOptions { MinComplexity = 1 };
            var symbols = new Dictionary<string, ExtractedSymbol>(StringComparer.Ordinal);

            await foreach (var sym in this.analyzer.AnalyzeAsync(options.Solution, analysisOptions, cancellationToken).ConfigureAwait(false))
            {
                symbols[sym.FullyQualifiedName] = sym;
            }

            this.console.MarkupLine($"Extracted [cyan]{symbols.Count}[/] symbols from solution.");

            // Count existing JSONL rows in the directory (flat layout — legacy format).
            var jsonlFiles = Directory.GetFiles(options.DatasetDir, "*.jsonl", SearchOption.TopDirectoryOnly);
            var promptVersion = RowIdentity.DefaultPromptVersion(options.DatasetType);

            var rows = new List<ManifestRow>();
            var skipped = 0;
            var offset = 0;

            foreach (var file in jsonlFiles)
            {
                var lines = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    // Legacy rows don't carry symbol_fqn — we can't map them back.
                    // Log a warning and skip. A future heuristic (§11 risk #6) could
                    // match by instruction-text similarity.
                    this.logger.LogWarning("migrate: cannot map row at offset {Offset} in {File} back to a symbol (no symbol_fqn in legacy rows); skipping.", offset, file);
                    skipped++;
                    offset++;
                }
            }

            // Build manifest from all symbols in the current solution. Row offsets are 0-based
            // within a notional rewrite of the data file; existing legacy data is NOT rewritten.
            var manifestRows = symbols.Values.Select((sym, i) => new ManifestRow
            {
                Id = RowIdentity.ComputeRowId(sym.FullyQualifiedName, options.DatasetType, promptVersion),
                SymbolFqn = sym.FullyQualifiedName,
                SymbolKind = sym.Kind,
                DatasetType = options.DatasetType,
                BodySha = RowIdentity.ComputeBodySha(sym.BodyText),
                PromptVersion = promptVersion,
                RowOffset = i,
            }).ToList();

            var headSha = GitHelper.TryGetHeadSha(options.Solution);
            var branch = GitHelper.TryGetBranch(options.Solution);
            var commitRef = headSha is not null
                ? (branch is not null ? $"{branch}@{headSha[..Math.Min(7, headSha.Length)]}" : headSha[..Math.Min(7, headSha.Length)])
                : null;

            var manifest = new DatasetManifest
            {
                FormatVersion = 1,
                GeneratedAt = DateTimeOffset.UtcNow,
                Source = new ManifestSource
                {
                    SolutionPath = options.Solution,
                    SolutionSha = headSha,
                    Commit = commitRef,
                },
                Rows = manifestRows,
            };

            await manifest.SaveAsync(manifestPath, cancellationToken).ConfigureAwait(false);

            this.console.MarkupLine($"[green]Seeded manifest with {manifestRows.Count} symbol(s). {skipped} legacy row(s) could not be mapped and were skipped.[/]");
            this.console.MarkupLine($"[grey]Manifest written to {manifestPath}[/]");
            return 0;
        }
        catch (OperationCanceledException)
        {
            this.console.MarkupLine("[yellow]Cancelled.[/]");
            return 130;
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "dataset migrate failed");
            this.console.MarkupLine($"[red]Failed: {ex.Message}[/]");
            return 1;
        }
    }
}
