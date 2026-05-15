using DistSharp.Cli.Progress;
using DistSharp.Core.Abstractions;
using DistSharp.Core.Configuration;
using DistSharp.Core.Pipeline;
using DistSharp.Core.Steps;
using DistSharp.Roslyn.Steps;
using Microsoft.Extensions.Logging;

namespace DistSharp.Cli;

/// <summary>Translates a <see cref="PipelineConfig"/> into a <see cref="PipelineDefinition"/> with concrete <see cref="IStep"/> instances.</summary>
public sealed class PipelineBuilder
{
    private readonly ISolutionAnalyzer analyzer;
    private readonly ILlmProviderFactory providerFactory;
    private readonly ILoggerFactory loggerFactory;

    /// <summary>Initializes a new instance of the <see cref="PipelineBuilder"/> class.</summary>
    /// <param name="analyzer">The solution analyzer.</param>
    /// <param name="providerFactory">The LLM provider factory.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public PipelineBuilder(ISolutionAnalyzer analyzer, ILlmProviderFactory providerFactory, ILoggerFactory loggerFactory)
    {
        this.analyzer = analyzer;
        this.providerFactory = providerFactory;
        this.loggerFactory = loggerFactory;
    }

    /// <summary>Builds a <see cref="PipelineDefinition"/> from <paramref name="config"/>.</summary>
    /// <param name="config">The pipeline configuration.</param>
    /// <returns>A definition with steps wrapped in <see cref="MetricsTrackingStep"/> for live progress.</returns>
    public PipelineDefinition Build(PipelineConfig config)
    {
        var defs = new List<StepDefinition>(config.Steps.Count);
        foreach (var step in config.Steps)
        {
            var concrete = this.BuildStep(step, config);
            var tracked = new MetricsTrackingStep(concrete);
            defs.Add(new StepDefinition(step.Name, tracked, step.DependsOn));
        }

        return new PipelineDefinition(string.IsNullOrEmpty(config.Name) ? "pipeline" : config.Name, defs);
    }

    private static string? GetString(IDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static bool GetBool(IDictionary<string, object?> map, string key, bool defaultValue)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return defaultValue;
        }

        if (value is bool b)
        {
            return b;
        }

