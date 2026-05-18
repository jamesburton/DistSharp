# DistSharp — Preference Datasets (DPO / ORPO Ranked Pairs)

**Date:** 2026-05-18
**Scope:** Design for generating `{prompt, chosen, rejected, margin?}` preference pair rows from C# codebases, enabling Direct Preference Optimization (DPO) and Odds Ratio Preference Optimization (ORPO) fine-tuning workflows.

---

## 1. Goals

1. **Unlock DPO / ORPO training** — produce datasets consumable by TRL `DPOTrainer`, Axolotl's `dpo` recipe, and any HuggingFace-compatible trainer that reads preference pairs.
2. **Leverage existing dataset types** — preference rows are produced _from_ the seven existing types (`explanation`, `completion`, `bug-fix`, `unit-test`, `docstring`, `refactor`, `architecture-qa`), not in addition to them. Each type learns its own "good vs. less-good" axis.
3. **Multiple rejection strategies** — the pipeline is configurable: high-temperature sampling, smaller model, degraded prompt, or LlmJudge-driven pairwise scoring. Users pick based on cost tolerance and signal quality requirements.
4. **Compose with existing pipeline** — a `PreferencePairStep` fits naturally into the existing `IStep` / `ChannelReader<Row>` / `ChannelWriter<Row>` model without requiring new pipeline DAG primitives.
5. **Export to canonical HF columnar format** — `{ "prompt": ..., "chosen": ..., "rejected": ... }` as the canonical shape, with a ShareGPT-preference variant. Plugs into the existing converter pattern from Phase 7.

---

## 2. Approach — Strategies to Produce the Rejected Response

Each row needs at least two LLM responses: one to become `chosen`, one `rejected`. The strategies below differ in cost, signal quality, and the degree to which the ranking is asserted vs. verified.

### 2.1 Same Model, Higher Temperature (recommended default)

**How it works:** Call the same model twice. First call: temperature `0.4` → `chosen` candidate. Second call: temperature `1.2` → `rejected` candidate. Then LlmJudge scores both head-to-head (call 3) and assigns `chosen`/`rejected` from the winner/loser.

**Cost:** 3 LLM calls per row.

**Quality:** High. Both responses are from the same capability level; the judge resolves which is truly better, avoiding self-preference artefacts. The higher-temperature second call naturally introduces imprecision, verbosity, off-topic drift, or hallucinated APIs — realistic rejection signals.

**Tradeoffs:**
- Requires a judge call (adds latency and cost).
- If the high-temperature response happens to be better (it occasionally is), the judge will correctly swap them — the strategy does not presuppose which direction quality goes.
- Judge introduces its own scoring variance; a 1-vs-2 margin is weak, a 4-vs-1 margin is strong.

**Why this is the default:** No second provider config needed, no "small model is always worse" assumption to defend. The judge breaks ambiguity and composes with the existing `LlmJudge` rubric vocabulary.

### 2.2 Smaller / Cheaper Model

**How it works:** `chosen` comes from the main (larger) model at normal temperature. `rejected` comes from a smaller, cheaper model (e.g. `gpt-4o-mini` vs `gpt-4o`, or `llama-3-8b` vs `llama-3-70b`).

**Cost:** 2 LLM calls per row. No judge needed if the smaller model is accepted as categorically inferior — though this assumption is weak on short, well-defined tasks.

**Quality:** Monotonicity assumption is often correct for 70B-vs-8B pairs on complex reasoning tasks, but is unreliable for short, formulaic answers (docstrings, simple completions). Recommended only when cost is the dominant constraint.

**Tradeoffs:**
- Cheapest configuration per row.
- Requires a second provider config (`rejected_provider`, `rejected_model`).
- If the small-model response is actually better, no mechanism corrects it without a judge call (at which point cost advantage over strategy 2.1 shrinks).
- Dataset quality degrades if the size gap is insufficient.

### 2.3 Deliberately Degraded Prompt ("Curt Persona")

