# DistSharp — Mixed Fan-out + Per-Builder Symbol-Kind Filter

**Date:** 2026-05-18
**Status:** Design / planning. Not yet implemented.
**Scope:** Two tightly-coupled features: (1) `mixed` dataset type fans out across all (or a selected subset of) prompt builders in one run; (2) each prompt builder declares which symbol kinds it accepts, so nonsense pairings (e.g. `unit-test` for an `enum`) are silently skipped rather than sent to the LLM.

---

## 1. Goals

What this lets users do that they cannot do today:

1. **Single command, multi-purpose dataset.** `distsharp generate --dataset-type mixed` produces training rows covering all prompt builder perspectives simultaneously, rather than requiring seven separate invocations and a manual merge. One Roslyn pass, one config file, one export.
2. **Semantically valid symbol-builder pairings.** Today every builder accepts every symbol kind. A `unit-test` row for an `enum` is noise; a `docstring` row for a property getter is equally dubious. The kind filter ensures each builder only receives symbols it can produce a meaningful output for, improving dataset quality without user effort.
3. **Selective mixed runs.** Power users can request a subset of builders via `--dataset-type mixed:explanation,unit-test,docstring`, enabling targeted regeneration without giving up the single-run convenience.
4. **Predictable cost before commit.** The `--dry-run` flag gains meaning: it can report exactly how many LLM calls `mixed` would make — broken down per builder — before any API calls happen.

---

## 2. Approach

### 2.1 Where kind-acceptance lives — interface instances (chosen)

The existing builders are `public static class` types dispatched via a `switch` in `PromptBuilderRegistry`. Three options were considered:

| Option | Assessment |
|---|---|
| **A — Refactor to `IPromptBuilder` instances, registered via DI** | ✅ Chosen. `mixed` must iterate builders anyway; a plain dictionary/list of instances is far cleaner than iterating a switch. Adds `IPromptBuilder` with `Name`, `AcceptedKinds`, and `Build(ExtractedSymbol)`. Every static class gets a thin wrapper. The registry becomes a `IReadOnlyList<IPromptBuilder>`. Blast radius: 7 builder wrappers + registry + `LlmStep` wiring. |
| B — Keep statics, add a `KindAcceptance` side-table in `PromptBuilderRegistry` | Minimal change, but `mixed` still needs an enumerable list of builders — two parallel structures (switch + dict) that must stay in sync. Rejected. |
| C — YAML-only or attribute on static class | YAML acceptance with no compiled default means the tool is broken out-of-the-box without a config file. Attributes on static classes require reflection and cannot be unit-tested without instantiation. Rejected. |

**Chosen contract:**

```csharp
/// <summary>Builds prompts for a single dataset type.</summary>
public interface IPromptBuilder
{
    /// <summary>Gets the dataset-type name, e.g. <c>unit-test</c>.</summary>
    string Name { get; }

    /// <summary>Gets the set of <see cref="ExtractedSymbol.Kind"/> values this builder accepts.</summary>
    IReadOnlySet<string> AcceptedKinds { get; }

    /// <summary>Builds a prompt for <paramref name="symbol"/>.</summary>
    PromptResult Build(ExtractedSymbol symbol);
}
```

Each existing static builder gets a `sealed class XxxPromptBuilder : IPromptBuilder` wrapper whose `Build` delegates to the existing static method. This is purely additive; the static methods are not deleted (they remain callable by tests). After validation the statics can be removed in a follow-up.

### 2.2 Fan-out implementation

`mixed` changes `LlmStep`'s output from 0-or-1 row per input row to 0-to-N rows. Two approaches:

| Option | Assessment |
|---|---|
| **LlmStep iterates a list of builders per input row** | ✅ Chosen. One step, one channel, N writes per input. Workers are shared across all builders for a given symbol — backpressure applies naturally. Simpler pipeline topology. |
| Materialise N parallel LlmStep instances merged downstream | Correct but requires a fan-out splitter step and a merge step, complicates the pipeline builder significantly, and spreads the cost-cap enforcement across N steps. Rejected for now. |

