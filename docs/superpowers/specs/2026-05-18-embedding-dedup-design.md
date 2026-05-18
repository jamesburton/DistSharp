# DistSharp — Embedding-Based Deduplication Design

**Date:** 2026-05-18
**Status:** Design / planning. Not yet implemented.
**Scope:** New `EmbeddingDeduplicator` pipeline step plus `IEmbeddingProvider` abstraction, embedding model choices, similarity index strategy, caching, CLI/YAML surface, and phased delivery plan.

---

## 1. Goals — what cases MinHash misses

`MinHashDeduplicator` measures lexical (surface-form) similarity using Jaccard distance over word-level shingles. It is fast and allocation-efficient, but it will accept as distinct two rows that express the same meaning using different words.

Concrete cases that slip through MinHash at its default threshold (0.85):

| Case | Example pair | MinHash Jaccard | Semantic cosine |
|---|---|---|---|
| Synonym substitution | "Iterates over the list" / "Loops through the collection" | ~0.10 | ~0.93 |
| Passive/active reformulation | "The method is called by..." / "Callers invoke..." | ~0.15 | ~0.91 |
| Template variation | Two `/// <summary>` stubs differing only in the symbol name | ~0.30 | ~0.97 |
| Cross-language near-copies | English vs. slightly different English after LLM paraphrase | ~0.20 | ~0.95 |
| Length mismatch | A one-sentence summary vs. a three-sentence explanation of the same method | ~0.05 | ~0.89 |

In a dataset generation context — where hundreds of LLM outputs describe methods from the same class hierarchy — duplicate semantics inflate apparent dataset size, reduce training diversity, and can leak near-identical rows into eval splits.

MinHash remains appropriate for exact and near-exact textual duplicates at scale and will not be removed or deprecated. The two steps address complementary problems and should be composable.

---

## 2. Approach

### 2.1 Step shape

The step processes the pipeline's streaming `Channel<Row>` in three sub-phases:

```
Accumulate → Embed (batched) → Deduplicate (similarity index)
```

Because embedding requires a full batch to amortise API call or model-load cost, the step buffers all rows before emitting any output. This differs from `MinHashDeduplicator`, which is fully streaming. The trade-off is acceptable because:

1. Embedding is the compute-intensive part and must be batched for efficiency regardless.
2. Most downstream steps (export, write) are also not latency-sensitive.
3. A memory ceiling option (`MaxBufferRows`) can spill to disk if the row count is extreme.

Sub-phase detail:

**Accumulate.** Read every row from the input channel into an in-memory `List<Row>`. Cap at `MaxBufferRows` (default: unlimited); once exceeded, spill to a temp file using the existing `ICheckpointStore` infrastructure.

**Embed.** For each row, extract the target field (same `Field` option pattern as `MinHashOptions`). Call `IEmbeddingProvider.EmbedBatchAsync(texts, cancellationToken)`. Batch size is controlled by `BatchSize` (default: 512 for cloud; 64 for local). Cache vectors by `(body_sha256, model_id, model_version)` key (see §2.4 and §4 for cache location).

**Deduplicate.** For each embedding (in input order), query the similarity index for the nearest neighbour. If the cosine similarity of the nearest stored vector exceeds `Threshold`, the row is a duplicate and is dropped. Otherwise, insert the embedding into the index and emit the row. This preserves the first-occurrence semantics that `MinHashDeduplicator` uses, making the two steps behaviourally equivalent from a user perspective.

### 2.2 Abstraction layer — `IEmbeddingProvider` vs `IEmbeddingGenerator`

The ONNX provider spec (§10, out-of-scope note) explicitly deferred to "a separate provider role (`IEmbeddingProvider`)". This spec honours that design intent.

Two options were considered:

| Option | Pro | Con |
|---|---|---|
| Own `IEmbeddingProvider` | Consistent with DistSharp's `ILlmProvider` pattern; no external type dependency in `DistSharp.Core` | Another interface to maintain; can't reuse M.E.A. adapters directly |
| `Microsoft.Extensions.AI.IEmbeddingGenerator<string, Embedding<float>>` | First-party .NET abstraction; all cloud and ONNX providers already ship M.E.A. adapters; avoids reinventing | Pulls `Microsoft.Extensions.AI` into `DistSharp.Core` as a compile dependency |

**Decision: own `IEmbeddingProvider` in `DistSharp.Core.Abstractions`, wrapping `IEmbeddingGenerator<string, Embedding<float>>` inside each concrete implementation.**

