# DistSharp — Phase 4: LLM Provider Layer

**Date:** 2026-05-15
**Scope:** `DistSharp.Providers` project — concrete `ILlmProvider` implementations and the factory that resolves them.

---

## 1. Goals

Implement the seven providers listed in the README:

| Provider | `--provider` value | Underlying protocol |
|---|---|---|
| OpenAI | `openai` | OpenAI Chat Completions |
| Anthropic | `anthropic` | Anthropic Messages API |
| Azure OpenAI | `azure-openai` | OpenAI Chat Completions (Azure host) |
| Google Gemini | `gemini` | Google Generative Language API |
| Ollama (local) | `ollama` | OpenAI-compatible Chat Completions |
| LM Studio | `lmstudio` | OpenAI-compatible Chat Completions |
| OpenAI-compatible | `openai-compatible` | OpenAI Chat Completions |

Plus `LlmProviderFactory` that resolves a provider by name from configuration + environment variables.

---

## 2. Approach

**No third-party SDK packages.** Write minimal HTTP clients ourselves to:
- Avoid dependency risk and version churn
- Keep the binary small for global-tool distribution
- Maintain full control over retry / cancellation / streaming behaviour

**Shared base class:** `OpenAICompatibleProvider` (abstract). Used by OpenAI, Azure OpenAI, Ollama, LM Studio, and OpenAI-compatible. Differences are just base URL, auth header style, and default model.

**Independent providers:** `AnthropicProvider`, `GeminiProvider`. Different request/response schemas.

---

## 3. Components

### `LlmProviderOptions` (shared base)

```csharp
public class LlmProviderOptions
{
    public string? ApiKey { get; set; }
    public string? BaseUrl { get; set; }
    public string? DefaultModel { get; set; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
    public int MaxRetries { get; set; } = 3;
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);
}
```

Provider-specific subclasses override defaults (e.g. `OllamaProviderOptions.BaseUrl = "http://localhost:11434"`).

### `OpenAICompatibleProvider` (abstract base)

```csharp
public abstract class OpenAICompatibleProvider : ILlmProvider
{
    protected OpenAICompatibleProvider(HttpClient http, LlmProviderOptions options, ILogger logger);

    public abstract string ProviderName { get; }
    protected abstract string ChatCompletionsPath { get; }   // default "/v1/chat/completions"
    protected abstract void ApplyAuthentication(HttpRequestMessage request);
    protected virtual JsonObject BuildRequestBody(IReadOnlyList<ChatMessage> messages, LlmRequestOptions opts);
    protected virtual string ExtractContentFromResponse(JsonNode response);

    public async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, LlmRequestOptions options, CancellationToken ct);
}
```

Request body (OpenAI-style):
```json
{
  "model": "gpt-4.1-mini",
  "messages": [
    {"role": "system", "content": "..."},
    {"role": "user", "content": "..."}
  ],
  "temperature": 0.7,
  "max_tokens": 1024
}
```

Response extraction: `response["choices"][0]["message"]["content"]`.

### `OpenAIProvider : OpenAICompatibleProvider`

- `BaseUrl`: `https://api.openai.com`
- `ApiKey`: from `OPENAI_API_KEY` env var
- Auth: `Authorization: Bearer {key}`

### `AzureOpenAIProvider : OpenAICompatibleProvider`

- `BaseUrl`: from `AZURE_OPENAI_ENDPOINT` env var (e.g. `https://my-resource.openai.azure.com`)
- `ApiKey`: from `AZURE_OPENAI_API_KEY` env var
- Auth: `api-key: {key}` header
- Path: `/openai/deployments/{model}/chat/completions?api-version=2024-08-01-preview`
- `Model` in request body is omitted (model name is in URL as deployment name)

### `OpenAICompatibleEndpointProvider : OpenAICompatibleProvider`

- `BaseUrl`: from `OPENAI_COMPATIBLE_BASE_URL`
- `ApiKey`: from `OPENAI_COMPATIBLE_API_KEY` (optional)
- Auth: `Authorization: Bearer {key}` if key present
- Used for any third-party OpenAI-compatible endpoint

### `OllamaProvider : OpenAICompatibleProvider`

