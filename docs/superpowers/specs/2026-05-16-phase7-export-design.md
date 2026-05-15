# DistSharp — Phase 7: Export + Hugging Face

**Date:** 2026-05-16
**Scope:** Real implementation of the `export` command — Alpaca and ShareGPT format converters, Hugging Face Hub REST upload, plus also addressing the YAML/Dictionary binding gap noted in Phase 6.

---

## 1. Goals

1. **Format converters** — read JSONL/CSV/Parquet datasets and convert to:
   - Same format passthrough (with metadata stripped or kept)
   - Alpaca format: `{"instruction": ..., "input": ..., "output": ...}`
   - ShareGPT format: `{"conversations": [{"from": "human", "value": ...}, {"from": "gpt", "value": ...}]}`
2. **Hugging Face Hub uploader** — REST client that:
   - Validates the repo (creates it if missing, given a token with `write` scope)
   - Uploads dataset files via the HF API's preupload + commit endpoints (or simpler: single-file uploads via the `/api/datasets/{repo}/upload/{revision}/{path}` PUT endpoint)
   - Reports progress via the same Spectre live display
3. **`ExportCommandHandler` real implementation** — wires the above two pieces
4. **YAML→Dictionary binding fix** — the Phase 6 concern with `PipelineRunCommandHandler` loading step config dictionaries
5. **`README.md` is treated as authoritative documentation** — the implementation matches the README usage exactly

---

## 2. Components

### Reader: `JsonlDatasetReader`

`src/DistSharp.Core/Export/JsonlDatasetReader.cs`:

```csharp
public sealed class JsonlDatasetReader
{
    public async IAsyncEnumerable<Dictionary<string, JsonElement>> ReadAsync(string filePath, [EnumeratorCancellation] CancellationToken ct);
}
```

Each line is parsed as a JSON object; rows that fail to parse are skipped with a debug log.

For Phase 7, only JSONL reading is implemented (Alpaca/ShareGPT conversion only operates from a DistSharp-produced JSONL dataset). Parquet and CSV reading can be added later — the export workflow assumes JSONL input.

### Converters: `AlpacaConverter`, `ShareGptConverter`

```csharp
public static class AlpacaConverter
{
    public static Dictionary<string, object?> Convert(Dictionary<string, JsonElement> row)
    {
        // Determine which fields to map based on what's present
        var instruction = GetString(row, "instruction") ?? string.Empty;
        var input = GetString(row, "input") ?? GetString(row, "context") ?? GetString(row, "code") ?? string.Empty;
        var output = GetString(row, "output") ?? GetString(row, "response") ?? GetString(row, "answer") ?? GetString(row, "completion") ?? string.Empty;

        // For bug-fix and refactor rows where output is structured, serialize the structured field as JSON
        if (string.IsNullOrEmpty(output) && row.ContainsKey("fixed_code"))
            output = $"{GetString(row, "fixed_code")}\n\n{GetString(row, "explanation")}";
        if (string.IsNullOrEmpty(output) && row.ContainsKey("refactored_code"))
            output = $"{GetString(row, "refactored_code")}\n\n{GetString(row, "explanation")}";

        return new Dictionary<string, object?>
        {
            ["instruction"] = instruction,
            ["input"] = input,
            ["output"] = output,
        };
    }
}

public static class ShareGptConverter
{
    public static Dictionary<string, object?> Convert(Dictionary<string, JsonElement> row)
    {
        var human = GetString(row, "instruction") ?? GetString(row, "question") ?? GetString(row, "prompt") ?? string.Empty;
        var input = GetString(row, "input") ?? GetString(row, "context") ?? GetString(row, "code");
        if (!string.IsNullOrEmpty(input))
            human = $"{human}\n\n{input}";

        var gpt = GetString(row, "output") ?? GetString(row, "response") ?? GetString(row, "answer") ?? GetString(row, "completion") ?? string.Empty;

        if (string.IsNullOrEmpty(gpt) && row.ContainsKey("fixed_code"))
            gpt = $"{GetString(row, "fixed_code")}\n\n{GetString(row, "explanation")}";

        return new Dictionary<string, object?>
        {
            ["conversations"] = new[]
            {
                new Dictionary<string, object?> { ["from"] = "human", ["value"] = human },
                new Dictionary<string, object?> { ["from"] = "gpt", ["value"] = gpt },
            },
        };
    }
}
```