**How it works:** `chosen` is generated with the normal system prompt. `rejected` is generated with the same user prompt but a degraded system: e.g. "Respond as briefly as possible. Use minimal explanation. Cut corners." or "You are a junior developer who writes quick-and-dirty code."

**Cost:** 2 LLM calls per row (no judge needed — the degraded response is definitionally `rejected`).

**Quality:** Low to medium. The rejected responses tend to be uniformly short or terse rather than realistically wrong. This produces datasets that train models to be verbose, not necessarily more correct. Useful as a warm-up or curriculum stage, not for final DPO.

**Tradeoffs:**
- No judge required.
- Fast and cheap.
- Rejection signal is stylistic, not semantic — weak for code-quality tasks where a terse correct solution is fine.
- Better fit for explanation/docstring tasks where depth is clearly valued.

### 2.4 Pre-Tuning Checkpoint vs. Post-Tuning

**How it works:** `chosen` comes from the fine-tuned model (or a strong base). `rejected` comes from a known-earlier checkpoint. Applicable during iterative training loops.

**Cost:** 2 calls per row, but requires infrastructure to serve multiple model checkpoints simultaneously.

**Quality:** Strong if checkpoints genuinely differ. Produces on-policy rejection data, which is theoretically optimal for DPO.

**Tradeoffs:**
- Requires checkpoint management outside DistSharp's scope.
- High operational complexity.
- Out of scope for Phase 1; noted as a future integration point.

### 2.5 LlmJudge Pairwise Mode

**How it works:** Generate two responses from the same model (any temperatures). Then invoke `LlmJudge` in a new `pairwise` mode: the judge receives both responses and returns `{ "winner": 1|2, "margin": 1–4, "reason": "..." }`. The winner becomes `chosen`, the loser `rejected`.

**Cost:** 3 LLM calls per row (same as 2.1 with judge, but decoupled from the temperature strategy).

**Quality:** Highest signal quality. The judge can be swapped independently of the generation strategy. The `margin` field becomes the preference `margin` column in the output.

**Recommendation:** This is the preferred architecture for pairwise judging because it composes cleanly with the existing `LlmJudge` rubric vocabulary. Extend `LlmJudge` with a `mode: pairwise` rather than building a separate comparator step.

---

## 3. Pipeline Shape

### 3.1 Core Design Decision: `PreferencePairStep`

`LlmStep` is currently 1-row-in → 1-row-out. Preference rows require generating ≥ 2 candidates per symbol and combining them into a single output row. Two approaches exist:

**Option A — `PreferencePairStep` (recommended):** A new, self-contained step that makes ≥ 2 LLM calls internally and emits one row with `{ prompt, chosen, rejected, margin? }`. Clean, no join semantics needed in the pipeline executor. Mirrors how `LlmJudge` encapsulates its scoring logic rather than splitting it across two steps.

**Option B — Two `LlmStep` instances + a `PairJoinStep`:** Reuses `LlmStep` but requires the pipeline executor to support fan-in (merging two upstream channels by a key, e.g. `symbol.FullyQualifiedName`). The current `PipelineExecutor` performs a linear topological sort and does not support fan-in. This would require a new pipeline concept and is deferred.

Option A is the implementation target.

### 3.2 Row Schema

A preference row carries:

| Field | Type | Notes |
|---|---|---|
| `prompt` | `string` | The instruction/question sent to the model. Mirrors `instruction` from existing types. |
| `context` | `string?` | Code context, if applicable. Preserved for traceability. |
| `chosen` | `string` | The preferred response. |
| `rejected` | `string` | The dispreferred response. |
| `margin` | `float?` | Score gap (chosen_score − rejected_score), 0–4. Populated when a judge is used. |
| `chosen_score` | `float?` | Raw judge score for chosen (1–5). |
| `rejected_score` | `float?` | Raw judge score for rejected (1–5). |
| `rejection_strategy` | `string` | The strategy used: `high_temperature`, `smaller_model`, `degraded_prompt`, or `pairwise_judge`. |
| `dataset_type` | `string` | Originating type, e.g. `explanation`. |
| `symbol_fqn` | `string` | Fully-qualified symbol name, for deduplication and traceability. |