In the chosen approach, `LlmStepOptions.DatasetType` is replaced by a structured type in the options that carries both the mode (`single` vs `mixed`) and the builder list. When `mixed` is active, the worker loop iterates `_builders` (an `IReadOnlyList<IPromptBuilder>` resolved at step construction) and writes one output row per builder that accepts the symbol's kind.

**Current worker loop (simplified):**

```
await foreach row in input:
    symbol = row["symbol"]
    prompt = PromptBuilderRegistry.Build(options.DatasetType, symbol)
    raw = await provider.CompleteAsync(prompt.Messages, ...)
    parsed = prompt.ParseResponse(raw)
    await output.WriteAsync(row + prompt.PreparedFields + parsed)
```

**New worker loop with `mixed` support:**

```
await foreach row in input:
    symbol = row["symbol"]
    builders = _config is Single(name) ? [ Resolve(name) ]
                                        : _activeMixedBuilders
    for each builder in builders:
        if not builder.AcceptedKinds.Contains(symbol.Kind): continue
        if _quota[builder.Name] <= 0: continue   // cap enforced here
        prompt = builder.Build(symbol)
        raw = await provider.CompleteAsync(prompt.Messages, ...)
        parsed = prompt.ParseResponse(raw)
        _quota[builder.Name]--
        await output.WriteAsync(row + prompt.PreparedFields + parsed
                                    + { dataset_type = builder.Name })
```

The output row gains a `dataset_type` field (`"explanation"`, `"unit-test"`, etc.) so downstream steps and exports can route or split by builder. This field is already implicit in single-type runs (the dataset-type is known from config); in `mixed` it must be explicit on each row.

For single-type runs the new code path is identical to the old one: `builders` resolves to a single-element list and the quota check is always passing (quota = `int.MaxValue` when no cap). No behaviour change for existing users.

### 2.3 Cost-cap behaviour

See §6 for full analysis. The rule in short: `--max-rows` is a **post-fanout** cap on total output rows. When it binds, proportional fair-share is allocated across active builders. See §6 for the sampling algorithm.

---

## 3. Default symbol-kind acceptance

All nine kinds actually emitted by `SymbolExtractor` are included: `method`, `property`, `constructor`, `class`, `struct`, `record`, `record-struct`, `interface`, `enum`. Note: `ExtractedSymbol.Kind`'s doc-comment is stale (lists only 4 kinds) — it must be updated as part of this work.

The rationale for each cell is in brackets.

| Prompt builder | method | property | constructor | class | struct | record | record-struct | interface | enum |
|---|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| `explanation` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `completion` | ✅ | ✅ | ✅ | — | — | — | — | — | — |
| `bug-fix` | ✅ | — | ✅ | — | — | — | — | — | — |
| `unit-test` | ✅ | — | ✅ | — | — | — | — | — | — |
| `docstring` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `refactor` | ✅ | — | ✅ | ✅ | ✅ | ✅ | ✅ | — | — |
| `architecture-qa` | — | — | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |

**Rationale for key exclusions:**

- `completion` requires a splittable body — type and enum declarations have no single body to truncate at midpoint, and property auto-accessors are too short to be useful. Constructors with non-trivial bodies are included.
- `bug-fix` injects a subtle logic bug — only symbols with imperative bodies (methods, non-trivial constructors) are useful; properties are excluded because accessor-level bugs are degenerate.
- `unit-test` writes an xUnit test for a callable member — type declarations, enums, and properties are not directly testable in isolation.
- `refactor` reduces cyclomatic complexity — only symbols that have branching logic are meaningful. Interfaces (no body) and enums (no logic) are excluded.
- `architecture-qa` generates a question/answer about a type's role in the codebase — it only makes sense at the type level, not for individual members.

These defaults are **fixed per builder** in code. A per-run YAML override mechanism is out of scope for this design (see §9) but the interface property makes it straightforward to add later.

---

## 4. Components & file layout

### New / changed files

