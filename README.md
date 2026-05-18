# DistSharp

[![CI](https://github.com/jamesburton/DistSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/jamesburton/DistSharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/DistSharp.svg)](https://www.nuget.org/packages/DistSharp/)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Roslyn-aware synthetic-data pipelines for .NET. DistSharp parses your `.sln`/`.csproj` with the real C# compiler, samples symbols by complexity, and turns them into instruction-tuning datasets via OpenAI / Anthropic / Gemini / Ollama / LM Studio — then optionally judges quality, deduplicates, and exports to JSONL, Parquet, CSV, Alpaca, ShareGPT, or pushes to Hugging Face Hub.

Inspired by [distilabel](https://github.com/argilla-io/distilabel) and built for the .NET ecosystem. Ships as a NuGet global tool — zero install via `dnx` on any machine with .NET 10+.

```bash
dnx DistSharp inspect ./MyApp.sln
dnx DistSharp models --provider openai            # list available models
dnx DistSharp generate ./MyApp.sln --max-rows 5000 --provider openai
dnx DistSharp pipeline run ./distsharp.yaml
dnx DistSharp export ./distsharp-out --format alpaca --hf-repo myorg/dataset
```

---

## Contents

- [Why DistSharp](#why-distsharp)
- [Install](#install)
- [Quick start](#quick-start)
- [Commands](#commands) — `inspect`, `generate`, `models`, `init`, `pipeline run`, `export`, `dataset sync`, `dataset migrate`
- [Dataset types](#dataset-types)
- [LLM providers](#llm-providers) — incl. local ONNX (Phase 1, CPU)
- [Pipeline configuration](#pipeline-configuration)
- [Architecture](#architecture)
- [Comparison with distilabel](#comparison-with-distilabel)
- [Development](#development)
- [Roadmap](#roadmap)
- [Acknowledgements](#acknowledgements)
- [Licence](#licence)

---

## Why DistSharp

Generic synthetic-data tools treat C# as plain text. DistSharp uses Roslyn to extract semantic information that's then composed into prompts:

- Fully-qualified type and method names (`MyApp.Services.OrderService<T>.PlaceOrderAsync`)
- Method body, signature, and existing XML documentation
- Cyclomatic complexity (real, computed via a `CSharpSyntaxWalker`)
- Containing type, namespace, file path
- Symbol kind: `method` / `property` / `class` / `interface` / `record` / `record-struct` / `enum` / `constructor`
- Filters: include/exclude tests, skip generated files, namespace exclusions, min-complexity gate

With that, prompts can reference real type names and real edge cases instead of text heuristics. The resulting datasets are noticeably more grounded in the codebase they came from.

---

## Install

### Zero-install via `dnx` (recommended)

The `dnx` runner is built into the .NET 10 SDK. It downloads and caches the latest DistSharp package on first use.

```bash
# Latest
dnx DistSharp generate ./MyApp.sln --out-dir ./datasets

# Pin a specific version
dnx DistSharp@0.1.0 generate ./MyApp.sln --out-dir ./datasets
```

### Install as a global tool

```bash
dotnet tool install -g DistSharp
distsharp generate ./MyApp.sln --out-dir ./datasets
```

### Install as a local (per-repo) tool

```bash
dotnet new tool-manifest      # if you don't already have a .config/dotnet-tools.json
dotnet tool install DistSharp
dotnet distsharp generate ./MyApp.sln --out-dir ./datasets
```

### Requirements

| Requirement | Minimum |
|---|---|
| .NET SDK | 10.0+ |
| Memory | 4 GB RAM (8 GB recommended for solutions with 1k+ classes) |
| Disk | ~500 MB for the .NET 10 SDK cache + your dataset output |

---

## Quick start

```bash
# 1. Preview what DistSharp will analyse (no LLM cost)
dnx DistSharp inspect ./MyApp.sln --report ./inspect.json

# 2. Set your provider key
export OPENAI_API_KEY="sk-..."

# 3. Generate an explanation dataset
dnx DistSharp generate ./MyApp.sln \
  --provider openai \
  --model gpt-5.4 \
  --dataset-type explanation \
  --max-rows 5000 \
  --workers 4 \
  --out-dir ./training-data

# 4. Export to Alpaca format for Axolotl
dnx DistSharp export ./training-data \
  --format alpaca \
  --out-dir ./alpaca-data
```

For richer pipelines (LLM-as-judge filtering, MinHash deduplication, multiple branches) use a YAML config — see [Pipeline configuration](#pipeline-configuration).

---

## Commands

### `inspect`

Analyses a solution and reports what DistSharp can see — no LLM calls.

```bash
dnx DistSharp inspect <solution> [--report <path>] [--show-files] [--show-symbols] [--include-tests]
```

Output: a Spectre.Console table with file count, symbol counts per kind, and average method complexity. `--report` writes the same data plus every symbol as JSON.

### `generate`

The primary command. Builds a default `extract → sample → llm` pipeline and runs it.

```bash
dnx DistSharp generate <solution> [options]
```

| Option | Default | Description |
|---|---|---|
| `--out-dir <path>` | `./distsharp-out` | Where to write the dataset |
| `--max-rows <n>` | `50000` | Maximum total rows to generate |
| `--dataset-type <type>` | `mixed` | `explanation` / `completion` / `bug-fix` / `unit-test` / `docstring` / `refactor` / `architecture-qa` / `mixed` |
| `--format <fmt>` | `jsonl` | `jsonl` / `parquet` / `csv` |
| `--provider <name>` | `openai` | See [LLM providers](#llm-providers) |
| `--model <name>` | provider default (see below) | e.g. `gpt-5.4`, `gpt-5.4-mini`, `claude-sonnet-4-6`, `gemini-2.5-flash`, `qwen2.5-coder:32b`. Run `distsharp models --provider <name>` if unsure. |
| `--include-tests` | `false` | Include test projects in analysis |
| `--include-generated` | `false` | Include `*.g.cs` and similar |
| `--min-complexity <n>` | `3` | Skip methods below this cyclomatic complexity |
| `--exclude-namespaces <ns>` | — | Comma-separated namespace prefixes to skip |
| `--workers <n>` | `4` | Parallel LLM workers |
| `--seed <n>` | — | Deterministic sampling |
| `--dry-run` | `false` | Analyse and sample only; skip LLM calls. The sampled symbols are written to disk so you can preview coverage before spending tokens. |

### `models`

Lists model IDs available from a provider's discovery endpoint. Useful when you don't remember the exact model name (the LLM providers change models often).

```bash
dnx DistSharp models --provider openai
dnx DistSharp models --provider anthropic --filter haiku
dnx DistSharp models --provider gemini
dnx DistSharp models --provider ollama
```

`--filter <text>` is a case-insensitive substring filter on the model ID.

Supported per provider: `openai`, `anthropic`, `gemini`, `azure-openai`, `ollama`, `lmstudio`, `openai-compatible`. Each hits its native models endpoint (`/v1/models`, `/v1beta/models`, etc.) using the same auth as completion calls.

### `init`

Scaffolds a `distsharp.yaml` from a template.

```bash
dnx DistSharp init [--name <name>] [--template <name>] [--output <path>]
```

Templates: `dotnet-mixed`, `dotnet-explanation`, `dotnet-unit-test`, `custom`.

### `pipeline run`

Executes a pipeline defined in YAML.

```bash
dnx DistSharp pipeline run <config.yaml> [--out-dir <path>] [--max-rows <n>]
```

Use this when you want LLM-as-judge filtering, MinHash deduplication, multiple branches, or anything beyond the default pipeline.

### `export`

Converts a JSONL dataset to another format and/or uploads to Hugging Face Hub.

```bash
dnx DistSharp export <dataset-dir> [options]
```

| Option | Description |
|---|---|
| `--format <fmt>` | `jsonl` / `alpaca` / `sharegpt` |
| `--out-dir <path>` | Local output directory |
| `--hf-repo <repo>` | Hugging Face dataset repo (`org/name`) — creates it if missing |
| `--hf-token <token>` | HF API token (or set `HF_TOKEN`) |
| `--split <name>` | Dataset split name (default `train`) |

### `dataset sync`

Re-runs the pipeline against the current state of a solution, regenerating only rows whose source symbol has changed since the dataset was last produced. Every `generate` run writes a `_distsharp/manifest.json` alongside the data file; `dataset sync` uses it to compute the new/stale/unchanged/orphan diff and only spends LLM tokens on what actually moved.

```bash
dnx DistSharp dataset sync <dataset-dir> --solution <sln> [--orphan-policy drop|keep|archive] [--dry-run]
```

| Option | Default | Description |
|---|---|---|
| `--solution <path>` | — | The `.sln`/`.csproj` to diff against the manifest. |
| `--orphan-policy <policy>` | `drop` | What to do with rows whose symbol no longer exists: `drop`, `keep`, or `archive` (move to `_distsharp/archive/`). |
| `--dry-run` | `false` | Print the plan (counts + estimated LLM call count) without invoking any provider. |

Pull/push to Hugging Face / git remotes, and `dataset merge` for combining snapshots, are planned for a later phase — see the [Roadmap](#roadmap). Today the command operates entirely locally.

### `dataset migrate`

Seeds `_distsharp/manifest.json` for a dataset directory that was generated before the manifest format existed, so the next `dataset sync` can compute a sensible diff.

```bash
dnx DistSharp dataset migrate <dataset-dir> --solution <sln>
```

No LLM calls — purely re-establishes row identities for the existing rows.

---

## Dataset types

Each type has a dedicated prompt builder under `src/DistSharp.Core/Prompts/`.

| Type | What it produces | Key fields |
|---|---|---|
| `explanation` | Free-text explanations of methods/types | `instruction`, `context`, `response` |
| `completion` | Half-of-body prompt → completion | `prompt`, `completion` |
| `bug-fix` | LLM-introduced bug + corrected version + explanation | `buggy_code`, `fixed_code`, `explanation` |
| `unit-test` | xUnit test grounded in the real signature | `instruction`, `context`, `response` |
| `docstring` | XML `///` documentation comments | `instruction`, `code`, `response` |
| `refactor` | Refactored higher-complexity method + rationale | `original_code`, `refactored_code`, `explanation` |
| `architecture-qa` | Question/answer pair about a type's role | `question`, `answer` |
| `mixed` | Currently selects `explanation`; fan-out across all types is on the roadmap | — |

JSON-output types (`bug-fix`, `refactor`, `architecture-qa`) parse the LLM response and drop rows where parsing fails. Other types pass the raw response through.

---

## LLM providers

Set with `--provider` / `--model` (or in the YAML config).

| Provider | `--provider` value | Auth |
|---|---|---|
| OpenAI | `openai` | `OPENAI_API_KEY` |
| Anthropic | `anthropic` | `ANTHROPIC_API_KEY` |
| Azure OpenAI | `azure-openai` | `AZURE_OPENAI_ENDPOINT` + `AZURE_OPENAI_API_KEY` |
| Google Gemini | `gemini` | `GEMINI_API_KEY` |
| Ollama (local or cloud) | `ollama` | `OLLAMA_BASE_URL` (default `http://localhost:11434`); cloud also needs `OLLAMA_API_KEY` |
| LM Studio (local) | `lmstudio` | `LMSTUDIO_BASE_URL` (default `http://localhost:1234`) |
| Any OpenAI-compatible endpoint | `openai-compatible` | `OPENAI_COMPATIBLE_BASE_URL` + `OPENAI_COMPATIBLE_API_KEY` |
| Local ONNX (Phase 1 in progress, CPU only) | `onnx` | `HF_TOKEN` (optional, for private repos) |

**Default models when `--model` is omitted:**

| Provider | Default model | Cheaper fallback |
|---|---|---|
| `openai` | `gpt-5.4` | `gpt-5.4-mini` |
| `anthropic` | `claude-haiku-4-5` | — |
| `gemini` | `gemini-2.5-flash` | — |
| `azure-openai`, `ollama`, `lmstudio`, `openai-compatible` | no default — pass `--model` (deployment name on Azure, tag on Ollama, etc.) | — |

### Ollama: local vs cloud

The `ollama` provider speaks the OpenAI-compatible chat completions protocol exposed by both a self-hosted Ollama server and Ollama's hosted "Turbo" service.

**Local server (default):**

```bash
ollama serve                          # listens on http://localhost:11434
ollama pull qwen2.5-coder:32b         # cache the model locally first

dnx DistSharp generate ./MyApp.sln \
  --provider ollama \
  --model qwen2.5-coder:32b
```

No auth header is sent in local mode. Set `OLLAMA_BASE_URL` if your Ollama listens on a non-default host/port (e.g. a remote workstation, a container, or behind a reverse proxy).

**Cloud / hosted Ollama:**

```bash
export OLLAMA_BASE_URL="https://ollama.com"   # or your gateway URL
export OLLAMA_API_KEY="ol-..."

dnx DistSharp generate ./MyApp.sln \
  --provider ollama \
  --model gpt-oss:120b
```

When `OLLAMA_API_KEY` (or `--ApiKey` on the provider options) is set, DistSharp sends `Authorization: Bearer <key>` on every request. `distsharp models --provider ollama` works the same way in both modes — it hits `/v1/models` against whatever `OLLAMA_BASE_URL` is configured.

### ONNX (local, CPU — Phase 1)

Run ONNX-format models directly on your machine using [ONNX Runtime GenAI](https://github.com/microsoft/onnxruntime-genai). Phase 1 supports CPU execution only; CUDA, DirectML, and Vulkan EPs are planned for Phase 2.

Models are loaded from the Hugging Face Hub cache (`~/.cache/huggingface/hub/` on Unix or `%USERPROFILE%\.cache\huggingface\hub\` on Windows). If the model is not cached, DistSharp downloads it automatically on first use via the HF Hub HTTP API (set `HF_TOKEN` for private repos).

```bash
dnx DistSharp generate ./MyApp.sln \
  --provider onnx \
  --model microsoft/Phi-4-mini-instruct-onnx \
  --accelerator cpu \
  --max-rows 500
```

`--model-variant` overrides the variant subdirectory (e.g. `cpu-int4-rtn-block-32-acc-level-4`). When omitted, Phase 1 applies this priority: `cpu-int4-rtn-block-32-acc-level-4` → first `cpu-int4*` → first `cpu-fp16*` → first `cpu-fp32*`.

Note: inference is significantly slower on CPU than on GPU. Expect several seconds per row. The `--workers` option does not parallelise ONNX sessions in Phase 1 (`MaxConcurrentSessions` defaults to 1).

All providers share a single retry helper that handles HTTP 408/429/500/502/503/504 with exponential backoff, honouring `Retry-After` when present. Authentication, request shape, and response parsing live in provider-specific classes under `src/DistSharp.Providers/`.

When the API returns a model-not-found error (HTTP 404 with a known signal), DistSharp surfaces the provider's own message and appends a hint:

```
HTTP 404 from openai: The model `gpt5.4` does not exist or you do not have access to it.
  — run 'distsharp models --provider openai' to list available models.
```

---

## Pipeline configuration

`distsharp.yaml` is bound into a strongly-typed `PipelineConfig`. Snake-case keys are mapped to PascalCase properties automatically, so the natural YAML style works.

```yaml
name: my-dotnet-pipeline
version: "1"

solution:
  path: ./MyApp.sln
  include_tests: false
  include_generated: false
  min_complexity: 3
  exclude_namespaces:
    - MyApp.Migrations

steps:
  - name: extract_symbols
    type: RoslynSymbolExtractor
    config:
      symbol_kinds: [method]
      min_complexity: 3

  - name: sample
    type: StratifiedSampler
    depends_on: [extract_symbols]
    config:
      max_rows: 5000
      strategy: complexity_weighted
      seed: 42

  - name: generate
    type: LlmStep
    depends_on: [sample]
    config:
      provider: openai
      model: gpt-5.4
      dataset_type: explanation
      workers: 4
      temperature: 0.7

  - name: judge
    type: LlmJudge
    depends_on: [generate]
    config:
      provider: anthropic
      model: claude-opus-4-7
      min_score: 3.5
      rubric: helpfulness_and_correctness

  - name: dedup
    type: MinHashDeduplicator
    depends_on: [judge]
    config:
      field: response
      threshold: 0.85

output:
  dir: ./training-data
  format: jsonl
  write_metadata: true
  checkpoint_every: 1000
```

**Available step types:** `RoslynSymbolExtractor`, `StratifiedSampler`, `LlmStep`, `LlmJudge`, `MinHashDeduplicator`.

**Sampling strategies:** `uniform` (Fisher–Yates), `complexity_weighted` (Efraimidis–Spirakis A-Res weighted reservoir).

**Judge rubrics:** `helpfulness_and_correctness` (default), `code_quality`. Or pass a full prompt via `prompt:`.

---

## Architecture

```
┌─────────────────┐      ┌──────────────────┐      ┌──────────────────┐
│ DistSharp.Cli   │      │ DistSharp.Core   │      │ DistSharp.Roslyn │
│                 │      │                  │      │                  │
│ Program.cs      │      │ Row              │      │ RoslynSolution-  │
│ HostBuilder     │      │ IStep            │◄─────│   Analyzer       │
│ PipelineBuilder │─────►│ IPipelineExec    │      │ SymbolExtractor  │
│ Yaml provider   │      │ PipelineExecutor │      │ RoslynSymbol-    │
│ Spectre live UI │      │ IDatasetWriter   │      │   ExtractorStep  │
│ 5 commands      │      │ ICheckpointStore │      │ StratifiedSampler│
└─────────────────┘      │ LlmStep          │      └──────────────────┘
        │                │ LlmJudge         │
        │                │ MinHashDedup     │      ┌──────────────────┐
        │                │ Writers (JSONL,  │      │ DistSharp.       │
        │                │  CSV, Parquet)   │      │   Providers      │
        ├───────────────►│ Export converters│◄─────│                  │
        │                │ HuggingFaceClient│      │ ILlmProvider     │
        │                │ 7 prompt builders│      │ OpenAI / Azure / │
        │                └──────────────────┘      │  Gemini / Ollama │
        │                                          │  / LM Studio /   │
        │                                          │  OpenAI-compat / │
        │                                          │  Anthropic       │
        │                                          │ LlmProviderFactory│
        └─────────────────────────────────────────►└──────────────────┘
```

### Pipeline execution model

Steps communicate over `System.Threading.Channels`. `PipelineExecutor`:

1. **Topological sort** — DFS with cycle detection. Throws `InvalidOperationException` on cycles.
2. **Channel creation** — bounded `Channel<Row>` (capacity 1000) per step output. Source steps get a pre-completed empty input channel.
3. **Fan-out** — a single "distributor" task per upstream step reads its output and copies each row to every consumer's input channel.
4. **Fan-in** — multi-writer bounded channel; a per-channel countdown latch (`Interlocked.Decrement` on `fanInCounters[]`) completes the writer when all distributors are done.
5. **Sink** — the terminal step's distributor drains directly to `IDatasetWriter.WriteAsync`, then `FlushAsync` is called once `Task.WhenAll` resolves.
6. **Backpressure** — bounded channels propagate slowness upstream automatically: a slow writer slows the last step, which slows earlier steps.
7. **Workers within `LlmStep`** — N concurrent tasks reading the same input channel and writing the same output channel.

### Solution layout

```
DistSharp/
├── src/
│   ├── DistSharp.Core/        # Pipeline engine, abstractions, step library, writers, prompts
│   ├── DistSharp.Roslyn/      # Roslyn symbol extraction (isolates Microsoft.CodeAnalysis.*)
│   ├── DistSharp.Providers/   # All LLM providers (OpenAI, Anthropic, Gemini, Ollama, ...)
│   └── DistSharp.Cli/         # CLI entry point, command handlers, Spectre.Console UI
├── tests/                     # xUnit + NSubstitute + FluentAssertions per project
├── docs/                      # Design specs and implementation plans
└── .comparison/               # Direct comparison harness vs distilabel
```

**Dependency rules:** `Core` never references Roslyn or any LLM SDK. `Roslyn` depends on Core. `Providers` depends on Core. `Cli` depends on all three. This keeps abstractions clean and forces the Roslyn/LLM SDK weight to live behind interfaces.

---

## Comparison with distilabel

DistSharp and distilabel are not direct competitors — distilabel is a general-purpose synthetic-data framework; DistSharp is a purpose-built .NET code-dataset generator.

A direct comparison was run on `c:\Development\ai-roi\AiRoi.sln` with both pipelines processing the same 10 methods through the same `gpt-5.4-mini` model with identical prompts:

| Test | DistSharp | distilabel |
|---|---|---|
| Output equivalence | avg 651 chars | avg 640 chars (within 2%) |
| LLM throughput (10 rows, 4 workers/batch=4) | ~45 s | 66 s |
| Pipeline definition size | 31 LoC (YAML) | 97 LoC (Python) |
| Roslyn / cyclomatic-complexity analysis | native | ❌ not supported |
| LLM provider coverage | 7 providers | 7+ providers |
| LLM-as-judge | `LlmJudge` step | `UltraFeedback` + variants |
| MinHash dedup | built-in | built-in |
| Output formats | JSONL / CSV / Parquet / Alpaca / ShareGPT | JSONL / CSV / Parquet / Alpaca / ShareGPT |
| Hugging Face Hub upload | `export --hf-repo` | `Distiset.push_to_hub` |
| Distribution | NuGet global tool | pip / uv |
| Argilla integration | ❌ | first-class |
| Multi-modal (image/audio) | ❌ | ✔ |
| vLLM / TGI / SGLang servers | via `openai-compatible` | first-class |
| Built-in step library | 5 steps | 50+ steps |

Conclusion: **for .NET code-dataset generation specifically, DistSharp matches distilabel's output quality with ~3× less config and adds semantic analysis distilabel architecturally cannot do**. For everything else — preference datasets, multi-modal, embeddings, Argilla workflows — use distilabel directly.

Full comparison with raw outputs: see [.comparison/COMPARISON.md](.comparison/COMPARISON.md).

---

## Development

```bash
git clone https://github.com/jamesburton/DistSharp.git
cd DistSharp
dotnet build
dotnet test
```

131 tests across Core, Roslyn, Providers, and CLI test projects. CI runs on every push and PR (`.github/workflows/ci.yml`). Release publishes to NuGet on every `v*` git tag (`.github/workflows/release.yml`) — requires a `NUGET_API_KEY` secret on the repo.

To cut a release:

```bash
git tag v0.1.0
git push origin v0.1.0
```

The release workflow then runs build → test → pack → push to nuget.org → upload nupkg as a build artifact.

---

## Roadmap

### Recently landed

- [x] **Local ONNX provider — Phase 1** — CPU baseline via `Microsoft.Extensions.AI.OnnxRuntimeGenAI`, fetch-on-first-use against the Hugging Face Hub cache. CUDA / DirectML / Vulkan EPs and Agent Framework integration still to come — see [`docs/superpowers/specs/2026-05-18-onnx-provider-design.md`](docs/superpowers/specs/2026-05-18-onnx-provider-design.md).
- [x] **Dataset sync — Phase 1** — `dataset sync` and `dataset migrate` commands, `_distsharp/manifest.json` written by every `generate` run, regenerate-only-what-changed semantics. Pull/push transports and `dataset merge` still to come — see [`docs/superpowers/specs/2026-05-18-dataset-sync-design.md`](docs/superpowers/specs/2026-05-18-dataset-sync-design.md).

### Next up

- [ ] ONNX Phases 2–5: DirectML + CUDA + Vulkan execution providers; Ollama cache awareness; per-EP NuGet packaging
- [ ] ONNX Phase 6: Agent Framework `agent_step` pipeline node
- [ ] Dataset sync Phases 2–3: `dataset pull` / `dataset push` (HF Hub + git) and `dataset merge` with conflict policies

### Backlog

- [ ] `mixed` dataset type fans out across all seven prompt builders in one run
- [ ] Per-prompt-builder symbol-kind filter (so `unit-test` only ever sees methods)
- [ ] Incremental mode — only re-generate rows for files changed since the last run (overlaps with `dataset sync` — needs reconciliation)
- [ ] Cross-project context (called symbols, implemented interfaces, inheritance chain) populated on `ExtractedSymbol`
- [ ] Preference datasets — ranked pairs (chosen/rejected) for DPO/ORPO
- [ ] Embedding-based deduplication via local embedding model
- [ ] MCP server mode — expose the running pipeline as a Model Context Protocol server
- [ ] Cost estimation in `inspect` based on real token-per-symbol measurements

---

## Acknowledgements

DistSharp's pipeline architecture is directly inspired by **[distilabel](https://github.com/argilla-io/distilabel)** by [Argilla](https://github.com/argilla-io). If you work in Python, use distilabel directly — it's an excellent framework with deep ecosystem integration. DistSharp exists to bring those ideas to the .NET world with native Roslyn integration.

---

## Licence

MIT