### `HuggingFaceClient`

`src/DistSharp.Core/Export/HuggingFaceClient.cs`:

```csharp
public sealed class HuggingFaceClient
{
    public HuggingFaceClient(HttpClient http, string apiToken);

    /// <summary>Creates the repository if it does not exist.</summary>
    public Task EnsureRepoAsync(string repoId, CancellationToken ct);

    /// <summary>Uploads a single file to the repo via the LFS / Git HTTPS upload endpoint.</summary>
    public Task UploadFileAsync(string repoId, string remotePath, string localPath, string commitMessage, CancellationToken ct);
}
```

HF Hub upload approach: use the simplest API — direct file upload via PUT to `/api/datasets/{repo}/upload/{branch}/{path}` with `Authorization: Bearer {token}` and the file body. This is the API used by `huggingface_hub` Python's `upload_file` helper internally for small files. For larger Parquet files, use multipart upload via the same endpoint (the HF API auto-routes to LFS).

If the simpler API doesn't work in practice, fall back to: create repo, get a commit URL via `/api/datasets/{repo}/preupload/main`, then PUT the file content there.

Base URL: `https://huggingface.co`.

Repo-creation endpoint: `POST /api/repos/create` with body `{ "type": "dataset", "name": "...", "organization": "..." }`.

### `ExportCommandHandler` (replace stub)

```csharp
public sealed class ExportCommandHandler
{
    private readonly IAnsiConsole console;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly ILogger<ExportCommandHandler> logger;

    public async Task<int> InvokeAsync(ExportCommandOptions options, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(options.DatasetDir))
            return DiagnosticError($"Dataset directory not found: {options.DatasetDir}");

        var jsonlFiles = Directory.GetFiles(options.DatasetDir, "*.jsonl");
        if (jsonlFiles.Length == 0)
            return DiagnosticError($"No .jsonl files found in {options.DatasetDir}");

        var fmt = (options.Format ?? "jsonl").ToLowerInvariant();
        var outDir = options.OutDir ?? Path.Combine(options.DatasetDir, $"export-{fmt}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(outDir);

        // 1. Convert (if format differs)
        var outputFiles = fmt switch
        {
            "jsonl" => ConvertOrPassthrough(jsonlFiles, outDir, row => row.ToDictionary(kv => kv.Key, kv => (object?)kv.Value)),  // identity
            "alpaca" => ConvertToFiles(jsonlFiles, outDir, AlpacaConverter.Convert, "alpaca", ".jsonl"),
            "sharegpt" => ConvertToFiles(jsonlFiles, outDir, ShareGptConverter.Convert, "sharegpt", ".jsonl"),
            "parquet" => ConvertToParquet(jsonlFiles, outDir),
            "csv" => ConvertToCsv(jsonlFiles, outDir),
            _ => null,
        };

        if (outputFiles is null)
            return DiagnosticError($"Unknown export format: {options.Format}");

        this.console.MarkupLine($"[green]Wrote {outputFiles.Count} file(s) to {outDir}[/]");

        // 2. Upload to HF if requested
        if (!string.IsNullOrEmpty(options.HfRepo))
        {
            var token = options.HfToken ?? Environment.GetEnvironmentVariable("HF_TOKEN");
            if (string.IsNullOrEmpty(token))
                return DiagnosticError("HF token required. Pass --hf-token or set HF_TOKEN.");

            var http = this.httpClientFactory.CreateClient("HuggingFace");
            var client = new HuggingFaceClient(http, token);
            await client.EnsureRepoAsync(options.HfRepo, cancellationToken).ConfigureAwait(false);

            foreach (var file in outputFiles)
            {
                var remotePath = $"{options.Split}/{Path.GetFileName(file)}";
                this.console.MarkupLine($"  uploading [cyan]{remotePath}[/]");
                await client.UploadFileAsync(options.HfRepo, remotePath, file, "DistSharp export", cancellationToken).ConfigureAwait(false);
            }

            this.console.MarkupLine($"[green]Uploaded to https://huggingface.co/datasets/{options.HfRepo}[/]");
        }

        return 0;
    }
}
```

