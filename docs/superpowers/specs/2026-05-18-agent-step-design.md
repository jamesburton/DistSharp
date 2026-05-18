# DistSharp — `agent_step` Pipeline Node Design

**Date:** 2026-05-18
**Status:** Design / planning. Not yet implemented.
**Scope:** New `agent_step` pipeline step type in `DistSharp.Core`, backed by `Microsoft.Agents.AI`, with declarative tool definitions in YAML.
**Parent:** ONNX provider Phase 6 — see [`2026-05-18-onnx-provider-design.md`](2026-05-18-onnx-provider-design.md) §5.

---

## 1. Goals

`LlmStep` makes a single LLM call per row, writing a static prompt → response pair into the dataset. That is enough for tasks where the question and all the context fit in one message. It is not enough when:

- The correct answer requires **inspecting source code** that was not included in the initial prompt (e.g. "explain this method" needs to look up the types it references).
- **Multi-hop reasoning** is needed — the model must decide which facts to gather and in what order before it can write a response.
- The desired dataset captures **tool use traces** (calls + arguments + results) as training signal, not just the final text.
- The response needs to be **verified at generation time** by compiling or running the produced code, with the agent correcting itself on failure.

`agent_step` closes these gaps:

1. Run a `Microsoft.Agents.AI` `ChatAgent` for each row, with declarative tools loaded from YAML config.
2. Capture the agent's final natural-language response as `row["response"]`, matching `LlmStep`'s output contract so downstream steps (`LlmJudge`, `MinHashDeduplicator`) require no change.
3. Optionally capture the full tool-call trace as `row["agent_trace"]` for trace-supervised fine-tuning.
4. Work with any provider that exposes an `IChatClient` — initially ONNX only; other providers require an adapter shim (see §6).

---

## 2. Approach

### 2.1 Channel integration

`AgentStep` implements `IStep`, consuming and producing `Channel<Row>` exactly like every other step. The executor sees it as a black box; no changes to `PipelineExecutor` or `PipelineBuilder`'s fan-in/fan-out logic are needed.

One key constraint: `OgaModel` (ONNX Runtime GenAI) is not thread-safe. `LlmStep` defaults to 4 workers; `AgentStep` must default `workers: 1` for ONNX and document the linear VRAM cost of increasing it. For future hosted providers this default can be relaxed.

### 2.2 Agent construction per row

`Microsoft.Agents.AI.ChatAgent` is lightweight to construct — it holds an `IChatClient` reference and a list of `AIFunction` tool registrations. Construction cost is negligible, so a new `ChatAgent` is created per row. This avoids cross-row memory contamination (the agent SDK may buffer conversation history between turns in a single run, but that history must not leak to the next row). The `IChatClient` itself is expensive (ONNX model load — several seconds on first access, ~500 MB–3 GB VRAM depending on the model variant) and is created once at step initialisation, reused across all rows.

```
AgentStep.ExecuteAsync
  ├── initialise once: IChatClient (from OnnxProvider.GetChatClientAsync())
  └── per row (single worker loop):
        ├── resolve tool instances (bound to this row's read-only context)
        ├── new ChatAgent(chatClient, tools)
        ├── agent.RunAsync(prompt, cancellationToken)
        ├── extract final message text → row["response"]
        ├── optionally serialise trace → row["agent_trace"]
        └── write row to output channel
```

### 2.3 Tool declaration in YAML

Tools are declared under a `tools:` list in the step's `config:` block. Each entry has a `name` that maps to a built-in tool factory registered in `AgentToolRegistry`, plus optional per-tool parameters in a nested `config:` map. The registry of built-in tools is fixed for v1; custom tools are out of scope (see §9).

At step construction time, `PipelineBuilder` iterates the `tools:` list, looks each name up in `AgentToolRegistry`, and instantiates the corresponding `ITool` with the per-tool config values. Unknown names produce a `InvalidOperationException` at startup, not at runtime. This fail-fast behaviour ensures a misconfigured YAML does not silently drop all tool calls mid-pipeline.

```yaml
- name: agent_gen
  type: agent_step
  depends_on: [sampler]
  config:
    provider: onnx
    model: microsoft/Phi-4-mini-instruct-onnx
    system_prompt: |
      You are an expert .NET developer. Use the available tools to explore
      the codebase and produce a high-quality explanation.
    max_iterations: 8
    timeout_seconds: 120
    temperature: 0.0
    tools:
      - name: read_symbol
      - name: read_file
        config:
          root: ./src
          max_lines: 400
      - name: run_xunit_test
        config:
          enable_unsafe_tools: true
          timeout_seconds: 30
    drop_on_error: true
    capture_trace: false
```

