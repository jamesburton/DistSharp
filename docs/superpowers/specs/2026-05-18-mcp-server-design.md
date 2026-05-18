# DistSharp — MCP Server Mode Design

**Date:** 2026-05-18
**Status:** Design / planning. Not yet implemented.
**Scope:** Expose DistSharp as a Model Context Protocol (MCP) server so LLM clients (Claude Code, Cursor, Copilot Chat, etc.) can drive dataset generation, inspection, and sync natively from their tool-calling loop.

---

## 1. Goals

### What an LLM client gains over running the CLI directly

Running `distsharp generate` as a shell command is a dead-end integration:
- The client must serialize all parameters as a flat string command, losing type safety.
- Output is unstructured text; the client cannot iterate over rows, query the manifest, or read dataset metadata programmatically.
- Long-running operations block the shell; there is no way to observe intermediate progress without scraping stdout.
- The client cannot ask "what symbols does this solution contain?" before committing to a full `generate` run.
- There is no canonical way to surface DistSharp's built-in prompt templates to a client that wants to preview or tweak them before generation.

An MCP server eliminates all of these:

| Goal | Mechanism |
|---|---|
| Typed, composable tool calls | `[McpServerTool]` with JSON Schema parameter descriptions |
| Live progress during `generate` / `sync` | `IProgress<ProgressNotificationValue>` — streamed to the client's progress panel |
| Dataset contents as readable resources | `[McpServerResource]` URIs — client can read/browse without file-system access |
| Prompt previewing | `[McpServerPrompt]` — client can list and render any of the 7 dataset-type prompts for a concrete symbol before committing tokens |
| Ambient context injection | Claude Code can read `distsharp://manifest` and include live dataset stats as context in any conversation |
| No shell escaping hell | Tool parameters are JSON objects; solution paths, flags, provider names are all first-class |

### Non-goal

MCP server mode does not replace the CLI. Both remain; the MCP server uses the same `IServiceCollection` wiring and the same `IPipelineExecutor` underneath.

---

## 2. Approach

### SDK choice

Use the official **`ModelContextProtocol`** NuGet package (`/modelcontextprotocol/csharp-sdk`), maintained by Microsoft in collaboration with Anthropic. This is the only production-grade .NET MCP SDK. It provides:

- Attribute-based tool, resource, and prompt registration (`[McpServerTool]`, `[McpServerResource]`, `[McpServerPrompt]`)
- Stdio transport (production-ready, required for Claude Code / Cursor integration)
- HTTP+SSE transport via `ModelContextProtocol.AspNetCore` (enables multi-client scenarios and remote access)
- First-class `IProgress<ProgressNotificationValue>` support for long-running tool calls
- Integrates with `Microsoft.Extensions.Hosting` — the same host model DistSharp already uses

**No hand-rolled JSON-RPC.** The protocol surface is large enough that maintaining a custom implementation would be a liability.

### Transport choice

Two transports are exposed, selected at startup:

| Transport | Flag | Use case |
|---|---|---|
| **stdio** | `--transport stdio` (default) | Claude Code, Cursor, Copilot Chat — all spawn the MCP server as a child process and talk via stdin/stdout |
| **HTTP+SSE** | `--transport http [--port N]` | Remote access, multi-client dashboards, CI pipelines that want to drive generation over the network |

The default is stdio. HTTP requires `ModelContextProtocol.AspNetCore` and an ASP.NET Core host; stdio runs on the plain Generic Host already in use. Both transports share the same tool/resource/prompt registrations — transport is a startup concern only.

---

## 3. Server surface

### 3.1 Tools

Tools map 1:1 to existing CLI commands. Each tool parameter corresponds to an option on the matching command handler; descriptions come from the existing XML doc comments.

#### `inspect_solution`

Analyse a .NET solution and return the symbol inventory. Fast — no LLM calls.