```
src/DistSharp.Core/
  Prompts/
    IPromptBuilder.cs                    NEW  — interface with Name, AcceptedKinds, Build
    ExplanationPromptBuilder.cs          CHANGE  — add IPromptBuilder instance wrapper
    CompletionPromptBuilder.cs           CHANGE  — add IPromptBuilder instance wrapper
    BugFixPromptBuilder.cs               CHANGE  — add IPromptBuilder instance wrapper
    UnitTestPromptBuilder.cs             CHANGE  — add IPromptBuilder instance wrapper
    DocstringPromptBuilder.cs            CHANGE  — add IPromptBuilder instance wrapper
    RefactorPromptBuilder.cs             CHANGE  — add IPromptBuilder instance wrapper
    ArchitectureQaPromptBuilder.cs       CHANGE  — add IPromptBuilder instance wrapper
    PromptBuilderRegistry.cs             CHANGE  — switch → IReadOnlyList<IPromptBuilder>;
                                                   add Resolve(name) and ResolveForMixed(subset)
  Steps/
    LlmStepOptions.cs                    CHANGE  — DatasetType: string becomes DatasetTypeConfig
    LlmStep.cs                           CHANGE  — worker loop iterates builders for mixed mode
  Models/
    ExtractedSymbol.cs                   CHANGE  — update Kind doc-comment to list all 9 kinds
    DatasetTypeConfig.cs                 NEW  — discriminated union: Single(name) | Mixed(builders)

src/DistSharp.Cli/
  Commands/GenerateCommandHandler.cs     CHANGE  — parse mixed:b1,b2 syntax into DatasetTypeConfig
```

### `DatasetTypeConfig`

```csharp
/// <summary>Discriminated union representing either a single dataset type or a mixed fan-out.</summary>
public abstract record DatasetTypeConfig
{
    /// <summary>A single named prompt builder.</summary>
    public sealed record Single(string Name) : DatasetTypeConfig;

    /// <summary>Fan-out across a list of named builders (all builders when list is empty).</summary>
    public sealed record Mixed(IReadOnlyList<string> BuilderNames) : DatasetTypeConfig;
}
```

`LlmStepOptions.DatasetType` changes type from `string` to `DatasetTypeConfig`, defaulting to `new DatasetTypeConfig.Single("explanation")`. Callers that set it as a plain string (YAML binding, CLI) go through a parse helper on `DatasetTypeConfig` that understands both `"explanation"` and `"mixed:explanation,unit-test"`.

### `PromptBuilderRegistry` (revised)

The static switch is replaced by a static list:

```csharp
public static class PromptBuilderRegistry
{
    public static IReadOnlyList<IPromptBuilder> All { get; } = new IPromptBuilder[]
    {
        new ExplanationPromptBuilder(),
        new CompletionPromptBuilder(),
        new BugFixPromptBuilder(),
        new UnitTestPromptBuilder(),
        new DocstringPromptBuilder(),
        new RefactorPromptBuilder(),
        new ArchitectureQaPromptBuilder(),
    };

    public static IPromptBuilder Resolve(string name) { ... }   // throws on unknown
    public static IReadOnlyList<IPromptBuilder> ResolveForMixed(
        IReadOnlyList<string> names) { ... }                     // empty → All
}
```

`Build(string, ExtractedSymbol)` is kept as a compat shim that delegates to `Resolve(name).Build(symbol)`.

### Concrete wrapper example — `UnitTestPromptBuilder`

Before (static class, unchanged):

```csharp
public static class UnitTestPromptBuilder
{
    public static PromptResult Build(ExtractedSymbol symbol) { ... }
}
```

After (static class kept; new instance wrapper added beneath it in the same file):

```csharp
/// <summary>
/// <see cref="IPromptBuilder"/> wrapper for <see cref="UnitTestPromptBuilder"/>.
/// Accepts methods and constructors only — unit tests for type declarations or enums
/// have no meaningful test target.
/// </summary>
internal sealed class UnitTestPromptBuilderInstance : IPromptBuilder
{
    private static readonly IReadOnlySet<string> Kinds =
        new HashSet<string>(StringComparer.Ordinal) { "method", "constructor" };

    /// <inheritdoc/>
    public string Name => "unit-test";

    /// <inheritdoc/>
    public IReadOnlySet<string> AcceptedKinds => Kinds;

    /// <inheritdoc/>
    public PromptResult Build(ExtractedSymbol symbol) =>
        UnitTestPromptBuilder.Build(symbol);
}
```