### 2.4 Determinism

Instruction-tuning datasets must be reproducible — running the same pipeline twice should produce the same rows. `LlmStep` achieves this with `temperature: 0.0` and seeded sampling upstream. `AgentStep` is harder because:

- Tool *selection order* varies with floating-point non-determinism across runs and execution providers.
- Future tool results (e.g. file I/O) may vary if the codebase changes between runs.

The design takes a two-tier approach:

**Tier 1 — Soft determinism (default):** Set `temperature: 0.0`. This makes the model's token distribution deterministic given identical context. Tool selection becomes deterministic if the model always picks the same tool given the same conversation state. This is sufficient for most datasets.

**Tier 2 — Record-and-replay (opt-in):** When `replay_cache: <path>` is set, the first run records `(row_id → tool_calls[] → final_response)` to a JSONL cache file. Subsequent runs detect a cache hit and replay the recorded tool call sequence, bypassing the model entirely. This guarantees bit-for-bit reproducibility regardless of runtime or model version. Cache misses fall back to Tier 1. The cache key is a SHA-256 of the row's prompt and tool list names. The replay format is an open question (see §8 Q3).

---

## 3. Components & file layout

```
src/DistSharp.Core/
  Steps/
    AgentStep.cs                    # IStep implementation; channel wiring; worker loop
    AgentStepOptions.cs             # options class (mirroring LlmStepOptions)
    AgentToolRegistry.cs            # maps YAML tool name → ITool factory
    ITool.cs                        # tool abstraction (description, invoke)
    Tools/
      ReadSymbolTool.cs             # read_symbol — look up extracted symbol body
      ReadFileTool.cs               # read_file — read source file by relative path
      RunXunitTestTool.cs           # run_xunit_test — build + run test, return output
      CompileCsharpTool.cs          # compile_csharp — syntax + type-check a snippet
    Replay/
      AgentReplayCache.cs           # record-and-replay JSONL cache
      AgentReplayEntry.cs           # serialisation model for one cached run

src/DistSharp.Cli/
  PipelineBuilder.cs                # add "agent_step" case to BuildStep switch
```

No new project files are needed. All types land in `DistSharp.Core` and `DistSharp.Cli`.

### Key types

#### `ITool`

```csharp
/// <summary>A tool that can be invoked by an agent during a pipeline step.</summary>
public interface ITool
{
    /// <summary>Gets the tool name as declared in YAML (e.g. <c>read_symbol</c>).</summary>
    string Name { get; }

    /// <summary>Gets a one-sentence description shown to the model in its tool manifest.</summary>
    string Description { get; }

    /// <summary>Returns the <see cref="AIFunction"/> registered with <c>Microsoft.Agents.AI</c> for this row's context.</summary>
    /// <param name="row">The current pipeline row, providing read-only context (e.g. solution path).</param>
    /// <returns>A callable <see cref="AIFunction"/>.</returns>
    AIFunction CreateFunction(Row row);
}
```

#### `AgentStepOptions`

```csharp
/// <summary>Configuration for <see cref="AgentStep"/>.</summary>
public sealed class AgentStepOptions
{
    /// <summary>Gets or sets the LLM provider name (must resolve to an <see cref="IChatClient"/>).</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Gets or sets the model identifier passed to the provider.</summary>
    public string? Model { get; set; }

    /// <summary>Gets or sets the system prompt for the agent.</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>Gets or sets the sampling temperature. Default: 0.0 for determinism.</summary>
    public float Temperature { get; set; } = 0.0f;

    /// <summary>Gets or sets the maximum number of tool-calling iterations before the agent is forced to produce a final answer.</summary>
    public int MaxIterations { get; set; } = 8;

    /// <summary>Gets or sets the per-row wall-clock timeout in seconds. Default: 120.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Gets or sets the number of parallel worker tasks. Default: 1 (ONNX sessions are not thread-safe).</summary>
    public int Workers { get; set; } = 1;

    /// <summary>Gets or sets a value indicating whether rows that fail (timeout, error, parse failure) are dropped. Default: <see langword="true"/>.</summary>
    public bool DropOnError { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the full tool-call trace is serialised to <c>agent_trace</c> on the output row.</summary>
    public bool CaptureTrace { get; set; } = false;

    /// <summary>Gets or sets the path to the replay cache file, or <see langword="null"/> to disable record-and-replay.</summary>
    public string? ReplayCache { get; set; }

    /// <summary>Gets or sets the tool declarations loaded from YAML config.</summary>
    public List<AgentToolDeclaration> Tools { get; set; } = new();
}
```