- `BaseUrl`: from `OLLAMA_BASE_URL` env var, default `http://localhost:11434`
- No auth
- Path: `/v1/chat/completions` (Ollama's OpenAI-compatible endpoint)

### `LmStudioProvider : OpenAICompatibleProvider`

- `BaseUrl`: from `LMSTUDIO_BASE_URL` env var, default `http://localhost:1234`
- No auth
- Path: `/v1/chat/completions`

### `AnthropicProvider : ILlmProvider`

- `BaseUrl`: `https://api.anthropic.com`
- `ApiKey`: from `ANTHROPIC_API_KEY` env var
- Auth: `x-api-key: {key}` + `anthropic-version: 2023-06-01`
- Path: `/v1/messages`

Request body shape:
```json
{
  "model": "claude-opus-4-7",
  "system": "...",
  "messages": [
    {"role": "user", "content": "..."}
  ],
  "max_tokens": 1024,
  "temperature": 0.7
}
```

Notes:
- Anthropic requires `max_tokens` to always be set. Default to 4096 if `options.MaxTokens` is null.
- System messages from `ChatMessage[]` are extracted and joined into the top-level `system` field; remaining messages alternate user/assistant.
- Response extraction: `response["content"][0]["text"]` (Anthropic returns content as an array of typed blocks).

### `GeminiProvider : ILlmProvider`

- `BaseUrl`: `https://generativelanguage.googleapis.com`
- `ApiKey`: from `GEMINI_API_KEY` env var
- Auth: query string `?key={key}`
- Path: `/v1beta/models/{model}:generateContent?key=...`

Request body shape:
```json
{
  "contents": [
    {"role": "user", "parts": [{"text": "..."}]},
    {"role": "model", "parts": [{"text": "..."}]}
  ],
  "systemInstruction": {"parts": [{"text": "..."}]},
  "generationConfig": {
    "temperature": 0.7,
    "maxOutputTokens": 1024
  }
}
```

Notes:
- Gemini uses `user` and `model` for roles, not `user`/`assistant`. Convert.
- System messages → `systemInstruction.parts[].text`.
- Response extraction: `response["candidates"][0]["content"]["parts"][0]["text"]`.

### `LlmProviderFactory : ILlmProviderFactory`

```csharp
public sealed class LlmProviderFactory : ILlmProviderFactory
{
    public LlmProviderFactory(IServiceProvider services);
    public ILlmProvider Create(string providerName);  // lookup by canonical name
}
```

Resolution table (case-insensitive):

| Input | Concrete type |
|---|---|
| `openai` | `OpenAIProvider` |
| `anthropic` | `AnthropicProvider` |
| `azure-openai`, `azure` | `AzureOpenAIProvider` |
| `gemini`, `google` | `GeminiProvider` |
| `ollama` | `OllamaProvider` |
| `lmstudio`, `lm-studio` | `LmStudioProvider` |
| `openai-compatible`, `compatible` | `OpenAICompatibleEndpointProvider` |

Unknown name → `InvalidOperationException`.

### DI extension method

`ServiceCollectionExtensions.AddDistSharpProviders(IServiceCollection)`:

Registers all providers as singletons, binds their options from configuration (`Providers:OpenAI`, `Providers:Anthropic`, etc.), and registers `ILlmProviderFactory → LlmProviderFactory`. Also registers a single `HttpClient` for each provider via `IHttpClientFactory`.

---

## 4. Retry / error handling (shared)

- Retry on HTTP 429, 500, 502, 503, 504 with exponential backoff (`InitialRetryDelay * 2^attempt`)
- Respect `Retry-After` header when present (overrides backoff calculation)
- Final failure throws `LlmProviderException` containing provider name, status code, and response body
- `OperationCanceledException` is never caught — bubbles up to caller

`LlmProviderException` is a public class in `DistSharp.Core.Abstractions` (added there to be visible to step library).

---

## 5. Tests

`tests/DistSharp.Providers.Tests/`:

1. **`OpenAICompatibleProviderTests`** — mock `HttpMessageHandler` via NSubstitute or a stub class
   - Builds correct request body
   - Sends correct auth header
   - Parses content from response
   - Retries on 429 with backoff
   - Stops retrying after `MaxRetries`
   - Honours `Retry-After`

2. **`AnthropicProviderTests`** — verify Anthropic-specific schema
   - System messages flattened to top-level `system` field
   - `max_tokens` defaulted to 4096 when null
   - `anthropic-version` header present
   - Response content extraction

3. **`GeminiProviderTests`** — verify Gemini-specific schema
   - System → `systemInstruction`
   - Role mapping `assistant` → `model`
   - Response extraction from `candidates[0].content.parts[0].text`

4. **`LlmProviderFactoryTests`** — resolves each name, throws for unknown name

---

## 6. Files to create

```
src/DistSharp.Providers/
├── DistSharp.Providers.csproj (modify)
├── LlmProviderOptions.cs
├── LlmProviderFactory.cs
├── ServiceCollectionExtensions.cs
├── Internal/
│   ├── HttpRetryHelper.cs (shared retry logic)
│   └── JsonHelpers.cs (shared JSON parse/build utilities)
├── OpenAI/
│   ├── OpenAICompatibleProvider.cs (abstract)
│   ├── OpenAIProvider.cs
│   ├── AzureOpenAIProvider.cs
│   ├── OpenAICompatibleEndpointProvider.cs
│   ├── OllamaProvider.cs
│   └── LmStudioProvider.cs
├── Anthropic/
│   └── AnthropicProvider.cs
└── Gemini/
    └── GeminiProvider.cs

src/DistSharp.Core/Abstractions/
└── LlmProviderException.cs (new)

tests/DistSharp.Providers.Tests/
├── OpenAICompatibleProviderTests.cs
├── AnthropicProviderTests.cs
├── GeminiProviderTests.cs
└── LlmProviderFactoryTests.cs
```

---

## 7. Out of scope (defer)

- Streaming completions (yield tokens) — not required by the pipeline contract; `CompleteAsync` returns full text
- Function calling / structured outputs — pipeline does not need them at this phase
- Cost tracking — README mentions estimated cost in `inspect` but it can be computed from token counts later