        return bool.TryParse(value.ToString(), out var parsed) ? parsed : defaultValue;
    }

    private static int GetInt(IDictionary<string, object?> map, string key, int defaultValue)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return defaultValue;
        }

        if (value is int i)
        {
            return i;
        }

        if (value is long l)
        {
            return checked((int)l);
        }

        return int.TryParse(value.ToString(), out var parsed) ? parsed : defaultValue;
    }

    private static int? GetIntOrNull(IDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        if (value is int i)
        {
            return i;
        }

        return int.TryParse(value.ToString(), out var parsed) ? parsed : (int?)null;
    }

    private static float GetFloat(IDictionary<string, object?> map, string key, float defaultValue)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return defaultValue;
        }

        if (value is float f)
        {
            return f;
        }

        if (value is double d)
        {
            return (float)d;
        }

        return float.TryParse(value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
    }

    private static double GetDouble(IDictionary<string, object?> map, string key, double defaultValue)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return defaultValue;
        }

        if (value is double d)
        {
            return d;
        }

        if (value is float f)
        {
            return f;
        }

        return double.TryParse(value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
    }

    private static List<string> GetStringList(IDictionary<string, object?> map, string key, IReadOnlyList<string> defaultValue)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return defaultValue.ToList();
        }

        if (value is IEnumerable<string> strings)
        {
            return strings.ToList();
        }

        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            return enumerable.Cast<object?>().Select(o => o?.ToString() ?? string.Empty).ToList();
        }

        return defaultValue.ToList();
    }

    private static IStep BuildSampler(StepConfig step)
    {
        var strategyText = GetString(step.Config, "strategy") ?? "complexity_weighted";
        var strategy = strategyText.Equals("uniform", StringComparison.OrdinalIgnoreCase)
            ? SamplingStrategy.Uniform
            : SamplingStrategy.ComplexityWeighted;

        var options = new StratifiedSamplerOptions
        {
            MaxRows = GetInt(step.Config, "max_rows", 50_000),
            Strategy = strategy,
            Seed = GetIntOrNull(step.Config, "seed"),
        };

        return new StratifiedSamplerStep(step.Name, options);
    }

    private static IStep BuildDeduplicator(StepConfig step)
    {
        var options = new MinHashOptions
        {
            Field = GetString(step.Config, "field") ?? "response",
            Threshold = GetDouble(step.Config, "threshold", 0.85),
            NumHashes = GetInt(step.Config, "num_hashes", 128),
            ShingleSize = GetInt(step.Config, "shingle_size", 3),
            Seed = GetInt(step.Config, "seed", 42),
        };

        return new MinHashDeduplicator(step.Name, options);
    }

    private IStep BuildStep(StepConfig step, PipelineConfig config)
    {
        return step.Type switch
        {
            "RoslynSymbolExtractor" => this.BuildSymbolExtractor(step, config),
            "StratifiedSampler" => BuildSampler(step),
            "LlmStep" => this.BuildLlmStep(step),
            "LlmJudge" => this.BuildJudge(step),
            "MinHashDeduplicator" => BuildDeduplicator(step),
            _ => throw new InvalidOperationException($"Unknown step type: '{step.Type}'. Supported: RoslynSymbolExtractor, StratifiedSampler, LlmStep, LlmJudge, MinHashDeduplicator."),
        };
    }

    private IStep BuildSymbolExtractor(StepConfig step, PipelineConfig config)
    {
        var options = new RoslynSymbolExtractorOptions
        {
            SolutionPath = GetString(step.Config, "solution_path") ?? config.Solution.Path,
            IncludeTests = GetBool(step.Config, "include_tests", config.Solution.IncludeTests),
            IncludeGenerated = GetBool(step.Config, "include_generated", config.Solution.IncludeGenerated),
            MinComplexity = GetInt(step.Config, "min_complexity", config.Solution.MinComplexity),
            ExcludeNamespaces = GetStringList(step.Config, "exclude_namespaces", config.Solution.ExcludeNamespaces),
            SymbolKinds = GetStringList(step.Config, "symbol_kinds", new List<string> { "method", "property", "class", "interface" }),
            MaxBodyLines = GetInt(step.Config, "max_body_lines", 0),
        };

        return new RoslynSymbolExtractorStep(step.Name, this.analyzer, options);
    }

    private IStep BuildLlmStep(StepConfig step)
    {
        var providerName = GetString(step.Config, "provider") ?? throw new InvalidOperationException($"LlmStep '{step.Name}' requires a 'provider' config value.");
        var provider = this.providerFactory.Create(providerName);

        var options = new LlmStepOptions
        {
            DatasetType = GetString(step.Config, "dataset_type") ?? "explanation",
            Model = GetString(step.Config, "model"),
            Temperature = GetFloat(step.Config, "temperature", 0.7f),
            MaxTokens = GetIntOrNull(step.Config, "max_tokens"),
            Workers = GetInt(step.Config, "workers", 4),
            SystemPromptOverride = GetString(step.Config, "system_prompt"),
            DropOnError = GetBool(step.Config, "drop_on_error", true),
        };

        return new LlmStep(step.Name, provider, options, this.loggerFactory.CreateLogger<LlmStep>());
    }

    private IStep BuildJudge(StepConfig step)
    {
        var providerName = GetString(step.Config, "provider") ?? throw new InvalidOperationException($"LlmJudge '{step.Name}' requires a 'provider' config value.");
        var provider = this.providerFactory.Create(providerName);

        var options = new LlmJudgeOptions
        {
            Model = GetString(step.Config, "model"),
            Temperature = GetFloat(step.Config, "temperature", 0.0f),
            Workers = GetInt(step.Config, "workers", 2),
            MinScore = GetFloat(step.Config, "min_score", 3.0f),
            Rubric = GetString(step.Config, "rubric") ?? "helpfulness_and_correctness",
            PromptOverride = GetString(step.Config, "prompt"),
        };

        return new LlmJudge(step.Name, provider, options, this.loggerFactory.CreateLogger<LlmJudge>());
    }
}
