# DistSharp — ONNX EP Expansion Design (Phases 2–5)

**Date:** 2026-05-18
**Status:** Design / planning. Not yet implemented.
**Scope:** Phases 2 (DirectML), 3 (CUDA), 4 (Vulkan investigation), and 5 (Ollama cache awareness) of the ONNX provider. Phase 1 (CPU baseline + HF Hub cache reuse) has already landed.
**Parent spec:** [`2026-05-18-onnx-provider-design.md`](2026-05-18-onnx-provider-design.md) — read §3, §4, §8, §9 for context that this document extends but does not repeat.

---

## 1. Goals

Success for each phase is defined as follows.

### Phase 2 — DirectML

- `--accelerator dml` (or `auto`) runs inference on any DX12-capable GPU on Windows using the `Microsoft.ML.OnnxRuntimeGenAI.DirectML` package.
- Auto-detection probes DirectML before falling back to CPU; result is cached for the process lifetime.
- The HF variant selection logic picks `directml-int4-*` (or `directml-fp16-*`) directories for the DML EP.
- A separate opt-in NuGet package `DistSharp.Onnx.DirectML` carries the native dependency; the base `DistSharp` tool remains CPU-only.
- Clear error message when DML probe fails (e.g., running on Linux, or DX12 not available).

### Phase 3 — CUDA

- `--accelerator cuda` (or `auto`) runs inference on any NVIDIA GPU with CUDA compute capability ≥ 7.0 via `Microsoft.ML.OnnxRuntimeGenAI.Cuda`.
- Runtime requirement for CUDA 12.x and cuDNN 9 is surfaced in the error message when the probe fails (not a silent crash).
- `DistSharp.Onnx.Cuda` is a separate NuGet/tool extension — not bundled into the base tool — because the `.Cuda` nupkg alone is 105.8 MB.
- HF variant selection picks `cuda-int4-*` or `cuda-fp16-*` directories.
- A "this will take a while" progress message is shown on first `dotnet tool install` / first run.
- `DISTSHARP_ONNX_DISABLE_CUDA=1` escape hatch for machines where CUDA libraries are present but broken.

### Phase 4 — Vulkan investigation

- **Vulkan is not a shipping EP for ONNX Runtime GenAI** as of ORT GenAI 0.13.2 (confirmed May 2026 — Vulkan is absent from the ORT GenAI supported-EP list; it appears only in the base `Microsoft.ML.OnnxRuntime` Vulkan preview package which is not consumed by the GenAI layer).
- Phase 4 goal is therefore an **investigation + decision gate**, not a direct implementation:
  - Track `microsoft/onnxruntime-genai` for a Vulkan EP announcement.
  - If a `Microsoft.ML.OnnxRuntimeGenAI.Vulkan` package ships, Phase 4 follows the same pattern as Phases 2–3.
  - If ORT GenAI Vulkan remains blocked, evaluate **LlamaSharp** as an alternative backend for cross-vendor GPU (AMD on Linux, AMD/Intel on macOS) — see §5.4 for the comparison.
- `DISTSHARP_ONNX_VULKAN=1` env-var gate is already specified in the parent doc; keep it, but wire it to a "not yet available" diagnostic rather than a probe until the package exists.
- Success = a written decision record (ADR) appended to this spec, either: (a) "ORT GenAI Vulkan EP landed, Phase 4 follows Phase 2/3 pattern" or (b) "LlamaSharp Vulkan selected, here is the separate implementation design."

### Phase 5 — Ollama cache awareness

- `distsharp models --provider onnx` output includes a "You already have this model in Ollama" hint when the user has a corresponding GGUF model in `~/.ollama/`.
- When the user specifies an Ollama model name (e.g., `--model llama3.1:8b`) against `--provider onnx`, a friendly cross-provider suggestion is returned immediately rather than attempting an HF download.
- `distsharp models --provider onnx --installed` lists both cached ONNX repos (with disk usage) and any detected Ollama models that have known ONNX equivalents.
- `distsharp models --provider onnx --gc --keep <repo,...>` deletes ONNX cache entries not in the keep list (Ollama blobs are never touched — read-only).
- No writing to `~/.ollama/` — Ollama cache awareness is **read-only**.

---

## 2. Approach — packaging strategy

### Decision: per-EP NuGet extension packages (option 2 from parent §3)

