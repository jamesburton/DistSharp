using System.CommandLine;
using System.CommandLine.Invocation;
using DistSharp.Cli.Commands;
using DistSharp.Core.Sync;
using DistSharp.Providers.Onnx;
using Microsoft.Extensions.DependencyInjection;

namespace DistSharp.Cli;

/// <summary>Entry point for the DistSharp CLI.</summary>
public static class Program
{
    private const string PhaseOneAcceleratorError =
        "Accelerator '{0}' is not supported. Phase 1 supports CPU only — see docs/superpowers/specs/2026-05-18-onnx-provider-design.md";

    /// <summary>The main entry point.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Process exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        using var host = HostBuilder.Build(args);
        var root = BuildRootCommand(host.Services);
        return await root.InvokeAsync(args).ConfigureAwait(false);
    }

    private static RootCommand BuildRootCommand(IServiceProvider services)
    {
        var root = new RootCommand("DistSharp — Roslyn-aware synthetic data pipelines for .NET");
        root.AddCommand(BuildGenerateCommand(services));
        root.AddCommand(BuildInspectCommand(services));
        root.AddCommand(BuildInitCommand(services));
        root.AddCommand(BuildPipelineCommand(services));
        root.AddCommand(BuildExportCommand(services));
        root.AddCommand(BuildModelsCommand(services));
        root.AddCommand(BuildDatasetCommand(services));
        return root;
    }

    private static Command BuildModelsCommand(IServiceProvider services)
    {
        var provider = new Option<string>("--provider", () => "openai", "LLM provider to query (openai, anthropic, gemini, ollama, lmstudio, azure-openai, openai-compatible, onnx)");
        var filter = new Option<string?>("--filter", () => null, "Case-insensitive substring to filter the model list");

        var cmd = new Command("models", "List models exposed by an LLM provider's /models endpoint")
        {
            provider, filter,
        };

        cmd.SetHandler(async (context) =>
        {
            var options = new ModelsCommandOptions
            {
                Provider = context.ParseResult.GetValueForOption(provider)!,
                Filter = context.ParseResult.GetValueForOption(filter),
            };

            var handler = services.GetRequiredService<ModelsCommandHandler>();
            context.ExitCode = await handler.InvokeAsync(options, context.GetCancellationToken());
        });

        return cmd;
    }

    private static Command BuildGenerateCommand(IServiceProvider services)
    {
        var solution = new Argument<string>("solution", "Path to .sln, .csproj, or directory");
        var outDir = new Option<string>("--out-dir", () => "./distsharp-out", "Output directory");
        var maxRows = new Option<int>("--max-rows", () => 50_000, "Maximum total rows");
        var datasetType = new Option<string>("--dataset-type", () => "mixed", "Dataset type");
        var format = new Option<string>("--format", () => "jsonl", "Output format");
        var provider = new Option<string>("--provider", () => "openai", "LLM provider");
        var model = new Option<string?>("--model", () => null, "Model identifier");
        var includeTests = new Option<bool>("--include-tests", () => false, "Include test projects");
        var includeGenerated = new Option<bool>("--include-generated", () => false, "Include generated files");
        var minComplexity = new Option<int>("--min-complexity", () => 3, "Minimum complexity");
        var excludeNamespaces = new Option<string>("--exclude-namespaces", () => string.Empty, "Comma-separated namespace prefixes");
        var workers = new Option<int>("--workers", () => 4, "Parallel LLM workers");
        var seed = new Option<int?>("--seed", () => null, "Random seed");
        var dryRun = new Option<bool>("--dry-run", () => false, "Skip LLM calls");
        var accelerator = new Option<string?>("--accelerator", () => null, "ONNX execution provider. Phase 1 accepts: cpu");
        var modelVariant = new Option<string?>("--model-variant", () => null, "ONNX model variant subdirectory, e.g. cpu-int4-rtn-block-32-acc-level-4");

        var cmd = new Command("generate", "Analyse a solution and generate a training dataset")
        {
            solution,
            outDir,
            maxRows,
            datasetType,
            format,
            provider,
            model,
            includeTests,
            includeGenerated,
            minComplexity,
            excludeNamespaces,
            workers,
            seed,
            dryRun,
            accelerator,
            modelVariant,
        };

        cmd.SetHandler(async (context) =>
        {
            var acceleratorValue = context.ParseResult.GetValueForOption(accelerator);
            if (!string.IsNullOrEmpty(acceleratorValue) &&
                !acceleratorValue.Equals("cpu", StringComparison.OrdinalIgnoreCase))
            {
                context.Console.Error.Write(string.Format(PhaseOneAcceleratorError, acceleratorValue) + Environment.NewLine);
                context.ExitCode = 2;
                return;
            }

            // Forward ONNX-specific options to the registered singleton before the handler runs.
            if (acceleratorValue is not null || context.ParseResult.GetValueForOption(modelVariant) is not null)
            {
                var onnxOpts = services.GetRequiredService<OnnxProviderOptions>();
                onnxOpts.Accelerator = acceleratorValue;
                onnxOpts.ModelVariant = context.ParseResult.GetValueForOption(modelVariant);
            }

            var options = new GenerateCommandOptions
            {
                Solution = context.ParseResult.GetValueForArgument(solution),
                OutDir = context.ParseResult.GetValueForOption(outDir)!,
                MaxRows = context.ParseResult.GetValueForOption(maxRows),
                DatasetType = context.ParseResult.GetValueForOption(datasetType)!,
                Format = context.ParseResult.GetValueForOption(format)!,
                Provider = context.ParseResult.GetValueForOption(provider)!,
                Model = context.ParseResult.GetValueForOption(model),
                IncludeTests = context.ParseResult.GetValueForOption(includeTests),
                IncludeGenerated = context.ParseResult.GetValueForOption(includeGenerated),
                MinComplexity = context.ParseResult.GetValueForOption(minComplexity),
                ExcludeNamespaces = ParseList(context.ParseResult.GetValueForOption(excludeNamespaces)),
                Workers = context.ParseResult.GetValueForOption(workers),
                Seed = context.ParseResult.GetValueForOption(seed),
                DryRun = context.ParseResult.GetValueForOption(dryRun),
            };

            var handler = services.GetRequiredService<GenerateCommandHandler>();
            context.ExitCode = await handler.InvokeAsync(options, context.GetCancellationToken());
        });

        return cmd;
    }

    private static Command BuildInspectCommand(IServiceProvider services)
    {
        var solution = new Argument<string>("solution", "Path to .sln, .csproj, or directory");
        var report = new Option<string?>("--report", () => null, "Write a JSON report to this path");
        var showFiles = new Option<bool>("--show-files", () => false, "List included files");
        var showSymbols = new Option<bool>("--show-symbols", () => false, "List included symbols");
        var includeTests = new Option<bool>("--include-tests", () => false, "Include test projects");

        var cmd = new Command("inspect", "Analyse a solution and report coverage without generating data")
        {
            solution,
            report,
            showFiles,
            showSymbols,
            includeTests,
        };

        cmd.SetHandler(async (context) =>
        {
            var options = new InspectCommandOptions
            {
                Solution = context.ParseResult.GetValueForArgument(solution),
                Report = context.ParseResult.GetValueForOption(report),
                ShowFiles = context.ParseResult.GetValueForOption(showFiles),
                ShowSymbols = context.ParseResult.GetValueForOption(showSymbols),
                IncludeTests = context.ParseResult.GetValueForOption(includeTests),
            };

            var handler = services.GetRequiredService<InspectCommandHandler>();
            context.ExitCode = await handler.InvokeAsync(options, context.GetCancellationToken());
        });

        return cmd;
    }

    private static Command BuildInitCommand(IServiceProvider services)
    {
        var name = new Option<string>("--name", () => "distsharp-pipeline", "Pipeline name");
        var template = new Option<string>("--template", () => "dotnet-mixed", "Template identifier");
        var output = new Option<string>("--output", () => "./distsharp.yaml", "Output path");

        var cmd = new Command("init", "Scaffold a pipeline configuration file") { name, template, output };

        cmd.SetHandler(async (context) =>
        {
            var options = new InitCommandOptions
            {
                PipelineName = context.ParseResult.GetValueForOption(name)!,
                Template = context.ParseResult.GetValueForOption(template)!,
                OutputPath = context.ParseResult.GetValueForOption(output)!,
            };

            var handler = services.GetRequiredService<InitCommandHandler>();
            context.ExitCode = await handler.InvokeAsync(options, context.GetCancellationToken());
        });

        return cmd;
    }

    private static Command BuildPipelineCommand(IServiceProvider services)
    {
        var pipelineCmd = new Command("pipeline", "Pipeline operations");

        var configPath = new Argument<string>("config", "Path to a YAML pipeline config");
        var outDir = new Option<string?>("--out-dir", () => null, "Override output directory");
        var maxRows = new Option<int?>("--max-rows", () => null, "Override max rows");

        var run = new Command("run", "Run a pipeline defined in YAML") { configPath, outDir, maxRows };
        run.SetHandler(async (context) =>
        {
            var options = new PipelineRunCommandOptions
            {
                ConfigPath = context.ParseResult.GetValueForArgument(configPath),
                OutDir = context.ParseResult.GetValueForOption(outDir),
                MaxRows = context.ParseResult.GetValueForOption(maxRows),
            };

            var handler = services.GetRequiredService<PipelineRunCommandHandler>();
            context.ExitCode = await handler.InvokeAsync(options, context.GetCancellationToken());
        });

        pipelineCmd.AddCommand(run);
        return pipelineCmd;
    }

    private static Command BuildExportCommand(IServiceProvider services)
    {
        var datasetDir = new Argument<string>("dataset-dir", "Directory containing the dataset to export");
        var format = new Option<string?>("--format", () => null, "Target format: jsonl, parquet, csv, alpaca, sharegpt");
        var hfRepo = new Option<string?>("--hf-repo", () => null, "Hugging Face repo");
        var hfToken = new Option<string?>("--hf-token", () => null, "Hugging Face API token");
        var outDir = new Option<string?>("--out-dir", () => null, "Local output directory");
        var split = new Option<string>("--split", () => "train", "Dataset split");

        var cmd = new Command("export", "Convert a dataset to another format or upload to Hugging Face")
        {
            datasetDir,
            format,
            hfRepo,
            hfToken,
            outDir,
            split,
        };

        cmd.SetHandler(async (context) =>
        {
            var options = new ExportCommandOptions
            {
                DatasetDir = context.ParseResult.GetValueForArgument(datasetDir),
                Format = context.ParseResult.GetValueForOption(format),
                HfRepo = context.ParseResult.GetValueForOption(hfRepo),
                HfToken = context.ParseResult.GetValueForOption(hfToken),
                OutDir = context.ParseResult.GetValueForOption(outDir),
                Split = context.ParseResult.GetValueForOption(split)!,
            };

            var handler = services.GetRequiredService<ExportCommandHandler>();
            context.ExitCode = await handler.InvokeAsync(options, context.GetCancellationToken());
        });

        return cmd;
    }

    private static Command BuildDatasetCommand(IServiceProvider services)
    {
        var datasetCmd = new Command("dataset", "Dataset lifecycle operations (sync, migrate)");
        datasetCmd.AddCommand(BuildDatasetSyncCommand(services));
        datasetCmd.AddCommand(BuildDatasetMigrateCommand(services));
        return datasetCmd;
    }

    private static Command BuildDatasetSyncCommand(IServiceProvider services)
    {
        var datasetDir = new Argument<string>("dataset-dir", "Path to the dataset directory");
        var solution = new Option<string>("--solution", "Path to .sln, .csproj, or directory") { IsRequired = true };
        var datasetType = new Option<string>("--dataset-type", () => "explanation", "Dataset type (explanation, unit-test, etc.)");
        var provider = new Option<string>("--provider", () => "openai", "LLM provider for regeneration");
        var orphanPolicy = new Option<OrphanPolicy>("--orphan-policy", () => OrphanPolicy.Drop, "What to do with rows whose symbols no longer exist");
        var dryRun = new Option<bool>("--dry-run", () => false, "Show plan without making LLM calls or writing files");

        var cmd = new Command("sync", "Regenerate only changed rows, applying orphan policy")
        {
            datasetDir,
            solution,
            datasetType,
            provider,
            orphanPolicy,
            dryRun,
        };

        cmd.SetHandler(async (context) =>
        {
            var options = new DatasetSyncCommandOptions
            {
                DatasetDir = context.ParseResult.GetValueForArgument(datasetDir),
                Solution = context.ParseResult.GetValueForOption(solution)!,
                DatasetType = context.ParseResult.GetValueForOption(datasetType)!,
                Provider = context.ParseResult.GetValueForOption(provider)!,
                OrphanPolicy = context.ParseResult.GetValueForOption(orphanPolicy),
                DryRun = context.ParseResult.GetValueForOption(dryRun),
            };

            var handler = services.GetRequiredService<DatasetSyncCommandHandler>();
            context.ExitCode = await handler.InvokeAsync(options, context.GetCancellationToken());
        });

        return cmd;
    }

    private static Command BuildDatasetMigrateCommand(IServiceProvider services)
    {
        var datasetDir = new Argument<string>("dataset-dir", "Path to the legacy dataset directory (no manifest yet)");
        var solution = new Option<string>("--solution", "Path to .sln, .csproj, or directory") { IsRequired = true };
        var datasetType = new Option<string>("--dataset-type", () => "explanation", "Dataset type the legacy dataset was generated with");

        var cmd = new Command("migrate", "Seed _distsharp/manifest.json for an existing dataset directory")
        {
            datasetDir,
            solution,
            datasetType,
        };

        cmd.SetHandler(async (context) =>
        {
            var options = new DatasetMigrateCommandOptions
            {
                DatasetDir = context.ParseResult.GetValueForArgument(datasetDir),
                Solution = context.ParseResult.GetValueForOption(solution)!,
                DatasetType = context.ParseResult.GetValueForOption(datasetType)!,
            };

            var handler = services.GetRequiredService<DatasetMigrateCommandHandler>();
            context.ExitCode = await handler.InvokeAsync(options, context.GetCancellationToken());
        });

        return cmd;
    }

    private static IReadOnlyList<string> ParseList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
