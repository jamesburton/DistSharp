# DistSharp — Cross-Project Context on `ExtractedSymbol`

**Date:** 2026-05-18
**Status:** Design / planning. Not yet implemented.
**Scope:** Enriching `ExtractedSymbol` with cross-symbol context — called symbols, implemented interfaces, inheritance chain, and (optionally) callers — so prompt builders can reference real surrounding type names rather than relying on heuristics derived from body text alone.

---

## 1. Goals

Each context dimension listed below addresses a concrete deficiency in the current prompt builders:

| Context dimension | Problem it solves | Dataset types that benefit |
|---|---|---|
| **Called symbols** (direct callees) | Prompts for `UnitTestPromptBuilder` and `BugFixPromptBuilder` ask for mocks / buggy code but have no idea what dependencies a method uses — the model guesses from body text. Providing the FQNs of called methods lets the prompt explicitly enumerate `NSubstitute` setup calls and inject the right dependency names. | unit-test, bug-fix, completion |
| **Implemented interfaces** | `ArchitectureQaPromptBuilder` asks "what role does this type play" without knowing which contracts it fulfils. Providing interface FQNs allows the prompt to ground architectural questions in real contract names (`IOrderService`, `IDisposable`). | architecture-qa, explanation |
| **Inheritance chain** | Inherited behaviour is invisible in a leaf method's body. A method may rely on a base-class field or override a virtual with a specific semantic contract. Surfacing the chain enables prompts to note overridden behaviour and inheritance context. | explanation, refactor, architecture-qa |
| **Base-type members** (optional, phase 3) | The set of inherited virtual/abstract members a type can override. Useful for completion and unit-test prompts where stub generation is otherwise incomplete. | unit-test, completion |
| **Callers / called-by** (optional, phase 4) | Inverse of callees — who uses this method. Useful for impact-analysis prompts and refactor guidance. **Requires a two-pass or buffered strategy** — see §7. | refactor, explanation |

Collectively the goal is to shift prompt builders from relying on body text alone to reasoning with a lightweight semantic graph attached to each symbol.

---

## 2. Approach

### 2.1 Roslyn API surface

All dimensions are extractable from existing `ISymbol` subtypes. No additional MSBuild or NuGet operations are needed.

| Dimension | Roslyn API | Notes |
|---|---|---|
| Direct callees | Walk the body's `SyntaxNode`; call `semanticModel.GetSymbolInfo(invocation).Symbol` for each `InvocationExpressionSyntax`, `MemberAccessExpressionSyntax`, and `ObjectCreationExpressionSyntax`. | Yields `IMethodSymbol` or `IPropertySymbol`. |
| Implemented interfaces | `INamedTypeSymbol.Interfaces` (direct only) or `.AllInterfaces` (transitive). | `.Interfaces` is preferred for depth 1; `.AllInterfaces` adds `IDisposable` / `IEquatable<T>` noise. |
| Inheritance chain | Walk `INamedTypeSymbol.BaseType` up to `object`, collecting display strings. | Stop at `object` or when the chain exceeds the configured depth cap (default: 5). |
| Overridden member | `IMethodSymbol.OverriddenMethod`, `IPropertySymbol.OverriddenProperty`. | One hop — the immediate overridden symbol. |
| Base-type members (phase 3) | Enumerate `INamedTypeSymbol.GetMembers()` on each base type in the chain. | Memory-intensive; capped per base type (phase 3 only). |
| Callers (phase 4) | Requires `SymbolFinder.FindCallersAsync` across the full compilation. | Cannot run per-file; requires buffered or two-pass strategy — see §7. |

### 2.2 Transitive depth policy

Different dimensions warrant different depth limits:

| Dimension | Default depth | Rationale |
|---|---|---|
| Called symbols | 1 (direct) | Transitive call chains quickly reach framework internals. Direct callees give the model enough to name mocks/stubs. Opt-in transitive mode available via option flag. |
| Implemented interfaces | 1 (direct interfaces of the declaring type) | `AllInterfaces` introduces `IDisposable`, `IComparable<T>`, etc. on almost every type — high noise for architecture prompts. Explicit opt-in via `AllInterfaces = true`. |
| Inheritance chain | Full chain, capped at `MaxInheritanceDepth` (default: 5) | The full chain is the information; `object` is stripped; depth cap prevents runaway for deep hierarchies. |
| Overridden member | Always 1 hop | Immediate override is the semantically meaningful reference. |

