# DistSharp — Phase 5: Step Library + Dataset Writers

**Date:** 2026-05-15
**Scope:** `DistSharp.Core` (existing project) — dataset writers, generation steps, judges, deduplication, and prompt templates for all 7 dataset types.

---

## 1. Goals

1. **Dataset writers** — `JsonlDatasetWriter`, `CsvDatasetWriter`, `ParquetDatasetWriter` + `DatasetWriterFactory`
2. **`LlmStep`** — calls a provider for each input row using N parallel workers, writes result to `response` field
3. **`LlmJudge`** — scores each row 1–5 using an LLM, filters by `min_score`
4. **`MinHashDeduplicator`** — drops rows with text similar to already-seen rows
5. **Prompt builders** — one per dataset type (`explanation`, `completion`, `bug-fix`, `unit-test`, `docstring`, `refactor`, `architecture-qa`) that builds `ChatMessage[]` from a `Row`
6. **DI extension** — `AddDistSharpStepLibrary` registers writers and step types

---

## 2. Dataset writers

### `JsonlDatasetWriter`

- Writes one `{...}` JSON object per line to a `.jsonl` file inside `output.Dir`
- Filename: `<pipeline-name>-<timestamp>.jsonl`, where timestamp is `yyyyMMdd-HHmmss`
- `WriteAsync(row, ct)` serializes the `Row.Fields` dictionary (excluding fields starting with `_` which are pipeline internal)
- Special case: a `symbol` field of type `ExtractedSymbol` is serialized as a nested object
- Special case: if `OutputConfig.WriteMetadata = false`, only `instruction`, `input`, `output`, `prompt`, `completion`, `response`, `question`, `answer`, `context`, `code`, `buggy_code`, `fixed_code`, `original_code`, `refactored_code`, `explanation` fields are kept (the dataset-type fields); everything else is dropped
- `FlushAsync` flushes the underlying `StreamWriter`
- `DisposeAsync` flushes and closes

### `CsvDatasetWriter`

- Writes a CSV file with header
- Columns are determined lazily from the first row's fields
- Subsequent rows that have additional fields cause those columns to be added... actually no — keep it simple: header is fixed from the first row, missing fields become empty cells, extra fields are ignored
- Quote values containing `,`, `"`, `\r`, `\n` per RFC 4180
- Complex values (dictionaries, ExtractedSymbol) are JSON-serialized into a single cell

### `ParquetDatasetWriter`

- Uses `Parquet.Net` library
- Buffers rows in memory then flushes a row group every N rows (default 1000)
- Schema inferred from first row; subsequent rows must match (extras dropped, missing → null)
- `WriteAsync` adds to buffer, calls `FlushRowGroup` when buffer reaches threshold
- `FlushAsync` writes any remaining buffered rows as a final row group
- `DisposeAsync` finishes the file

### `DatasetWriterFactory : IDatasetWriterFactory`

```csharp
public IDatasetWriter Create(OutputConfig config)
{
    Directory.CreateDirectory(config.Dir);
    var fileBase = $"distsharp-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";

    return config.Format.ToLowerInvariant() switch
    {
        "jsonl" => new JsonlDatasetWriter(Path.Combine(config.Dir, fileBase + ".jsonl"), config.WriteMetadata),
        "csv" => new CsvDatasetWriter(Path.Combine(config.Dir, fileBase + ".csv"), config.WriteMetadata),
        "parquet" => new ParquetDatasetWriter(Path.Combine(config.Dir, fileBase + ".parquet"), config.WriteMetadata),
        _ => throw new InvalidOperationException($"Unsupported output format: {config.Format}"),
    };
}
```

---

## 3. Prompt builders

Each dataset type has its own prompt-building function. All take an `ExtractedSymbol` and produce `ChatMessage[]`.

Pattern: `static class {Type}PromptBuilder { static ChatMessage[] Build(ExtractedSymbol symbol); }` — system message sets persona, user message asks the question with context.

### Common system message persona

> "You are a senior .NET engineer with deep expertise in C# and the .NET ecosystem. You explain code precisely, reference type names, and call out important edge cases."

(Each builder may customise this.)

### `ExplanationPromptBuilder`

- **System:** "You are a senior .NET engineer explaining code to a capable colleague. Be precise, reference type names, and mention important edge cases."
- **User:** "Explain what the `{Kind}` `{FullyQualifiedName}` does. Provide your answer in 2–4 sentences focused on intent and behaviour, not line-by-line.\n\nCode:\n```csharp\n{BodyText}\n```"
- Response field: `response`

### `CompletionPromptBuilder`

