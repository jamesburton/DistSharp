# DistSharp — Architecture Design

**Date:** 2026-05-15
**Scope:** Overall solution structure, core abstractions, pipeline execution model, and build sequence. This spec covers phases 1–2 of the build plan. Subsequent specs cover the Roslyn analyzer, LLM provider layer, step library, CLI, and export subsystems.

---

## 1. Solution Structure

Multi-project solution under a single repository. Source projects live under `src/`, test projects under `tests/`.

```
DistSharp/
├── src/
│   ├── DistSharp.Core/        # Pipeline engine, abstractions, step library, dataset types
│   ├── DistSharp.Roslyn/      # Roslyn symbol extraction (isolates heavy CodeAnalysis deps)
│   ├── DistSharp.Providers/   # All LLM providers (OpenAI, Anthropic, Gemini, Ollama, Azure)
│   └── DistSharp.Cli/         # CLI entry point, command handlers, Spectre.Console UI
├── tests/
│   ├── DistSharp.Core.Tests/
│   ├── DistSharp.Roslyn.Tests/
│   ├── DistSharp.Providers.Tests/
│   └── DistSharp.Cli.Tests/
├── docs/
├── Directory.Build.props
├── Directory.Packages.props
├── global.json
├── .editorconfig
└── DistSharp.sln
```

### Project dependency rules

- `DistSharp.Core` — no dependency on any DistSharp project; depends only on `Microsoft.Extensions.*`
- `DistSharp.Roslyn` — depends on `DistSharp.Core` + `Microsoft.CodeAnalysis.*`
- `DistSharp.Providers` — depends on `DistSharp.Core` + provider SDK packages
- `DistSharp.Cli` — depends on all three above + `System.CommandLine` + `Spectre.Console`

`DistSharp.Core` must never reference Roslyn or any LLM SDK. It interacts with them only through interfaces.

---

## 2. Technology Choices

| Concern | Choice | Rationale |
|---|---|---|
| Runtime | .NET 10 | Matches README spec; required for `dnx` global tool runner |
| CLI parsing | `System.CommandLine` | Standard Microsoft library, good help generation |
| Terminal UI | `Spectre.Console` | Rich progress tables and live output for long pipeline runs |
| Application composition | `Microsoft.Extensions.*` Generic Host | Standard DI, config binding, structured logging; provider registration via extension methods |
| Pipeline execution | `System.Threading.Channels` | Natural backpressure, bounded memory, straightforward fan-out/fan-in for DAG |
| YAML config | `YamlDotNet` + custom `IConfigurationProvider` | Parses `distsharp.yaml` into `IConfiguration`, binds to `PipelineConfig` |
| Testing | xUnit + NSubstitute + FluentAssertions | Standard .NET testing stack |
| Code style | StyleCop Analyzers + `.editorconfig` | Consistent SA rules across all projects |
| NuGet management | Central Package Management (`Directory.Packages.props`) | Single version source of truth across all projects |

---

## 3. Core Abstractions (`DistSharp.Core`)

### `Row`

The fundamental unit of data flowing through the pipeline. Immutable; transformations return new instances.

```csharp
public sealed record Row(IReadOnlyDictionary<string, object?> Fields)
{
    public static Row Empty { get; } = new(ImmutableDictionary<string, object?>.Empty);

    public T? Get<T>(string key);
    public bool TryGet<T>(string key, out T? value);
    public Row With(string key, object? value);
    public Row With(IReadOnlyDictionary<string, object?> fields);
}
```

### `IStep`

A unit of work in the pipeline. The step reads rows from an input channel and writes transformed rows to an output channel. Channel wiring is handled entirely by the executor — steps never create channels.

```csharp
public interface IStep
{
    string Name { get; }

    Task ExecuteAsync(
        ChannelReader<Row> input,
        ChannelWriter<Row> output,
        CancellationToken cancellationToken);
}
```

### `ILlmProvider`

The only LLM abstraction `DistSharp.Core` knows about. Concrete implementations live in `DistSharp.Providers`.

```csharp
public interface ILlmProvider
{
    string ProviderName { get; }

    Task<string> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        LlmRequestOptions options,
        CancellationToken cancellationToken);
}

public sealed record ChatMessage(ChatRole Role, string Content);
public enum ChatRole { System, User, Assistant }

public sealed class LlmRequestOptions
{
    public string? Model { get; init; }
    public float? Temperature { get; init; }
    public int? MaxTokens { get; init; }
}
```

### `ISolutionAnalyzer`

The only Roslyn abstraction `DistSharp.Core` knows about. Concrete implementation lives in `DistSharp.Roslyn`.

```csharp
public interface ISolutionAnalyzer
{
    IAsyncEnumerable<ExtractedSymbol> AnalyzeAsync(
        string solutionPath,
        SolutionAnalysisOptions options,
        CancellationToken cancellationToken);
}
```

`ExtractedSymbol` is defined in `DistSharp.Core` (it is a plain data record with no Roslyn types) so that `LlmStep` and other core steps can consume it without depending on `DistSharp.Roslyn`.

### `IDatasetWriter`

Incremental row sink. Implementations in `DistSharp.Core` cover JSONL, CSV, and Parquet.

```csharp
public interface IDatasetWriter : IAsyncDisposable
{
    Task WriteAsync(Row row, CancellationToken cancellationToken);
    Task FlushAsync(CancellationToken cancellationToken);
}
```

