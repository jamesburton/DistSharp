using DistSharp.Cli.Progress;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Pipeline;
using DistSharp.Core.Sync;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace DistSharp.Cli.Commands;

/// <summary>Handles the <c>generate</c> command.</summary>
public sealed class GenerateCommandHandler
{
    private readonly PipelineBuilder pipelineBuilder;
    private readonly IDatasetWriterFactory writerFactory;
    private readonly IPipelineExecutor executor;
    private readonly LiveProgressDisplay progress;
    private readonly IAnsiConsole console;
    private readonly ILogger<GenerateCommandHandler> logger;
    private readonly ILoggerFactory loggerFactory;

    /// <summary>Initializes a new instance of the <see cref="GenerateCommandHandler"/> class.</summary>
    /// <param name="pipelineBuilder">Builds pipeline definitions from configuration.</param>
    /// <param name="writerFactory">Creates dataset writers from output configuration.</param>
    /// <param name="executor">Executes the pipeline graph.</param>
    /// <param name="progress">Renders live progress to the console.</param>
    /// <param name="console">The Spectre console for output.</param>
    /// <param name="logger">Logger for error reporting.</param>
    /// <param name="loggerFactory">Logger factory for creating typed loggers.</param>
    public GenerateCommandHandler(
        PipelineBuilder pipelineBuilder,
        IDatasetWriterFactory writerFactory,
        IPipelineExecutor executor,
        LiveProgressDisplay progress,
        IAnsiConsole console,
        ILogger<GenerateCommandHandler> logger,
        ILoggerFactory loggerFactory)
    {
        this.pipelineBuilder = pipelineBuilder;
        this.writerFactory = writerFactory;
        this.executor = executor;
        this.progress = progress;
        this.console = console;
        this.logger = logger;
        this.loggerFactory = loggerFactory;
    }

    /// <summary>Runs the generate command.</summary>
    /// <param name="options">The parsed command options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Process exit code: 0 on success, 1 on failure, 2 on bad arguments, 130 on cancellation.</returns>
    public async Task<int> InvokeAsync(GenerateCommandOptions options, CancellationToken cancellationToken)
    {
        this.console.MarkupLine($"[bold]Generating dataset from [cyan]{options.Solution}[/][/]");

        if (string.IsNullOrEmpty(options.Solution))
        {
            this.console.MarkupLine("[red]Error: solution path is required.[/]");
            return 2;
        }

        var config = BuiltInPipeline.Create(
            options.Solution,
            options.DatasetType,
            options.MaxRows,
            options.OutDir,
            options.Format,
            options.Provider,
            options.Model,
            options.Workers,
            options.IncludeTests,
            options.IncludeGenerated,
            options.MinComplexity,
            options.ExcludeNamespaces,
            options.Seed,
            options.DryRun);

        var pipeline = this.pipelineBuilder.Build(config);
        var metrics = pipeline.Steps
            .Select(s => ((MetricsTrackingStep)s.Step).Metrics)
            .ToList();

        var innerWriter = this.writerFactory.Create(config.Output);
        await using var writer = new ManifestWriter(
            innerWriter,
            config.Output.Dir,
            options.Solution,
            options.DatasetType,
            this.loggerFactory.CreateLogger<ManifestWriter>());

        try
        {
            await this.progress.RunAsync(
                metrics,
                ct => this.executor.ExecuteAsync(pipeline, writer, ct),
                cancellationToken).ConfigureAwait(false);

            var totalOut = metrics[^1].RowsOut;
            this.console.MarkupLine($"[green]Done — {totalOut} rows written to {options.OutDir}[/]");
            return 0;
        }
        catch (OperationCanceledException)
        {
            this.console.MarkupLine("[yellow]Cancelled.[/]");
            return 130;
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "generate failed");
            this.console.MarkupLine($"[red]Failed: {ex.Message}[/]");
            return 1;
        }
    }
}