The parent spec already chose option 2. This section makes it implementation-ready.

**Why not monolithic (option 1):** The `.Cuda` nupkg is 105.8 MB and the base CPU package is 92.07 MB. Bundling all EPs into a single `dotnet tool install DistSharp` download would approach 300+ MB before model weights. This is incompatible with zero-friction `dnx` first-run UX and penalises users who only need CPU.

**Why not side-load (option 3):** Custom native loader work (P/Invoke shims, RID-graph bypass) is significant and fragile. The .NET NuGet tool restore pipeline handles native asset layout correctly; we should use it, not bypass it.

**Chosen layout:**

| Package | What it carries | Who installs it |
|---|---|---|
| `DistSharp` (base tool) | `Microsoft.ML.OnnxRuntimeGenAI` (CPU, 92 MB) | Default — always installed |
| `DistSharp.Onnx.DirectML` | `Microsoft.ML.OnnxRuntimeGenAI.DirectML` (4.5 MB) | Windows GPU users |
| `DistSharp.Onnx.Cuda` | `Microsoft.ML.OnnxRuntimeGenAI.Cuda` (+`Microsoft.ML.OnnxRuntime.Gpu`, ~106 MB) | NVIDIA GPU users |
| `DistSharp.Onnx.Vulkan` | TBD — pending ORT GenAI Vulkan EP availability | Future |

Each extension package is a lightweight `.csproj` that adds only the matching NuGet dependency and wires the EP name into the `OnnxAcceleratorSelector` via a presence-probe assembly attribute. The base tool does not reference these projects — it discovers them at runtime by reflection-probing for a known type (see §3).

**`dotnet tool install` vs `dnx`:**

- `dotnet tool install -g DistSharp` installs the base CPU-only package. To add DirectML: `dotnet tool install -g DistSharp.Onnx.DirectML`. The extension package declares a `<ToolCommandName>` of `dnx-dml` (a noop command) so it installs cleanly; its only real purpose is to place native binaries on the tool's NuGet probe path.
  - **(OPEN QUESTION 1 — BLOCKING)** Whether a `dotnet tool` extension package that adds native assets to another tool's runtime actually works this way — needs prototype verification before implementing. See §7.
- `dnx` (the bespoke zero-install launcher) would need a separate mechanism: either bundle the EP selection into its own manifest, or direct users to `dotnet tool install DistSharp.Onnx.Cuda` first.

---

## 3. Components & file layout

### New files to create

```
src/
  DistSharp.Providers.Onnx.DirectML/
    DistSharp.Providers.Onnx.DirectML.csproj    # adds Microsoft.ML.OnnxRuntimeGenAI.DirectML dep
    DirectMlEpRegistration.cs                   # assembly-level attribute marking DML available
  DistSharp.Providers.Onnx.Cuda/
    DistSharp.Providers.Onnx.Cuda.csproj        # adds Microsoft.ML.OnnxRuntimeGenAI.Cuda dep
    CudaEpRegistration.cs                       # assembly-level attribute marking CUDA available
  DistSharp.Providers.Onnx.Vulkan/              # placeholder; empty until ORT GenAI Vulkan ships
    DistSharp.Providers.Onnx.Vulkan.csproj
    VulkanEpRegistration.cs
src/DistSharp.Providers/
  Onnx/
    EpProbeCache.cs                             # NEW: persist & invalidate EP probe results
    OllamaCacheAwareness.cs                     # NEW: read ~/.ollama manifests, build name→family map
```

### Existing files to modify

| File | Change |
|---|---|
| `Onnx/OnnxAcceleratorSelector.cs` | Replace stub with full probe logic: load EP assemblies by reflection, call `OgaModel` smoke-test per EP, cache result. |
| `Onnx/OnnxProviderOptions.cs` | Remove `NotSupportedException` for non-CPU accelerators; add `DisableCuda` bool property. |
| `Onnx/OnnxProvider.cs` | Pass selected EP name to `OnnxRuntimeGenAIChatClient` constructor (if/when API allows it — see §7 Q2); integrate `OllamaCacheAwareness` for cross-suggestion on `CompleteAsync` and `ListModelsAsync`. |
| `Onnx/HuggingFaceModelCache.cs` | Expand `VariantPriority`, `IsInTargetVariant`, and `SelectVariant` to be EP-keyed (see §3.1 below). Also honour `HUGGINGFACE_HUB_CACHE` env var (existing TODO comment). |
| CLI `models` command | Add `--installed` (default for onnx), `--gc`, `--keep` flags. |
| CLI `generate` / `pipeline run` | Bind `--accelerator` to `OnnxProviderOptions.Accelerator` (flag already accepted in Phase 1 for `pipeline run`; confirm `generate` also). |

