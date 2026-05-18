# DistSharp — Cost Estimation in `inspect` Design

**Date:** 2026-05-18
**Status:** Design / planning. Not yet implemented.
**Scope:** Extends `distsharp inspect` to show estimated LLM spend before the user runs `generate`.

---

## 1. Goals

Cost estimation in `inspect` serves one concrete decision: **should I run this generation, and if so, with which provider and model?**

Users need to know:

- Whether the bill will be $0.50 or $50 before they commit to a long generation run.
- How different provider/model choices compare so they can trade accuracy for cost.
- Whether local providers (Ollama, ONNX) are worth the latency given the free-tier concern.

This is a **pre-flight gate**, not a billing audit. Accuracy within ±30% is useful; accuracy within ±5% requires a live call and is out of scope for v1.

---

## 2. Approach

Cost estimation has three sequential steps. All three execute within `inspect` — no network calls are made unless `--pilot N` is specified or the configured provider has a free token-counting endpoint.

### Step A — Tokenise sampled prompts

`SymbolSampler` draws `N` symbols (default 30) from the full `ExtractedSymbol` list already produced by `ISolutionAnalyzer`. For each sampled symbol and each requested dataset type, `PromptBuilderRegistry.Build` constructs the `PromptResult` (system + user messages). `TokenCounterFactory` selects the appropriate `ITokenCounter` for the target provider, and each prompt's token count is recorded.

The sample produces: `mean_prompt_tokens`, `p10_prompt_tokens`, `p90_prompt_tokens` per (dataset_type, provider) pair.

### Step B — Project tokens × rows × price

```
prompt_tokens_total  = mean_prompt_tokens × row_count
completion_tokens_total = mean_completion_tokens × row_count
cost = (prompt_tokens_total  × price_per_1k_input  / 1000)
     + (completion_tokens_total × price_per_1k_output / 1000)
```

`row_count` is the count of symbols in the relevant kind bucket (e.g. method count for `unit-test`, all symbols for `explanation`). This is already in the `inspect` output; no extra analysis is needed.

Three scenarios are produced per (dataset_type, model):

| Scenario | Prompt tokens | Completion tokens | When |
|---|---|---|---|
| Best case | p10 of sample | dataset-type floor (see §5) | Short symbols, capped responses |
| Expected | mean of sample | heuristic or pilot-measured mean | Typical run |
| Worst case | p90 of sample | `MaxTokens` setting, or model context window cap | Worst-case symbol + max output |

### Step C — Display ranges

Render a Spectre.Console table appended to the existing `inspect` output, showing best/expected/worst cost and token totals. Gated behind `--estimate-cost`; shown per configured provider by default, expandable to all providers with `--all-providers`.

The estimate is marked with its accuracy tier:
- **measured** — token counter used the provider's native endpoint (exact prompt tokens).
- **local** — SharpToken used (exact for GPT-family models, approximate for others).
- **approx** — chars/4 fallback (may be off ±15% on prompt tokens).

---

## 3. Tokenisation

### Provider-native counters (preferred)

Where the provider offers a free token-counting endpoint, prefer it over local approximations — it is exact for that provider and has no native dependency cost.

| Provider | Endpoint / method | Notes |
|---|---|---|
| **Anthropic** | `POST /v1/messages/count_tokens` | Free, not billed. Returns `input_tokens`. |
| **Gemini** | `POST /v1/models/{model}:countTokens` | Free. Returns `totalTokens`. |
| **OpenAI** | No dedicated endpoint | Use local tokeniser (see below). |
| **Azure OpenAI** | No dedicated endpoint | Same as OpenAI — use local tokeniser. |
| **Ollama / LM Studio / ONNX** | No HTTP counter | Local approximation. |
| **OpenAI-compatible** | Varies | Fall back to local approximation. |

### Local tokenisers

