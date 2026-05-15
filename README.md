# DistSharp

> **Note: Some features may be incomplete as this is a work-in-progress.**

A .NET 10 port and extension of [distilabel](https://github.com/argilla-io/distilabel) — a framework for building scalable synthetic data pipelines for AI training. DistSharp adds first-class support for .NET solutions: it can parse your codebase with Roslyn, understand your architecture, and generate richly-targeted instruction datasets for fine-tuning agents on your own code.

Published to NuGet as a global tool, DistSharp runs zero-install on any machine with .NET 10+ via `dnx`.

---

## Table of Contents

- [Quick start](#quick-start)
- [Installation](#installation)
- [Core concepts](#core-concepts)
- [Commands](#commands)
  - [generate](#generate)
  - [inspect](#inspect)
  - [init](#init)
  - [pipeline run](#pipeline-run)
  - [export](#export)
- [.NET solution integration](#net-solution-integration)
- [Dataset types](#dataset-types)
- [LLM providers](#llm-providers)
- [Pipeline configuration](#pipeline-configuration)
- [Output formats](#output-formats)
- [Advanced usage](#advanced-usage)
- [Examples](#examples)
- [Configuration reference](#configuration-reference)

---

## Quick start

No install needed — requires .NET 10+:

```bash
dnx DistSharp generate ./MyApp.sln --max-rows=10000 --out-dir ./datasets
```

This analyses your solution, extracts code structure via Roslyn, and generates an instruction-tuning dataset ready for fine-tuning with OpenAI, Together AI, Axolotl, or any JSONL-compatible trainer.

---

## Installation

### Zero-install via `dnx` (recommended)

The `dnx` runner is built into the .NET 10 SDK. It downloads and runs the latest DistSharp package from NuGet on first use, caching it locally for subsequent runs.

```bash
# Run directly — no install step
dnx DistSharp generate ./MyApp.sln --out-dir ./datasets

# Pin a specific version
dnx DistSharp@1.2.0 generate ./MyApp.sln --out-dir ./datasets
```

### Install as a global tool

```bash
dotnet tool install -g DistSharp
distsharp generate ./MyApp.sln --out-dir ./datasets
```

### Install as a local tool

```bash
dotnet tool install DistSharp
dotnet distsharp generate ./MyApp.sln --out-dir ./datasets
```

### System requirements

| Requirement | Minimum |
|---|---|
| .NET SDK | 10.0+ |
| Memory | 4 GB RAM (8 GB recommended for large solutions) |
| Disk | 500 MB for cache + output |

---

## Core concepts

DistSharp builds on the same ideas as distilabel, adapted for .NET:

- **Pipeline** — a declarative graph of `Step` nodes, each transforming or annotating a stream of rows.
- **Dataset** — an in-memory or streamed collection of rows, written incrementally to disk so progress is never lost.
- **Step** — a unit of work: code extraction, LLM generation, scoring, deduplication, filtering, etc.
- **LLM** — any provider-backed model used within a step (OpenAI, Anthropic, Azure OpenAI, Ollama, etc.).
- **Solution context** — the Roslyn-powered analysis of a `.sln` or `.csproj` that feeds code-aware steps.

Pipelines can be defined in code or in a YAML config file. The default `generate` command runs a built-in pipeline tuned for .NET code datasets.

---

## Commands

### `generate`

The primary command. Analyses a .NET solution and generates a training dataset.

```bash
dnx DistSharp generate <solution> [options]
```

| Argument | Description |
|---|---|
| `<solution>` | Path to a `.sln`, `.csproj`, or directory to scan recursively |

| Option | Default | Description |
|---|---|---|
| `--out-dir <path>` | `./distsharp-out` | Directory to write dataset files |
| `--max-rows <n>` | `50000` | Maximum total rows to generate |
| `--batch-size <n>` | `200` | Rows generated per LLM batch |
| `--dataset-type <type>` | `mixed` | Dataset type — see [Dataset types](#dataset-types) |
| `--format <fmt>` | `jsonl` | Output format: `jsonl`, `parquet`, `csv` |
| `--provider <name>` | `openai` | LLM provider — see [LLM providers](#llm-providers) |
| `--model <name>` | provider default | Model name/ID |
| `--include-tests` | false | Include test projects in analysis |
| `--include-generated` | false | Include generated (`*.g.cs`) files |
| `--min-complexity <n>` | `3` | Minimum cyclomatic complexity for a method to be included |
| `--exclude-namespaces <ns>` | — | Comma-separated namespace prefixes to skip |
| `--config <path>` | — | Path to a pipeline config YAML (overrides other options) |
| `--workers <n>` | `4` | Parallel LLM request workers |
| `--seed <n>` | — | Random seed for reproducible sampling |
| `--dry-run` | false | Analyse and preview without calling any LLM |
| `--resume` | false | Resume a previous interrupted run from checkpoint |

**Examples:**

```bash
# Basic — 100k rows, mixed dataset types
dnx DistSharp generate ./MyApp.sln --max-rows=100000 --out-dir ./training-data

# Code explanation dataset, Anthropic Claude, Parquet output
dnx DistSharp generate ./src/Core.csproj \
  --dataset-type=explanation \
  --provider=anthropic \
  --model=claude-opus-4-7 \
  --format=parquet \
  --out-dir ./datasets/core-explanations

# Dry run to preview coverage before burning tokens
dnx DistSharp generate ./BigSolution.sln --max-rows=50000 --dry-run

# Resume an interrupted run
dnx DistSharp generate ./MyApp.sln --max-rows=100000 --out-dir ./training-data --resume
```

---

### `inspect`

Analyses a .NET solution and reports what DistSharp can see — without generating any data. Use this before `generate` to understand coverage, complexity distribution, and estimated token cost.

```bash
dnx DistSharp inspect <solution> [options]
```

| Option | Default | Description |
|---|---|---|
| `--report <path>` | stdout | Write a detailed JSON report to a file |
| `--show-files` | false | List every included source file |
| `--show-symbols` | false | List every included symbol (type/method) |

**Example output:**

```
Solution: MyApp.sln
  Projects analysed:        12
  Source files:            847
  Public types:            412
  Public methods:        2,841
  Internal types:          198
  Interfaces:               67
  Abstract members:        124
  Average complexity:      4.2
  Test projects excluded:    3

Estimated dataset capacity by type:
  explanation     up to  2,841 rows  (1 per public method)
  completion      up to 14,205 rows  (5 per public method)
  bug-fix         up to  5,682 rows  (2 per public method)
  unit-test       up to  2,841 rows  (1 per public method)
  refactor        up to  1,420 rows  (high-complexity methods only)
  docstring       up to  2,841 rows  (1 per public method)

Estimated LLM cost (gpt-4.1-mini):
  10,000 rows  ≈  $1.20
  50,000 rows  ≈  $5.80
 100,000 rows  ≈ $11.40
```

---

### `init`

Scaffolds a pipeline configuration file in the current directory.

```bash
dnx DistSharp init [options]
```

| Option | Default | Description |
|---|---|---|
| `--name <name>` | `distsharp-pipeline` | Pipeline name used in output metadata |
| `--template <name>` | `dotnet-mixed` | Starting template — `dotnet-mixed`, `dotnet-explanation`, `dotnet-unit-test`, `custom` |
| `--output <path>` | `./distsharp.yaml` | Where to write the config file |

```bash
dnx DistSharp init --template=dotnet-unit-test --output=./pipelines/unit-tests.yaml
```

---

### `pipeline run`

Execute a pipeline defined in a YAML config file.

```bash
dnx DistSharp pipeline run <config> [options]
```

| Option | Description |
|---|---|
| `--out-dir <path>` | Override the output directory from the config |
| `--max-rows <n>` | Override the row limit from the config |
| `--resume` | Resume from checkpoint |

```bash
dnx DistSharp pipeline run ./pipelines/unit-tests.yaml --max-rows=25000
```

---

### `export`

Convert an existing DistSharp dataset to another format or upload to Hugging Face Hub.

```bash
dnx DistSharp export <dataset-dir> [options]
```

| Option | Description |
|---|---|
| `--format <fmt>` | Target format: `jsonl`, `parquet`, `csv`, `alpaca`, `sharegpt` |
| `--hf-repo <repo>` | Hugging Face repo to push to (e.g. `myorg/my-dataset`) |
| `--hf-token <token>` | Hugging Face API token (or set `HF_TOKEN` env var) |
| `--split <name>` | Dataset split name (default: `train`) |

```bash
# Convert to Alpaca format for use with Axolotl
dnx DistSharp export ./training-data --format=alpaca --out-dir ./alpaca-data

# Push directly to Hugging Face Hub
dnx DistSharp export ./training-data --hf-repo myorg/my-dotnet-dataset --hf-token $HF_TOKEN
```

---

## .NET solution integration

DistSharp uses the **Microsoft.CodeAnalysis (Roslyn)** compiler platform to deeply understand your codebase before generating any prompts. This is what distinguishes it from generic code-dataset tools.

### What gets extracted

For every public or internal symbol in scope, DistSharp captures:

| Field | Description |
|---|---|
| `FullyQualifiedName` | e.g. `MyApp.Services.OrderService.PlaceOrderAsync` |
| `SignatureText` | Full method signature with parameter names and types |
| `BodyText` | Cleaned source of the method/property body |
| `XmlDocComment` | Existing `<summary>` and `<param>` documentation |
| `ContainingType` | Parent class/struct/interface |
| `Namespace` | Containing namespace |
| `FilePath` | Relative source file path |
| `Complexity` | Cyclomatic complexity score |
| `ReturnType` | Fully qualified return type |
| `ParameterTypes` | List of parameter type names |
| `Attributes` | Applied attributes (e.g. `[HttpGet]`, `[Authorize]`) |
| `CalledSymbols` | Other symbols called within the body |
| `ImplementedInterfaces` | Interfaces the containing type implements |
| `InheritanceChain` | Base types |
| `NuGetDependencies` | NuGet packages referenced by the containing project |

### Solution graph

DistSharp builds a project dependency graph from the solution, which allows cross-project context:

- When generating an explanation for `IOrderRepository`, DistSharp can include the concrete implementations from other projects in the prompt.
- For interface/implementation pairs, it generates paired rows that teach both contract and implementation.
- Dependency injection registrations (detected via common patterns: `services.AddTransient`, `services.AddScoped`, Autofac, etc.) are surfaced as metadata so the LLM understands composition context.

---

## Dataset types

Control with `--dataset-type`. Use `mixed` (default) to get all types proportionally.

### `explanation`

Each row is an instruction asking the model to explain a method or type, with the ground-truth explanation generated by the LLM.

```json
{
  "instruction": "Explain what the method `PlaceOrderAsync` does in `OrderService`.",
  "context": "// OrderService.cs\npublic async Task<OrderResult> PlaceOrderAsync(Cart cart, PaymentInfo payment) {\n  ...\n}",
  "response": "PlaceOrderAsync validates the cart contents, charges the payment method via the injected IPaymentGateway, persists the order to the database, and dispatches a confirmation email. It returns an OrderResult containing the new order ID and status."
}
```

### `completion`

Partial code is presented; the model must complete it. High value for autocomplete-style fine-tuning.

```json
{
  "prompt": "Complete the following C# method:\n\npublic async Task<OrderResult> PlaceOrderAsync(Cart cart, PaymentInfo payment)\n{\n    if (cart is null || !cart.Items.Any())\n        throw new ArgumentException(\"Cart must not be empty\", nameof(cart));\n\n    var charge = await _paymentGateway.ChargeAsync(",
  "completion": "payment, cart.TotalAmount, cancellationToken);\n    if (!charge.Success)\n        return OrderResult.Failed(charge.ErrorMessage);\n\n    var order = Order.FromCart(cart, charge.TransactionId);\n    await _repository.SaveAsync(order, cancellationToken);\n    await _emailService.SendConfirmationAsync(order);\n    return OrderResult.Ok(order.Id);\n}"
}
```

### `bug-fix`

A subtly broken version of a real method is paired with the corrected version and an explanation of the bug.

```json
{
  "instruction": "The following C# method contains a bug. Identify and fix it.",
  "buggy_code": "public decimal CalculateDiscount(Order order) {\n    if (order.Items.Count > 10)\n        return order.Subtotal * 0.1m;\n    return 0;\n}",
  "fixed_code": "public decimal CalculateDiscount(Order order) {\n    if (order.Items.Count >= 10)\n        return order.Subtotal * 0.1m;\n    return 0;\n}",
  "explanation": "The original uses `> 10` but the business rule is that the discount applies at 10 or more items. Changed to `>= 10`."
}
```

### `unit-test`

Generates a unit test for a public method, grounded in the actual signature and behaviour.

```json
{
  "instruction": "Write an xUnit unit test for `PlaceOrderAsync` covering the case where the payment charge fails.",
  "context": "public async Task<OrderResult> PlaceOrderAsync(Cart cart, PaymentInfo payment) { ... }",
  "response": "[Fact]\npublic async Task PlaceOrderAsync_WhenChargeFails_ReturnsFailedResult()\n{\n    var gateway = Substitute.For<IPaymentGateway>();\n    gateway.ChargeAsync(Arg.Any<PaymentInfo>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())\n           .Returns(ChargeResult.Fail(\"Insufficient funds\"));\n    var sut = new OrderService(gateway, ...);\n\n    var result = await sut.PlaceOrderAsync(TestData.NonEmptyCart, TestData.ValidPayment);\n\n    Assert.False(result.IsSuccess);\n    Assert.Equal(\"Insufficient funds\", result.ErrorMessage);\n}"
}
```

### `docstring`

Generates XML documentation comments for undocumented members.

```json
{
  "instruction": "Write XML documentation comments for the following C# method.",
  "code": "public async Task<OrderResult> PlaceOrderAsync(Cart cart, PaymentInfo payment, CancellationToken cancellationToken = default)",
  "response": "/// <summary>\n/// Places an order using the provided cart and payment information.\n/// </summary>\n/// <param name=\"cart\">The shopping cart containing items to purchase.</param>\n/// <param name=\"payment\">Payment details for charging the order total.</param>\n/// <param name=\"cancellationToken\">Token to cancel the asynchronous operation.</param>\n/// <returns>\n/// An <see cref=\"OrderResult\"/> indicating success with the new order ID,\n/// or failure with an error message if the payment was declined.\n/// </returns>\n/// <exception cref=\"ArgumentException\">Thrown when <paramref name=\"cart\"/> is null or empty.</exception>"
}
```

### `refactor`

High-complexity methods (cyclomatic complexity > threshold) are paired with refactored versions.

```json
{
  "instruction": "Refactor the following C# method to improve readability and reduce cyclomatic complexity.",
  "original_code": "// complexity: 14\npublic string ProcessPayment(Order order) { ... }",
  "refactored_code": "// complexity: 4\npublic string ProcessPayment(Order order) { ... }",
  "explanation": "Extracted the validation logic into a ValidateOrder method, replaced nested if-else with early returns, and extracted the currency formatting into a helper."
}
```

### `architecture-qa`

High-level question-and-answer pairs about project structure, patterns, and design decisions, derived from the solution graph.

```json
{
  "question": "What design pattern does the `OrderService` class follow, and how is it composed?",
  "answer": "OrderService follows the Dependency Injection pattern. It receives IPaymentGateway, IOrderRepository, and IEmailService via constructor injection, registered in ServiceCollectionExtensions.AddOrderServices(). It implements the IOrderService interface defined in the Domain project, with the concrete class living in the Application project."
}
```

### `mixed` (default)

Proportional sampling across all types above, with weights tunable in the pipeline config.

---

## LLM providers

Configure with `--provider` and `--model`.

| Provider | `--provider` value | Authentication |
|---|---|---|
| OpenAI | `openai` | `OPENAI_API_KEY` env var |
| Anthropic | `anthropic` | `ANTHROPIC_API_KEY` env var |
| Azure OpenAI | `azure-openai` | `AZURE_OPENAI_ENDPOINT` + `AZURE_OPENAI_API_KEY` |
| Google Gemini | `gemini` | `GEMINI_API_KEY` env var |
| Ollama (local) | `ollama` | `OLLAMA_BASE_URL` (default: `http://localhost:11434`) |
| LM Studio | `lmstudio` | `LMSTUDIO_BASE_URL` |
| Any OpenAI-compatible | `openai-compatible` | `OPENAI_COMPATIBLE_BASE_URL` + `OPENAI_COMPATIBLE_API_KEY` |

```bash
# Use a local Ollama model — no API key required
dnx DistSharp generate ./MyApp.sln \
  --provider=ollama \
  --model=qwen2.5-coder:32b \
  --out-dir ./local-datasets

# Azure OpenAI
dnx DistSharp generate ./MyApp.sln \
  --provider=azure-openai \
  --model=gpt-4.1 \
  --out-dir ./datasets
```

---

## Pipeline configuration

The `init` command creates a `distsharp.yaml` that you can customise:

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
    - MyApp.Scaffolding

steps:
  - name: extract_symbols
    type: RoslynSymbolExtractor
    config:
      symbol_kinds: [method, property, class, interface]
      max_body_lines: 200

  - name: sample
    type: StratifiedSampler
    depends_on: [extract_symbols]
    config:
      max_rows: 50000
      strategy: complexity_weighted   # sample harder code more heavily

  - name: generate_explanations
    type: LlmStep
    depends_on: [sample]
    config:
      dataset_type: explanation
      provider: openai
      model: gpt-4.1-mini
      workers: 8
      temperature: 0.7
      system_prompt: |
        You are a senior .NET engineer explaining code to a capable colleague.
        Be precise, reference type names, and mention important edge cases.

  - name: generate_unit_tests
    type: LlmStep
    depends_on: [sample]
    config:
      dataset_type: unit-test
      provider: openai
      model: gpt-4.1
      workers: 4
      temperature: 0.2

  - name: score_quality
    type: LlmJudge
    depends_on: [generate_explanations, generate_unit_tests]
    config:
      provider: anthropic
      model: claude-opus-4-7
      rubric: helpfulness_and_correctness
      min_score: 3.5   # rows below this threshold are discarded

  - name: deduplicate
    type: MinHashDeduplicator
    depends_on: [score_quality]
    config:
      threshold: 0.85

output:
  dir: ./training-data
  format: jsonl
  schema: alpaca          # instruction / input / output
  write_metadata: true    # include symbol metadata as extra fields
  checkpoint_every: 1000  # flush to disk every N rows
```

---

## Output formats

### JSONL (default)

One JSON object per line. Standard for most fine-tuning workflows.

```jsonl
{"instruction":"Explain PlaceOrderAsync","input":"...code...","output":"...explanation...","metadata":{"file":"OrderService.cs","complexity":7}}
```

### Alpaca

Compatible with [Alpaca](https://github.com/tatsu-lab/stanford_alpaca) and Axolotl's `alpaca` format.

```json
{"instruction": "...", "input": "...", "output": "..."}
```

### ShareGPT

Multi-turn conversation format, compatible with Axolotl's `sharegpt` format and Unsloth.

```json
{
  "conversations": [
    {"from": "human", "value": "Explain PlaceOrderAsync..."},
    {"from": "gpt", "value": "PlaceOrderAsync validates..."}
  ]
}
```

### Parquet

Columnar binary format. Efficient for large datasets and direct loading with Hugging Face `datasets`.

```python
from datasets import load_dataset
ds = load_dataset("parquet", data_files="./training-data/*.parquet")
```

### CSV

Plain CSV for quick inspection in spreadsheet tools.

---

## Advanced usage

### Multi-solution datasets

Generate a single unified dataset from multiple solutions for training a cross-repo agent:

```bash
dnx DistSharp generate ./solutions/ \
  --max-rows=200000 \
  --out-dir ./unified-dataset \
  --seed=42
```

Passing a directory causes DistSharp to discover all `.sln` files recursively.

### Quality scoring with LLM-as-judge

Add a `LlmJudge` step in your pipeline YAML to score each generated row on a 1–5 rubric before writing it to the output. Rows below `min_score` are discarded. Use a more capable model as judge than the generation model for best results.

### Stratified sampling

By default DistSharp samples methods proportionally to complexity so that your dataset contains a healthy mix of trivial helpers and complex business logic. Override with `strategy: uniform` or `strategy: complexity_weighted`.

### Resumable runs

Long generation jobs can be interrupted and resumed without losing progress:

```bash
# Start a large run
dnx DistSharp generate ./BigSolution.sln --max-rows=500000 --out-dir ./data

# Ctrl+C part way through, then resume
dnx DistSharp generate ./BigSolution.sln --max-rows=500000 --out-dir ./data --resume
```

Checkpoint files are written to `<out-dir>/.checkpoint/` every N rows (configurable).

### Reproducing a run

Pass `--seed` to fix sampling randomness, and pin the tool version with `dnx DistSharp@x.y.z` to reproduce a dataset exactly.

```bash
dnx DistSharp@1.2.0 generate ./MyApp.sln \
  --max-rows=50000 \
  --seed=12345 \
  --out-dir ./reproducible-dataset
```

---

## Examples

### Fine-tune a code assistant on your own codebase

```bash
# 1. Preview what DistSharp will analyse
dnx DistSharp inspect ./MyApp.sln

# 2. Generate a mixed dataset
dnx DistSharp generate ./MyApp.sln \
  --max-rows=100000 \
  --dataset-type=mixed \
  --provider=openai \
  --model=gpt-4.1-mini \
  --out-dir ./training-data

# 3. Export to ShareGPT format for Unsloth
dnx DistSharp export ./training-data \
  --format=sharegpt \
  --out-dir ./training-data-sharegpt
```

### Generate unit test training data at scale

```bash
dnx DistSharp generate ./src/ \
  --dataset-type=unit-test \
  --include-tests=false \
  --max-rows=50000 \
  --provider=anthropic \
  --model=claude-sonnet-4-6 \
  --format=parquet \
  --out-dir ./unit-test-dataset
```

### Local generation with Ollama — no API key, no cost

```bash
# Requires Ollama running locally with the model pulled
dnx DistSharp generate ./MyApp.sln \
  --provider=ollama \
  --model=qwen2.5-coder:32b \
  --max-rows=20000 \
  --workers=2 \
  --out-dir ./local-data
```

### Upload a finished dataset to Hugging Face

```bash
dnx DistSharp export ./training-data \
  --format=parquet \
  --hf-repo myorg/my-codebase-dataset \
  --hf-token $HF_TOKEN
```

---

## Configuration reference

### Environment variables

| Variable | Purpose |
|---|---|
| `OPENAI_API_KEY` | OpenAI API key |
| `ANTHROPIC_API_KEY` | Anthropic API key |
| `AZURE_OPENAI_ENDPOINT` | Azure OpenAI endpoint URL |
| `AZURE_OPENAI_API_KEY` | Azure OpenAI key |
| `GEMINI_API_KEY` | Google Gemini API key |
| `OLLAMA_BASE_URL` | Ollama base URL (default: `http://localhost:11434`) |
| `LMSTUDIO_BASE_URL` | LM Studio base URL |
| `OPENAI_COMPATIBLE_BASE_URL` | Custom OpenAI-compatible endpoint |
| `OPENAI_COMPATIBLE_API_KEY` | Key for custom endpoint |
| `HF_TOKEN` | Hugging Face API token for hub export |
| `DISTSHARP_CACHE_DIR` | Override default cache directory |

### `distsharp.yaml` top-level keys

| Key | Type | Description |
|---|---|---|
| `name` | string | Pipeline name, used in output metadata |
| `version` | string | Config schema version |
| `solution` | object | Solution analysis settings |
| `steps` | list | Ordered list of pipeline steps |
| `output` | object | Output format and location settings |

---

## Roadmap

- [ ] MCP server mode — expose the running pipeline as a Model Context Protocol server for real-time agent sampling
- [ ] Incremental mode — only generate rows for files changed since the last run (git-diff aware)
- [ ] Azure DevOps integration — pull work-item context to generate requirement-grounded datasets
- [ ] Preference datasets — generate ranked pairs (chosen/rejected) for DPO/ORPO training
- [ ] Embedding-based deduplication — semantic deduplication via local embedding model
- [ ] Web UI — local dashboard for monitoring pipeline progress and browsing generated rows

---

## Acknowledgements

DistSharp is inspired by and owes its core pipeline architecture to **[distilabel](https://github.com/argilla-io/distilabel)** by [Argilla](https://github.com/argilla-io) — a superb framework for building synthetic data pipelines. If you work in Python, use distilabel directly. DistSharp exists to bring those ideas to the .NET ecosystem with native Roslyn integration.

---

## Licence

MIT