```
Parameters:
  solution        string   Path to .sln, .csproj, or directory.
  include_tests   bool     Include test projects. Default: false.
  show_symbols    bool     Include per-symbol list in result. Default: false.
Returns:
  JSON object: { project_count, symbol_count, symbols?: [...] }
```

Maps to `InspectCommandHandler`. Backed by `ISolutionAnalyzer`.

---

#### `extract_symbols`

Stream all symbols from a solution with full metadata (complexity, kind, file, namespace, body). The lower-level primitive that `generate_dataset` calls internally — exposed separately so a client can sample, filter, or preview symbols before committing to an LLM run.

```
Parameters:
  solution            string      Path to .sln, .csproj, or directory.
  include_tests       bool        Default: false.
  include_generated   bool        Default: false.
  min_complexity      int         Default: 3.
  exclude_namespaces  string[]    Namespace prefixes to skip. Default: [].
Returns:
  JSON array of ExtractedSymbol records.
```

Backed by `ISolutionAnalyzer.AnalyzeAsync`.

---

#### `generate_dataset`

Run the full generate pipeline: extract symbols, sample, call the LLM, write JSONL/Parquet. Long-running — emits progress notifications during the LLM step.

```
Parameters:
  solution            string      Required. Path to .sln, .csproj, or directory.
  out_dir             string      Output directory. Default: "./distsharp-out".
  dataset_type        string      One of: explanation, completion, bug-fix, unit-test,
                                  docstring, refactor, architecture-qa. Default: mixed.
  max_rows            int         Default: 50000.
  format              string      jsonl | parquet. Default: jsonl.
  provider            string      LLM provider name. Default: openai.
  model               string?     Provider model override.
  workers             int         Parallel LLM workers. Default: 4.
  min_complexity      int         Default: 3.
  exclude_namespaces  string[]    Default: [].
  include_tests       bool        Default: false.
  dry_run             bool        Skip LLM calls; write empty placeholders. Default: false.
  seed                int?        Random seed for reproducibility.
Returns:
  JSON object: { rows_written, out_dir, manifest_path, duration_seconds }
```

Backed by `GenerateCommandHandler` / `IPipelineExecutor`.

---

#### `run_pipeline`

Run an arbitrary pipeline defined by a YAML configuration file. Complements `generate_dataset` for users who have authored custom `pipeline.yaml` files.

```
Parameters:
  config_path     string   Path to pipeline YAML file.
  out_dir         string?  Override output directory.
  max_rows        int?     Override max rows.
  accelerator     string?  ONNX execution provider override (e.g. cpu).
  model_variant   string?  ONNX model variant override.
Returns:
  JSON object: { rows_written, out_dir, manifest_path, duration_seconds }
```

Backed by `PipelineRunCommandHandler`.

---

#### `sync_dataset`

Synchronise an existing dataset directory against the current state of the solution. Regenerates only rows whose source symbol has changed; leaves unchanged rows intact.

```
Parameters:
  dataset_dir     string   Path to local dataset directory (must contain _distsharp/manifest.json).
  solution        string   Path to .sln, .csproj, or directory.
  dataset_type    string   Dataset type to sync. Default: explanation.
  orphan_policy   string   drop | keep | archive. Default: drop.
  provider        string   LLM provider. Default: openai.
  dry_run         bool     Plan only — no LLM calls or file writes. Default: false.
Returns:
  JSON object: { added, updated, dropped, unchanged, duration_seconds }
```

Backed by `DatasetSyncCommandHandler`.

---

#### `export_dataset`

Export a local dataset to a target format or Hugging Face Hub repository.

```
Parameters:
  dataset_dir   string    Path to local dataset directory.
  format        string?   Target format override (jsonl | parquet).
  hf_repo       string?   Hugging Face repo ID (e.g. "myorg/myrepo").
  out_dir       string?   Local output directory for format conversion.
  split         string    Dataset split name. Default: train.
Returns:
  JSON object: { rows_exported, destination }
```

Backed by `ExportCommandHandler`.

---

#### `list_dataset_types`