### 3.3 `PreferencePairStep` Options

```
PreferencePairStepOptions:
  DatasetType:           string        // existing type name, e.g. "explanation"
  Provider:              string        // provider name for the "chosen" call
  RejectedProvider:      string?       // if null, uses Provider
  RejectedModel:         string?       // if null, uses Model (for smaller-model strategy)
  ChosenTemperature:     float = 0.4
  RejectedTemperature:   float = 1.2
  RejectionStrategy:     string = "high_temperature"
                                       // "high_temperature" | "smaller_model" |
                                       // "degraded_prompt" | "pairwise_judge"
  DegradedSystemPrompt:  string?       // custom degraded system prompt for "degraded_prompt" strategy
  JudgeEnabled:          bool = true   // when true, runs pairwise judge to assign chosen/rejected
  JudgeProvider:         string?       // if null, uses Provider
  JudgeModel:            string?
  JudgeRubric:           string = "code_quality"
  MinChosenScore:        float = 3.0   // chosen must clear this floor; rows below are dropped
  Workers:               int = 4
  DropOnError:           bool = true
```

`MinChosenScore` applies to the chosen response only. The rejected response has no minimum floor by design — a low-quality rejection is a valid training signal.

### 3.4 `LlmJudge` Pairwise Extension

`LlmJudge` gains a `Mode` property:

```
LlmJudgeOptions:
  Mode:  string = "score"   // "score" (existing) | "pairwise" (new)
```

In `pairwise` mode the judge prompt changes to:

> "You are a senior .NET reviewer. Given the instruction below and two candidate responses, decide which is better. Output JSON: `{ \"winner\": 1, \"margin\": 2, \"reason\": \"...\" }`. winner is 1 or 2. margin is 1 (slight preference) to 4 (decisive)."

`PreferencePairStep` calls this internally when `JudgeEnabled: true`. The pairwise judge is not exposed as a standalone pipeline step in Phase 1 (though it can be wired manually via `LlmJudge` with `Mode: pairwise` if the user already has two candidate fields on the row).

---

## 4. Per-Dataset-Type Adaptation

### 4.1 Suitability Matrix

| Type | Preference-ifiable? | Best Rejection Strategy | Notes |
|---|---|---|---|
| `explanation` | Yes — strong | High-temperature or degraded-prompt | Quality axis is clear: depth, precision, correct terminology. High-temperature rejects tend to hallucinate API names. |
| `unit-test` | Yes — strong | High-temperature with judge | A correct compilable test vs. a test with wrong assertion logic or missing edge case is a high-signal pair. |
| `docstring` | Yes — medium | Degraded-prompt ("be terse") | Short vs. comprehensive doc is a valid axis. High-temperature noise is less meaningful here. |
| `refactor` | Yes — medium | High-temperature with judge | One idiomatic refactor vs. a syntactically-valid-but-worse refactor. |
| `architecture-qa` | Yes — strong | Pairwise judge | The answer has a factual grounding in the code; the judge can assess correctness against the source. |
| `bug-fix` | Yes — strong | Dedicated: inject wrong fix | The "rejected" can be the intentionally-buggy code (already generated by `BugFixPromptBuilder`). This avoids a second LLM call: reuse `buggy_code` as `rejected`, `fixed_code` as `chosen`. Special-case in `PreferencePairStep`. |
| `completion` | Marginal | High-temperature only | Multiple valid completions exist for a given prefix; the "wrong" completion may be debatable. Recommend excluding from preference datasets by default. |

### 4.2 `bug-fix` Special Case

`BugFixPromptBuilder` already produces `{ buggy_code, fixed_code, explanation }` in a single LLM call. For preference rows from `bug-fix`:

- `prompt` = "The following C# method contains a bug. Identify and fix it."
- `context` = the original (clean) method body
- `chosen` = `fixed_code`
- `rejected` = `buggy_code`
- `margin` = 4.0 (deterministic: correct vs. incorrect fix)
- No judge call needed.

This reduces the cost for `bug-fix` preference rows to 1 LLM call — the same as the existing non-preference dataset. `PreferencePairStep` detects `DatasetType == "bug-fix"` and takes this path automatically.

---

## 5. Export Format

### 5.1 Canonical: HF Columnar

The canonical output format is the HuggingFace columnar preference format, directly loadable by TRL `DPOTrainer` and Axolotl's `dpo` recipe:

```json
{"prompt": "Explain what ILlmProvider.CompleteAsync does...", "chosen": "CompleteAsync sends a chat message list...", "rejected": "It calls the LLM.", "margin": 2.0}
```

Column names: `prompt`, `chosen`, `rejected`. Optional: `margin`, `chosen_score`, `rejected_score`, `rejection_strategy`, `dataset_type`, `symbol_fqn`.

TRL `DPOTrainer` reads this directly. Axolotl `dpo` config expects a `chat_template`-formatted prompt if the dataset uses the conversational variant, but accepts raw strings in the columnar format when `dataset_type: alpaca` is set and the `response` column is absent — the `chosen`/`rejected` columns are consumed directly.

### 5.2 ShareGPT Preference Variant

For trainers that consume ShareGPT format with preference extensions (e.g. OpenRLHF):

```json
{
  "conversations": [{"from": "human", "value": "Explain what ILlmProvider.CompleteAsync does..."}],
  "chosen": {"from": "gpt", "value": "CompleteAsync sends a chat message list..."},
  "rejected": {"from": "gpt", "value": "It calls the LLM."}
}
```

This variant is produced by a new `ShareGptPreferenceConverter` class sitting alongside the existing `ShareGptConverter` in `src/DistSharp.Core/Export/`.

### 5.3 Raw JSONL

All fields are written as-is. This is the default internal format and the input to any downstream conversion. Users who want custom field mapping or metadata preservation should use raw JSONL and convert externally.

### 5.4 Axolotl vs TRL Compatibility Notes

| Trainer | Format | Required columns | `margin` support |
|---|---|---|---|
| TRL `DPOTrainer` | HF columnar | `prompt`, `chosen`, `rejected` | `label` column (not `margin`); margin not natively used |
| Axolotl `dpo` | HF columnar | `prompt`, `chosen`, `rejected` | `score_chosen` / `score_rejected` (mapped from our `chosen_score` / `rejected_score`) |
| OpenRLHF | ShareGPT preference | `conversations`, `chosen`, `rejected` | Not standard |

The `export` command's `--format preference-columnar` is the recommended target for most users. The `--format preference-sharegpt` variant is provided for OpenRLHF.

---

## 6. Components and File Layout

```
src/
  DistSharp.Core/
    Steps/
      PreferencePairStep.cs          // new — main preference generation step
      PreferencePairStepOptions.cs   // new — configuration POCO
      LlmJudge.cs                    // modified — add Mode property, pairwise prompt path
      LlmJudgeOptions.cs             // modified — add Mode: "score" | "pairwise"
    Prompts/
      (no new builders required — existing builders used internally by PreferencePairStep)
    Export/
      PreferenceColumnarConverter.cs // new — writes {prompt, chosen, rejected, margin?}
      ShareGptPreferenceConverter.cs // new — ShareGPT preference variant

  DistSharp.Cli/
    PipelineBuilder.cs               // modified — add "PreferencePairStep" case
    Commands/
      ExportCommandOptions.cs        // modified — add "preference-columnar", "preference-sharegpt" formats
      ExportCommandHandler.cs        // modified — wire new converters
```

No new projects. No new abstractions beyond `PreferencePairStep` implementing `IStep`.

---

## 7. CLI and YAML Surface

### 7.1 YAML Pipeline Step