Rationale: `DistSharp.Core` currently has zero dependency on `Microsoft.Extensions.AI` — that package is a `DistSharp.Providers` concern. Crossing that boundary in `Core` would be a regressive coupling. Each provider-side implementation wraps the M.E.A. adapter internally, so we still gain the benefit of the standard adapter ecosystem without polluting the core contract.

Interface shape:

```csharp
// src/DistSharp.Core/Abstractions/IEmbeddingProvider.cs
public interface IEmbeddingProvider
{
    /// <summary>Gets the provider name, e.g. <c>openai</c>, <c>onnx</c>.</summary>
    string ProviderName { get; }

    /// <summary>Gets the model identifier used to embed, e.g. <c>text-embedding-3-small</c>.</summary>
    string ModelId { get; }

    /// <summary>Gets the dimensionality of the embedding vectors returned by this provider.</summary>
    int Dimensions { get; }

    /// <summary>Embeds a batch of texts and returns one vector per input, in order.</summary>
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken);
}
```

A companion `IEmbeddingProviderFactory` resolves a named provider from DI, parallel to `ILlmProviderFactory`.

---

## 3. Embedding model choices

### 3.1 Selection criteria

For a code-documentation deduplication use case the relevant axes are:

- **Semantic quality on short text** (method summaries, docstrings, one-line explanations — 50–300 tokens)
- **.NET / NuGet integration cost**
- **Inference latency per 512-text batch**
- **Monetary cost per 50 K rows** (each row ~150 tokens on average)

### 3.2 Options

#### (a) Local — ONNX via `Microsoft.ML.OnnxRuntime`

Representative models: **`BAAI/bge-small-en-v1.5`** (384-dim), **`sentence-transformers/all-MiniLM-L6-v2`** (384-dim). Both are available in ONNX format on Hugging Face and reuse the HF cache layout introduced by the ONNX provider spec.

| Attribute | Value |
|---|---|
| NuGet packages | `Microsoft.ML.OnnxRuntime` (CPU) or `Microsoft.ML.OnnxRuntime.Gpu` (CUDA) |
| Additional dep | `Microsoft.Extensions.AI.OnnxRuntimeGenAI` or direct `InferenceSession` |
| Model size on disk | BGE-small: ~130 MB; MiniLM-L6: ~90 MB |
| Accuracy (MTEB benchmark) | MiniLM-L6 ~56 MTEB; BGE-small-en ~62 MTEB |
| Cost per 50 K rows | $0 (compute cost only; negligible on CPU for 50 K × 150-token rows) |
| Latency (CPU, batch 64) | ~300 ms/batch → ~4 min total for 50 K rows on modern laptop |
| When to choose | Air-gapped environments; cost-sensitive; already shipping ONNX runtime for `LlmStep` |

Note: ONNX embedding inference does not require the `OnnxRuntimeGenAI` layer (that is GenAI-specific). Plain `Microsoft.ML.OnnxRuntime` with a manual tokenizer (`Microsoft.ML.Tokenizers`) suffices. Recommend `Microsoft.ML.Tokenizers` with the `BertTokenizer` or `BpeTokenizer` depending on the chosen model.

#### (b) Local — sentence-transformers via Python subprocess

Representative models: same as (a), but invoked via `sentence-transformers` Python library.

| Attribute | Value |
|---|---|
| NuGet packages | None (Python IPC only) |
| Additional dep | Python 3.9+; `sentence-transformers` pip install (~400 MB) |
| Accuracy | Same as ONNX equivalents |
| Cost per 50 K rows | $0 |
| Latency | Higher IPC overhead; ~20% slower than direct ONNX on CPU; GPU parity |
| When to choose | Prototyping only |

**Verdict: Rejected for production.** The Python subprocess model breaks the "single .NET global tool with no external runtime" goal. ONNX is the correct local path.

#### (c) OpenAI `text-embedding-3-small`

| Attribute | Value |
|---|---|
| NuGet packages | `Microsoft.Extensions.AI.OpenAI` (already in `DistSharp.Providers`) |
| Dimensions | 1536 native; Matryoshka truncation to 256/512/768 supported |
| Accuracy | MTEB ~62 (1536-dim); ~60 at 512-dim |
| Pricing (verify at publish) | $RATE_OPENAI_TE3S per 1 M tokens |
| Cost formula | `50000 rows × 150 avg tokens / 1000000 × $RATE_OPENAI_TE3S` |
| Illustrative cost at $0.02/1M tokens | ≈ $0.15 for 50 K rows |
| Re-run cache hit | $0 if all vectors cached (see §4) |
| When to choose | Best accuracy/cost ratio for cloud; existing OpenAI API key; no local GPU |