Returns the list of supported dataset types and a short description of each. No parameters.

```
Returns:
  JSON array of { name, description } objects for:
  explanation, completion, bug-fix, unit-test, docstring, refactor, architecture-qa.
```

Backed by `PromptBuilderRegistry.SupportedTypes`.

---

### 3.2 Resources

Resources expose read-only dataset state that a client can subscribe to and read without calling a tool.

#### `distsharp://datasets`

List all dataset directories known to the server. In stdio mode, "known" means the current working directory and any paths passed at startup via `--datasets-root`. In HTTP mode, the server scans a configurable root.

```
Returns:
  JSON array of { path, dataset_type, row_count, last_synced } — derived from manifest.json in each directory.
```

#### `distsharp://datasets/{dir}/manifest`

The raw `_distsharp/manifest.json` for the dataset at `dir` (URL-encoded path).

```
Returns: manifest.json contents as JSON.
```

#### `distsharp://datasets/{dir}/schema`

The `_distsharp/schema.json` for the dataset at `dir`, describing the row fields for the dataset type.

```
Returns: schema.json contents as JSON.
```

#### `distsharp://datasets/{dir}/pipeline`

The `_distsharp/pipeline.snapshot.yaml` for the dataset at `dir` — the frozen pipeline config that produced the current state.

```
Returns: pipeline.snapshot.yaml contents as text.
```

#### `distsharp://solutions/{path}/symbols`

On-demand symbol list for a solution path (URL-encoded). Returns the same data as `extract_symbols` but as a subscribable resource — useful for clients that want to cache and watch symbol counts.

```
Returns: JSON array of ExtractedSymbol records.
```

---

### 3.3 Prompts

MCP prompts let a client list, preview, and inject DistSharp's built-in prompt templates into a conversation. There is one prompt per supported dataset type, plus one meta-prompt for pipeline YAML authoring.

Each prompt takes a symbol as input (or a placeholder if no symbol is available) and returns the system + user message pair that DistSharp would send to the LLM.

| Prompt name | Dataset type | Description |
|---|---|---|
| `explain_symbol` | explanation | "Explain what this method does and why." |
| `complete_symbol` | completion | "Complete the missing implementation." |
| `fix_bug` | bug-fix | "Identify and fix the bug in this method." |
| `generate_unit_test` | unit-test | "Write xUnit tests for this method." |
| `write_docstring` | docstring | "Write XML documentation for this symbol." |
| `suggest_refactor` | refactor | "Refactor this method for clarity and testability." |
| `architecture_qa` | architecture-qa | "Answer an architectural question about this type." |
| `pipeline_yaml_author` | (meta) | Renders a prompt that helps a user draft a `pipeline.yaml` from a natural-language description of their use case. |

All seven dataset-type prompts are backed by the existing `PromptBuilderRegistry` and the corresponding `*PromptBuilder` classes in `DistSharp.Core/Prompts/`.

---

## 4. CLI entry point

New subcommand: `distsharp mcp serve`.

```
distsharp mcp serve [options]

Options:
  --transport <stdio|http>   Transport to use. Default: stdio.
  --port <number>            HTTP port. Default: 3001. Ignored for stdio.
  --datasets-root <path>     Root directory scanned for dataset subdirectories.
                             May be specified multiple times.
  --name <string>            Server name reported in MCP handshake.
                             Default: "DistSharp".
  --version <string>         Server version. Default: assembly version.
```

This follows the same pattern as the existing `pipeline` command group — `mcp` is a parent command with `serve` as a subcommand, leaving room for `mcp client` or `mcp inspect` in future.

### Host wiring

The `mcp serve` handler reuses `HostBuilder.Build(args)` but then calls into a new `McpServerRunner` class that:

1. Calls `services.AddMcpServer().WithStdioServerTransport()` (or `.WithHttpTransport()`) and registers tool/resource/prompt types from `DistSharp.Mcp`.
2. Runs the host to completion (blocking until the transport closes).