### 2.3 Memory cost at depth 1 (50k-symbol solution)

Estimates based on a C# solution of average method density (each method calls ~5 dependencies; each type implements ~1 interface; average inheritance chain ~2 hops; average FQN length 80 characters):

| Dimension | Per-symbol cost | Total (50k symbols) |
|---|---|---|
| Called symbols (5 FQNs × 80 chars) | ~400 bytes | ~20 MB |
| Implemented interfaces (1 FQN × 80 chars) | ~80 bytes | ~4 MB |
| Inheritance chain (2 FQNs × 80 chars) | ~160 bytes | ~8 MB |
| Overridden member (1 FQN × 80 chars) | ~80 bytes | ~4 MB |
| **Total depth-1 enriched index** | **~720 bytes** | **~36 MB** |

At depth 2 (transitive callees), the callee fan-out multiplies by ~5×: ~100 MB for callees alone. At depth 3, ~500 MB — beyond a comfortable in-process budget for a CLI tool. The default is therefore **depth 1 for all dimensions**, with transitive callee expansion gated behind an opt-in flag.

"Called by" (inverse) is excluded from the memory model above because it is unbounded: a widely-used helper method (e.g. `ILogger.LogDebug`) may have thousands of callers across the solution, making per-symbol caller lists impractical to store inline. See §7, risk #3.

### 2.4 Storage: inline vs sidecar index

Two options were evaluated:

**Option A — Inline on `ExtractedSymbol`** (recommended): New `IReadOnlyList<string>` fields hold FQN lists. No lookup required at prompt-build time. At depth 1, the memory overhead (~36 MB for 50k symbols) is acceptable. The `Row` emitted per symbol already carries `symbol` as an `ExtractedSymbol`; prompt builders simply read the new fields.

**Option B — Sidecar index keyed by FQN**: A separate `Dictionary<string, SymbolContextEntry>` is built during extraction and injected into prompt builders. Avoids inflating `ExtractedSymbol` but requires every prompt builder to accept an additional `ISymbolContextIndex` parameter, complicates DI wiring, and the lookup overhead removes the "zero cost if not used" benefit that field-level nullability already provides inline.

**Decision:** Option A. At the per-dimension caps described above the memory cost is modest, prompt builders access context with no extra indirection, and serialisation to JSONL/Parquet is transparent (string lists serialize naturally).

---

## 3. Context Dimensions

### 3.1 Called symbols (direct callees)

**Definition.** The set of methods, properties, and constructors invoked inside the body of a `method`, `constructor`, or `property` setter/getter. Does not include type references that are not invoked (e.g. parameter type declarations).

**Extraction.** In `SymbolExtractor.BuildForMethod` (and analogous constructor / property builder), after the `ExtractedSymbol` is constructed, walk the body `SyntaxNode` for `InvocationExpressionSyntax` and `ObjectCreationExpressionSyntax` nodes. For each, call `semanticModel.GetSymbolInfo(node).Symbol`. Resolve the symbol's display string using the existing `FullNameFormat`. Deduplicate (a loop body may call the same method many times). Cap at `MaxCalleesPerSymbol` (default: 20) to bound output for complex methods.

For generic instantiations (e.g. `List<Order>.Add`), surface the **constructed method's FQN** (`List<Order>.Add(Order)`) rather than the open-generic form. The constructed form is more useful to a prompt that names real types. Opt-in to open-generic form via `UseOpenGenericCallees = true`.

**Serialisation on `ExtractedSymbol`.**

```csharp
/// <summary>Gets the FQNs of methods and constructors directly called by this symbol's body. Empty when not enriched.</summary>
public IReadOnlyList<string> CalledSymbols { get; init; } = Array.Empty<string>();
```

**Prompt-builder use.** `UnitTestPromptBuilder` can enumerate `CalledSymbols` to produce an explicit dependency list:

```
Dependencies called by this method:
- MyApp.Data.IOrderRepository.SaveAsync(Order, CancellationToken)
- MyApp.Services.IEmailService.SendConfirmationAsync(string)
```

This lets the model produce correct NSubstitute `Substitute.For<T>()` calls without guessing.

---

### 3.2 Implemented interfaces

**Definition.** The interfaces directly implemented by the containing type of a symbol. Applicable to `class`, `record`, `struct`, and `interface` symbols. For methods and properties, this is the interface list of their `ContainingType`.

**Extraction.** In `SymbolExtractor`, when processing a `ClassDeclarationSyntax`, `RecordDeclarationSyntax`, `StructDeclarationSyntax`, or `InterfaceDeclarationSyntax`, call `INamedTypeSymbol.Interfaces` on the resolved symbol. Collect FQN display strings. For methods and properties, the same list is derived from `symbol.ContainingType.Interfaces`. Cap at `MaxInterfacesPerType` (default: 10).

When `IncludeAllInterfaces = true`, use `.AllInterfaces` instead of `.Interfaces` to include transitively inherited interfaces. Default is `false` to keep noise low.

**Serialisation on `ExtractedSymbol`.**

```csharp
/// <summary>Gets the FQNs of interfaces directly implemented by the containing type. Empty when not enriched.</summary>
public IReadOnlyList<string> ImplementedInterfaces { get; init; } = Array.Empty<string>();
```

**Prompt-builder use.** `ArchitectureQaPromptBuilder` can qualify its system prompt:

```
This type implements: IOrderService, IDisposable
```

`ExplanationPromptBuilder` can note the contract satisfied by the symbol's containing type.

---

### 3.3 Inheritance chain

**Definition.** The sequence of fully qualified base type names from the immediate parent up to (but not including) `object`, following `BaseType` links. For structs, the chain is always empty (structs are sealed and cannot have user-specified base types). For interfaces, the chain is the list of extended interfaces.

**Extraction.** In `SymbolExtractor`, for type symbols, walk `INamedTypeSymbol.BaseType` iteratively. Skip `System.Object`. Collect FQN display strings in order (nearest ancestor first). Stop when `BaseType` is null or display string is `System.Object`, or when the chain length exceeds `MaxInheritanceDepth` (default: 5). For interface symbols, walk `INamedTypeSymbol.Interfaces` recursively.

For method and property symbols, the chain of the `ContainingType` is used.

**Serialisation on `ExtractedSymbol`.**

```csharp
/// <summary>Gets the inheritance chain of the containing type, nearest ancestor first, excluding System.Object. Empty when not enriched.</summary>
public IReadOnlyList<string> InheritanceChain { get; init; } = Array.Empty<string>();
```

**Prompt-builder use.** `RefactorPromptBuilder` can note `"This method overrides behaviour defined in {InheritanceChain[0]}"`. `ExplanationPromptBuilder` can note whether a method is overriding inherited logic.

---

### 3.4 Overridden member

**Definition.** The FQN of the immediately overridden method or property, if any. `null` when not an override. Applicable to `method` and `property` kinds only.

**Extraction.** After `semanticModel.GetDeclaredSymbol` resolves an `IMethodSymbol`, check `symbol.OverriddenMethod` (or `IPropertySymbol.OverriddenProperty`). If non-null, call `.ToDisplayString(FullNameFormat)`.

**Serialisation on `ExtractedSymbol`.**

```csharp
/// <summary>Gets the FQN of the immediately overridden method or property, or <see langword="null"/> if not an override.</summary>
public string? OverriddenMember { get; init; }
```

**Prompt-builder use.** Signals to explanation and refactor prompts that the method has a semantic predecessor. Example addition to `ExplanationPromptBuilder`:

```
This method overrides: MyApp.Base.BaseService.ProcessAsync(Order, CancellationToken)
```

---

### 3.5 Base-type members (phase 3, optional)

**Definition.** The set of virtual and abstract members declared on each type in the inheritance chain. Useful when generating stub completions or unit tests for abstract base classes.

