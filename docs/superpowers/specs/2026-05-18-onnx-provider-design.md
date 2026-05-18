# DistSharp — Local ONNX Provider Design

**Date:** 2026-05-18
**Status:** Design / planning. Not yet implemented.
**Scope:** New `onnx` provider in `DistSharp.Providers`, plus model resolution that reuses Hugging Face and Ollama caches where possible.

---

## 1. Goals

Run DistSharp's `LlmStep` against a fully local model with no external network calls after first-run download. Specifically:

1. Add a new `ILlmProvider` named `onnx` driven by **Microsoft.Extensions.AI.OnnxRuntimeGenAI** (the `IChatClient` adapter over **ONNX Runtime GenAI**).
2. Pick a hardware execution provider (EP) at runtime: **CPU**, **CUDA**, **DirectML**, or **Vulkan**. Auto-detect with an explicit override (`--accelerator <ep>`).
3. **Fetch-on-first-use** model loading from Hugging Face, with the on-disk layout reusing the standard HF Hub cache so a model already downloaded by another tool is not re-downloaded.
4. Surface, but do not duplicate, models the user already has in their **Ollama** cache (different file format, but the same logical model — at minimum, point the user at the ONNX equivalent).
5. Optional, opt-in integration with **Microsoft Agent Framework** (`Microsoft.Agents.AI`) so the same `IChatClient` can be promoted to a tool-calling agent inside a pipeline step.