Dimensions should be kept at the default 1536 unless storage is a concern. 512-dim is adequate for deduplication thresholding with minimal quality loss.

#### (d) Cohere Embed v3

| Attribute | Value |
|---|---|
| NuGet packages | None official; use `HttpClient` or community `Cohere.Net` wrapper |
| Dimensions | 1024 |
| Accuracy | MTEB ~64 (English); strong on short text with `input_type=search_document` |
| Pricing (verify at publish) | $RATE_COHERE_EMBED per 1 M tokens (check `embed-english-v3.0` tier) |
| Cost formula | `50000 × 150 / 1000000 × $RATE_COHERE_EMBED` |
| Integration cost | Medium — no official M.E.A. adapter; custom `IEmbeddingProvider` wrapper needed |
| When to choose | Slightly higher quality ceiling than `text-embedding-3-small` for general text; enterprise Cohere contracts |

#### (e) Voyage AI (`voyage-code-2` / `voyage-3`)

| Attribute | Value |
|---|---|
| NuGet packages | None official; use `HttpClient` |
| Dimensions | 1024 (voyage-code-2); 1024 (voyage-3) |
| Accuracy | `voyage-code-2` tuned for code; strong MTEB Code |
| Pricing (verify at publish) | $RATE_VOYAGE per 1 M tokens |
| Integration cost | Medium — no official M.E.A. adapter; custom wrapper needed |
| When to choose | Best option when the text field is source code rather than natural-language documentation; code snippet dedup |

**Phase 1 recommendation: OpenAI `text-embedding-3-small`.** It has an existing `Microsoft.Extensions.AI.OpenAI` adapter in-tree, no new NuGet surface, and adequate accuracy for documentation-text deduplication. Phase 2 adds local ONNX (BGE-small or MiniLM-L6) using the HF cache infrastructure from the ONNX provider spec.

---

## 4. Similarity index — brute force vs ANN

### 4.1 Brute-force ceiling (analytic)

Cosine similarity between two D-dimensional float vectors is O(D) dot product + two L2-norms. With SIMD (AVX2 on x64, ARM NEON) this reduces to ~1 µs per pair comparison at D=1536.

```
N=1 000:  N(N-1)/2 ≈   500 K comparisons  →   0.5 s    — fine
N=5 000:  N(N-1)/2 ≈  12.5 M comparisons  →  12.5 s    — acceptable
N=20 000: N(N-1)/2 ≈ 200 M comparisons    →  200 s     — borderline
N=50 000: N(N-1)/2 ≈ 1.25 B comparisons   →  ~21 min   — unacceptable
```

At D=384 (local models), all figures scale by ×0.25. The practical brute-force ceiling is **~10 K rows** for cloud embeddings (D=1536) and **~20 K rows** for local embeddings (D=384). Beyond those limits an approximate nearest-neighbour index is required.

### 4.2 ANN library choice

Three candidates were evaluated for .NET 10 / cross-platform suitability:

| Library | NuGet | Implementation | .NET 10 compatible | Notes |
|---|---|---|---|---|
| **HNSW.Net** | `HNSW.Net` | Pure C#, HNSW algorithm | Yes | Last NuGet publish 2021; maintained on GitHub; no native deps; pure managed code works on all DistSharp target platforms. Candidate. |
| **FaissNet / FaissMask** | `FaissMask` | P/Invoke to native FAISS | Yes (native binaries) | High accuracy; large binary; platform-specific native assets clash with DistSharp's global-tool model. Rejected. |
| **Microsoft.SemanticKernel (Volatile Memory)** | `Microsoft.SemanticKernel.Core` | Brute-force linear scan | Yes | SK's in-process `VolatileMemoryStore` is a linear brute-force store, not HNSW. Pulls SK as a dependency for a capability no better than our own `BruteForceIndex`. Rejected — dependency cost not justified. |
| **Pgvector** | `Pgvector` | Postgres extension | Requires Postgres | Not viable for an in-process step. Rejected. |

**Decision: HNSW.Net for ANN; brute force for N ≤ 10 K.** HNSW.Net is pure managed C#, has no native-asset packaging cost, and implements the HNSW graph algorithm which gives O(log N) query time with high recall (>0.95 at ef=200). The step auto-selects the strategy:

```csharp
IVectorIndex index = rows.Count <= options.BruteForceThreshold
    ? new BruteForceIndex(options.Dimensions)
    : new HnswIndex(options.Dimensions, options.HnswM, options.HnswEf);
```

`BruteForceThreshold` defaults to 10 000 (configurable). This avoids the HNSW construction cost for small datasets where brute force is faster end-to-end.

**Risk on HNSW.Net last-publish date:** Confirm `HNSW.Net` NuGet package is current against .NET 10 before finalising. If it is stale, the fallback is vendoring the ~400-line core implementation from the MIT-licensed source, which is practical given its small size. Mark as **(BLOCKING — verify NuGet compatibility before implementation)** in §9.

### 4.4 Why not LSH (locality-sensitive hashing for cosine)?

Locality-sensitive hashing (random-projection LSH) is the classical alternative to HNSW for approximate cosine search: project each vector onto D random hyperplanes, assign a binary hash, and place vectors in a hash bucket. Items with similar orientations land in the same bucket with high probability.

LSH was evaluated and rejected for this use case for three reasons:

1. **Recall/precision at small N is poor.** LSH requires many independent hash tables to achieve the recall of HNSW, and tuning the number of tables (L) and hash functions (K) against the target threshold is non-trivial. At the dataset sizes where ANN is needed (20 K–100 K rows), HNSW with ef=200 delivers recall >0.95 without tuning.
2. **No maintained pure-C# implementation.** Existing .NET LSH libraries are older and less maintained than HNSW.Net. HNSW.Net has a better maintenance signal.
3. **Insert-time complexity is not better.** Both HNSW and LSH degrade from brute-force for pure online-insert workloads. HNSW's O(log N) query advantage over LSH's O(1)-but-imprecise bucket lookup dominates at typical deduplication result sizes.

If future profiling shows HNSW construction time is a bottleneck at extreme N (>500 K), LSH should be reconsidered as a pre-filter before HNSW refinement.

### 4.3 Threshold defaults

**Do not inherit MinHash's 0.85 threshold for cosine similarity.** The spaces are incommensurable.

For `text-embedding-3-small` (1536-dim) on short English text (50–300 tokens):
- Identical paraphrase pairs typically score 0.92–0.97.
- Topically related but distinct pairs typically score 0.75–0.88.
- Unrelated pairs: 0.10–0.60.

**Default: `Threshold = 0.92` for cloud embedding models (1536-dim).**

For local models (384-dim, BGE-small / MiniLM-L6) the embedding space is slightly less discriminative at the high end:

**Default: `Threshold = 0.90` for local embedding models (384-dim).**

The options class exposes `Threshold` without a built-in per-model mapping; the YAML documentation should call out the model-dependency, and the provider registration should emit a warning if the user sets a threshold below 0.80 (likely too loose) or above 0.99 (likely too tight).

---

## 5. Components & file layout

```
src/
  DistSharp.Core/
    Abstractions/
      IEmbeddingProvider.cs            # new: embedding abstraction
      IEmbeddingProviderFactory.cs     # new: resolves named provider from DI
    Steps/
      EmbeddingDeduplicator.cs         # new: IStep implementation
      EmbeddingDeduplicatorOptions.cs  # new: configuration class
      Internal/
        BruteForceIndex.cs             # new: O(N²) cosine index for small N
        HnswIndex.cs                   # new: HNSW.Net wrapper for large N
        IVectorIndex.cs                # new: common interface for both

  DistSharp.Providers/
    Embeddings/
      OpenAiEmbeddingProvider.cs       # new: wraps IEmbeddingGenerator from M.E.A.OpenAI
      OnnxEmbeddingProvider.cs         # Phase 2: wraps ORT InferenceSession
      EmbeddingProviderFactory.cs      # new: IEmbeddingProviderFactory impl
      EmbeddingCache.cs                # new: (body_sha, model_id, version) → vector cache

  DistSharp.Core/
    Configuration/
      EmbeddingDeduplicatorConfig.cs   # new: YAML-bound config (mirrors StepConfig.Config)
```

Cache store (see §2.4 and §7):

```
<dataset-dir>/
  _distsharp/
    embeddings.parquet                 # new: (row_id, model_id, model_version, vector[])
```

The `embeddings.parquet` sidecar is co-located with `manifest.json` from the dataset-sync design, keyed on `row_id` (stable across re-runs) and tagged with `model_id` + `model_version`. On re-run, if an embedding exists for `(body_sha, model_id, model_version)` it is loaded from cache — no API call is made.