### `ICheckpointStore`

Persists pipeline progress so interrupted runs can resume from a known row offset.

```csharp
public interface ICheckpointStore
{
    Task<PipelineCheckpoint?> LoadAsync(string pipelineId, CancellationToken cancellationToken);
    Task SaveAsync(PipelineCheckpoint checkpoint, CancellationToken cancellationToken);
}

public sealed record PipelineCheckpoint(
    string PipelineId,
    string StepName,
    long RowsWritten,
    DateTimeOffset SavedAt);
```

The default implementation (`FileCheckpointStore`) writes to `<out-dir>/.checkpoint/`.

### `PipelineConfig`

Strongly-typed representation of `distsharp.yaml` (and CLI flag overrides). Bound via `IOptions<PipelineConfig>`.

```csharp
public sealed class PipelineConfig
{
    public string Name { get; set; } = string.Empty;
    public SolutionConfig Solution { get; set; } = new();
    public List<StepConfig> Steps { get; set; } = [];
    public OutputConfig Output { get; set; } = new();
}
```

---

## 4. Pipeline Execution Model

The `PipelineExecutor` runs a `PipelineDefinition` (the validated, instantiated form of `PipelineConfig`) using `System.Threading.Channels`.

### Step graph execution

1. **Topological sort** — steps are sorted by their `DependsOn` declarations.
2. **Channel creation** — a bounded `Channel<Row>` is created for each step's output. Channel capacity is configurable (default: 1000 rows).
3. **Fan-out** — when multiple downstream steps declare the same `DependsOn` step, the executor wraps the upstream step's single output channel with a multiplexing writer that copies each row into N separate channels (one per downstream step). The upstream step itself always writes to a single `ChannelWriter<Row>` and is unaware of fan-out.
4. **Fan-in** — when a step declares multiple `DependsOn` entries, the executor inserts an internal `MergeStep` that drains all upstream channels into a single output channel before passing it to the step.
5. **Concurrent execution** — all steps are started via `Task.WhenAll`. Each step runs until its input channel is completed, then completes its own output channel.
6. **Backpressure** — bounded channels propagate backpressure upstream automatically. A slow `IDatasetWriter` will slow the last step, which will slow earlier steps.

### Parallelism within `LlmStep`

`LlmStep` accepts a `Workers` configuration value. It spawns N concurrent tasks that all read from the same input `ChannelReader<Row>` and all write to the same output `ChannelWriter<Row>`. This provides parallelism at the LLM call level without requiring any changes to the executor.

### Checkpointing

The `IDatasetWriter` sink step increments a counter after each successful `WriteAsync`. Every N rows (configurable via `checkpoint_every`) the executor calls `ICheckpointStore.SaveAsync`. On resume (`--resume`), the executor skips rows from the source until the checkpointed offset is reached.

---

## 5. Generic Host Wiring (`DistSharp.Cli`)

### Configuration sources (applied in order, later overrides earlier)

1. Built-in defaults (coded into each step's config class)
2. `distsharp.yaml` in the working directory (via YamlDotNet `IConfigurationProvider`)
3. Environment variables (`OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, etc.)
4. CLI flags (`--provider`, `--max-rows`, `--model`, etc.) — highest priority

All sources are merged into a single `PipelineConfig` via `IOptions<PipelineConfig>`.

### Service registrations

| Interface | Implementation | Lifetime |
|---|---|---|
| `IPipelineExecutor` | `PipelineExecutor` | Singleton |
| `ICheckpointStore` | `FileCheckpointStore` | Singleton |
| `ISolutionAnalyzer` | `RoslynSolutionAnalyzer` | Singleton |
| `ILlmProviderFactory` | `LlmProviderFactory` | Singleton |
| `IDatasetWriterFactory` | `DatasetWriterFactory` | Singleton |
| `IAnsiConsole` | `AnsiConsole.Console` | Singleton |
| Command handlers | `GenerateCommandHandler`, etc. | Transient |

### Command flow

`Program.cs` builds the `IHost` once at startup. System.CommandLine routes `args[]` to the appropriate command handler. Each handler is resolved from the DI container and calls `IPipelineExecutor.ExecuteAsync(pipeline)`. Spectre.Console renders a live progress table for the duration of the run.

---

## 6. Build Sequence

| Phase | Covers | Spec |
|---|---|---|
| 1 | Solution scaffolding — `.sln`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `.editorconfig`, project files, StyleCop, CI skeleton | This spec |
| 2 | Core abstractions + `PipelineExecutor` + `FileCheckpointStore` | This spec |
| 3 | Roslyn analyzer — `RoslynSolutionAnalyzer`, `ExtractedSymbol`, `RoslynSymbolExtractorStep`, `StratifiedSamplerStep` | Roslyn spec |
| 4 | LLM provider layer — `ILlmProviderFactory`, OpenAI/Azure/compatible, Anthropic, Gemini, Ollama/LM Studio | Providers spec |
| 5 | Step library — `LlmStep`, `LlmJudge`, `MinHashDeduplicator`, prompt builders for all 7 dataset types | Step library spec |
| 6 | Dataset writers — JSONL, Parquet, CSV | Step library spec |
| 7 | CLI + Spectre.Console — Generic Host wiring, all 5 commands, live progress table, YAML config provider | CLI spec |
| 8 | Export + Hugging Face Hub upload — Alpaca/ShareGPT converters, HF REST API client | Export spec |