#### `AgentToolDeclaration`

```csharp
/// <summary>A declarative tool entry from the YAML <c>tools:</c> list.</summary>
public sealed class AgentToolDeclaration
{
    /// <summary>Gets or sets the tool name (e.g. <c>read_symbol</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets optional per-tool configuration values.</summary>
    public Dictionary<string, object?> Config { get; set; } = new();
}
```

---

## 4. CLI / API + YAML surface

### Complete `distsharp.yaml` example with `agent_step`

```yaml
name: phi4-agent-explanations
solution:
  path: ./MyApp.sln
  include_tests: false
  min_complexity: 5

steps:
  - name: extract
    type: RoslynSymbolExtractor
    config:
      symbol_kinds: [method, property]
      max_body_lines: 200

  - name: sampler
    type: StratifiedSampler
    depends_on: [extract]
    config:
      max_rows: 2000
      strategy: complexity_weighted
      seed: 42

  - name: agent_gen
    type: agent_step
    depends_on: [sampler]
    config:
      provider: onnx
      model: microsoft/Phi-4-mini-instruct-onnx
      temperature: 0.0
      max_iterations: 8
      timeout_seconds: 120
      workers: 1
      system_prompt: |
        You are an expert .NET developer. Use the available tools to inspect
        the codebase before writing your explanation. Be concise and accurate.
      tools:
        - name: read_symbol
        - name: read_file
          config:
            root: ./src
            max_lines: 400
      capture_trace: false
      drop_on_error: true

  - name: judge
    type: LlmJudge
    depends_on: [agent_gen]
    config:
      provider: onnx
      model: microsoft/Phi-4-mini-instruct-onnx
      rubric: code_quality
      min_score: 3.5
      temperature: 0.0

  - name: dedup
    type: MinHashDeduplicator
    depends_on: [judge]
    config:
      field: response
      threshold: 0.85

output:
  format: jsonl
  path: ./output/phi4-agent-explanations.jsonl
```

### Programmatic API (DI / testing)

```csharp
services.AddAgentStep(options =>
{
    options.Provider = "onnx";
    options.Model = "microsoft/Phi-4-mini-instruct-onnx";
    options.MaxIterations = 8;
    options.Tools.Add(new AgentToolDeclaration { Name = "read_symbol" });
});
```

The `AddAgentStep` extension method lives in `DistSharp.Core.ServiceCollectionExtensions` and follows the same pattern used by `AddLlmStep`.

---

## 5. Tool model

Four tools ship in v1. All tools are **read-only by default**. Tools that execute code are gated behind `enable_unsafe_tools: true` as an explicit opt-in in the tool's `config:` block.

### 5.1 `read_symbol`

| | |
|---|---|
| **Signature** | `read_symbol(fully_qualified_name: string) → string` |
| **Returns** | The full source body of the named symbol (method, property, class, interface), extracted from the `ExtractedSymbol` objects already held in the pipeline row's upstream context. Falls back to a Roslyn lookup if not found in the current row's symbol set. |
| **Safety** | Read-only. Accesses only symbols in the currently-analysed solution. No network, no subprocess. |
| **Notes** | The row already carries an `ExtractedSymbol` for the primary symbol being described. `read_symbol` allows the agent to look up *other* symbols referenced in that body, giving it grounding without embedding the entire codebase in the prompt. |

### 5.2 `read_file`

| | |
|---|---|
| **Signature** | `read_file(path: string, start_line: int = 1, end_line: int = max_lines) → string` |
| **Returns** | Lines `start_line` through `end_line` (inclusive) of the file at `path`, relative to the `root` configured in YAML. |
| **Safety** | Read-only. Path is validated to be within `root` (no `../` traversal). `max_lines` cap (default 400) prevents the model from reading multi-MB files in one call. |
| **Notes** | Useful when the agent needs to read imports, surrounding types, or test files not captured as named symbols. |

### 5.3 `run_xunit_test`