For stdio, the Generic Host's `IHostedService` model fits naturally — the MCP server runs as a hosted service, and the process exits when stdin closes.

---

## 5. Auth

| Transport | Auth model |
|---|---|
| **stdio** | No auth. The spawning process (Claude Code, Cursor) owns the process; OS-level process isolation is sufficient. This matches how all stdio MCP servers work. |
| **HTTP (local)** | No auth by default (loopback only: `http://localhost:{port}`). Suitable for single-user local use. |
| **HTTP (remote / `--host 0.0.0.0`)** | Bearer token auth via `Authorization: Bearer <token>` header. Token set via `--token` CLI option or `DISTSHARP_MCP_TOKEN` environment variable. ASP.NET Core middleware validates the token before routing requests to the MCP handler. |

No OAuth or multi-tenant auth in scope for v1. If needed, a reverse proxy (nginx, Caddy, Tailscale Funnel) handles that externally.

---

## 6. Long-running operations

`generate_dataset` and `sync_dataset` can run for minutes (thousands of LLM calls). The MCP spec supports progress notifications via `notifications/progress`, and the `ModelContextProtocol` C# SDK surfaces this as `IProgress<ProgressNotificationValue>` — a parameter the tool method receives via dependency injection.

### Design

Tool methods that wrap long-running operations accept `IProgress<ProgressNotificationValue>` and `CancellationToken` as parameters alongside the user-provided arguments. Progress is reported per batch of rows:

```csharp
[McpServerTool, Description("...")]
public async Task<GenerateResult> GenerateDataset(
    GenerateDatasetArgs args,
    IProgress<ProgressNotificationValue> progress,
    CancellationToken cancellationToken)
{
    // wrap IPipelineExecutor with a progress-reporting adapter
}
```

The progress adapter listens to the pipeline's existing progress channel (the same one `LiveProgressDisplay` uses in the CLI) and converts it to `ProgressNotificationValue` messages with `Progress`, `Total`, and `Message` fields.

Clients that do not support `notifications/progress` simply ignore the notifications — there is no protocol-level breakage.

### Cancellation

`CancellationToken` is injected by the SDK and is cancelled when the client sends a `cancel` request. The token is plumbed through to `IPipelineExecutor.ExecuteAsync` and `ISolutionAnalyzer.AnalyzeAsync`, both of which already accept and honour it.

### No job-ID polling

The SDK's native progress model is sufficient. A job-ID + poll pattern (returning immediately with a handle and requiring a separate `get_job_result` call) is unnecessary complexity and breaks the simple request-response model that most MCP clients expect. It would also require state persistence across requests, which has implications for concurrent client safety (see §9, risk 3).

---

## 7. Components and file layout

A new `DistSharp.Mcp` class library project holds all MCP-specific code. It does not contain business logic — it is a thin adapter layer that translates between MCP tool parameters and the existing service interfaces.

```
src/
  DistSharp.Mcp/
    DistSharp.Mcp.csproj              # refs: DistSharp.Core, DistSharp.Cli (for handlers),
                                      #       ModelContextProtocol, ModelContextProtocol.AspNetCore
    McpServerRunner.cs                # AddMcpServer() wiring + transport selection
    Tools/
      InspectTool.cs                  # [McpServerToolType] — wraps InspectCommandHandler
      ExtractSymbolsTool.cs           # [McpServerToolType] — wraps ISolutionAnalyzer directly
      GenerateTool.cs                 # [McpServerToolType] — wraps GenerateCommandHandler
      PipelineRunTool.cs              # [McpServerToolType] — wraps PipelineRunCommandHandler
      SyncTool.cs                     # [McpServerToolType] — wraps DatasetSyncCommandHandler
      ExportTool.cs                   # [McpServerToolType] — wraps ExportCommandHandler
      ListDatasetTypesTool.cs         # [McpServerToolType] — reads PromptBuilderRegistry
    Resources/
      DatasetDirectoryResource.cs     # distsharp://datasets
      ManifestResource.cs             # distsharp://datasets/{dir}/manifest
      SchemaResource.cs               # distsharp://datasets/{dir}/schema
      PipelineSnapshotResource.cs     # distsharp://datasets/{dir}/pipeline
      SolutionSymbolsResource.cs      # distsharp://solutions/{path}/symbols
    Prompts/
      DatasetTypePrompts.cs           # 7 [McpServerPrompt] methods, backed by PromptBuilderRegistry
      PipelineYamlAuthorPrompt.cs     # meta prompt for YAML authoring
    Transport/
      StdioTransportHostedService.cs  # IHostedService for stdio lifecycle
      HttpTransportStartup.cs        # WebApplication + app.MapMcp() for HTTP mode
  DistSharp.Cli/
    Commands/
      McpServeCommandHandler.cs       # new: parses --transport, delegates to McpServerRunner
      McpServeCommandOptions.cs       # new: --transport, --port, --datasets-root, etc.
    Program.cs                        # add mcp serve subcommand to root command
```