This is one of the largest single features on the roadmap — see [§8 Effort](#8-effort) for a phased delivery plan.

---

## 2. Why ONNX Runtime GenAI (and not something else)

Three viable options were considered:

| Option | Verdict | Reason |
|---|---|---|
| **Microsoft.Extensions.AI.OnnxRuntimeGenAI** | ✅ Chosen | First-party Microsoft package, implements the same `IChatClient` interface used by every other Microsoft.Extensions.AI client. Already supports CPU / CUDA / DirectML EPs. Models distributed as standard HF repos (`microsoft/Phi-4-mini-instruct-onnx`, `microsoft/Phi-3.5-mini-instruct-onnx`, etc.). |
| LlamaSharp (llama.cpp bindings) | Rejected for primary path | Different model format (GGUF), separate ecosystem, but **does** ship a Vulkan backend today. Reserved as a fallback for cross-vendor GPU on Linux/Mac. |
| TorchSharp + native PyTorch | Rejected | Huge native dependency footprint; defeats the global-tool distribution model. |

`Microsoft.Extensions.AI.OnnxRuntimeGenAI` exposes a `ChatClient` that we wrap inside a new `OnnxProvider : ILlmProvider`. The existing `ILlmProvider.CompleteAsync(messages, options, ct)` contract maps 1:1 to `IChatClient.GetResponseAsync(messages, options, ct)`, so this provider plugs into `LlmStep` with no changes to `DistSharp.Core`.

---

## 3. Hardware execution providers

ONNX Runtime ships a separate EP per accelerator. The CLR-side selection is a single string ("CPU", "CUDA", "DML", "Vulkan") passed when constructing the `OgaModel`. Packaging matters: each EP is a *different* NuGet metapackage that brings in the matching native binaries.

| EP | NuGet package | Hardware | Notes |
|---|---|---|---|
| **CPU** | `Microsoft.ML.OnnxRuntimeGenAI` | Any x64 / ARM64 | Baseline. Always available. Use `cpu-int4-rtn-block-32-acc-level-4` model variants for sensible RAM. |
| **CUDA** | `Microsoft.ML.OnnxRuntimeGenAI.Cuda` | NVIDIA GPUs (compute ≥ 7.0) | Requires CUDA 12 + cuDNN 9 runtime DLLs. Fastest on supported hardware. |
| **DirectML** | `Microsoft.ML.OnnxRuntimeGenAI.DirectML` | Any DX12 GPU on Windows (NVIDIA / AMD / Intel / Arc) | Best portable Windows GPU story. No vendor-specific runtime required. |
| **Vulkan** | `Microsoft.ML.OnnxRuntime.Vulkan` (preview, ORT ≥ 1.20) | Any Vulkan 1.3 GPU on Linux / Windows | Preview status as of writing — feature-gate behind `DISTSHARP_ONNX_VULKAN=1` until ORT marks it GA. Fallback to llama.cpp Vulkan via LlamaSharp if blocked. |

### Selection logic (`--accelerator`)

```
auto  → probe in order: cuda → directml → vulkan → cpu  (first one that loads wins)
cpu   → force CPU
cuda  → require CUDA EP, fail with actionable message if unavailable
dml   → require DirectML, fail with actionable message if unavailable
vulkan→ require Vulkan EP, gated behind env-var until GA
```

Probing means "try to construct `OgaModel` with that EP and a tiny tokenizer-only smoke load; if it throws `OnnxRuntimeGenAIException`, try the next one." Done once at startup and cached for the process lifetime.

### Packaging trade-off

Bundling all EPs makes the global tool ~800 MB (CUDA alone is ~600 MB native). Two options:

1. **One package, all EPs.** Simple install, large download. Rejected — incompatible with `dnx` zero-install ergonomic.
2. **Per-EP NuGet variants.** Ship `DistSharp` (CPU only by default) and `DistSharp.Onnx.Cuda` / `DistSharp.Onnx.DirectML` as opt-in extension packages that override the EP-specific transitive deps. Recommended.
3. **Native-asset side-load.** Detect EP at first run and download just the matching `onnxruntime-genai-<ep>.dll` set into `%LOCALAPPDATA%\DistSharp\runtimes\`. Most ergonomic but requires custom native loader work — defer.

Going with option (2) for v1. Option (3) is a Phase 5 improvement.

---

## 4. Model resolution & caching

### Naming convention

Users pass an HF repo ID:

```
--model microsoft/Phi-4-mini-instruct-onnx
--model microsoft/Phi-3.5-mini-instruct-onnx
--model Qwen/Qwen2.5-Coder-7B-Instruct-ONNX
```

These repos contain *multiple* variants in subdirectories — e.g. `cpu-int4-rtn-block-32-acc-level-4/`, `directml-int4-awq-block-128/`, `cuda-int4-rtn-block-32/`. The variant is picked based on the selected EP:

| EP | Variant prefix (priority order) |
|---|---|
| CPU | `cpu-int4-*`, then `cpu-fp16-*`, then `cpu-fp32-*` |
| CUDA | `cuda-int4-*`, then `cuda-fp16-*` |
| DirectML | `directml-int4-*`, then `directml-fp16-*` |
| Vulkan | `vulkan-int4-*` (falls back to CPU if not published) |

A `--model-variant <subdir>` escape hatch lets the user pin an exact directory.

### Cache layout

ONNX Runtime GenAI loads from a directory containing `genai_config.json` + `model.onnx` + `model.onnx.data` + `tokenizer.json`. Standard HF Hub cache layout:

```
%HF_HOME%\hub\                                        (Windows: %USERPROFILE%\.cache\huggingface\hub)
  models--microsoft--Phi-4-mini-instruct-onnx\
    snapshots\
      <commit-sha>\
        cpu-int4-rtn-block-32-acc-level-4\
          genai_config.json
          model.onnx
          model.onnx.data
          tokenizer.json
        directml-int4-awq-block-128\
          ...
```

DistSharp uses the **same** layout so that a model already pulled by `huggingface-cli download` (or by any tool using the Python `huggingface_hub` library) is reused without re-downloading. Concretely:

1. Resolve `HF_HOME` env var, else `%USERPROFILE%\.cache\huggingface` (Windows) / `~/.cache/huggingface` (Unix).
2. Compute the directory `<HF_HOME>/hub/models--<repo-owner>--<repo-name>/snapshots/<sha>`.
3. If absent, call the HF Hub HTTP API (`/api/models/{repo}/tree/main`, then per-file `/{repo}/resolve/{sha}/{path}`) to download only the files in the chosen variant subdirectory.
4. Hand the variant subdirectory path to `OgaModel`.

### Ollama cache awareness

Ollama stores models as GGUF blobs at `~/.ollama/models/blobs/` with manifests under `~/.ollama/models/manifests/registry.ollama.ai/`. **GGUF and ONNX are not interchangeable** — we cannot reuse the bytes. But we can:

- Read the manifest directory at startup and build `{ollama-name → underlying HF model family}` map.
- When the user asks for `--model llama3.1:8b` against the `onnx` provider, return a friendly hint: *"You have `llama3.1:8b` available via Ollama (GGUF). For ONNX, try `--model unsloth/Llama-3.1-8B-Instruct-ONNX` or `--provider ollama --model llama3.1:8b`."*

That's the entirety of the "deduplication" story for Ollama — surfacing, not sharing.

### Disk-space accounting

`distsharp models --provider onnx --installed` lists already-cached ONNX repos with their disk usage. `distsharp models --provider onnx --gc --keep <repo>,<repo>` deletes everything else. (New flags; both fit the existing `models` command structure.)

---

## 5. Agent Framework (optional, opt-in)

**Microsoft.Agents.AI** wraps an `IChatClient` into a `ChatAgent` with tool-calling, memory, and multi-step planning. Because our `OnnxProvider` will already be exposing an `IChatClient` internally, adding agent support is a thin façade — not a re-implementation.

Concretely, a future `agent_step` pipeline step type (mirroring `LlmStep`) would:

1. Resolve `IChatClient` from the configured provider (same factory).
2. Construct `new ChatAgent(client, agentOptions)` with declarative tools loaded from the YAML config (e.g. `read_symbol`, `run_xunit_test`).
3. Run the agent's `RunAsync(prompt)` for each row, capturing the final response as the dataset `response`.

This is **not** in scope for the initial ONNX provider work — it's a separate `agent_step` design that the ONNX provider unlocks. Filed under Phase 4 below for sequencing.

---

## 6. Public surface (CLI / DI)

### New `--provider onnx`

```bash
# CPU baseline — works on any machine with .NET 10
dnx DistSharp generate ./MyApp.sln \
  --provider onnx \
  --model microsoft/Phi-4-mini-instruct-onnx

# Force a specific EP
dnx DistSharp generate ./MyApp.sln \
  --provider onnx \
  --model microsoft/Phi-4-mini-instruct-onnx \
  --accelerator cuda

# Pin a variant subdirectory
dnx DistSharp generate ./MyApp.sln \
  --provider onnx \
  --model microsoft/Phi-4-mini-instruct-onnx \
  --model-variant cpu-int4-rtn-block-32-acc-level-4
```

### New options class

```csharp
public sealed class OnnxProviderOptions : LlmProviderOptions
{
    public string? Accelerator { get; set; }          // null = auto
    public string? ModelVariant { get; set; }         // null = auto-pick by EP
    public string? CacheRoot { get; set; }            // null = HF_HOME default
    public int MaxConcurrentSessions { get; set; } = 1;  // ONNX sessions are not cheap; usually 1
}
```

### Env vars (mirroring the convention used by other providers)

| Var | Meaning |
|---|---|
| `ONNX_MODEL` | Default model repo ID. |
| `ONNX_ACCELERATOR` | `auto` / `cpu` / `cuda` / `dml` / `vulkan`. |
| `HF_HOME` | Standard HF cache root override. Honoured. |
| `HF_TOKEN` | Used for gated repo downloads. Same var the `export --hf-repo` path already reads. |
| `DISTSHARP_ONNX_VULKAN` | `1` to opt into the Vulkan EP while it is in preview. |

### Models discovery

`distsharp models --provider onnx` returns the cached set (no remote enumeration — the HF Hub model list for ONNX-format models is too large and noisy to display). `--installed` is the default mode for this provider. A `--search <term>` mode would proxy to HF's `/api/models?filter=onnx&search=<term>` and is reasonable as a Phase 2 add.

---

## 7. Components & file layout

```
src/DistSharp.Providers/
  Onnx/
    OnnxProviderOptions.cs           # options class
    OnnxProvider.cs                  # ILlmProvider wrapping IChatClient
    OnnxAcceleratorSelector.cs       # probe order: cuda → dml → vulkan → cpu
    HuggingFaceModelCache.cs         # HF cache layout + variant resolution + first-use fetch
    OllamaCacheAwareness.cs          # read ~/.ollama/manifests/ for cross-suggestions
```

`DistSharp.Providers.Onnx.Cuda.csproj` / `.DirectML.csproj` / `.Vulkan.csproj` would be sibling projects each adding the matching transitive native dependency, packed as separate NuGet packages.

The CLI gets a new `models --installed` / `--gc` flag set, and `generate` / `pipeline run` learn `--accelerator` and `--model-variant` options.

---

## 8. Effort

Phased so each phase is shippable in isolation:

| Phase | Scope | Estimate | Dependencies |
|---|---|---|---|
| **1: CPU baseline** | OnnxProvider + Microsoft.Extensions.AI.OnnxRuntimeGenAI on CPU + HF cache fetch | 3 days | none |
| **2: DirectML** | DirectML EP variant package + auto-detect on Windows | 1–2 days | Phase 1 |
| **3: CUDA** | CUDA EP variant package + auto-detect + CUDA-12 runtime requirement docs | 2 days | Phase 1 |
| **4: Vulkan** | Vulkan EP integration *if* ORT marks it GA; otherwise LlamaSharp fallback investigation | 3–5 days | Phase 1; ORT release gate |
| **5: Ollama awareness** | Read Ollama manifests + cross-provider hints + GC command | 1 day | Phase 1 |
| **6: Agent Framework opt-in** | `agent_step` pipeline step type backed by `Microsoft.Agents.AI` | 5 days | Phase 1; separate spec for `agent_step` |

**Total to "CPU + DirectML + CUDA + HF cache reuse"** (the realistic v1 target on a Windows dev box): ~7 working days.

Vulkan is genuinely the long pole — ORT's Vulkan EP was added as preview in 1.20 and its maturity / coverage of GenAI ops will likely dictate whether Phase 4 ships now or falls back to the LlamaSharp escape hatch.

---

## 9. Risks & open questions

1. **EP probing failure modes.** Constructing `OgaModel` against a missing CUDA runtime can throw `DllNotFoundException` rather than a typed exception. Probe needs to catch broadly and log at debug.
2. **Native binary size.** CUDA EP brings ~600 MB of native binaries. The `DistSharp.Onnx.Cuda` package is large enough that `dnx` first-run UX needs a "this will take a while" message.
3. **Variant directory schema drift.** Microsoft's published model variants are not stable names — `Phi-3.5-mini-instruct-onnx` uses different subdirectory names than `Phi-4-mini-instruct-onnx`. The variant-prefix priority list in §4 is a heuristic and may need a per-repo override table.
4. **HF_TOKEN scope.** Gated models (e.g. Llama 3.x) require an HF token with the right repo access. Reuse `HF_TOKEN` from the existing `export --hf-repo` path; surface a clean error on 401.
5. **Concurrency model.** `OgaModel` is not thread-safe for inference. `LlmStep` already runs N parallel workers — for ONNX, force `workers = 1` unless `MaxConcurrentSessions > 1` is set, and document the implication (peak VRAM scales linearly).
6. **Streaming.** `IChatClient.GetStreamingResponseAsync` works for ONNX RT GenAI, but DistSharp's `ILlmProvider` is currently non-streaming. Token-by-token streaming is out of scope; map to a single `CompleteAsync` call.

---

## 10. Out of scope (for this design)

- Multi-modal models (vision / audio) — separate design, the `ILlmProvider` contract is text-only today.
- ONNX-format embedding models for the planned embedding-based deduplication step — that is a separate provider role (`IEmbeddingProvider`) and a separate design.
- Replacing the existing HTTP-based providers with their `IChatClient` equivalents from `Microsoft.Extensions.AI.*` — a sensible refactor, but unrelated to the local-ONNX delivery and dramatically widens scope.

---

## 11. Sequencing relative to the rest of the roadmap

This sits behind the dataset-sync work (see [`2026-05-18-dataset-sync-design.md`](2026-05-18-dataset-sync-design.md)) on the immediate roadmap because sync unblocks the "keep datasets current" workflow even for users on hosted providers, whereas the ONNX provider only matters once you want local inference. Recommended order:

1. Dataset sync — broad user value, smaller code change.
2. ONNX Phase 1 (CPU baseline + HF cache reuse) — the smallest useful local-inference slice.
3. ONNX Phase 2 + 3 (DirectML, CUDA) in parallel — both are EP-only changes.
4. Vulkan (gated on ORT GA) and Agent Framework (gated on a separate `agent_step` design).
