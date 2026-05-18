using DistSharp.Core.Abstractions;
using DistSharp.Core.Sync;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace DistSharp.Cli.Commands;

/// <summary>Handles the <c>dataset sync</c> command.</summary>
public sealed class DatasetSyncCommandHandler
{
    private readonly ISolutionAnalyzer analyzer;
    private readonly ILlmProviderFactory providerFactory;
    private readonly IAnsiConsole console;
    private readonly ILogger<DatasetSyncCommandHandler> logger;
    private readonly ILoggerFactory loggerFactory;

    /// <summary>Initializes a new instance of the <see cref="DatasetSyncCommandHandler"/> class.</summary>
    /// <param name="analyzer">The solution analyser.</param>
    /// <param name="providerFactory">The LLM provider factory.</param>
    /// <param name="console">The Spectre console for output.</param>
    /// <param name="logger">Logger for error reporting.</param>
    /// <param name="loggerFactory">Logger factory for creating typed loggers.</param>
    public DatasetSyncCommandHandler(
        ISolutionAnalyzer analyzer,
        ILlmProviderFactory providerFactory,
        IAnsiConsole console,
        ILogger<DatasetSyncCommandHandler> logger,
        ILoggerFactory loggerFactory)
    {
        this.analyzer = analyzer;
        this.providerFactory = providerFactory;
        this.console = console;
        this.logger = logger;
        this.loggerFactory = loggerFactory;
    }

    /// <summary>Runs the dataset sync command.</summary>
    /// <param name="options">The parsed command options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Process exit code: 0 on success, 1 on failure, 2 on bad arguments.</returns>
    public async Task<int> InvokeAsync(DatasetSyncCommandOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(options.DatasetDir))
        {
            this.console.MarkupLine("[red]Error: dataset directory is required.[/]");
            return 2;
        }

        if (string.IsNullOrEmpty(options.Solution))
        {
            this.console.MarkupLine("[red]Error: --solution is required.[/]");
            return 2;
        }

        try
        {
            var provider = this.providerFactory.Create(options.Provider);
            var orchestrator = new DatasetSyncOrchestrator(
                this.analyzer,
                provider,
                this.loggerFactory.CreateLogger<DatasetSyncOrchestrator>());

            var plan = await orchestrator.SyncAsync(
                options.DatasetDir,
                options.Solution,
                options.DatasetType,
                options.OrphanPolicy,
                options.DryRun,
                cancellationToken).ConfigureAwait(false);

            this.PrintPlan(plan, options.DryRun);
            return 0;
        }
        catch (OperationCanceledException)
        {
            this.console.MarkupLine("[yellow]Cancelled.[/]");
            return 130;
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "dataset sync failed");
            this.console.MarkupLine($"[red]Failed: {ex.Message}[/]");
            return 1;
        }
    }

    private void PrintPlan(SyncPlan plan, bool dryRun)
    {
        var label = dryRun ? "[bold yellow](dry run)[/] " : string.Empty;
        this.console.MarkupLine($"{label}[bold]Dataset sync plan[/]");

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Category")
            .AddColumn(new TableColumn("Count").RightAligned());

        table.AddRow("[green]New[/]", plan.NewCount.ToString());
        table.AddRow("[yellow]Stale (will regenerate)[/]", plan.StaleCount.ToString());
        table.AddRow("[grey]Unchanged (skipped)[/]", plan.UnchangedCount.ToString());
        table.AddRow("[red]Orphaned[/]", plan.OrphanCount.ToString());
        table.AddRow("[cyan]Estimated LLM calls[/]", plan.EstimatedLlmCalls.ToString());

        this.console.Write(table);

        if (!dryRun)
        {
            this.console.MarkupLine("[green]Sync complete.[/]");
        }
    }
}