**Extraction.** For each type in `InheritanceChain`, resolve the `INamedTypeSymbol` via `semanticModel.Compilation.GetTypeByMetadataName`. Call `.GetMembers()` and filter to `IsVirtual || IsAbstract`. Cap at `MaxBaseMembersPerType` (default: 15).

This is deferred to phase 3 because it requires a per-compilation type lookup pass that adds non-trivial extraction time and memory.

**Serialisation.** Not defined here — addressed in the phase 3 design.

---

### 3.6 Callers / called-by (phase 4, optional)

**Definition.** The set of methods that directly call this symbol. Inverse of §3.1.

The key problem: `SymbolFinder.FindCallersAsync` requires a fully-built compilation across all projects and cannot be called per-file in a streaming walk. Emitting callers inline on `ExtractedSymbol` during the streaming `IAsyncEnumerable` pass would require either a two-pass strategy (build a full index first, then stream enriched symbols) or buffering the entire symbol set in memory. Both are significant divergences from the current streaming architecture.

This dimension is therefore deferred to phase 4 with a design decision required on pass strategy before implementation begins. See §7, risk #3.

---

## 4. Components & File Layout

### 4.1 `ExtractedSymbol` (`DistSharp.Core/Models/ExtractedSymbol.cs`)

Add four new optional fields — all default to empty / null so existing code continues to compile without changes:

```csharp
/// <summary>Gets the FQNs of methods and constructors directly called by this symbol's body. Empty when not enriched.</summary>
public IReadOnlyList<string> CalledSymbols { get; init; } = Array.Empty<string>();

/// <summary>Gets the FQNs of interfaces directly implemented by the containing type. Empty when not enriched.</summary>
public IReadOnlyList<string> ImplementedInterfaces { get; init; } = Array.Empty<string>();

/// <summary>Gets the inheritance chain of the containing type, nearest ancestor first, excluding System.Object. Empty when not enriched.</summary>
public IReadOnlyList<string> InheritanceChain { get; init; } = Array.Empty<string>();

/// <summary>Gets the FQN of the immediately overridden method or property, or <see langword="null"/> if not an override.</summary>
public string? OverriddenMember { get; init; }
```

No existing field changes. No FQN format changes (the `FullNameFormat` `SymbolDisplayFormat` remains identical). Fully additive — the dataset-sync spec's row identity key `(symbol_fqn, dataset_type)` is unaffected.

### 4.2 `SolutionAnalysisOptions` (`DistSharp.Core/Models/SolutionAnalysisOptions.cs`)

Add enrichment options. Kept in `DistSharp.Core` so they are bindable from YAML config without a Roslyn dependency:

```csharp
/// <summary>Gets or sets a value indicating whether cross-project context dimensions are populated on each symbol. Default: <see langword="false"/>.</summary>
public bool EnrichContext { get; set; }

/// <summary>Gets or sets the maximum number of direct callees recorded per symbol. Default: 20.</summary>
public int MaxCalleesPerSymbol { get; set; } = 20;

/// <summary>Gets or sets the maximum inheritance depth to walk. Default: 5.</summary>
public int MaxInheritanceDepth { get; set; } = 5;

/// <summary>Gets or sets a value indicating whether all transitively implemented interfaces are included rather than only direct interfaces. Default: <see langword="false"/>.</summary>
public bool IncludeAllInterfaces { get; set; }

/// <summary>Gets or sets a value indicating whether callee FQNs use the constructed generic form (e.g. <c>List&lt;Order&gt;.Add</c>) rather than the open-generic form. Default: <see langword="true"/>.</summary>
public bool UseConstructedGenericCallees { get; set; } = true;
```

All new options default to values that produce no change in behaviour when `EnrichContext = false`.

### 4.3 `RoslynSymbolExtractorOptions` (`DistSharp.Roslyn/Steps/RoslynSymbolExtractorOptions.cs`)

Mirror the new `SolutionAnalysisOptions` fields as step-level overrides so they can be set per-step in YAML:

```csharp
/// <summary>Gets or sets a value indicating whether cross-project context is populated. Default: <see langword="false"/>.</summary>
public bool EnrichContext { get; set; }

/// <summary>Gets or sets the maximum direct callees per symbol. Default: 20.</summary>
public int MaxCalleesPerSymbol { get; set; } = 20;

/// <summary>Gets or sets the maximum inheritance depth. Default: 5.</summary>
public int MaxInheritanceDepth { get; set; } = 5;

/// <summary>Gets or sets a value indicating whether all interfaces (not just direct) are included. Default: <see langword="false"/>.</summary>
public bool IncludeAllInterfaces { get; set; }

/// <summary>Gets or sets a value indicating whether constructed generic callees are used. Default: <see langword="true"/>.</summary>
public bool UseConstructedGenericCallees { get; set; } = true;
```

`RoslynSymbolExtractorStep.ExecuteAsync` forwards these into `SolutionAnalysisOptions`.

### 4.4 `SymbolExtractor` (`DistSharp.Roslyn/Internal/SymbolExtractor.cs`)

The core extraction work lives here. No new classes are required; the four existing `BuildFor*` methods each gain an optional enrichment path gated on `options.EnrichContext`.

New private helpers to add:

```
ExtractCallees(SyntaxNode body, int maxCount) → IReadOnlyList<string>
ExtractImplementedInterfaces(INamedTypeSymbol type, bool allInterfaces, int maxCount) → IReadOnlyList<string>
ExtractInheritanceChain(INamedTypeSymbol type, int maxDepth) → IReadOnlyList<string>
ExtractOverriddenMember(ISymbol symbol) → string?
```

`SymbolExtractor` already receives `semanticModel` in its constructor — all of these helpers operate only on the semantic model and the symbol resolved at emit time. No additional constructor parameters are required.

### 4.5 `RoslynSolutionAnalyzer` (`DistSharp.Roslyn/RoslynSolutionAnalyzer.cs`)

Minor change: the `SolutionAnalysisOptions` passed into `SymbolExtractor` must carry the enrichment options. Currently the extractor receives only the `namespaceIncluded` predicate. Options are passed through `SymbolExtractorOptions` (a new small struct or by extending the constructor). The analyzer's streaming architecture is unchanged — enrichment happens at emit time, inside the existing per-tree loop.

### 4.6 Prompt builders (`DistSharp.Core/Prompts/`)

No breaking changes. Each builder currently accepts `ExtractedSymbol` directly. Builders that benefit from context can read the new fields with `symbol.CalledSymbols.Count > 0` guards so they degrade gracefully when enrichment is disabled.

Builders to update in phase 1 (highest impact):

| Builder | Context to use |
|---|---|
| `UnitTestPromptBuilder` | `CalledSymbols` — enumerate as mock setup hints |
| `ExplanationPromptBuilder` | `ImplementedInterfaces`, `InheritanceChain`, `OverriddenMember` |
| `ArchitectureQaPromptBuilder` | `ImplementedInterfaces`, `InheritanceChain` |
| `RefactorPromptBuilder` | `OverriddenMember`, `InheritanceChain` |

Builders `BugFixPromptBuilder`, `CompletionPromptBuilder`, `DocstringPromptBuilder` gain lower value from context and are updated in phase 2.

---

## 5. Memory / Performance

### 5.1 Memory model (depth 1, inline storage)

| Scenario | Symbols | Estimated enriched index size |
|---|---|---|
| Small solution | 5,000 | ~3.6 MB |
| Medium solution | 20,000 | ~14.4 MB |
| Large solution | 50,000 | ~36 MB |
| Extra-large solution | 150,000 | ~108 MB |

Basis: ~720 bytes per symbol (§2.3). The index resides only during the active extraction pass — symbols are streamed out and the `Row` channel handles backpressure. The full set of enriched symbols is **not** simultaneously resident unless the downstream step buffers them (e.g. `StratifiedSamplerStep` which buffers by design).

`StratifiedSamplerStep` currently buffers all symbols to compute per-namespace strata. At 50k symbols × 720 bytes = ~36 MB additional memory during sampling; at 150k symbols this reaches ~108 MB. This is within normal working set expectations for a CLI tool on a developer machine but should be noted in the `StratifiedSamplerStep` documentation.

### 5.2 Transitive callees (opt-in)