| | |
|---|---|
| **Signature** | `run_xunit_test(test_filter: string) → string` |
| **Returns** | `dotnet test --filter <test_filter>` stdout + stderr, trimmed to 4 KB. Exit code prepended as `EXIT:<n>`. |
| **Safety** | **Unsafe — requires `enable_unsafe_tools: true`.** Builds and runs code from the configured solution path. Runs in a child process. Working directory is isolated to `%TEMP%\distsharp-agent-<guid>\` per row. Wall-clock cap enforced (`timeout_seconds` from tool config, default 30 s). No network egress gating — the test code runs with full user permissions. Risk: a malicious or buggy test may write to arbitrary paths or make network calls. |
| **Notes** | Highest-value tool for code-generation tasks (the model can self-verify a generated method passes its tests before finalising its answer), but also the highest-risk. Human decision required on whether to ship in v1 — see §8 Q2. |

### 5.4 `compile_csharp`

| | |
|---|---|
| **Signature** | `compile_csharp(source: string, references: string[] = []) → string` |
| **Returns** | `"OK"` if the snippet compiles, otherwise a newline-separated list of Roslyn diagnostic messages (`CS####: ...`). |
| **Safety** | **Unsafe — requires `enable_unsafe_tools: true`.** Uses `Microsoft.CodeAnalysis.CSharp` (already a dependency via `DistSharp.Roslyn`). Does **not** execute the code — only parses and type-checks. No subprocess. Lower risk than `run_xunit_test`, but arbitrary source is processed in-process, which could trigger Roslyn memory exhaustion on pathological input. Mitigate with a source-length cap (16 KB default). |
| **Notes** | Lets the model check that a generated snippet is syntactically valid before writing it as the final response. The optional `references` list accepts assembly short names (`System.Linq`, `MyApp.Core`) resolved against the project's reference set. |

### Tool availability matrix

| Tool | Default enabled | `enable_unsafe_tools` required | Subprocess | Network |
|---|---|---|---|---|
| `read_symbol` | Yes | No | No | No |
| `read_file` | Yes | No | No | No |
| `compile_csharp` | No | Yes | No | No |
| `run_xunit_test` | No | Yes | Yes | Yes (test code) |

---

## 6. Provider compatibility

`Microsoft.Agents.AI.ChatAgent` is constructed over an `IChatClient` from `Microsoft.Extensions.AI`. The DistSharp provider abstraction (`ILlmProvider`) uses its own `CompleteAsync` contract and does not expose `IChatClient`.

| Provider | `IChatClient` available? | Agent-step compatible? |
|---|---|---|
| `onnx` | Yes — `OnnxProvider` holds an internal `IChatClient` via `OnnxRuntimeGenAIChatClient`. | Yes (v1). |
| `openai` | No — wraps OpenAI HTTP SDK directly, no `Microsoft.Extensions.AI` client. | No (requires adapter shim). |
| `anthropic` | No — wraps Anthropic HTTP API directly. | No (requires adapter shim). |
| `gemini` | No — wraps Gemini HTTP API directly. | No (requires adapter shim). |
| `ollama` | Not checked — likely wraps Ollama HTTP API. | Probably no; `Microsoft.Extensions.AI.Ollama` would provide a shim. |

**v1 ships ONNX only.** Extending to other providers requires either:

- **Per-provider shim:** Implement a thin `IChatClient` wrapper over each `ILlmProvider`. Low risk, moderate effort (1–2 days per provider). The hosted providers already use HTTP; the shim delegates back to `CompleteAsync`.
- **Refactor to `IChatClient` everywhere:** Replace `ILlmProvider` with `IChatClient` as the core abstraction, using `Microsoft.Extensions.AI.*` clients throughout. Better long-term, but a breaking change to the core abstractions and out of scope for this phase.

The per-provider shim approach is recommended for Phase 2 and can be done incrementally without touching `DistSharp.Core`.

`AgentStep` enforces the restriction at construction time: if the resolved provider does not implement `IChatClientProvider` (a new marker interface returning `IChatClient`), it throws `InvalidOperationException` with an actionable message naming the incompatible provider and pointing at this limitation.

---

## 7. Effort

| Phase | Scope | Estimate |
|---|---|---|
| **1: Core infrastructure** | `IStep` scaffold (`AgentStep`, `AgentStepOptions`, `AgentToolDeclaration`), `AgentToolRegistry`, `ITool` abstraction, `PipelineBuilder` wiring, basic `read_symbol` tool | 2 days |
| **2: Additional read-only tools** | `read_file` with path-traversal guard | 0.5 days |
| **3: Trace capture** | `capture_trace: true` — serialise `AgentTrace` to `row["agent_trace"]` as JSON | 0.5 days |
| **4: Record-and-replay cache** | `AgentReplayCache` read/write, JSONL format, SHA-256 keying | 1 day |
| **5: Unsafe tools** | `compile_csharp` (in-process Roslyn), `run_xunit_test` (child process, isolated workdir, timeout) | 2 days |
| **Total** | | **6 days** |