```yaml
name: explanation-preference-pipeline
version: "1"
solution:
  path: ./MyProject.sln
  min_complexity: 3

steps:
  - name: extract
    type: RoslynSymbolExtractor

  - name: sampler
    type: StratifiedSampler
    depends_on: [extract]
    config:
      max_rows: 2000

  - name: preference-pairs
    type: PreferencePairStep
    depends_on: [sampler]
    config:
      dataset_type: explanation
      provider: openai
      rejection_strategy: high_temperature   # "high_temperature" | "smaller_model" | "degraded_prompt" | "pairwise_judge"
      chosen_temperature: 0.4
      rejected_temperature: 1.2
      judge_enabled: true
      judge_provider: openai
      judge_model: gpt-4o-mini
      judge_rubric: code_quality
      min_chosen_score: 3.0
      workers: 4

  - name: dedup
    type: MinHashDeduplicator
    depends_on: [preference-pairs]
    config:
      field: chosen

output:
  dir: ./distsharp-out
  format: jsonl
```

`judge_enabled: false` skips the judge call; the lower-temperature response is assumed to be `chosen`.

### 7.2 CLI `generate` Command Extension

The `--dataset-type` flag on `distsharp generate` accepts a `-preference` suffix:

```
distsharp generate ./MyProject.sln \
  --dataset-type explanation-preference \
  --format preference-columnar \
  --provider openai \
  --model gpt-4o \
  --rejection-strategy high_temperature
```

Internally, `GenerateCommandHandler` maps `explanation-preference` → `PreferencePairStep` with `DatasetType: explanation`. This is a convenience wrapper over the YAML approach; the YAML pipeline provides full control.

### 7.3 `export` Command Extension

```
distsharp export ./distsharp-out/dataset.jsonl \
  --format preference-columnar \
  --out ./hf-dataset/
```

Detects the presence of `chosen` / `rejected` fields in the JSONL automatically. Emits a `dataset_info.json` alongside the output for HF Hub compatibility.

---

## 8. Cost Model

Per-row call counts and indicative costs using `gpt-4o-mini` at $0.15/1M input tokens and $0.60/1M output tokens (2025-Q2 pricing). Assume ~600 input tokens (symbol + instruction + context) and ~300 output tokens per call.

| Strategy | LLM calls/row | Approx cost per 1,000 rows |
|---|---|---|
| High-temperature, no judge | 2 | ~$0.45 |
| High-temperature + judge (default) | 3 | ~$0.68 |
| Smaller model (no judge) | 2 | ~$0.25 (judge call on main model; rejected call on mini) |
| Degraded prompt, no judge | 2 | ~$0.45 |
| `bug-fix` special case | 1 | ~$0.23 |

For 10,000 rows with the default strategy (high-temperature + judge): approximately **$6.80** using `gpt-4o-mini`. Using `gpt-4o` as the main model ($2.50 / $10.00 per 1M): approximately **$40–50 per 10k rows**.

**Budget guidance:**
- Start with 1,000 rows to validate output quality (< $1 on `gpt-4o-mini`).
- Use `bug-fix` preference rows to pad row count cheaply; they have deterministic margin and no judge overhead.
- Set `--max-rows` conservatively; preference pairs cannot be de-ranked cheaply after generation.

---

## 9. Effort — Phased Day Estimate

### Phase 1 — Core preference generation (3 days)

- `PreferencePairStep` + `PreferencePairStepOptions` — 1 day
- `LlmJudge` pairwise mode extension — 0.5 days
- `PipelineBuilder` registration + YAML wiring — 0.5 days
- Unit tests for `PreferencePairStep` (mock provider, all four strategies, `bug-fix` shortcut) — 1 day

### Phase 2 — Export and CLI (1.5 days)

- `PreferenceColumnarConverter` + `ShareGptPreferenceConverter` — 0.5 days
- `export` command extension, `--format preference-columnar/preference-sharegpt` — 0.5 days
- `generate` command `--dataset-type *-preference` suffix handling — 0.5 days