### Why a separate project instead of folding into the CLI?

- Keeps MCP SDK references (`ModelContextProtocol`, `ModelContextProtocol.AspNetCore`) out of the CLI project. The CLI should not take a dependency on ASP.NET Core for the HTTP transport path.
- Enables future packaging of the MCP server as a standalone `dotnet tool` without shipping the full CLI (see open question 4 in §9).
- The `DistSharp.Mcp` project has a single, clear responsibility: MCP adapter. It does not make the CLI larger or slower when MCP mode is not used.

### DI / IConfiguration integration

`McpServerRunner` calls `HostBuilder.Build(args)` (the same method the CLI uses) before adding MCP services. This means `IConfiguration`, `IPipelineExecutor`, `ISolutionAnalyzer`, `ILlmProviderFactory`, etc. are all available via normal DI injection into the tool/resource/prompt types. There is no second DI container.

---

## 8. Effort estimate

| Phase | Scope | Estimate |
|---|---|---|
| **Phase 1 — stdio MVP** | `DistSharp.Mcp` project skeleton; `inspect_solution`, `extract_symbols`, `generate_dataset` tools; stdio transport; `mcp serve` CLI subcommand; no resources or prompts | **3–4 days** |
| **Phase 2 — Full tool surface** | `run_pipeline`, `sync_dataset`, `export_dataset`, `list_dataset_types` tools; progress notifications for generate + sync | **2–3 days** |
| **Phase 3 — Resources** | All 5 resource URIs; resource list + read implementation | **1–2 days** |
| **Phase 4 — Prompts** | 7 dataset-type prompts + pipeline YAML meta-prompt | **1 day** |
| **Phase 5 — HTTP transport** | `ModelContextProtocol.AspNetCore`; bearer token auth middleware; `--transport http` flag | **2 days** |
| **Phase 6 — Polish** | Integration tests; Claude Code `mcp.json` config example; README section | **1–2 days** |

**Total: ~10–14 days.** Phase 1 alone is the minimum viable integration — it makes DistSharp usable from Claude Code via `mcp.json` with the three most important tools. Phases 2–4 can land incrementally without breaking the Phase 1 contract. Phase 5 (HTTP) is independent and can be deferred.

---

## 9. Risks and open questions

1. **(BLOCKING) SDK API stability.** `ModelContextProtocol` is currently at pre-release versions (0.x or 1.0-rc). If the attribute API (`[McpServerTool]`, `[McpServerResource]`, `[McpServerPrompt]`) changes between now and when DistSharp ships this feature, the tool wrappers will need updates. **Resolution needed before Phase 1 starts:** pin to a specific package version in `Directory.Packages.props` and document the upgrade path.