### 3.1 EP-keyed variant tables in `HuggingFaceModelCache`

The current `VariantPriority` array is a single flat list (`cpu-int4-rtn-block-32-acc-level-4`). Replace with a dictionary keyed by EP name:

```csharp
private static readonly IReadOnlyDictionary<string, string[]> VariantPriorityByEp =
    new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["cpu"]    = ["cpu-int4-rtn-block-32-acc-level-4", "cpu-int4-*", "cpu-fp16-*", "cpu-fp32-*"],
        ["cuda"]   = ["cuda-int4-rtn-block-32", "cuda-int4-*", "cuda-fp16-*"],
        ["dml"]    = ["directml-int4-awq-block-128", "directml-int4-*", "directml-fp16-*"],
        ["vulkan"] = ["vulkan-int4-*"],  // fallback to cpu if absent
    };
```

`SelectVariant` and `IsInTargetVariant` accept the active EP name and select from the matching priority list. `IsInTargetVariant` (used during download) filters to the appropriate `<ep>-*` subdirs only, avoiding downloading CUDA variants when the user is on CPU.

### 3.2 `EpProbeCache`

Stores the probe result at `%LOCALAPPDATA%\DistSharp\ep-probe-cache.json` (Windows) / `~/.local/share/DistSharp/ep-probe-cache.json` (Linux/macOS).

```json
{
  "probedAt": "2026-05-18T10:00:00Z",
  "ortGenAiVersion": "0.13.2",
  "fingerprint": "gpu=NVIDIA GeForce RTX 4090;driver=560.81",
  "selected": "cuda",
  "available": ["cuda", "cpu"]
}
```