When `EnrichContext = true` and transitive callees are requested (depth 2), the callee list grows by ~5× on average (each callee also has ~5 callees). For a 50k-symbol solution this is ~100 MB in callee strings alone. Transitive mode is therefore opt-in and capped. The default max-callees cap (20 per symbol) applies to the merged transitive set, not per-hop, to maintain the bound.

### 5.3 Extraction time overhead

Baseline `distsharp inspect` on a 50k-symbol solution: not yet benchmarked (see §7, open question #6). The enrichment pass adds:

- Per method body: a `DescendantNodes()` walk to collect invocations. Cost is proportional to body size; typically O(body lines). For the 99th-percentile method (~200 nodes), this is ~0.1 ms — dominated by existing complexity calculation which already walks the same tree.
- Per type: two calls (`INamedTypeSymbol.Interfaces`, `BaseType` chain). These are in-memory property reads on an already-constructed symbol — effectively free.

Expected total extraction overhead with enrichment: **< 5% additional wall time** at depth 1. Transitive callee expansion adds another ~10–20% depending on solution fan-out. These are estimates; benchmarking against the actual solution should be done as part of phase 2 acceptance.

### 5.4 Serialisation cost

JSONL output: string lists serialise as JSON arrays of strings — no custom serialiser needed. Parquet output: list columns are supported by the existing Parquet.Net dependency. CSV output: list fields would be joined as semicolon-delimited strings. No changes to `IDatasetWriter` implementations are required beyond the Parquet schema update.

---

## 6. Effort

Phased delivery against an estimated working day budget:

| Phase | Deliverable | Estimate |
|---|---|---|
| **Phase 1** | `ExtractedSymbol` new fields; `SolutionAnalysisOptions` / `RoslynSymbolExtractorOptions` enrichment options; `SymbolExtractor` extraction helpers for all four depth-1 dimensions; unit tests for each helper; `UnitTestPromptBuilder` and `ExplanationPromptBuilder` updated to consume context | **2 days** |
| **Phase 2** | `ArchitectureQaPromptBuilder`, `RefactorPromptBuilder` updated; remaining prompt builder updates (`BugFixPromptBuilder`, `DocstringPromptBuilder`); YAML config wiring (`distsharp.yaml` `enrich_context` key under `solution:`); integration test against a small fixture solution verifying callee / interface / chain extraction; benchmarks against full solution | **1.5 days** |
| **Phase 3** | Base-type members (`virtual`/`abstract` member list per ancestor); `CompletionPromptBuilder` update; Parquet schema update for list columns | **1 day** |
| **Phase 4** | Caller / called-by dimension; pass-strategy decision (two-pass or buffered); `ISolutionAnalyzer` contract revision if needed; prompt builder(s) using caller context | **2–3 days** (depends on pass-strategy decision) |
| **Total** | | **6.5–7.5 days** |

Phases 1 and 2 are independent of phases 3 and 4. Phase 4 may require a non-trivial API change to `ISolutionAnalyzer` if a two-pass strategy is chosen.

---

## 7. Risks & Open Questions

1. **(BLOCKING) Streaming vs two-pass for caller dimension.** The current `ISolutionAnalyzer` is a forward-streaming `IAsyncEnumerable`. The caller ("called-by") dimension requires `SymbolFinder.FindCallersAsync` across all compilations, which cannot run per-file. Three strategies are available: (a) drop "called-by" entirely in phases 1–3; (b) add a separate `BuildCallerIndexAsync` method to `ISolutionAnalyzer` that must be awaited before streaming, changing the call site contract; (c) buffer the full symbol list in `RoslynSolutionAnalyzer` before yielding, abandoning streaming. The recommended decision is (a) for phases 1–3, with option (b) designed in phase 4 — this keeps the streaming contract intact and defers the complexity to a focused phase. **Decision required before phase 4 begins.**

2. **(BLOCKING) Generic callee representation.** When a method body contains `_list.Add(item)` where `_list` is `List<Order>`, should `CalledSymbols` record `System.Collections.Generic.List<Order>.Add(Order)` (constructed) or `System.Collections.Generic.List<T>.Add(T)` (open generic)? The constructed form is more useful for named-type prompts; the open-generic form is more stable across codebase changes. Default proposed: constructed form, opt-in to open-generic via `UseConstructedGenericCallees = false`. **Decision should be confirmed before phase 1 ship.**

3. **"Called by" is unbounded for popular symbols.** A method like `Guard.NotNull` or `ILogger.LogDebug` may be called in thousands of places. Storing callers inline without a hard cap would make specific symbols arbitrarily large. If phase 4 is implemented, a strict per-symbol caller cap (e.g. `MaxCallersPerSymbol = 50`) is required, with excess silently truncated. This changes the semantic completeness of the field and should be documented in the generated schema.

4. **Cross-project boundary for third-party callees.** A call to `Newtonsoft.Json.JsonConvert.SerializeObject` resolves to a symbol whose FQN is fully qualified but whose source is not in the solution. These callees are still useful (the model knows `Newtonsoft.Json`) but may add noise if a user only wants in-solution dependency names. Mitigation: add a `CalleeScope` option with values `solution-only | all` (default: `all`). If `solution-only`, filter by checking whether the callee's containing assembly is one of the loaded compilation assemblies.

5. **`SymbolExtractor` constructor signature change.** Currently `SymbolExtractor` takes `semanticModel`, `relativeFilePath`, and `namespaceIncluded`. Adding enrichment options requires either a new constructor parameter (breaking internal API) or a separate `SymbolExtractorOptions` value object. The value-object approach is cleaner and avoids a long parameter list. This is an internal type so the change has no external impact.

6. **Baseline `inspect` performance is unmeasured.** The spec's extraction overhead estimate (< 5%) is based on reasoning about Roslyn operation complexity, not measured data. Before phase 2 ships, a benchmark run against a reference solution (e.g. DistSharp itself) should establish the baseline and the enriched delta. If overhead exceeds 15%, the callee walk may need to be lazily deferred to a separate enrichment step rather than inline in `SymbolExtractor`.

7. **Dataset-sync compatibility.** The 2026-05-18-dataset-sync spec keys rows by `(symbol_fqn, dataset_type)`. The new fields are purely additive and do not change `FullyQualifiedName`. A row from an old run (without enrichment) and a new run (with enrichment) will have the same key but different column presence in JSONL/Parquet. The merge/sync logic must treat missing context columns as empty, not as conflicts — this should be documented in the dataset-sync spec when enrichment fields are added.

8. **Parquet list columns require schema update.** `ParquetDatasetWriter` currently writes scalar string columns. Adding `IReadOnlyList<string>` fields requires list-typed Parquet columns (or a semicolon-joined scalar fallback). The Parquet.Net library supports list columns; this needs a deliberate schema change, not an automatic one, to avoid corrupting existing dataset files that downstream tools may have schema-sniffed.

---

## 8. Out of Scope

- **Symbol cross-references beyond the current solution.** Enrichment applies only to symbols resolvable within the loaded MSBuild workspace. External framework types (BCL, NuGet packages) are referenced by FQN in callee lists but their bodies and metadata are not extracted.
- **Dynamic dispatch.** Callee extraction captures statically-resolved method calls. Calls through `dynamic`, reflection, or `Action<T>`/`Func<T>` delegates are not captured — doing so would require data-flow analysis beyond the Roslyn symbol model.
- **Dataflow / taint analysis.** Understanding which callee's return value flows to which argument is not in scope. Only the set of callees (not the data-flow graph) is captured.
- **UI surface for context.** The `distsharp inspect` command displays per-symbol metadata; extending it to show context fields is a separate CLI change and is not part of this spec.
- **Provider / LLM changes.** Context enrichment is a data-layer change only. No changes to `ILlmProvider`, `LlmStep`, or any provider implementation are required.
- **Hugging Face Hub schema changes.** The enrichment fields appear in generated datasets as additional columns. Schema registration, dataset cards, and Hub upload format changes are the responsibility of the export subsystem and are not covered here.
- **Semantic deduplication of callees across overloads.** If a method calls `Save(Order)` and `Save(Order, CancellationToken)`, both appear in `CalledSymbols` as distinct FQNs. Merging overloads to a single base name is not in scope.