- **System:** "You are a senior C# developer. Complete the partial method below following the patterns visible in the codebase. Output only the completion — no explanation, no surrounding code."
- **User:** "Complete the following C# method body:\n\n```csharp\n{first 50% of BodyText}\n```"
- Stored fields: `prompt` (the first half), `completion` (will be filled by LlmStep response)
- Note: The "first 50%" is computed by splitting `BodyText` at the midpoint between opening `{` and end (rough heuristic; details below in implementation)

### `BugFixPromptBuilder`

- **System:** "You are a senior C# developer. Introduce one subtle, realistic bug into the method below, then provide the corrected version and a one-sentence explanation of the bug. Output JSON with keys: `buggy_code`, `fixed_code`, `explanation`."
- **User:** "Original method:\n\n```csharp\n{BodyText}\n```"
- The LLM response will be parsed as JSON. If parsing fails, the row is dropped.
- Stored fields: `instruction` = "The following C# method contains a bug. Identify and fix it.", `buggy_code`, `fixed_code`, `explanation`

### `UnitTestPromptBuilder`

- **System:** "You are a senior C# developer. Write a single xUnit unit test for the method below using NSubstitute for mocking dependencies. Focus on one specific behaviour or edge case."
- **User:** "Write a unit test for `{FullyQualifiedName}`:\n\n```csharp\n{SignatureText}\n```\n\nFull method body for context:\n```csharp\n{BodyText}\n```"
- Stored fields: `instruction` = "Write an xUnit unit test for `{name}` covering an interesting case.", `context` = signature, `response` = LLM output

### `DocstringPromptBuilder`

- **System:** "You are a senior .NET engineer writing XML documentation comments. Output only valid `///` doc comments — no surrounding code, no markdown fences."
- **User:** "Write XML documentation comments for:\n\n```csharp\n{SignatureText}\n```"
- Stored fields: `instruction` = "Write XML documentation comments for the following C# method.", `code` = signature, `response` = LLM output

### `RefactorPromptBuilder`

- **System:** "You are a senior C# developer refactoring complex code. Improve readability and reduce cyclomatic complexity without changing behaviour. Output JSON with keys: `refactored_code`, `explanation`."
- **User:** "Refactor this method (current complexity: {Complexity}):\n\n```csharp\n{BodyText}\n```"
- LLM response parsed as JSON. Failures drop the row.
- Stored fields: `instruction` = "Refactor the following C# method to improve readability and reduce cyclomatic complexity.", `original_code` = BodyText, `refactored_code`, `explanation`

### `ArchitectureQaPromptBuilder`

- **System:** "You are a senior .NET architect explaining how a codebase fits together. Answer questions about design patterns, composition, and structure with grounded references to the actual code."
- **User:** "Given this type:\n\n```csharp\n{BodyText}\n```\n\nGenerate one architectural question and its answer about this type's role in the codebase. Output JSON with keys: `question`, `answer`."
- LLM response parsed as JSON. Failures drop the row.
- Stored fields: `question`, `answer`

### `PromptBuilderRegistry`

```csharp
public static class PromptBuilderRegistry
{
    public static ChatMessage[] Build(string datasetType, ExtractedSymbol symbol);
    public static IReadOnlyDictionary<string, object?> PreparedFields(string datasetType, ExtractedSymbol symbol);
    public static Action<IDictionary<string, object?>, string> StoreResponse(string datasetType);
}
```

- `Build(datasetType, symbol)` returns the messages to send
- `PreparedFields` returns any fields that should be set on the row before the LLM is called (e.g., `prompt`, `instruction`, etc.)
- `StoreResponse(datasetType)` returns a delegate that knows how to parse the LLM response and store the resulting fields on the row (handles plain text vs JSON-formatted responses)

---

## 4. `LlmStep`

```csharp
public sealed class LlmStep : IStep
{
    public LlmStep(string name, ILlmProvider provider, LlmStepOptions options, ILogger<LlmStep> logger);
    // Reads input rows; for each row:
    //   1. Calls PromptBuilderRegistry.Build(options.DatasetType, row.Get<ExtractedSymbol>("symbol"))
    //   2. Calls provider.CompleteAsync(messages, requestOpts, ct)
    //   3. Applies StoreResponse to merge response fields into the row
    //   4. Writes the row to output (or drops if storage failed, e.g. invalid JSON)
    // Runs `Workers` such loops concurrently against the same input/output channels.
}

public sealed class LlmStepOptions
{
    public string DatasetType { get; set; } = "explanation";
    public string? Model { get; set; }
    public float Temperature { get; set; } = 0.7f;
    public int? MaxTokens { get; set; }
    public int Workers { get; set; } = 4;
    public string? SystemPromptOverride { get; set; }
    public bool DropOnError { get; set; } = true;  // if false, error rows pass through with `error` field set
}
```

