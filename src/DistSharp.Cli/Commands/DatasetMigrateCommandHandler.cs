using DistSharp.Core.Sync;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace DistSharp.Cli.Commands;

/// <summary>
/// Handles the <c>dataset migrate</c> command — seeds an empty <c>_distsharp/manifest.json</c>
/// for an existing dataset directory that was generated before sync support was added.
/// Legacy JSONL rows do not carry <c>symbol_fqn</c>, so they cannot be mapped back to symbols;
/// the manifest is therefore seeded with an empty <c>rows[]</c>. The next <c>dataset sync</c>
/// run will treat all current symbols as new and regenerate all rows.
/// </summary>
public sealed class DatasetMigrateCommandHandler
{
    private readonly IAnsiConsole console;
    private readonly ILogger<DatasetMigrateCommandHandler> logger;

    /// <summary>Initializes a new instance of the <see cref="DatasetMigrateCommandHandler"/> class.</summary>
    /// <param name="console">The Spectre console for output.</param>
    /// <param name="logger">Logger for warnings and diagnostics.</param>
    public DatasetMigrateCommandHandler(
        IAnsiConsole console,
        ILogger<DatasetMigrateCommandHandler> logger)
    {
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
            // Count legacy JSONL rows so the user gets a useful summary.
            var jsonlFiles = Directory.GetFiles(options.DatasetDir, "*.jsonl", SearchOption.TopDirectoryOnly);
            var legacyRowCount = 0;

            foreach (var file in jsonlFiles)
            {
                var lines = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false);
                legacyRowCount += lines.Count(l => !string.IsNullOrWhiteSpace(l));
            }

            // Legacy rows don't carry symbol_fqn — mapping them back to symbols is not possible
            // without a heuristic (deferred to Phase 6, spec §11 risk #6).
            // Write a manifest with empty rows[]. The next `dataset sync` run will treat every
            // current symbol as new and regenerate all rows from scratch.
            this.logger.LogWarning(
                "migrate: {Count} legacy row(s) found but cannot be mapped back to symbols (no symbol_fqn). Seeding empty manifest; next sync will regenerate all rows.",
                legacyRowCount);

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
                Rows = new List<ManifestRow>(),
            };

            await manifest.SaveAsync(manifestPath, cancellationToken).ConfigureAwait(false);

            this.console.MarkupLine($"[green]Seeded empty manifest. {legacyRowCount} legacy row(s) could not be mapped (no symbol_fqn); next sync will regenerate all rows.[/]");
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