The parent ONNX spec estimated 5 days for Phase 6. This design adds a day to account for the replay cache and the two unsafe tools. Phases 1–4 (everything except unsafe tools) can be shipped independently and are the recommended v1 target. Phase 5 is an opt-in extension that can land later.

---

## 8. Risks & open questions

1. **(BLOCKING) Provider lock-in.** `agent_step` works only with the `onnx` provider in v1. Should the spec explicitly block on a decision about whether to also ship an `IChatClient` shim for `openai` before releasing `agent_step`? A hosted-provider shim would broaden the addressable use-cases enormously (cloud GPUs, larger models) and costs ~1–2 days. **Decision needed: ONNX-only v1, or include OpenAI shim?**

2. **(BLOCKING) Ship `run_xunit_test` in v1?** This tool runs arbitrary test code with the current user's permissions. There is no sandboxing beyond a child process and a wall-clock cap — the test project can write to disk, open sockets, or exhaust memory. For a single-tenant developer tool this may be acceptable; for a shared CI environment it is a real risk. **Decision needed: is `run_xunit_test` in-scope for v1, or deferred to a later phase behind a feature flag?**

3. **Record-and-replay format.** The replay cache entries must store (at minimum) the sequence of tool calls and the final model response. Should it also store intermediate model messages? Storing only the tool sequence and final answer allows full replay with no model calls; storing full message history enables offline inspection and debugging but is larger. No decision needed before implementation, but the schema should be agreed before Phase 4 of the effort plan.

4. **`max_iterations` interaction with token budget.** Each iteration makes at least one LLM call. A row with `max_iterations: 8` and an average of 1 K tokens per call consumes 8 K tokens — 8× the cost of `LlmStep`. There is currently no cross-step token budget or rate-limit mechanism in DistSharp. Concretely: a 2 000-row dataset with `max_iterations: 8` against a hosted provider at $1/M tokens costs ~$16 vs ~$2 for `LlmStep`. For ONNX this is wall-clock time, not money, but the same blowup applies. A future shared `IBudgetTracker` interface should gate both `LlmJudge` and `agent_step` — not required for v1 but should be designed in at the options level (`max_total_tokens`, `max_total_seconds` across all rows).

5. **Memory between rows.** `ChatAgent` can maintain conversation history. A new agent instance is created per row to prevent cross-row contamination, but this means every row starts cold. If a user *wants* cross-row continuity (e.g. progressive dataset generation), this design does not support it. That is intentional for now — dataset reproducibility requires rows to be independent.

6. **`LlmJudge` cost overlap.** `LlmJudge` already makes one additional LLM call per row to score the response. Chaining `agent_step` → `LlmJudge` multiplies per-row LLM cost further (`max_iterations + 1` calls per passing row). This is not a conflict — both steps serve different roles — but pipelines combining both should document the combined cost in `distsharp.yaml` comments or in a future `--dry-run --estimate-cost` flag.

7. **`run_xunit_test` workdir cleanup.** Isolated workdirs under `%TEMP%\distsharp-agent-<guid>\` accumulate on long runs. A cleanup sweep at pipeline end (or a `--gc-agent-workdirs` flag) is needed to avoid filling the temp disk. Out of scope for Phase 5 implementation but must be filed as a follow-up issue before shipping.

8. **Custom tool extensibility.** There is no mechanism in v1 for users to register their own tools. The `ITool` interface is designed for internal use only — `AgentToolRegistry` is sealed to a known list. If users want custom tools, the options are: (a) a plugin assembly loaded at runtime (high risk, complex), (b) a scripting hook (e.g. a local HTTP endpoint the step calls), or (c) a first-class `ITool` extension point in a future phase. No decision required for v1; defer.

---

## 9. Out of scope

- **Custom user-defined tools** — the tool registry is closed in v1. Extension point design is deferred (see §8 Q8).
- **Non-ONNX providers** — `agent_step` requires `IChatClient`. Adapter shims for OpenAI, Anthropic, Gemini, and Ollama are deferred to a separate work item.
- **Cross-row memory / stateful agents** — each row starts a fresh `ChatAgent` instance with no history.
- **Streaming tool responses** — tool results are returned synchronously as strings. Streaming within an agent iteration is not exposed to the pipeline.
- **Vision / audio tools** — `ILlmProvider` is text-only; multimodal tool responses are out of scope until the provider contract is extended.
- **Distributed / multi-node agent execution** — the pipeline runs in a single process; parallelism is limited to `workers` in-process threads.
- **Cost estimation / dry-run** — a `--dry-run --estimate-cost` flag that projects total LLM calls and token cost is not implemented in this phase.