### YAML → step Config dictionary fix

The Phase 6 issue: `Microsoft.Extensions.Configuration.Binder` doesn't naturally bind into `Dictionary<string, object?>`. The fix in `PipelineRunCommandHandler` is to extract step configs manually from `IConfiguration` rather than rely on `Bind`.

Approach: after `Bind(pipelineConfig)`, post-process by walking `configuration.GetSection("steps")`:

```csharp
private static void PopulateStepConfigs(IConfiguration configuration, PipelineConfig pipelineConfig)
{
    var stepsSection = configuration.GetSection("steps");
    var i = 0;
    foreach (var stepSection in stepsSection.GetChildren())
    {
        if (i >= pipelineConfig.Steps.Count)
            break;

        var configSection = stepSection.GetSection("config");
        if (configSection.Exists())
        {
            pipelineConfig.Steps[i].Config = FlattenConfigSection(configSection);
        }
        i++;
    }
}

private static Dictionary<string, object?> FlattenConfigSection(IConfigurationSection section)
{
    var result = new Dictionary<string, object?>();
    foreach (var child in section.GetChildren())
    {
        if (child.Value is not null)
        {
            // Leaf value
            result[child.Key] = ParseScalar(child.Value);
        }
        else
        {
            // Sub-section: could be a list (children with integer keys) or object
            var children = child.GetChildren().ToList();
            if (children.All(c => int.TryParse(c.Key, out _)))
                result[child.Key] = children.Select(c => c.Value ?? string.Empty).ToList<object>();
            else
                result[child.Key] = FlattenConfigSection(child);
        }
    }
    return result;
}

private static object? ParseScalar(string raw)
{
    if (bool.TryParse(raw, out var b)) return b;
    if (int.TryParse(raw, out var i)) return i;
    if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
    return raw;
}
```

Call `PopulateStepConfigs(configuration, pipelineConfig)` after `configuration.Bind(pipelineConfig)` in `PipelineRunCommandHandler`.

---

## 3. Files to create

```
src/DistSharp.Core/Export/
├── JsonlDatasetReader.cs
├── AlpacaConverter.cs
├── ShareGptConverter.cs
└── ConverterHelpers.cs (shared GetString utility)

src/DistSharp.Core/HuggingFace/
└── HuggingFaceClient.cs

src/DistSharp.Cli/Commands/
└── ExportCommandHandler.cs (rewrite — replace stub)

src/DistSharp.Cli/Commands/
└── PipelineRunCommandHandler.cs (modify — add PopulateStepConfigs fix)

tests/DistSharp.Core.Tests/Export/
├── AlpacaConverterTests.cs
├── ShareGptConverterTests.cs
└── JsonlDatasetReaderTests.cs

tests/DistSharp.Cli.Tests/Commands/
└── ExportCommandHandlerTests.cs (covers local export, not HF upload)

tests/DistSharp.Cli.Tests/
└── PipelineConfigBindingTests.cs (covers the YAML fix)
```

---

## 4. Out of scope

- Parquet → JSONL / CSV → JSONL reverse conversion (only JSONL is supported as the source)
- Resumable uploads to HF Hub
- Multi-part LFS uploads (the simple PUT works for files under ~5 GB)
- HF Hub authentication via OAuth or login flow (token-only)
