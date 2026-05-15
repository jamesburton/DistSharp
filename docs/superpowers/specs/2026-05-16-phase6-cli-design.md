# DistSharp — Phase 6: CLI + Spectre.Console

**Date:** 2026-05-16
**Scope:** `DistSharp.Cli` project — Program entry point, Generic Host wiring, all 5 commands, YAML config provider, live progress UI.

---

## 1. Goals

1. **Generic Host wiring** — single `Host` built at startup wires config sources, DI, logging
2. **YAML config provider** — `IConfigurationProvider` that reads `distsharp.yaml`
3. **5 commands** — `generate`, `inspect`, `init`, `pipeline run`, `export` (export's body is a stub until Phase 7)
4. **Live progress UI** — Spectre.Console live table showing per-step row counts
5. **`PipelineBuilder`** — translates a `PipelineConfig` (built-in or YAML-loaded) into a `PipelineDefinition` with `StepDefinition` nodes wired to concrete `IStep` instances

---

## 2. Components

### `Program.cs`

```csharp
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var host = BuildHost(args);
        var rootCommand = BuildRootCommand(host.Services);

        return await rootCommand.InvokeAsync(args);
    }

    private static IHost BuildHost(string[] args) { ... }
    private static RootCommand BuildRootCommand(IServiceProvider services) { ... }
}
```

`BuildHost` chains:
- `Host.CreateApplicationBuilder(args)`
- Add `YamlConfigurationSource` if `distsharp.yaml` exists in `Directory.GetCurrentDirectory()`
- Bind `IOptions<PipelineConfig>` from `IConfiguration`
- Register `services.AddDistSharpCore()`, `AddDistSharpProviders()`, `AddDistSharpRoslyn()`, `AddDistSharpStepLibrary()`
- Register all command handlers as transient
- Configure Spectre.Console singleton `AnsiConsole.Console`

### `YamlConfigurationProvider`

`src/DistSharp.Cli/Configuration/YamlConfigurationSource.cs`:

```csharp
public sealed class YamlConfigurationSource : IConfigurationSource
{
    public string Path { get; init; } = "distsharp.yaml";
    public bool Optional { get; init; } = true;
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new YamlConfigurationProvider(this);
}

internal sealed class YamlConfigurationProvider : ConfigurationProvider
{
    public override void Load() { /* read YAML, flatten into Data dictionary */ }
}
```

YAML to flat-key conversion:
- `solution.path: ./MyApp.sln` → `solution:path = ./MyApp.sln`
- `steps[0].name: extract` → `steps:0:name = extract`
- Nested maps use `:` separator (.NET Configuration convention)
- Lists use index notation

Uses `YamlDotNet` library.

### `PipelineBuilder`

`src/DistSharp.Cli/PipelineBuilder.cs`:

```csharp
public sealed class PipelineBuilder
{
    public PipelineBuilder(IServiceProvider services, ILogger<PipelineBuilder> logger);
    public PipelineDefinition Build(PipelineConfig config);
}
```

The builder maps `StepConfig.Type` strings to concrete IStep implementations:

| Type | Concrete |
|---|---|
| `RoslynSymbolExtractor` | `RoslynSymbolExtractorStep` |
| `StratifiedSampler` | `StratifiedSamplerStep` |
| `LlmStep` | `LlmStep` |
| `LlmJudge` | `LlmJudge` |
| `MinHashDeduplicator` | `MinHashDeduplicator` |

For each `StepConfig`:
1. Look up the type in the map; throw `InvalidOperationException` if unknown
2. Use a type-specific builder method to:
   - Read step's `Config` dictionary
   - Build the step's options object (e.g. `RoslynSymbolExtractorOptions`)
   - Resolve dependencies from DI (`ISolutionAnalyzer`, `ILlmProvider`, etc.)
   - Instantiate the `IStep`
3. Wrap in `StepDefinition(name, step, dependsOn)`

For `LlmStep`/`LlmJudge`, the step's `Config` has a `provider` key (e.g. `"openai"`) that the builder uses to call `ILlmProviderFactory.Create(name)`.

### Commands

Each command is a class implementing the routing for its System.CommandLine command. Pattern:

```csharp
public sealed class GenerateCommandHandler
{
    public GenerateCommandHandler(...dependencies...);
    public async Task<int> InvokeAsync(GenerateCommandOptions options, CancellationToken ct);
}
```

#### `generate`

Args: positional `solution` (required) + flags from README (`--out-dir`, `--max-rows`, `--batch-size`, `--dataset-type`, `--format`, `--provider`, `--model`, `--include-tests`, `--include-generated`, `--min-complexity`, `--exclude-namespaces`, `--config`, `--workers`, `--seed`, `--dry-run`, `--resume`).

Flow:
1. Build a `PipelineConfig` by merging:
   - Defaults
   - YAML config (already loaded into IOptions<PipelineConfig> via the config provider)
   - CLI flag overrides
2. If `dataset-type == mixed`, build a parallel pipeline with one LlmStep per supported dataset type splitting from the sampler (fan-out); else build a linear pipeline for that type
3. Build a `PipelineDefinition` from the config via `PipelineBuilder`
4. Create the writer via `IDatasetWriterFactory.Create(config.Output)`
5. If `--dry-run`: skip LlmStep entirely (build pipeline that ends at sampler); print sampled symbol counts and exit
6. Run `IPipelineExecutor.ExecuteAsync(pipeline, writer, ct)` while a Spectre.Console `Live` table displays progress

#### `inspect`

Args: positional `solution` + `--report <path>`, `--show-files`, `--show-symbols`.

Flow:
1. Run `ISolutionAnalyzer.AnalyzeAsync` and collect all symbols
2. Compute aggregate stats: counts per Kind, average complexity, projects analysed, source files, test projects skipped
3. Print a Spectre.Console-rendered summary table
4. If `--show-files`/`--show-symbols`, print detail tables
5. If `--report` set, write the JSON report to that path

#### `init`

Args: `--name`, `--template`, `--output`.

Flow:
1. Generate a YAML pipeline config from a template (built-in templates: `dotnet-mixed`, `dotnet-explanation`, `dotnet-unit-test`, `custom`)
2. Write to `--output` path

Built-in template YAMLs are embedded as string constants in `InitCommandHandler` (or as files via `EmbeddedResource`).

#### `pipeline run`

Args: positional `config` (path to YAML) + `--out-dir`, `--max-rows`, `--resume`.

Flow:
1. Load the YAML config at `config` path (override `--out-dir`, `--max-rows` if provided)
2. Build pipeline + writer
3. Execute (same as `generate` from step 4 onward)

#### `export`

Args: positional `dataset-dir` + `--format`, `--hf-repo`, `--hf-token`, `--split`, `--out-dir`.

For Phase 6: stub that prints "Export not yet implemented" and returns exit code 1 — Phase 7 adds the real implementation.

### Spectre.Console live progress

A `LiveProgressDisplay` helper builds a `Table` (Step Name | Rows In | Rows Out | Status) and updates it via `AnsiConsole.Live(table).StartAsync(...)`. Each `IStep` is wrapped in a `MetricsTrackingStep` that increments counters as rows pass through, and the display reads from those counters every 250ms.

`MetricsTrackingStep` decorator:

```csharp
public sealed class MetricsTrackingStep : IStep
{
    private readonly IStep inner;
    public StepMetrics Metrics { get; }

    public async Task ExecuteAsync(ChannelReader<Row> input, ChannelWriter<Row> output, CancellationToken ct)
    {
        var trackedInput = new TrackingChannelReader(input, this.Metrics.RowsIn);
        var trackedOutput = new TrackingChannelWriter(output, this.Metrics.RowsOut);
        try
        {
            this.Metrics.Status = "running";
            await this.inner.ExecuteAsync(trackedInput, trackedOutput, ct);
            this.Metrics.Status = "complete";
        }
        catch (Exception ex)
        {
            this.Metrics.Status = "failed";
            this.Metrics.Error = ex.Message;
            throw;
        }
    }
}
```

The tracking channel reader/writer wrap a real `ChannelReader<Row>`/`ChannelWriter<Row>` and increment counters on each read/write. We need adapter classes since `ChannelReader<T>` is abstract — we can subclass it.

---

## 3. Files to create

```
src/DistSharp.Cli/
├── DistSharp.Cli.csproj (modify — add YamlDotNet, Spectre.Console, System.CommandLine)
├── Program.cs
├── HostBuilder.cs              (extracted host-building logic)
├── PipelineBuilder.cs
├── Configuration/
│   ├── YamlConfigurationSource.cs
│   └── YamlConfigurationProvider.cs
├── Commands/
│   ├── GenerateCommandHandler.cs + GenerateCommandOptions.cs
│   ├── InspectCommandHandler.cs + InspectCommandOptions.cs
│   ├── InitCommandHandler.cs + InitCommandOptions.cs
│   ├── PipelineRunCommandHandler.cs + PipelineRunCommandOptions.cs
│   └── ExportCommandHandler.cs + ExportCommandOptions.cs
├── Progress/
│   ├── StepMetrics.cs
│   ├── MetricsTrackingStep.cs
│   ├── TrackingChannelReader.cs
│   ├── TrackingChannelWriter.cs
│   └── LiveProgressDisplay.cs
└── Templates/
    ├── dotnet-mixed.yaml      (EmbeddedResource)
    ├── dotnet-explanation.yaml
    └── dotnet-unit-test.yaml

src/DistSharp.Roslyn/
└── ServiceCollectionExtensions.cs (add — exposes AddDistSharpRoslyn extension)

tests/DistSharp.Cli.Tests/
├── PipelineBuilderTests.cs
├── YamlConfigurationProviderTests.cs
└── Commands/
    ├── InitCommandHandlerTests.cs
    └── InspectCommandHandlerTests.cs (uses a small fixture)
```

---

## 4. Packages

Add to `Directory.Packages.props`:
- `System.CommandLine` (latest `2.0.0-beta` if needed, or use `Microsoft.Extensions.Configuration.CommandLine` style)
- `Spectre.Console` v0.49+
- `YamlDotNet` v15+

Add to `DistSharp.Cli.csproj`:
- All three above + ProjectReferences (already in place from Phase 1)

---

## 5. Out of scope

- Streaming / partial render of LLM responses (just full text)
- Cost estimation in `inspect` (deferred — the README mentions it but requires token-count math per provider; ship a simpler version that just shows row counts)
- `--seed` for deterministic LLM responses across all providers (each provider handles seed differently)