All seven wrapper classes follow the same pattern: `internal sealed`, delegate to the matching static, declare their `AcceptedKinds` as a `static readonly HashSet<string>` to avoid per-call allocation.

---

## 5. CLI / YAML surface

### CLI

```
distsharp generate --dataset-type <value>
```

`<value>` accepts:

| Value | Meaning |
|---|---|
| `explanation` (or any single builder name) | Existing behaviour — single builder, unchanged |
| `mixed` | Fan-out across all 7 builders with default kind filters |
| `mixed:explanation,unit-test,docstring` | Fan-out across the named subset only |

The colon-delimited subset syntax was chosen over a repeatable flag (`--builder explanation --builder unit-test`) because it maps cleanly to a single YAML scalar, stays consistent with how dataset-type appears in the dataset manifest, and avoids ambiguity when `--dataset-type` and `--builder` coexist.

Validation: unknown builder names in the subset list are an error at parse time (before any LLM calls), with a helpful message listing valid names.

### YAML (`distsharp.yaml`)

Full config block showing `dataset_type` in context with its siblings:

```yaml
pipeline:
  dataset_type: mixed                          # all 7 builders; use "mixed:a,b" for subset
  namespace_prefix: MyApp                      # existing — filter which namespaces to crawl
  exclude_test_projects: true                  # existing — skip test assemblies

  llm:
    provider: openai
    model: gpt-4o-mini
    temperature: 0.7
    max_tokens: 2048
    workers: 8
    max_rows: 5000                             # post-fanout row ceiling (see §6)
    drop_on_error: true

  export:
    format: jsonl
    output_dir: ./output
    hf_repo: myorg/my-dataset                  # optional
```

The `dataset_type` key accepts the same string syntax as the CLI. YAML binding goes through the same `DatasetTypeConfig.Parse(string)` helper. An unrecognised builder name in the subset list is a startup-time validation error (thrown before the pipeline starts, before any LLM calls are made).

### Dry-run output

When `--dry-run` is active and `mixed` is requested, the CLI prints a pre-flight table:

```
Builder          Matching symbols   Est. rows   Est. cost
explanation             3841            3841       $1.54
completion              2290            2290       $0.92
bug-fix                 2290            2290       $1.83
unit-test               2290            2290       $1.83
docstring               3841            3841       $0.77
refactor                1920            1920       $1.54
architecture-qa          872             872       $0.35
─────────────────────────────────────────────────────────
Total (no cap)         17344           17344       $8.78
Total (--max-rows 5000)                 5000       $2.53
```

Cost estimates use the same token-estimation heuristic already used for single-type dry-runs.

---

## 6. Cost model

### The problem

A 5 000-symbol solution with all 7 builders and no cap produces up to **35 000 LLM calls** (5 000 × 7), assuming every builder accepts every symbol. The default acceptance table in §3 reduces this: on a typical .NET solution, roughly 45 % of symbols are methods, 20 % properties, 15 % constructors, 20 % types/enums. Applying the acceptance table, the realistic multiplier is closer to 4.2× on average — approximately **21 000 calls** for 5 000 symbols. Still a large number.

### Cost-cap policy

`--max-rows N` is a **post-fanout** row cap. This is the correct mental model: users think of dataset rows, not input symbols. A user who says "give me 5 000 rows" expects 5 000 output rows whether running `explanation` or `mixed`.

When the cap binds:

1. **Compute each builder's uncapped quota**: `floor(N / active_builders)`, with remainder distributed one extra row to the first `N mod active_builders` builders (round-robin).
2. **Apply proportional sampling** within each builder's accepted symbol pool using stratified sampling (the existing `StratifiedSamplerStep` by kind). Each builder samples independently from its eligible symbols.
3. **Row ordering**: output rows are ordered by symbol (all builders for symbol A before symbol B), not by builder. This preserves the relationship between rows in the manifest's primary key `(symbol_fqn, dataset_type)`.