---

## 6. CLI / YAML surface

### 6.1 New step type: `EmbeddingDeduplicator`

```yaml
steps:
  - name: dedup_semantic
    type: EmbeddingDeduplicator
    depends_on: [generate]
    config:
      field: response            # field to embed — same as MinHashDeduplicator
      provider: openai           # or: onnx
      model: text-embedding-3-small
      threshold: 0.92
      batch_size: 512
      dimensions: 1536           # optional; omit to use provider default
      brute_force_threshold: 10000
      cache_dir: ./_distsharp    # optional; defaults to dataset output dir
```

### 6.2 Existing `MinHashDeduplicator` step — no change

```yaml
steps:
  - name: dedup_lexical
    type: MinHashDeduplicator
    depends_on: [generate]
    config:
      field: response
      threshold: 0.85
      num_hashes: 128
      shingle_size: 3
      seed: 42
```

`MinHashDeduplicator` YAML keys are unchanged. The two step types are independent and composable — a pipeline can run `MinHashDeduplicator` first (fast, streaming, low cost) to drop exact/near-exact duplicates, then `EmbeddingDeduplicator` on the survivors to catch semantic duplicates.

### 6.3 Backwards compatibility

- No existing YAML key is renamed or removed.
- `MinHashDeduplicator` is not deprecated.
- The new `provider` key on `EmbeddingDeduplicator` resolves via `IEmbeddingProviderFactory`, using the same provider-name convention as `LlmStep` (`provider: openai`).
- If `provider: openai` is specified, `OPENAI_API_KEY` is required (same env-var already used by `LlmStep`).

---

## 7. Cost — embedding 50 K rows

Assuming:
- 50 000 rows
- 150 tokens average per `response` field
- 7 500 000 total tokens

| Provider | Rate (verify at publish) | Cost formula | Illustrative total | Cache hit re-run |
|---|---|---|---|---|
| OpenAI `text-embedding-3-small` | $RATE_TE3S per 1 M tokens | 7.5 × $RATE_TE3S | ~$0.15 at $0.02/1M | $0.00 |
| OpenAI `text-embedding-3-large` | $RATE_TE3L per 1 M tokens | 7.5 × $RATE_TE3L | ~$0.98 at $0.13/1M | $0.00 |
| Cohere `embed-english-v3.0` | $RATE_COHERE per 1 M tokens | 7.5 × $RATE_COHERE | verify | $0.00 |
| Voyage `voyage-code-2` | $RATE_VOYAGE per 1 M tokens | 7.5 × $RATE_VOYAGE | verify | $0.00 |
| Local ONNX (BGE-small) | $0 | — | $0.00 | $0.00 |

**Cache hit on re-run:** The `embeddings.parquet` sidecar is keyed on `(body_sha256, model_id, model_version)`. If the `response` field is unchanged between runs (common in incremental `dataset sync` scenarios), the cached vector is loaded and no API call is made. A re-run that regenerates 10% of rows costs 10% of the original embedding bill. Combined with the dataset-sync manifest (which already tracks `body_sha`), cache reuse is near-automatic.

**Dimension truncation:** `text-embedding-3-small` supports Matryoshka dimensions (256/512/1536). At 512-dim the cost is identical (billing is per input token, not per dimension) but storage and comparison cost halve. For pure deduplication, 512-dim is adequate; set `dimensions: 512` to save index memory.

---

## 8. Effort — phased day estimate

| Phase | Scope | Estimate | Dependencies |
|---|---|---|---|
| **1: Core + OpenAI** | `IEmbeddingProvider`, `IEmbeddingProviderFactory`, `EmbeddingDeduplicator`, brute-force index, `OpenAiEmbeddingProvider`, `embeddings.parquet` cache, YAML wiring | 3–4 days | none beyond existing OpenAI key infrastructure |
| **2: HNSW index** | `HnswIndex` wrapper around HNSW.Net (or vendored impl), auto-switch at `BruteForceThreshold` | 1 day | Phase 1 |
| **3: Local ONNX embedding** | `OnnxEmbeddingProvider` using `Microsoft.ML.OnnxRuntime` + `Microsoft.ML.Tokenizers`, HF cache fetch for ONNX embedding model, `provider: onnx` in YAML | 2–3 days | Phase 1 + ONNX provider spec HF cache work |
| **4: Cohere / Voyage adapters** | Two thin `IEmbeddingProvider` wrappers, each ~100 LOC | 1 day each | Phase 1 |
| **5: Export embeddings as dataset column** | Optionally write the embedding vector into the JSONL/Parquet output row | 0.5 days | Phase 1 |