For providers without a counting endpoint, use **SharpToken** (a pure-C# tiktoken port — no native binaries, safe for a global `dotnet tool`). Model-to-encoding mapping:

| Model pattern | Encoding |
|---|---|
| `gpt-4o*`, `gpt-4-turbo`, `gpt-4*` | `o200k_base` (gpt-4o) or `cl100k_base` |
| `gpt-3.5*` | `cl100k_base` |
| Unknown / non-OpenAI | `cl100k_base` as reasonable proxy |

`SharpToken` is added as an optional NuGet dependency (not pulled in by default) — loaded lazily. If not available, fall back to the `chars / 4` heuristic.

### Heuristic fallback

`estimated_tokens ≈ chars / 4` — accurate to within ~15% for English code. Used only when no tokeniser is available and the provider has no counting endpoint. Clearly labelled in output as "approx".

### `ITokenCounter` abstraction

```
interface ITokenCounter {
    string ProviderName { get; }
    Task<int> CountAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct);
}
```

Implementations: `AnthropicTokenCounter`, `GeminiTokenCounter`, `SharpTokenCounter`, `ApproximateTokenCounter`. Selected in priority order above.

---

## 4. Pricing table

### Structure

A static JSON file checked into the repository:

```
src/DistSharp.Core/Pricing/model-prices.json
```

```jsonc
{
  "format_version": 1,
  "updated_at": "2026-05-18",
  // PRICE_FRESHNESS: provider pricing changes without notice. Verify at:
  //   OpenAI    → https://openai.com/api/pricing
  //   Anthropic → https://www.anthropic.com/pricing
  //   Gemini    → https://ai.google.dev/pricing
  //   Azure     → https://azure.microsoft.com/en-us/pricing/details/cognitive-services/openai-service/
  "models": [
    {
      "provider": "openai",
      "model": "gpt-4o",
      "input_per_1k_tokens": 0.0025,
      "output_per_1k_tokens": 0.010,
      "context_window": 128000
    },
    {
      "provider": "openai",
      "model": "gpt-4o-mini",
      "input_per_1k_tokens": 0.00015,
      "output_per_1k_tokens": 0.00060,
      "context_window": 128000
    },
    {
      "provider": "openai",
      "model": "o3",
      "input_per_1k_tokens": 0.010,
      "output_per_1k_tokens": 0.040,
      "context_window": 200000
    },
    {
      "provider": "anthropic",
      "model": "claude-opus-4-5",
      "input_per_1k_tokens": 0.015,
      "output_per_1k_tokens": 0.075,
      "context_window": 200000
    },
    {
      "provider": "anthropic",
      "model": "claude-sonnet-4-6",
      "input_per_1k_tokens": 0.003,
      "output_per_1k_tokens": 0.015,
      "context_window": 200000
    },
    {
      "provider": "anthropic",
      "model": "claude-haiku-4-5",
      "input_per_1k_tokens": 0.00080,
      "output_per_1k_tokens": 0.004,
      "context_window": 200000
    },
    {
      "provider": "gemini",
      "model": "gemini-2.0-flash",
      "input_per_1k_tokens": 0.00010,
      "output_per_1k_tokens": 0.00040,
      "context_window": 1000000
    },
    {
      "provider": "gemini",
      "model": "gemini-2.5-pro",
      "input_per_1k_tokens": 0.00125,
      "output_per_1k_tokens": 0.010,
      "context_window": 1000000
    }
    // ollama, onnx, lmstudio: no entry — $0.00 local
  ]
}
```

Model matching uses longest prefix: `gpt-4o-mini` matches before `gpt-4o`. Unknown models fall back to the nearest ancestor if one exists in the table; if no match is found, the estimate is labelled "price unknown — update model-prices.json".

`updated_at` is shown in `inspect` output alongside the estimate so users know how fresh the prices are:
```
Prices from model-prices.json (updated 2026-05-18). Run with --refresh-prices to update.
```

### Freshness strategy — v1

Hand-maintained. Prices are updated in the file when a developer notices a change; a `PRICE_FRESHNESS` note in the file header reminds contributors. This is deliberately simple for v1.

### Freshness strategy — v2 (out of scope for this spec)

`--refresh-prices` fetches from a lightweight endpoint (e.g. `https://openrouter.ai/api/v1/models` exposes price data for most providers). Fails gracefully — falls back to the bundled file with a warning.

### Env-var override

Users can set `DISTSHARP_PRICE_INPUT_<PROVIDER>=<rate>` and `DISTSHARP_PRICE_OUTPUT_<PROVIDER>=<rate>` (e.g., `DISTSHARP_PRICE_INPUT_OPENAI=0.0030`) to override for enterprise or negotiated pricing. Takes precedence over the file.

### Local providers

Ollama, ONNX, and LM Studio report `$0.00 (local)` with no cost table row. A note in `inspect` output reads: "CPU/GPU-time estimation for local providers is not yet supported."

---

## 5. Sampling

### Why sampling rather than full tokenisation

Full tokenisation of every symbol before generating doubles inspect time for large solutions. A sample of 30 symbols gives a mean within ~10% of the population mean (CLT), which is accurate enough for the ±30% goal.

### Sample strategy

Stratified random sample across symbol kinds (method, class, property, etc.) and complexity buckets (low/medium/high). Seed is fixed to a hash of the solution path so results are reproducible — running `inspect` twice gives identical estimates.

### Completion length

Completion length is the hardest unknown without a live call. v1 heuristics per dataset type:

| Dataset type | Expected completion tokens | Basis |
|---|---|---|
| `docstring` | 80 | Typical XML doc block |
| `explanation` | 200 | Short paragraph |
| `unit-test` | 350 | Single test method + class shell |
| `refactor` | same as prompt | Code-in, code-out |
| `bug-fix` | same as prompt | Code-in, code-out |
| `completion` | 150 | Partial method body |
| `architecture-qa` | 300 | Short answer paragraph |

These are labelled "heuristic" in output. The worst-case scenario uses `MaxTokens` from `LlmRequestOptions` (or model context cap when `MaxTokens` is null).

### Mini-pilot (optional, `--pilot N`)

When `--pilot N` is passed (default: off), `inspect --estimate-cost` makes `N` real LLM calls (one per dataset type, using short/medium/long representative symbols) and measures the actual completion token count from the response. The measured mean replaces the heuristic in the expected-case scenario.

Pilot call count: one call per dataset type × 3 complexity tiers = up to 21 calls for 7 types. In practice `N` acts as a cap — if `N = 7`, one symbol per dataset type is used.

Pilot calls are billed but typically cost < $0.05 for `N = 7` with a fast model like `gpt-4o-mini`.

**Interface extension required.** Because `ILlmProvider.CompleteAsync` currently returns only `string`, measuring completion tokens requires extending the interface. Two options:

- **Option A:** Add `CompleteWithUsageAsync` returning a new `LlmCompletionResult { string Text; int? PromptTokens; int? CompletionTokens; }`. Clean but breaks the abstraction for providers that don't surface usage.
- **Option B:** Add an optional `EstimateCompletionTokensAsync` method via default-interface implementation — returns `null` when not supported, real value when the provider can surface it cheaply (e.g., non-streaming mode with `stream_options.include_usage: true`).

Option B is preferred for v1: non-breaking, transparent about capability gaps. The pilot feature is gated behind `--pilot` so providers that return null simply skip the calibration.

---

## 6. Output UX

### Inline in `inspect` (gated behind `--estimate-cost`)

The existing Spectre.Console rounded table gets a new section below the symbol counts:

```
╭──────────────────────────────────────────────────────────╮
│ Metric                                  Value             │
├──────────────────────────────────────────────────────────┤
│ Source files                            142               │
│   method                                891               │
│   class                                 203               │
│ Average method complexity               3.2               │
├──────────────────────────────────────────────────────────┤
│ Cost estimate — openai / gpt-4o (unit-test × 891 rows)   │
│   Prompt tokens        expected         189M              │
│   Completion tokens    expected          62M              │
│   Cost                 best / exp / worst                 │
│                        $14.20 / $19.60 / $31.00          │
╰──────────────────────────────────────────────────────────╯
Prices from model-prices.json (updated 2026-05-18). Heuristic completion estimate.
```

### Per-dataset-type breakdown

When `--all-types` is passed, one sub-table per dataset type is shown. Default: the dataset type(s) specified in the active pipeline config, or `unit-test` if no config is present.

### What-if: all providers (`--all-providers`)

Adds a side-by-side table:

```
╭──────────────────────────────────────────────────────────╮
│ Provider               Model             Expected cost    │
├──────────────────────────────────────────────────────────┤
│ openai                 gpt-4o            $19.60           │
│ anthropic              claude-sonnet-4-6 $11.20           │
│ gemini                 gemini-2.0-flash  $3.40            │
│ ollama (local)         llama-3.3-70b     $0.00 (local)    │
│ onnx (local)           phi-3-mini        $0.00 (local)    │
╰──────────────────────────────────────────────────────────╯
```

Providers with no pricing data are omitted unless `--all-providers` explicitly requested.

---

## 7. Components & file layout

```
src/
  DistSharp.Core/
    Abstractions/
      ITokenCounter.cs             # Interface: ProviderName + CountAsync
    Estimation/
      CostEstimator.cs             # Orchestrates sample → tokenise → compute
      CostEstimate.cs              # Result model (best/expected/worst cost + tokens per scenario)
      EstimationOptions.cs         # SampleSize, PilotN, DatasetTypes, Providers
      SymbolSampler.cs             # Stratified sample from IReadOnlyList<ExtractedSymbol>
      TokenCounterFactory.cs       # Selects ITokenCounter by provider name
      CompletionLengthHeuristics.cs # Per-dataset-type floor/expected/ceiling values
      Tokenisers/
        AnthropicTokenCounter.cs   # Calls POST /v1/messages/count_tokens
        GeminiTokenCounter.cs      # Calls POST /v1/models/{model}:countTokens
        SharpTokenCounter.cs       # SharpToken NuGet, pure-C# tiktoken port
        ApproximateTokenCounter.cs # chars / 4 fallback; labelled "approx" in output
    Pricing/
      PricingTable.cs              # Loads model-prices.json; longest-prefix model match
      ModelPriceEntry.cs           # Record: provider, model, input/output rates, context window
      model-prices.json            # Hand-maintained; EmbeddedResource in DistSharp.Core.csproj
  DistSharp.Cli/
    Commands/
      InspectCommandOptions.cs     # New: EstimateCost, Provider, Model, AllProviders,
                                   #      AllTypes, PilotN, SampleSize
      InspectCommandHandler.cs     # Calls CostEstimator when EstimateCost = true;
                                   #      passes ITokenCounter from DI
```

### Key type signatures

```csharp
// DistSharp.Core.Abstractions
public interface ITokenCounter
{
    string ProviderName { get; }
    Task<int> CountAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct);
}

// DistSharp.Core.Estimation
public sealed record CostEstimate
{
    public string Provider { get; init; }
    public string Model { get; init; }
    public string DatasetType { get; init; }
    public long RowCount { get; init; }
    public CostScenario BestCase { get; init; }
    public CostScenario Expected { get; init; }
    public CostScenario WorstCase { get; init; }
    public string AccuracyTier { get; init; }  // "measured" | "local" | "approx"
}

public sealed record CostScenario
{
    public long PromptTokens { get; init; }
    public long CompletionTokens { get; init; }
    public decimal CostUsd { get; init; }
}
```

`ILlmProvider` gains an optional default-interface method:

```csharp
// Optional; providers that cannot surface token counts return null.
Task<int?> EstimateCompletionTokensAsync(
    IReadOnlyList<ChatMessage> messages,
    LlmRequestOptions options,
    CancellationToken cancellationToken)
    => Task.FromResult<int?>(null);
```

This is used only by `--pilot N` and never called otherwise — no impact on existing providers until they opt in.

---

## 8. CLI surface

```bash
# Show cost estimate for the configured provider and dataset type(s)
distsharp inspect MyApp.sln --estimate-cost

# Estimate for a specific provider + model, all dataset types
distsharp inspect MyApp.sln --estimate-cost --provider openai --model gpt-4o --all-types

# Side-by-side what-if across all providers in model-prices.json
distsharp inspect MyApp.sln --estimate-cost --all-providers

# Use a mini-pilot for better completion-length accuracy (7 real calls)
distsharp inspect MyApp.sln --estimate-cost --pilot 7

# Override sample size (default 30)
distsharp inspect MyApp.sln --estimate-cost --sample-size 50
```

New flags added to `InspectCommandOptions`:

| Flag | Type | Default | Description |
|---|---|---|---|
| `--estimate-cost` | `bool` | `false` | Enable cost estimation section |
| `--provider <x>` | `string?` | configured | Override provider for what-if |
| `--model <x>` | `string?` | configured | Override model for what-if |
| `--all-providers` | `bool` | `false` | Show all providers from pricing table |
| `--all-types` | `bool` | `false` | Show per-dataset-type breakdown |
| `--pilot <N>` | `int?` | null | Make N real calls for completion calibration |
| `--sample-size <N>` | `int` | `30` | Number of symbols to sample for tokenisation |

---

## 9. Effort

| Phase | Scope | Days |
|---|---|---|
| 1 | `SymbolSampler` + `ApproximateTokenCounter` + `PricingTable` (JSON load) + basic `CostEstimator` + inspect output (one provider, one type) | 2 |
| 2 | `SharpTokenCounter`, `AnthropicTokenCounter`, `GeminiTokenCounter`, `TokenCounterFactory` | 1.5 |
| 3 | `--all-providers` what-if table, `--all-types` breakdown, env-var price override | 1 |
| 4 | `--pilot N` mini-pilot (requires `ILlmProvider` extension) | 1 |
| **Total** | | **~5.5 days** |

Phase 1 is independently shippable and provides 80% of the user value.

---

## 10. Risks & open questions

1. **(BLOCKING) Pricing data source of truth.** A hand-maintained file goes stale fast (providers change prices with days' notice). The estimate is worse than useless if it's confidently wrong. Decision needed: accept staleness with a clear `updated_at` annotation + env-var override, or invest in `--refresh-prices` at the start rather than deferring to v2.

2. **(BLOCKING) `ILlmProvider` extension for token counting.** The `AnthropicTokenCounter` and `GeminiTokenCounter` need HTTP access to the provider's base URL and credentials — currently encapsulated inside provider implementations. Options: (a) expose a `CountTokensAsync` default-interface method on `ILlmProvider`; (b) add a separate `ITokenCountingProvider` interface; (c) keep the counters independent with their own config. Option (a) is least invasive but requires providers to opt in.

3. Completion length heuristics. Without a pilot, completion length is substantially unknown — especially for `unit-test` and `refactor` where output grows with method body size. The expected-cost scenario may be off by 2× for complex methods. Mitigation: document the heuristic basis, show the best/worst range prominently.

4. `SharpToken` dependency footprint. SharpToken ships encoding data files as embedded resources (~2 MB). Acceptable for a CLI tool; verify it does not bloat the `dotnet tool` package beyond reason before committing.

5. `--all-providers` information density. Showing eight providers by default may overwhelm users who only care about their configured one. Mitigation: show only configured provider by default; require explicit `--all-providers` flag.

6. `dataset sync` interaction. `dataset sync` regenerates only changed rows. The cost estimate from `inspect` applies to a full re-run; a partial-sync estimate is a separate concern and not addressed here.

---

## 11. Out of scope

- Actual token usage recording during `generate` (post-generation billing summary).
- `--refresh-prices` automatic pricing update from OpenRouter or provider APIs.
- CPU/GPU-time estimation for local providers (Ollama, ONNX, LM Studio).
- Cost estimation for embedding calls (not currently a DistSharp operation).
- Per-row cost breakdown in the JSON `--report` output (though the total estimate could be added there as a later enhancement).
- Multi-turn / conversation datasets where prompt length grows across turns.