**Per-builder quota is a ceiling, not a target.** If a builder has fewer eligible symbols than its allocated quota (e.g. `architecture-qa` on a method-heavy codebase), total output rows may be below `--max-rows`. Unused quota is not redistributed in this design (see risk #6 in §8).

### Fully worked example — `--max-rows 5000`, all 7 builders

Assume a solution with 5 000 symbols distributed as follows (approximate, typical .NET service project):

| Kind | Count |
|---|---|
| method | 2 250 (45 %) |
| property | 1 000 (20 %) |
| constructor | 750 (15 %) |
| class | 500 (10 %) |
| interface | 250 (5 %) |
| record | 150 (3 %) |
| record-struct | 50 (1 %) |
| struct | 25 (0.5 %) |
| enum | 25 (0.5 %) |

Applying the acceptance table from §3 to compute each builder's eligible pool:

| Builder | Accepted kinds | Eligible symbols | Fair-share quota (5000 / 7 = 714, rem 2) | Quota actually used |
|---|---|---|---|---|
| `explanation` | all 9 | 5 000 | 715 | 715 |
| `completion` | method, property, constructor | 4 000 | 715 | 715 |
| `bug-fix` | method, constructor | 3 000 | 714 | 714 |
| `unit-test` | method, constructor | 3 000 | 714 | 714 |
| `docstring` | all 9 | 5 000 | 714 | 714 |
| `refactor` | method, constructor, class, struct, record, record-struct | 3 725 | 714 | 714 |
| `architecture-qa` | class, struct, record, record-struct, interface, enum | 1 000 | 714 | 714 |

Total output rows: 5 000. Total LLM calls: 5 000. All seven builders have enough eligible symbols to fill their quota.

Now consider the same run but on a solution with only 800 symbols total, all of which are methods (an extreme case):

| Builder | Eligible | Quota | Rows produced |
|---|---|---|---|
| `explanation` | 800 | 715 | 715 |
| `completion` | 800 | 715 | 715 |
| `bug-fix` | 800 | 714 | 714 |
| `unit-test` | 800 | 714 | 714 |
| `docstring` | 800 | 714 | 714 |
| `refactor` | 800 | 714 | 714 |
| `architecture-qa` | 0 | 714 | **0** |

Total output rows: **4 286**, not 5 000. `architecture-qa` produces zero rows because it accepts no `method` symbols. Unused quota is not redistributed. A warning is logged: "architecture-qa: 0 / 714 quota rows filled (no eligible symbols for active kinds)."

When `--max-rows` is not set, `mixed` runs uncapped. The dry-run pre-flight table (§5) makes the cost visible before the user commits.

**Rejected alternatives:**
- Per-builder hard cap (`--max-rows-per-builder`): more granular but violates the "rows = dataset rows" mental model. Can be added as a future advanced option.
- Single total cap applied after all rows are generated: wastes LLM calls — the cap should gate calls, not discard output.
- Redistributing unused quota to other builders: adds significant complexity (multi-pass allocation), creates uneven coverage between builders, and obscures why a builder has more rows than others. Logged warning is sufficient.

### Interaction with existing `--max-rows`

`--max-rows` currently applies to the `StratifiedSamplerStep` which runs *before* `LlmStep`. For `mixed`, this ordering changes: stratified sampling must happen *per builder* inside `LlmStep`'s mixed dispatch, not upstream. The upstream sampler still runs to cap the total symbol pool size; the per-builder fair-share allocation is a second sampling stage inside `LlmStep`.

Concretely: `--max-rows 5000` in `mixed` mode → upstream sampler is bypassed (or set to `max-rows / min_builder_multiplier` as a rough pre-filter) → per-builder quota enforced inside `LlmStep`.

This interaction is complex and warrants a dedicated implementation spike before committing to exact numbers. See §8, risk #3.

---

## 7. Effort

| Task | Days |
|---|---|
| Define `IPromptBuilder`, wrap 7 builders, update registry | 0.5 |
| `DatasetTypeConfig` + parse helper + YAML binding | 0.5 |
| `LlmStep` mixed worker loop + per-builder kind filter | 1.0 |
| Post-fanout `--max-rows` fair-share allocation | 1.0 |
| CLI mixed:subset parsing + validation + dry-run table | 0.5 |
| Unit tests (acceptance table, fan-out, cap) | 1.0 |
| Integration test (full pipeline, mixed, small fixture project) | 0.5 |
| `ExtractedSymbol.Kind` doc-comment fix | 0.25 |
| **Total** | **5.25 days** |

Dependencies: this work has no dependency on the ONNX provider spec or the dataset-sync spec, though it interacts with dataset-sync (see §8, risk #5).

---

## 8. Risks & open questions

1. **(BLOCKING) Row shape: one row per builder output vs. one row per symbol with N output columns.** This spec recommends N rows (one per builder per symbol) because it preserves schema homogeneity, works cleanly with the existing `Row` type (one `dataset_type` per row), and matches how single-type datasets look. The alternative — one wide row with `explanation_response`, `unit_test_response`, etc. — is incompatible with the existing export pipeline and would require schema changes across JSONL/Parquet/HF. Confirm before implementation.

2. **(BLOCKING) Per-run kind-acceptance override.** Should users be able to add or remove kinds for a builder in their YAML? For example, force `unit-test` to also accept `class` symbols? The default table (§3) is conservative; teams with non-standard codebases may need overrides. This spec does not implement overrides; `AcceptedKinds` is fixed in code. Decide whether overrides belong in this feature or a follow-up.

3. **(BLOCKING) Upstream sampler interaction.** The existing `StratifiedSamplerStep` runs before `LlmStep` and controls total symbol count. For `mixed`, the correct sampling point is per-builder inside `LlmStep`. These two mechanisms conflict when both are active. Define the authoritative precedence rule before `LlmStep` is changed.

4. Should `mixed` produce a single JSONL file with a `dataset_type` column, or split into per-builder files (e.g. `train_explanation.jsonl`, `train_unit_test.jsonl`)? Single file is simpler and keeps the manifest's primary key clean; split files are friendlier for consumers who only want one builder's rows. This design assumes single-file with a `dataset_type` discriminator column; split-file export can be a flag on the export step.

5. **Migration — existing stub `mixed` datasets.** The current `mixed` stub routes to `explanation` and writes rows with no `dataset_type` field. These rows are indistinguishable from real `explanation` rows. A re-run against the same solution would duplicate them. The dataset-sync spec (see `2026-05-18-dataset-sync-design.md`) uses `(symbol_fqn, dataset_type)` as the primary key — rows missing `dataset_type` will not key correctly. Mitigation: add a `_distsharp/manifest.json` marker field `"generated_by_stub_mixed": true` that the sync logic can detect and offer to re-generate. Out of scope for this spec; flag as a known debt item.

6. How should `--max-rows` interact with `mixed` if some builders have very few eligible symbols? For example, `architecture-qa` only accepts 5 symbol kinds and on a method-heavy codebase may have far fewer than its fair-share quota. Should its unused quota be redistributed to other builders, or held as slack? This spec allocates fixed quotas and does not redistribute. Redistribution is more complex and can be added when the use case is demonstrated.

---

## 9. Out of scope

- Per-run YAML override of `AcceptedKinds` for a specific builder.
- Split-file export (one file per builder) — belongs in the export step design.
- New prompt builder types — this design wires up the 7 existing builders only.
- Parallelising `mixed` as N independent `LlmStep` instances (fan-out topology). Possible future optimisation; the chosen single-step approach is correct first.
- Changing the acceptance table after initial implementation — the table in §3 is a proposal. A follow-up ADR can revise it based on empirical dataset quality.
- Removing the existing static builder methods. They remain as delegation targets and for backward compatibility with direct tests.
- Cost estimation improvements (e.g. per-model token pricing) — orthogonal to this design.