Cache is invalidated and re-probed when any of the following change:
- `ortGenAiVersion` (the `FileVersionInfo` of `onnxruntime-genai.dll` in the tool's directory)
- `fingerprint` — built by querying `IDirect3DDevice` (Windows, via `OrtGetDevices` p/invoke if available) or reading `/sys/class/drm/card*/device/device` on Linux; fall back to `"unknown"` if query fails
- File age > 7 days (rolling re-probe regardless of fingerprint)

**(OPEN QUESTION 2)** The fingerprint strategy for Linux GPU detection without a DX12 layer. Options: parse `nvidia-smi -q` output, or accept `"unknown"` and rely only on version + age expiry.

### 3.3 `OllamaCacheAwareness`

Read-only. No writes to `~/.ollama/`.

```csharp
public sealed class OllamaCacheAwareness
{
    /// <summary>Reads Ollama manifests and returns a map of Ollama model name to detected HF family.</summary>
    public IReadOnlyDictionary<string, OllamaModelInfo> BuildModelMap();

    /// <summary>Returns a cross-provider suggestion string if the given repo/model name looks like an Ollama model.</summary>
    public string? TryCrossSuggest(string modelNameOrRepoId);
}
```

The Ollama manifest directory is `~/.ollama/models/manifests/registry.ollama.ai/`. Each subdirectory is a library name (`library/`, `hf.co/`, etc.); within each are per-tag JSON manifests. `OllamaCacheAwareness` reads these manifests to extract the model family name (from the `config.mediaType` or `labels` fields) and maps them to known ONNX HF repos via a small embedded lookup table (e.g., `"llama3.1"` → `"unsloth/Llama-3.1-8B-Instruct-ONNX"`).

---

## 4. CLI / API surface

### `--accelerator` accepted values

| Value | Meaning |
|---|---|
| `auto` (default) | Probe in order: cuda → dml → vulkan → cpu. First successful probe wins. |
| `cpu` | Force CPU EP. |
| `cuda` | Require CUDA EP; fail with actionable message if unavailable. |
| `dml` | Require DirectML EP; fail with actionable message if unavailable. |
| `vulkan` | Require Vulkan EP; currently returns "Vulkan EP not yet available in ORT GenAI — set `--accelerator dml` on Windows or `--provider ollama` for cross-vendor GPU." Gated behind `DISTSHARP_ONNX_VULKAN=1` until the ORT GenAI Vulkan package ships. |

### Environment variables

| Variable | Meaning | Phase |
|---|---|---|
| `ONNX_ACCELERATOR` | `auto` / `cpu` / `cuda` / `dml` / `vulkan` — mirrors `--accelerator`. | 2 |
| `DISTSHARP_ONNX_DISABLE_CUDA` | `1` to skip CUDA probe even if the `.Cuda` extension is installed. | 3 |
| `DISTSHARP_ONNX_VULKAN` | `1` to opt into Vulkan EP probe (has no effect until the package ships). | 4 |
| `HF_HOME` | HF cache root override. Honoured (Phase 1). | 1 |
| `HUGGINGFACE_HUB_CACHE` | Alternative HF cache root (same semantic as Python `huggingface_hub`). Phase 1 TODO, deliver in Phase 2. | 2 |
| `HF_TOKEN` | Bearer token for gated repos. Honoured (Phase 1). | 1 |
| `ONNX_MODEL` | Default model repo ID. | 1 |

### `models` command flags

```
distsharp models --provider onnx [--installed] [--search <term>]
                                  [--gc [--keep <repo>[,<repo>...]]]
```

| Flag | Behaviour |
|---|---|
| `--installed` (default for `onnx`) | Lists cached HF repos with disk usage and EP-tagged variant subdirectories present. Also lists Ollama models with known ONNX equivalents. |
| `--search <term>` | Proxies to HF `/api/models?filter=onnx&search=<term>` and shows top results with sizes. |
| `--gc` | Dry run by default: prints what would be deleted. Add `--yes` to execute. |
| `--keep <repo>[,...]` | Comma-separated list of HF repo IDs to retain; all others are candidates for deletion. |

Example output of `--installed`:

```
Cached ONNX models (HF Hub):
  microsoft/Phi-4-mini-instruct-onnx   [cuda-int4, cpu-int4]   12.4 GB
  microsoft/Phi-3.5-mini-instruct-onnx [cpu-int4]               3.8 GB

Ollama models with ONNX equivalents:
  llama3.1:8b  (Ollama/GGUF, 4.7 GB) → try: --model unsloth/Llama-3.1-8B-Instruct-ONNX
  mistral:7b   (Ollama/GGUF, 4.1 GB) → try: --model onnx-community/Mistral-7B-Instruct-v0.3-ONNX
```

### Ollama cross-suggestion UX

When `CompleteAsync` is called with a model name that matches a known Ollama model name pattern (e.g., `llama3.1:8b`, `mistral:7b`) against `--provider onnx`, the provider returns:

```
Error: 'llama3.1:8b' is not a valid Hugging Face repo ID.
You have llama3.1:8b in your Ollama cache (GGUF format, not compatible with the onnx provider).

Options:
  Use Ollama:   --provider ollama --model llama3.1:8b
  Use ONNX:     --provider onnx --model unsloth/Llama-3.1-8B-Instruct-ONNX
```

This is surfaced as a typed `OllamaCrossProviderException` (or equivalent) so callers can distinguish it from a generic `ArgumentException`.

---

## 5. Per-EP detail

### 5.1 DirectML (Phase 2)

| Attribute | Value |
|---|---|
| NuGet package | `Microsoft.ML.OnnxRuntimeGenAI.DirectML` 0.13.2 |
| nupkg size | 4.54 MB |
| Supported hardware | Any DX12-capable GPU on Windows: NVIDIA GeForce/Quadro, AMD Radeon (RDNA1+), Intel Iris Xe / Arc, Qualcomm Adreno 7xx |
| OS requirement | Windows 10 1903+ (WDDM 2.6) for DX12 feature level 12_0 |
| Version pin | `0.13.2` (align with base `Microsoft.ML.OnnxRuntimeGenAI` version) |
| Transitive deps | `Microsoft.ML.OnnxRuntime.DirectML` ≥ 1.25.1 (pulled transitively) |
| Availability detection | Probe by attempting to enumerate DX12 adapters via `IDXGIFactory4.EnumAdapters1`. Simpler fallback: attempt `OgaModel` construction with `ep="dml"` against a minimal tokenizer-only model; catch `OnnxRuntimeGenAIException`. |
| Model variants | `directml-int4-awq-block-128` (first priority), `directml-int4-*`, `directml-fp16-*` |
| Linux | Not supported (DirectML is Windows-only). Probe skipped on non-Windows platforms. |

**Probe error message (DML not available):**

```
ONNX DirectML EP is not available on this machine.
Possible causes:
  - Running on Linux or macOS (DirectML is Windows-only)
  - No DX12-capable GPU detected
  - Running in a VM without GPU passthrough
Fallback: --accelerator cpu
```

### 5.2 CUDA (Phase 3)

| Attribute | Value |
|---|---|
| NuGet package | `Microsoft.ML.OnnxRuntimeGenAI.Cuda` 0.13.2 |
| nupkg size | 105.8 MB |
| Transitive dep | `Microsoft.ML.OnnxRuntime.Gpu` ≥ 1.25.1 (CUDA 12.x build) |
| Supported hardware | NVIDIA GPUs with CUDA compute capability ≥ 7.0 (Volta/Turing/Ampere/Ada/Hopper) |
| Runtime requirement | CUDA 12.x toolkit **and** cuDNN 9 must be installed on the host machine; not bundled in the NuGet package. |
| OS requirement | Windows 10+ or Linux; not macOS (NVIDIA dropped macOS CUDA support in 2019). |
| Version pin | `0.13.2` |
| Availability detection | Probe catches `DllNotFoundException` (CUDA runtime not installed), `OnnxRuntimeGenAIException` (CUDA runtime present but GPU not compatible or no GPU), and broad `Exception` (log at debug, treat as unavailable). |
| Model variants | `cuda-int4-rtn-block-32` (first priority), `cuda-int4-*`, `cuda-fp16-*` |

**(OPEN QUESTION 3 — BLOCKING)** The nupkg is 105.8 MB but does not bundle the CUDA runtime itself. Users must independently install the CUDA 12 toolkit. Should we: (a) document this as a pre-requisite and emit a clear error, or (b) provide a `distsharp cuda-check` diagnostic command that validates `nvcc --version`, `libcudnn.so`/`cudnn.dll` presence, and minimum compute capability via `nvidia-smi`? Option (b) is significantly more user-friendly but adds scope.

**CUDA 12 vs 13:** As of ORT 1.25.1 / GenAI 0.13.2, the `.Gpu` transitive dependency targets CUDA 12.x. CUDA 13 is not yet supported upstream. Pin to CUDA 12 and document.

**Licensing note:** CUDA runtime libraries (distributed via NVIDIA's CUDA Toolkit) are governed by the NVIDIA CUDA EULA, which permits redistribution of the runtime libraries in binary form under specific conditions. The NuGet package itself does not redistribute CUDA — it loads it from the host system — so no additional licensing obligation applies to DistSharp.

**Download UX:** First `dotnet tool install -g DistSharp.Onnx.Cuda` displays the following before the restore starts:

```
Note: DistSharp.Onnx.Cuda includes ~106 MB of CUDA native libraries.
      Additionally, CUDA 12 and cuDNN 9 must be installed on your machine.
      See: https://developer.nvidia.com/cuda-downloads
```

This is injected via a `<Description>` in the `.csproj` that `dotnet tool install` surfaces, and also logged to the console at `LogInformation` level on first `OnnxAcceleratorSelector.SelectAccelerator()` call when CUDA is discovered.

### 5.3 Vulkan (Phase 4 — investigation gate)

**Current status (confirmed May 2026):** `Microsoft.ML.OnnxRuntimeGenAI.Vulkan` does not exist on NuGet. The Vulkan EP in `Microsoft.ML.OnnxRuntime.Vulkan` (base ORT) is a separate code path that the GenAI layer does not currently expose. The `microsoft/onnxruntime-genai` README lists these EPs as supported: CPU, CUDA, DirectML, NvTensorRtRtx (TRT-RTX), OpenVINO, QNN, WebGPU. Vulkan is absent.

**Phase 4 decision gate:**

1. Monitor `microsoft/onnxruntime-genai` releases for a Vulkan EP announcement.
2. If `Microsoft.ML.OnnxRuntimeGenAI.Vulkan` ships with GA status: implement `DistSharp.Onnx.Vulkan` following the DirectML/CUDA pattern (§5.1, §5.2). Required hardware: Vulkan 1.3 GPU (virtually all discrete GPUs from 2016+; most integrated GPUs from 2018+).
3. If Vulkan EP remains blocked at the time Phase 4 is scheduled: proceed with **LlamaSharp evaluation** as a cross-vendor GPU fallback.

### 5.4 LlamaSharp as Vulkan fallback

LlamaSharp (`LLamaSharp` NuGet, wrapping llama.cpp) ships a Vulkan backend today. Key differences vs ORT GenAI:

| Dimension | ONNX Runtime GenAI | LlamaSharp (llama.cpp) |
|---|---|---|
| Model format | ONNX (`.onnx` + `genai_config.json`) | GGUF |
| HF cache reuse | Native (same layout) | Partial — GGUF models on HF are in a different subdir structure |
| `IChatClient` adapter | `OnnxRuntimeGenAIChatClient` (first-party MEAI) | Custom wrapper needed (or `LLamaSharp.SemanticKernel`) |
| Vulkan maturity | N/A (not available) | Production-ready in llama.cpp, exposed via `LLamaSharp.Backend.Vulkan` |
| Windows DirectML | Available (Phase 2) | Not available in llama.cpp |

If LlamaSharp is adopted for Vulkan, it becomes a second code path within `OnnxProvider` (or a new `LlamaSharpProvider`) using the same `ILlmProvider` contract. It requires a separate design doc before implementation.

**(OPEN QUESTION 4)** Should LlamaSharp Vulkan live inside `OnnxProvider` as an EP fallback (same `--provider onnx` surface) or as a new `--provider llamasharp`? The GGUF model format means the HF cache path and model resolution are entirely different, which argues for a separate provider.

### 5.5 Other EPs (NvTensorRtRtx, QNN, WinML, OpenVINO)

Phase 3 research surfaced these additional ORT GenAI EP packages:
- `Microsoft.ML.OnnxRuntimeGenAI.QNN` — Qualcomm neural engine (Snapdragon X Elite devices)
- `Microsoft.ML.OnnxRuntimeGenAI.WinML` — Windows ML integration
- `Microsoft.ML.OnnxRuntimeGenAI.Foundry` — AI Foundry variant

These are **out of scope** for Phases 2–5. Flag as future work.

---

## 6. Effort

| Phase | Scope | Estimate | Dependencies |
|---|---|---|---|
| **2: DirectML** | `DistSharp.Providers.Onnx.DirectML.csproj` + probe logic in `OnnxAcceleratorSelector` + EP-keyed variant tables in `HuggingFaceModelCache` + `EpProbeCache` + `OnnxProviderOptions.Validate()` lift + CLI `--accelerator dml` binding | 2 days | Phase 1 (landed) |
| **3: CUDA** | `DistSharp.Providers.Onnx.Cuda.csproj` + CUDA probe (DllNotFoundException handling) + `DISTSHARP_ONNX_DISABLE_CUDA` env var + download UX message + `cuda-*` variant resolution + CUDA 12 pre-req documentation | 2 days | Phase 2 (EP probe infrastructure) |
| **4: Vulkan gate** | Monitor ORT GenAI releases; write ADR; prototype LlamaSharp Vulkan if needed | 1 day (gate evaluation) + 4–6 days (if LlamaSharp path chosen) | Phase 1; ORT GenAI release gate |
| **5: Ollama awareness** | `OllamaCacheAwareness.cs` + name→HF mapping table + `models --installed` Ollama section + `OllamaCrossProviderException` + `--gc` / `--keep` CLI flags | 2 days | Phase 1 |

**Total (Phases 2 + 3 + 5, the deterministic work):** ~6 working days.
**Phase 4 if Vulkan EP lands:** +2 days (follows P2/P3 pattern).
**Phase 4 if LlamaSharp path chosen:** +6–8 days (new model-format path, separate provider or EP fork, new design doc required first).

---

## 7. Risks & open questions

1. **(BLOCKING) `dotnet tool` extension package native-asset delivery.** The packaging strategy assumes a `DistSharp.Onnx.Cuda` package can deliver native binaries to the running `DistSharp` tool's load path via a second `dotnet tool install`. This is not how `dotnet tool` is designed — global tools are isolated per package. A prototype is required before committing to this strategy. Alternatives: (a) ship separate tool commands (`dnx-cuda`, `dnx-dml`) that share the core via `ProjectReference`; (b) monolithic package with a trimming configuration that strips unused native RIDs; (c) native-asset side-load (option 3 from parent §3, deferred). **Human decision needed: which packaging model to pursue.**

2. **(BLOCKING) `OnnxRuntimeGenAIChatClient` EP selection API.** `OnnxProvider.GetOrCreateChatClientAsync` currently calls `new OnnxRuntimeGenAIChatClient(variantDir)` with no EP parameter. If EP selection is purely a function of which native DLL is loaded (i.e., no API-level EP selector), then the per-EP NuGet package strategy is the only path. If ORT GenAI exposes a constructor or options object to specify EP, the packaging story simplifies significantly. **Verify against ORT GenAI 0.13.x API before implementing `OnnxAcceleratorSelector` probe logic.**

3. **(BLOCKING) Vulkan EP absent from ORT GenAI.** As confirmed above, `Microsoft.ML.OnnxRuntimeGenAI.Vulkan` does not exist. The parent spec §3 lists it as "preview, ORT ≥ 1.20" — this refers to the base `Microsoft.ML.OnnxRuntime.Vulkan` package, not the GenAI layer. Phase 4 cannot proceed as originally described. **Human decision needed: (a) wait for ORT GenAI to add Vulkan, (b) pursue LlamaSharp Vulkan as a separate provider, or (c) deprioritise cross-vendor GPU altogether (DirectML covers Windows; CUDA covers NVIDIA; most Linux workloads can use CPU quantised models).**

4. EP probe cache fingerprint on Linux. PCI device enumeration without DX12 requires parsing `/sys/class/drm/` or running `nvidia-smi`. If neither is available (container, Raspberry Pi, etc.), the fingerprint falls back to `"unknown"` and the cache expires every 7 days. Acceptable for v1; a platform-specific `IEpFingerprintProvider` interface would allow future improvement without changing the cache schema.

5. CUDA runtime not bundled — user pre-requisite. The `.Cuda` nupkg loads `cudart` from the host system. On a machine with no CUDA install, the probe throws `DllNotFoundException`. This exception must be caught broadly in `OnnxAcceleratorSelector` and logged at debug (not surfaced to the user unless `--accelerator cuda` was explicitly requested). The parent spec §9.1 flags this; Phase 3 must implement it.

6. Variant directory schema drift. HF model publishers use inconsistent variant subdir names (e.g., `Phi-3.5-mini-instruct-onnx` uses `cpu-int4-rtn-block-32-acc-level-4` while `Qwen2.5-Coder-ONNX` uses `cpu_int4_rtn_block_32`). The EP-keyed priority tables in §3.1 use glob-style prefix matching as a heuristic. A per-repo override table in `appsettings.json` (or embedded as a `models.json` resource) would allow explicit overrides without code changes.

7. Ollama model-name-to-HF-repo mapping table maintenance. The cross-suggestion table in `OllamaCacheAwareness` is hand-maintained. It will drift as new models are published. Options: (a) embed a versioned JSON resource file and update it in releases; (b) fetch a community-maintained mapping from a known URL on first use; (c) limit suggestions to the top 10–20 most popular models. Option (a) is recommended for v1; option (b) could be added later.

8. `HUGGINGFACE_HUB_CACHE` env var not yet honoured. There is an existing `// TODO (Phase 2)` comment in `HuggingFaceModelCache.cs`. This must be delivered in Phase 2 alongside the EP expansion — it is a correctness issue for users who set this variable via the Python `huggingface_hub` library.

---

## 8. Sequencing relative to in-flight specs

At the time of writing, the following spec files exist under `docs/superpowers/specs/`:

| Spec | Status | Interaction |
|---|---|---|
| `2026-05-15-architecture-design.md` | Landed | No conflict. Defines `ILlmProvider` contract that all phases here conform to. |
| `2026-05-15-phase3-roslyn-design.md` | Landed | No conflict. Independent. |
| `2026-05-15-phase4-providers-design.md` | Landed | This doc extends it. Providers project structure already established. |
| `2026-05-15-phase5-steps-design.md` | Landed | No conflict. |
| `2026-05-16-phase6-cli-design.md` | Landed | Phase 2 adds `--accelerator` and `HUGGINGFACE_HUB_CACHE` to the CLI surface; must align with CLI conventions defined here. |
| `2026-05-16-phase7-export-design.md` | Landed | No conflict. |
| `2026-05-18-dataset-sync-design.md` | In flight | No conflict; sync work is independent of local inference EPs. |
| `2026-05-18-onnx-provider-design.md` | Phase 1 landed | **This doc is the direct continuation**. §3, §4, §8, §9 of that spec inform every section here. |

**Recommended delivery order:**

1. Phase 2 (DirectML) immediately — EP probe infrastructure and EP-keyed variant tables benefit all subsequent phases. 2-day effort.
2. Phase 5 (Ollama awareness) in parallel with or immediately after Phase 2 — independent of GPU EPs, small scope, high UX value for existing Ollama users discovering DistSharp. 2-day effort.
3. Phase 3 (CUDA) after Phase 2 — reuses all probe infrastructure; adds only the `.Cuda` project and CUDA-specific error handling. 2-day effort.
4. Phase 4 (Vulkan gate) as an async track — the evaluation can proceed while Phase 3 ships. The gate adds no code until a decision is made.

### Probe flow (normative sequence diagram)

```
OnnxProvider.CompleteAsync
  └─ GetOrCreateChatClientAsync
       └─ OnnxAcceleratorSelector.SelectAccelerator(options.Accelerator)
            ├─ options.Accelerator == "cpu" → return "cpu"  (no probe)
            ├─ options.Accelerator == "cuda" → probe CUDA; throw if unavailable
            ├─ options.Accelerator == "dml" → probe DML; throw if unavailable
            └─ options.Accelerator == null/"auto":
                 1. EpProbeCache.TryLoad() → if valid cache hit, return cached EP
                 2. Probe CUDA (if .Cuda assembly present and DISTSHARP_ONNX_DISABLE_CUDA not set)
                 3. Probe DML  (if .DirectML assembly present and platform is Windows)
                 4. Probe Vulkan (if .Vulkan assembly present and DISTSHARP_ONNX_VULKAN=1)
                 5. Return "cpu" (always available)
                 6. EpProbeCache.Save(selected, available, fingerprint)
```

Each probe is implemented as a `Try<ep>()` method on `OnnxAcceleratorSelector`:
- Returns `true` and sets the EP if the ORT GenAI smoke-test succeeds.
- Logs the exception at `LogDebug` level and returns `false` on any failure.
- The smoke-test is a minimal `OgaModel` construction (tokenizer-only, no weights loaded) to avoid loading multi-GB model files during probe.

**(OPEN QUESTION 5)** Whether ORT GenAI supports constructing an `OgaModel` from a tokenizer-only directory (no `model.onnx`) for probe purposes, or whether the probe must use a small real model (e.g., a 50 MB test model shipped as a resource). A tokenizer-only probe is strongly preferred to avoid the 3–12 GB model download at probe time. **Verify against ORT GenAI 0.13.x before implementing probe.**

---

## 9. Out of scope

- **Phase 6 / Agent Framework** (`Microsoft.Agents.AI` integration, `agent_step` pipeline step) — separate design, different issue. The parent spec §5 describes this; it is not addressed here.
- **NvTensorRtRtx, QNN, WinML, OpenVINO EPs** — see §5.5. Candidate for a future "Phases 6–9" spec.
- **Streaming inference** — `IChatClient.GetStreamingResponseAsync` is available in ORT GenAI but DistSharp's `ILlmProvider` is non-streaming. Out of scope for all phases here; would require an `ILlmProvider` contract change.
- **Multi-modal ONNX models** — separate provider role; the `ILlmProvider` contract is text-only.
- **ONNX embedding models** — separate `IEmbeddingProvider` role; separate design.
- **Writing to `~/.ollama/`** — Ollama cache is read-only in Phase 5. Write-back (e.g., caching which HF ONNX repo corresponds to each Ollama model) is deferred to a future enhancement.
- **macOS MPS / Metal EP** — not in the ORT GenAI supported EP list. macOS users fall back to CPU.
- **WSL / GPU passthrough considerations** — DirectML probe skips gracefully on non-Windows; CUDA probe handles `DllNotFoundException`. No special WSL logic required.