2. **(BLOCKING) Progress streaming semantics for `generate_dataset`.** The C# SDK's `IProgress<ProgressNotificationValue>` pattern is confirmed, but it is only useful if the LLM step's inner pipeline exposes a progress channel. The existing `LiveProgressDisplay` pulls from an `IProgress<PipelineProgress>` interface. **Resolution needed:** confirm whether `GenerateCommandHandler` can be refactored to accept an `IProgress<PipelineProgress>` parameter that the MCP tool adapter can intercept, without breaking the CLI's Spectre.Console display path. If not, a second internal progress event needs to be added.

3. **(BLOCKING) Concurrent client safety.** In HTTP mode, multiple clients can connect simultaneously and call `generate_dataset` concurrently. `IPipelineExecutor` is registered as a singleton; it is not clear whether it is safe to run two pipelines in parallel against the same solution. **Resolution needed:** audit `PipelineExecutor` and `ISolutionAnalyzer` (MSBuild workspace) for thread-safety. If not safe, add a per-call semaphore or register `IPipelineExecutor` as transient. Stdio mode is single-client by design and is unaffected.

4. Should `distsharp mcp serve` ship as part of the existing `distsharp` `dotnet tool` package, or as a separate `distsharp-mcp` package? A single package is simpler to distribute and matches "one tool, all features". A separate package would keep the main CLI lean (no ASP.NET Core dependency). **Recommendation:** ship as one package; users who care about binary size will compile with `--self-contained false`. Revisit if `ModelContextProtocol.AspNetCore` pulls in unacceptable transitive dependencies.

5. Tool granularity: expose one `run_pipeline` mega-tool or many small tools (one per CLI command)? The spec recommends small tools — they are more discoverable, more composable, and map cleanly to the existing command surface. A single `run_pipeline` tool would push all complexity into a single JSON parameter blob and make the tool less useful for simple tasks like `inspect_solution`. The mega-tool alternative is documented here for completeness; it should not be adopted.

6. Every CLI flag must be re-expressed as a typed MCP tool parameter. The existing command option classes (`GenerateCommandOptions`, `DatasetSyncCommandOptions`, etc.) have ~8–12 properties each. The tool parameter types can largely mirror these, but descriptions must be added and some flags (e.g. `HfToken` in `ExportCommandOptions`) need to be flagged as secrets. Surface area is manageable but not trivial — budget ~0.5 days per tool for the parameter type definitions and descriptions.

7. How does the MCP server interact with `IConfiguration` and the built-in pipeline YAML? When a user calls `run_pipeline`, the tool path should load the specified YAML file and build a `PipelineConfig` via the existing `YamlConfigurationProvider`. This requires that the DI host is built with a way to pass the per-call YAML path — which is currently done via CLI arguments bound at host-build time. **Resolution:** make `PipelineRunCommandHandler` accept the config path as a method argument (not bound to host configuration at startup), or build a per-call scoped host. This is a small but real refactor.

---

## 10. Out of scope

- **Authentication beyond bearer tokens.** OAuth, OIDC, API-key rotation, and multi-tenant access control are all out of scope for v1.
- **MCP client mode.** DistSharp as an MCP _client_ (e.g. calling a remote LLM tool via MCP rather than the existing `ILlmProvider` interface) is a separate design concern and is not addressed here.
- **Sampling / elicitation.** The MCP spec supports server-initiated sampling (asking the client to run an LLM call) and elicitation (asking the client to prompt the user). Neither is needed for DistSharp's server surface, and both require stateful HTTP sessions that add complexity. Excluded.
- **Multi-solution workspace.** The MCP server operates on one solution per tool call. A "workspace" concept that tracks multiple open solutions is not in scope.
- **Real-time file-system watching.** Resources return a snapshot at read time. Watching solution files and pushing resource-changed notifications proactively is a future enhancement, not a v1 requirement.
- **Windows-only MSBuild dependency.** The Roslyn / MSBuild-based `ISolutionAnalyzer` already requires a local .NET SDK install. The MCP server inherits this constraint; cross-platform documentation should reflect it.
- **GUI or web dashboard.** HTTP transport is API-only (JSON-RPC over SSE). No web UI is planned.