Behaviour:
- `Workers` tasks are started inside `ExecuteAsync`; they all read from the same `ChannelReader<Row>` and write to the same `ChannelWriter<Row>`
- Use `Task.WhenAll` over the worker tasks before returning
- Per-row exceptions are logged; if `DropOnError = true`, the row is silently dropped; otherwise the row passes through with field `error = exception.Message`
- `OperationCanceledException` always re-thrown

## 5. `LlmJudge`

```csharp
public sealed class LlmJudge : IStep
{
    public LlmJudge(string name, ILlmProvider provider, LlmJudgeOptions options, ILogger<LlmJudge> logger);
}

public sealed class LlmJudgeOptions
{
    public string? Model { get; set; }
    public float Temperature { get; set; } = 0.0f;
    public int Workers { get; set; } = 2;
    public float MinScore { get; set; } = 3.0f;
    public string Rubric { get; set; } = "helpfulness_and_correctness";
    public string? PromptOverride { get; set; }
}
```

System prompt (built-in rubrics):
- `helpfulness_and_correctness`: rates 1–5 on whether the output is helpful and correct for the question
- `code_quality`: rates 1–5 on idiomatic C#, correctness, style

User prompt: shows the row's `instruction`/`prompt` and the response/output, asks for `{"score": 1-5, "reason": "..."}`.

Behaviour:
- For each row: build judge prompt from row fields (instruction + response), call LLM, parse score
- If `score >= MinScore`, attach `judge_score` and `judge_reason` to row and emit
- Else: drop the row (or log at debug level)

## 6. `MinHashDeduplicator`

```csharp
public sealed class MinHashDeduplicator : IStep
{
    public MinHashDeduplicator(string name, MinHashOptions options);
}

public sealed class MinHashOptions
{
    public string Field { get; set; } = "response";
    public double Threshold { get; set; } = 0.85;
    public int NumHashes { get; set; } = 128;
    public int ShingleSize { get; set; } = 3;  // word n-grams
    public int Seed { get; set; } = 42;
}
```

Algorithm:
- For each row, take `row.Get<string>(Field)` text
- Generate word shingles (token n-grams of size `ShingleSize`)
- Hash each shingle with `NumHashes` independent hash functions; take min of each → signature vector of length `NumHashes`
- Compute Jaccard estimate against all previously-seen signatures by counting equal indices / NumHashes
- If max similarity ≥ Threshold, drop the row; else add signature to set and emit

For speed at the scale we care about (~50–100k rows), keep all signatures in memory. (LSH banding can be added later for larger scale.)

Hash functions: use 32-bit MurmurHash3 with `NumHashes` seeds derived from `Seed`.

## 7. DI extension

```csharp
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDistSharpStepLibrary(this IServiceCollection services)
    {
        services.AddSingleton<IDatasetWriterFactory, DatasetWriterFactory>();
        // Steps are typically constructed by the CLI based on config — they don't go in DI directly,
        // but their helpers do.
        return services;
    }
}
```

---

## 8. Files to create

```
src/DistSharp.Core/
├── Writers/
│   ├── JsonlDatasetWriter.cs
│   ├── CsvDatasetWriter.cs
│   ├── ParquetDatasetWriter.cs
│   ├── DatasetWriterFactory.cs
│   └── RowSerializer.cs                (shared "row → dictionary" with metadata filtering)
├── Steps/
│   ├── LlmStep.cs
│   ├── LlmStepOptions.cs
│   ├── LlmJudge.cs
│   ├── LlmJudgeOptions.cs
│   ├── MinHashDeduplicator.cs
│   ├── MinHashOptions.cs
│   └── Internal/
│       ├── MinHashSignature.cs
│       └── MurmurHash3.cs
├── Prompts/
│   ├── PromptBuilderRegistry.cs
│   ├── PromptResult.cs                 (return type of builders)
│   ├── ExplanationPromptBuilder.cs
│   ├── CompletionPromptBuilder.cs
│   ├── BugFixPromptBuilder.cs
│   ├── UnitTestPromptBuilder.cs
│   ├── DocstringPromptBuilder.cs
│   ├── RefactorPromptBuilder.cs
│   └── ArchitectureQaPromptBuilder.cs
└── ServiceCollectionExtensions.cs

tests/DistSharp.Core.Tests/
├── Writers/
│   ├── JsonlDatasetWriterTests.cs
│   ├── CsvDatasetWriterTests.cs
│   ├── ParquetDatasetWriterTests.cs
│   └── DatasetWriterFactoryTests.cs
├── Steps/
│   ├── LlmStepTests.cs
│   ├── LlmJudgeTests.cs
│   └── MinHashDeduplicatorTests.cs
└── Prompts/
    └── PromptBuilderRegistryTests.cs
```

---

## 9. Packages

Add to `Directory.Packages.props`:
- `Parquet.Net` v5.0.2

Add to `src/DistSharp.Core/DistSharp.Core.csproj`:
- `PackageReference Include="Parquet.Net"`