**Total to "cloud embeddings + ANN + cache"** (the realistic v1 target): ~5 working days.

Phase 3 (local ONNX) should be sequenced after the ONNX provider spec is implemented, since both share the `HuggingFaceModelCache` infrastructure.

---

## 9. Risks & open questions

1. **(BLOCKING)** **Default embedding model policy.** The spec recommends `text-embedding-3-small` as Phase 1 default, but this requires an OpenAI API key and incurs cost. There is no zero-cost default embedding model until Phase 3 ships. Decision needed before implementation: does `EmbeddingDeduplicator` require an explicit `model:` / `provider:` in YAML (no default, fail fast), or does it silently default to cloud? Recommendation: require explicit configuration; document the ONNX local alternative as the zero-cost path.

2. **(BLOCKING)** **HNSW.Net .NET 10 compatibility.** The `HNSW.Net` NuGet package's last official publish predates .NET 10. Must be verified against `net10.0` TFM before committing to it. If incompatible, the fallback is vendoring ~400 lines of MIT-licensed source or evaluating an alternative managed HNSW implementation.

3. **(BLOCKING)** **Cache invalidation on prompt/model change.** The `embeddings.parquet` cache key includes `model_id` and `model_version`. If the model is updated (e.g., OpenAI silently bumps an embedding model revision), cached vectors from the old revision are stale but will still match the key unless `model_version` is explicit. Decide whether to use the provider-returned `model` string as the version key (safest, but may re-embed on every model revision) or a user-pinned version string.

4. **Buffering memory ceiling.** `EmbeddingDeduplicator` must buffer all rows before emitting output. For very large pipelines (100 K+ rows with `response` fields of several KB each), in-memory buffering can exhaust available RAM. A `MaxBufferRows` / `SpillToDisk` option is listed in the design but is not spelled out in the initial phase — this is a known gap to close in Phase 1 implementation.

5. **Threshold is model-specific.** The default `Threshold: 0.92` is calibrated for 1536-dim cloud models. Users switching to a local 384-dim model without updating the threshold may see dramatically different recall/precision. The YAML documentation should warn about this, and the provider registration could emit a runtime advisory when `provider: onnx` is used with a threshold above 0.95.

6. **Embedding vector export creates schema drift.** Exporting raw `float[]` vectors as dataset columns (Phase 5 scope) adds a schema column that is model-specific and very wide (1536 or 384 floats). Downstream consumers that do simple JSONL parsing may break on oversized records. This is explicitly deferred and marked out of scope for Phases 1–4.

7. **Interaction with `dataset sync` re-runs.** When `dataset sync` regenerates only changed rows (see dataset-sync design §2), the `EmbeddingDeduplicator` must re-embed the regenerated rows and re-check them against the full existing index — not just the new subset. The cache covers this for unchanged rows, but the index must be reconstructed from the cached sidecar on re-run. This loading path needs to be explicitly implemented and tested.

8. **Sentence-transformers option removed.** Option (b) (Python subprocess) was evaluated and rejected. If a user has a specific sentence-transformers model not available in ONNX format, there is no current support path. Document this limitation in the CLI help text.

---

## 10. Out of scope

- Replacing or deprecating `MinHashDeduplicator`.
- Multi-field embedding (computing a single embedding over a concatenation of multiple fields) — single `Field` is sufficient for the documented use cases.
- Cross-dataset deduplication (deduplicating the current pipeline output against a previously generated dataset) — a meaningful future feature but requires a separate design.
- Cluster-based deduplication (keep the centroid row per cluster, not just the first-seen row) — more complex retention policy; single-pass first-seen is consistent with MinHash and sufficient for v1.
- Exporting embedding vectors as dataset columns — deferred to Phase 5 and subject to schema-drift risk noted in §9.
- Integration with Microsoft Semantic Kernel's vector store abstractions — a reasonable future direction but adds a large dependency and is out of scope until there is a clear SK integration story for DistSharp.
- Real-time / streaming deduplication — the buffering model is a fundamental design choice. A streaming ANN index is theoretically possible but would require a different algorithmic approach (e.g., locality-sensitive hashing for cosine similarity) and is not warranted at the expected dataset sizes.
- ONNX-format embedding models for languages other than English — multilingual embedding models (e.g., `paraphrase-multilingual-MiniLM-L12-v2`) are a valid extension but out of scope for the initial design.
