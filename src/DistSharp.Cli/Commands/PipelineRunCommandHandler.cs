using System.Globalization;
using DistSharp.Cli.Configuration;
using DistSharp.Cli.Progress;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Configuration;
using DistSharp.Core.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace DistSharp.Cli.Commands;

/// <summary>Handles the <c>pipeline run</c> command.</summary>
public sealed class PipelineRunCommandHandler
{
    private readonly PipelineBuilder pipelineBuilder;
    private readonly IDatasetWriterFactory writerFactory;
    private readonly IPipelineExecutor executor;
    private readonly LiveProgressDisplay progress;
    private readonly IAnsiConsole console;
    private readonly ILogger<PipelineRunCommandHandler> logger;

    /// <summary>Initializes a new instance of the <see cref="PipelineRunCommandHandler"/> class.</summary>
    /// <param name="pipelineBuilder">Builds pipeline definitions from configuration.</param>
    /// <param name="writerFactory">Creates dataset writers from output configuration.</param>
    /// <param name="executor">Executes the pipeline graph.</param>
    /// <param name="progress">Renders live progress to the console.</param>
    /// <param name="console">The Spectre console for output.</param>
    /// <param name="logger">Logger for error reporting.</param>
    public PipelineRunCommandHandler(
        PipelineBuilder pipelineBuilder,
        IDatasetWriterFactory writerFactory,
        IPipelineExecutor executor,
        LiveProgressDisplay progress,
        IAnsiConsole console,
        ILogger<PipelineRunCommandHandler> logger)
    {
        this.pipelineBuilder = pipelineBuilder;
        this.writerFactory = writerFactory;
        this.executor = executor;
        this.progress = progress;
        this.console = console;
        this.logger = logger;
    }

    /// <summary>Runs the pipeline-run command.</summary>
    /// <param name="options">The parsed command options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Process exit code: 0 on success, 1 on failure, 2 on bad arguments, 130 on cancellation.</returns>
    public async Task<int> InvokeAsync(PipelineRunCommandOptions options, CancellationToken cancellationToken)
    {
        if (!File.Exists(options.ConfigPath))
        {
            this.console.MarkupLine($"[red]Config file not found: {options.ConfigPath}[/]");
            return 2;
        }

        var configuration = new ConfigurationBuilder()
            .AddYamlFile(options.ConfigPath, optional: false)
            .AddEnvironmentVariables()
            .Build();

        var pipelineConfig = new PipelineConfig();
        configuration.Bind(pipelineConfig);
        PopulateStepConfigs(configuration, pipelineConfig);

        if (!string.IsNullOrEmpty(options.OutDir))
        {
            pipelineConfig.Output.Dir = options.OutDir;
        }

        if (options.MaxRows is { } maxRows)
        {
            foreach (var step in pipelineConfig.Steps.Where(s => s.Type == "StratifiedSampler"))
            {
                step.Config["max_rows"] = maxRows;
            }
        }

        var pipeline = this.pipelineBuilder.Build(pipelineConfig);
        var metrics = pipeline.Steps.Select(s => ((MetricsTrackingStep)s.Step).Metrics).ToList();

        await using var writer = this.writerFactory.Create(pipelineConfig.Output);

        try
        {
            await this.progress.RunAsync(
                metrics,
                ct => this.executor.ExecuteAsync(pipeline, writer, ct),
                cancellationToken).ConfigureAwait(false);

            this.console.MarkupLine($"[green]Done — output in {pipelineConfig.Output.Dir}[/]");
            return 0;
        }
        catch (OperationCanceledException)
        {
            this.console.MarkupLine("[yellow]Cancelled.[/]");
            return 130;
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "pipeline run failed");
            this.console.MarkupLine($"[red]Failed: {ex.Message}[/]");
            return 1;
        }
    }

    private static void PopulateStepConfigs(IConfiguration configuration, PipelineConfig pipelineConfig)
    {
        var stepsSection = configuration.GetSection("steps");
        var i = 0;
        foreach (var stepSection in stepsSection.GetChildren())
        {
            if (i >= pipelineConfig.Steps.Count)
            {
                break;
            }

            var configSection = stepSection.GetSection("config");
            if (configSection.Exists())
            {
                pipelineConfig.Steps[i].Config = FlattenConfigSection(configSection);
            }

            i++;
        }
    }

    private static Dictionary<string, object?> FlattenConfigSection(IConfigurationSection section)
    {
        var result = new Dictionary<string, object?>();
        foreach (var child in section.GetChildren())
        {
            if (child.Value is not null)
            {
                result[child.Key] = ParseScalar(child.Value);
                continue;
            }

            var children = child.GetChildren().ToList();
            if (children.Count == 0)
            {
                result[child.Key] = null;
                continue;
            }

            if (children.All(c => int.TryParse(c.Key, out _)))
            {
                var list = new List<object?>();
                foreach (var item in children)
                {
                    if (item.Value is not null)
                    {
                        list.Add(ParseScalar(item.Value));
                    }
                    else
                    {
                        list.Add(FlattenConfigSection(item));
                    }
                }

                result[child.Key] = list;
            }
            else
            {
                result[child.Key] = FlattenConfigSection(child);
            }
        }

        return result;
    }

    private static object? ParseScalar(string raw)
    {
        if (bool.TryParse(raw, out var b))
        {
            return b;
        }

        if (int.TryParse(raw, out var i))
        {
            return i;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return d;
        }

        return raw;
    }
}