### Phase 3 — Integration and validation (1 day)

- End-to-end integration test with a fixture C# solution, mock provider returning known responses — 0.5 days
- Manual validation: load generated JSONL into TRL `DPOTrainer` locally, verify no schema errors — 0.5 days

**Total: 5.5 days**

---

## 10. Risks and Open Questions

1. **(BLOCKING)** **Which rejection strategy is the default in the `generate` convenience command?** Recommended: `high_temperature` with `judge_enabled: true`. This must be documented clearly; a user who omits `--rejection-strategy` incurs 3 LLM calls/row without realising it. The CLI must print a cost estimate before starting.

2. **(BLOCKING)** **`LlmJudge` pairwise mode prompt quality.** The existing `LlmJudge` was designed for absolute scoring. The pairwise prompt is new and untested. It must be calibrated before trusting `margin` values — a score-swap sanity check (swap the two responses; if margin is consistent, the judge is reliable) should be part of Phase 3.

3. **`min_score` composition.** The `chosen` response must clear `MinChosenScore` (default 3.0); rows where the judge scores the better response below this are dropped. This is the correct behaviour — a dataset with a mediocre `chosen` is not useful for DPO. This design is explicit in `PreferencePairStepOptions`.

4. **Sanity-checking generated pairs.** How do we verify a `rejected` is genuinely worse than `chosen` beyond the judge's assertion? Proposed: in Phase 3, run a random 5% sample of generated pairs through a second, independent judge call (the "A/B sanity check"). If judge agreement on winner exceeds 85%, the pairs are accepted. This is a validation procedure, not a production pipeline step.

5. **Format compatibility — Axolotl column names.** Axolotl's `dpo` YAML expects `score_chosen` and `score_rejected` (not `chosen_score`/`rejected_score`). The `PreferenceColumnarConverter` should map field names to Axolotl convention when `--format preference-columnar --target axolotl` is specified, or document the rename step clearly in the README.

6. **`completion` type exclusion.** `completion` produces multiple valid completions for any method prefix. It is excluded from preference datasets in Phase 1 because there is no defensible axis for "chosen vs. rejected" among valid completions. If desired in future, use a domain-specific rubric (e.g. "idiomatic vs. verbose").

7. **Deduplication field.** `MinHashDeduplicator` is currently used on `response`. For preference rows, deduplication should operate on `chosen` (and possibly check `symbol_fqn` uniqueness at the source level). The YAML example above deduplicates on `chosen`. This is sufficient but does not prevent two pairs with the same chosen response but different rejecteds — add a note in documentation.

8. **Row throughput.** With `workers: 4` and 3 calls/row, effective throughput is 4/3 rows per provider round-trip. For large symbol sets (50k symbols), this will take significant wall time. Add a `--max-rows-preference` cap and recommend starting at ≤5,000 rows.

---

## 11. Out of Scope

- **Human-in-the-loop labelling** — no UI, annotation tools, or Label Studio integration. Preference signal is entirely LLM-derived in this design.
- **KTO, IPO, SimPO, and other non-DPO/ORPO objective variants** — the row schema is compatible with KTO (it only needs `prompt` + `completion` + `label: bool`) but active support for KTO export is not designed here.
- **On-policy rejection sampling** — requires a fine-tuned model to be served during dataset generation. Out of scope; noted in §2.4 as a future integration.
- **Checkpoint management** — serving multiple model checkpoints side-by-side (strategy 2.4). Requires external infrastructure.
- **Reward model integration** — using a separate reward model to assign preference scores rather than LlmJudge.
- **Multi-turn preference pairs** — all current dataset types produce single-turn `{instruction → response}` pairs. Multi-turn preference (e.g. a refactor conversation with follow-up clarifications) is not designed here.
- **Parquet and CSV output for preference rows** — raw JSONL is the Phase 1 output. Parquet support follows the existing `IDatasetWriter` extension pattern and is deferred.
