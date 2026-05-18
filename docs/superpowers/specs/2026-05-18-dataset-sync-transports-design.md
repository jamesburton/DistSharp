# DistSharp — Dataset Sync Transports (Phases 2 + 3) Design

**Date:** 2026-05-18
**Status:** Design / planning. Phase 1 (manifest, row identity, diff, local sync/migrate) shipped.
**Scope:** Phases 2 and 3 from the parent spec (`2026-05-18-dataset-sync-design.md`):
transport backends for `dataset pull` / `dataset push` (Phase 2) and the `dataset merge` command with all five conflict policies (Phase 3).

---

## 1. Goals

### Phase 2 — Transports

| Command | Direction | What it enables |
|---|---|---|
| `dataset pull` | remote → local | Fetch the canonical state of an HF or git dataset repo into a local directory, ready for `sync` or offline editing. |
| `dataset push` | local → remote | Send the local directory — manifest plus data files — back to the remote with a single atomic commit (HF) or git push. |

Both commands are intentionally thin: they transfer bytes and record remotes. They do not regenerate rows or call the LLM. The `dataset sync` command (Phase 4 in the parent spec, Phase 1 locally-only already shipped) composes pull + local-sync + push.

A dataset directory can simultaneously have an **HF remote** and a **git remote**; a single push invocation can write to both. The remote URLs are persisted in `_distsharp/manifest.json` under a new `remotes` map so subsequent commands need no flags.

### Phase 3 — Merge

| Command | Direction | What it enables |
|---|---|---|
| `dataset merge` | local + local → local | Combine two dataset snapshots — typically a freshly pulled remote and a locally regenerated snapshot — into one, resolving per-row conflicts with a named policy. |

Merge is the foundation of multi-machine and multi-branch workflows. It has no transport dependency; a merge output is a normal local dataset directory that can be pushed separately.

---

## 2. Approach

### Transport abstraction: `IDatasetTransport`

All remote I/O flows through a single interface:

```csharp
/// <summary>Abstract transport for fetching and publishing a DistSharp dataset snapshot.</summary>
public interface IDatasetTransport
{
    /// <summary>Lists all files in the dataset repo at the given branch.</summary>
    Task<IReadOnlyList<RemoteFile>> ListFilesAsync(string branch, CancellationToken ct);

    /// <summary>Downloads a single file from the remote and writes it to <paramref name="localPath"/>.</summary>
    Task DownloadFileAsync(string remotePath, string localPath, string branch, CancellationToken ct);

    /// <summary>
    /// Uploads all files from <paramref name="localDir"/> to the remote as a single atomic commit.
    /// </summary>
    Task CommitAsync(IReadOnlyList<FileOperation> operations, string message, string branch, CancellationToken ct);
}

/// <summary>A file entry returned by <see cref="IDatasetTransport.ListFilesAsync"/>.</summary>
public sealed record RemoteFile(string Path, long Size, string? ContentSha);

/// <summary>An add, update, or delete operation within a <see cref="IDatasetTransport.CommitAsync"/> call.</summary>
public abstract record FileOperation(string RemotePath)
{
    /// <summary>Add or update a file from a local path.</summary>
    public sealed record Upsert(string RemotePath, string LocalPath) : FileOperation(RemotePath);

    /// <summary>Delete a file that is no longer present locally.</summary>
    public sealed record Delete(string RemotePath) : FileOperation(RemotePath);
}
```

This interface is narrow by design. The orchestrator (`DatasetPullService`, `DatasetPushService`) never speaks HTTP or shells out to git; only the two concrete implementations do.

### Backend selection

The correct transport is resolved in this order:

1. If `--hf-repo <repo>` is supplied, or if the manifest `remotes.hf` is set and the URL host is `huggingface.co` — use `HuggingFaceDatasetTransport`.
2. If `--git-remote <url>` is supplied, or if the manifest `remotes.git` is set — use `GitDatasetTransport`.
3. If both are set, both are used in sequence (pull from HF, push to both).

A dataset directory can therefore have at most one HF remote and one git remote. The `remotes` map in `manifest.json` persists both:

```jsonc
"remotes": {
  "hf": "my-org/my-dataset",            // short repo-id (no host prefix)
  "git": "https://github.com/me/ds.git", // full URL
  "default_branch": "main"
}
```

This map is written on first pull/push; subsequent commands read it, and explicit flags override it.

---

## 3. HF Transport

### Existing `HuggingFaceClient`

The existing client (`src/DistSharp.Core/HuggingFace/HuggingFaceClient.cs`) exposes two methods:
- `EnsureRepoAsync` — creates the dataset repo via `POST /api/repos/create`.
- `UploadFileAsync` — uploads a single file via `POST /api/datasets/{repo}/upload/main/{path}`.

The single-file upload endpoint is convenient for `export --hf-repo` (Phase 7 design) but produces one commit per file. Phase 2 requires multi-file atomic commits via the HF commit endpoint. The existing `UploadFileAsync` is retained for the `export` command; the transport layer uses the commit endpoint exclusively.

### Auth

Auth token resolution follows this precedence (first non-null/empty wins):

1. `--hf-token <token>` CLI flag
2. `HF_TOKEN` environment variable
3. `HUGGING_FACE_HUB_TOKEN` environment variable (huggingface_hub Python SDK convention)
4. `~/.distsharp/config.json` key `huggingface.token`

If no token is found, read-only operations (`pull` from a public repo) proceed without auth. Write operations fail with a clear error message pointing to option 2.

### HF branch model

HF dataset repos are git repositories. The default branch is `main`. Users may specify `--branch` to target a different revision. The `default_branch` is persisted in `remotes.default_branch` after the first pull.

### Required HTTP calls

#### 1. List files — `ListFilesAsync`

```
GET https://huggingface.co/api/datasets/{repoId}/tree/{branch}
Authorization: Bearer {token}
```

Returns a JSON array of objects. Relevant fields: `path` (string), `size` (number), `lfs.sha256` (string, present when the file is LFS-tracked). The transport maps these to `RemoteFile` records.

Pagination: if the response header `X-Total-Count` exceeds 1000, repeat with `?cursor=` from the `Link` response header until all pages are fetched.

#### 2. Download a file — `DownloadFileAsync`

```
GET https://huggingface.co/datasets/{repoId}/resolve/{branch}/{path}
Authorization: Bearer {token}
```

HF rewrites this URL server-side for LFS-tracked files (Parquet, large JSONL) — the caller gets the LFS content directly without any client-side LFS protocol. Stream the response body to `localPath` with `File.OpenWrite`.

For large files (> 50 MB) report progress by tracking `Content-Length` against bytes read and calling `IProgress<int>` (percentage).

#### 3. Multi-file atomic commit — `CommitAsync`

This replaces the per-file `upload/main/{path}` flow for the push path. The HF commit endpoint accepts a mixture of add/modify/delete operations in a single HTTP request.

**Step A — pre-upload LFS files**

For each `Upsert` operation where the local file size is above 10 MB (HF's default LFS threshold for datasets), pre-upload to LFS before the commit:

```
POST https://huggingface.co/api/datasets/{repoId}/preupload/{branch}
Authorization: Bearer {token}
Content-Type: application/json

{
  "files": [
    { "path": "data/train.parquet", "size": 123456789, "sample": "<base64 of first 512 bytes>" }
  ]
}
```

Response contains LFS upload instructions (batch endpoint + OID + size). Upload each file via the standard LFS batch protocol:

```
POST {lfs_batch_url}
Content-Type: application/vnd.git-lfs+json

{ "operation": "upload", "objects": [{ "oid": "<sha256>", "size": <size> }] }
```

Then PUT the file content to the returned upload URL.

**Step B — commit**

```
POST https://huggingface.co/api/datasets/{repoId}/commit/{branch}
Authorization: Bearer {token}
Content-Type: application/x-ndjson

{"summary": "<commit message>", "description": ""}
{"key": "header"}
{"key": "file", "value": {"path": "data/train.jsonl", "encoding": "base64", "content": "<base64>"}}
{"key": "lfsFile", "value": {"path": "data/train.parquet", "algo": "sha256", "oid": "<oid>", "size": <size>}}
{"key": "deletedFile", "value": {"path": "old-file.jsonl"}}
```

The body is newline-delimited JSON (NDJSON): one header line, then one operation line per file. For JSONL files under 10 MB the content is base64-encoded inline. For Parquet files the `lfsFile` line references the OID uploaded in Step A.

**Note on scope vs. existing `UploadFileAsync`:** The commit endpoint is a superset. The `export --hf-repo` path continues to use the simpler single-file upload endpoint (no change to Phase 7 behaviour); only the transport layer uses the commit endpoint. If a future cleanup consolidates both onto the commit endpoint, that should be tracked as a separate task.

### Divergence detection on push

Before `CommitAsync`, the transport reads the `generated_at` timestamp from the remote manifest (`DownloadFileAsync` on `_distsharp/manifest.json`) and compares it to the local manifest's `generated_at`. If the remote is newer than the local, the push is rejected with exit code 3 and a message:

```
Remote manifest is newer than local (remote: 2026-05-18T14:00Z, local: 2026-05-18T12:00Z).
Run 'dataset pull' first, or use --force to overwrite.
```

`--force` bypasses this check. This mirrors the `dataset push --force` flag described in the parent spec §7.

### Auto-create on first push

On `CommitAsync`, if the HF API returns `404` for the repo, the transport calls `EnsureRepoAsync` (existing method) to create it, then retries. This matches `export --hf-repo` behaviour. It can be suppressed with `--no-create`.

---

## 4. Git Transport

### Shell-out vs. LibGit2Sharp

**Decision: shell-out to `git` on PATH.**

Rationale:
- `GitHelper.cs` (already shipped in Phase 1) already shells out to `git` for `rev-parse HEAD` and `rev-parse --abbrev-ref HEAD`. The git transport is a natural extension of that pattern.
- LibGit2Sharp would add ~8 MB of native binaries per platform, a new package dependency, and incomplete LFS support (LFS requires the `git-lfs` filter driver anyway, so LibGit2Sharp buys nothing for Parquet datasets).
- Any developer who has DistSharp installed via `dnx` has git on PATH.

LibGit2Sharp is noted as a possible future upgrade if deeper git semantics (e.g. interactive rebase, sub-module support) are needed; for now it is out of scope.

### Operations

All git operations shell out via an extension of `GitHelper.RunGit`. The working directory is always the local dataset directory.

**Pull:**

```bash
# First pull (directory does not yet exist)
git clone --depth 1 --branch {branch} {url} {localDir}

# Subsequent pull (directory is an existing git clone)
git fetch origin {branch}
git reset --hard origin/{branch}
```

The depth-1 clone keeps history small. If full history is required (rare for datasets), `--depth 1` can be omitted via `--full-history` flag.

**Push:**

```bash
git add -A
git commit --allow-empty -m "{message}"
git push origin {branch}
```

`--allow-empty` is used so that a push where only the manifest timestamp changed (e.g. after a no-op sync) produces a commit. If this is undesirable, `git status --porcelain` can be checked first and the push skipped when there are no changes.

**LFS for Parquet datasets:**

When the local dataset directory contains `*.parquet` files, the transport checks for `*.parquet filter=lfs` in `.gitattributes`. If absent, it writes the `.gitattributes` entry before staging, matching the layout in the parent spec §3. `git lfs` must be on PATH; if it is not, the transport logs a warning and proceeds (LFS tracking will be absent, which is acceptable for small Parquet files).

### What git semantics are exposed

The transport intentionally exposes **only**: clone/fetch, hard-reset, stage-all, commit, push. It does not wrap merge, rebase, stash, or cherry-pick. If the user has merge conflicts at the git level (two machines pushed without pulling), they must resolve those with plain `git` in the directory. DistSharp's merge (`dataset merge`) operates on the manifest layer, not the git layer.

### Divergence detection on push

Before pushing, the transport runs `git fetch origin {branch}` and checks whether `origin/{branch}` is ahead of `HEAD` via `git rev-list --count HEAD..origin/{branch}`. If the count is > 0, the push is rejected with exit code 3 and the same "run pull first" message as the HF transport. `--force` maps to `git push --force-with-lease`.

---

## 5. Merge

### Algorithm

`dataset merge <dir-a> <dir-b> --out <dir-c>` reads both manifests and produces a third. The data files of `dir-c` are assembled from `dir-a` and `dir-b` data lines according to the policy table below.

**Input:** Two `DatasetManifest` objects, `A` and `B`. The union of all `row_id`s drives the walk.

**Full truth table:**

| Row in A? | Row in B? | `body_sha` match? | Default (`newer`) action |
|---|---|---|---|
| Yes | No | n/a | Take A |
| No | Yes | n/a | Take B |
| Yes | Yes | Yes | Keep (identical — take A, log at Trace) |
| Yes | Yes | No | **Conflict** — apply strategy |

### Conflict policy implementations

There are five strategies, passed as `--strategy`:

#### `ours`
Take the row from A. The row's `body_sha` and `generated_at` from A's manifest are preserved verbatim. No side-effects.

#### `theirs`
Take the row from B. Mirror of `ours`.

#### `newer`
Compare `generated_at` from each manifest (manifest-level timestamps, not per-row). The manifest with the later timestamp wins the entire conflict set. Rationale: manifests represent a point-in-time regeneration from a specific solution state; mixing rows from two different solution states is likely to produce incoherent data. If both manifests have identical `generated_at` (rare: two machines ran sync at the same second), fall through to `ours`.

This is the **default strategy** (matches the parent spec §6).

#### `regenerate`
Neither A nor B row is written into the output. Instead, the merged manifest records the row with a sentinel `body_sha = "pending"` and omits `row_offset`. A subsequent `dataset sync` will detect all `pending` rows as stale and regenerate them.

This strategy avoids data loss when neither snapshot is trusted but the user is willing to pay re-generation cost.

#### `prompt`
Neither A nor B row is written into the active output. Instead:
1. Both rows are serialised to `{dir-c}/_distsharp/conflicts/{row_id}.json`:

```jsonc
{
  "row_id": "abc123...",
  "symbol_fqn": "MyApp.Services.OrderService.PlaceOrderAsync",
  "a": { "body_sha": "...", "generated_at": "...", "fields": { /* full JSONL row */ } },
  "b": { "body_sha": "...", "generated_at": "...", "fields": { /* full JSONL row */ } }
}
```

2. The merged manifest records the row as `body_sha = "conflict"` with no `row_offset`.
3. The command exits with code **4** (distinct from transport errors at 3, argument errors at 2, and general errors at 1).

To resolve: the user edits or deletes files in `_distsharp/conflicts/`, then runs `dataset merge --resolve-conflicts` which promotes each resolved file into the active data file and clears the conflict marker in the manifest.

Interactive TTY-level prompting (show diff, ask ours/theirs per conflict) is left as future work. The file-based approach works in CI and in IDEs.

### Orphan handling in merge

Orphan rows (present in one manifest but not the other) are treated as **take the side that has it** — this matches the "No / Yes" and "Yes / No" rows in the truth table. There is no per-merge orphan policy flag; the orphan policy is only relevant to `dataset sync` (which knows about the solution's current symbol set).

### Output structure

`dir-c` is created (or emptied) before writing. Its layout:

```
dir-c/
  _distsharp/
    manifest.json          # merged manifest
    conflicts/             # non-empty only when --strategy prompt
      {row_id}.json
  data/
    train.jsonl            # reassembled from A + B rows (or pending rows omitted)
```

The `source` field in the merged manifest references both input manifests:

```jsonc
"source": {
  "merged_from": [
    { "path": "<dir-a>", "generated_at": "...", "solution_sha": "..." },
    { "path": "<dir-b>", "generated_at": "...", "solution_sha": "..." }
  ],
  "merged_at": "2026-05-18T15:00:00Z",
  "strategy": "newer"
}
```

### `MergeResult` record (returned to CLI)

```csharp
public sealed record MergeResult(
    int TakenFromA,
    int TakenFromB,
    int Identical,
    int Conflicts,          // rows written to conflicts/ dir
    int PendingRegenerate,  // rows marked pending (strategy=regenerate)
    int ConflictFilePath);  // path to conflicts dir, or null if none
```

---

## 6. Components & File Layout

### New files in `src/DistSharp.Core/Sync/`

| File | Purpose |
|---|---|
| `IDatasetTransport.cs` | Interface (`ListFilesAsync`, `DownloadFileAsync`, `CommitAsync`) + `RemoteFile` + `FileOperation` records |
| `HuggingFaceDatasetTransport.cs` | HF implementation (LFS pre-upload + NDJSON commit endpoint) |
| `GitDatasetTransport.cs` | Git shell-out implementation (clone, fetch/reset, add/commit/push) |
| `DatasetPullService.cs` | Orchestrates pull: resolves transport, calls `ListFilesAsync` + `DownloadFileAsync`, writes local files, updates `remotes` in manifest |
| `DatasetPushService.cs` | Orchestrates push: divergence check, builds `FileOperation` list from local dir diff against last-known remote snapshot, calls `CommitAsync` |
| `DatasetMergeService.cs` | Implements merge algorithm + conflict policy dispatch |
| `MergeResult.cs` | `sealed record MergeResult(...)` |
| `ConflictPolicy.cs` | `enum ConflictPolicy { Ours, Theirs, Newer, Regenerate, Prompt }` |

### Modifications to existing files in `src/DistSharp.Core/Sync/`

| File | Change |
|---|---|
| `DatasetManifest.cs` | Add `Remotes` property (`ManifestRemotes?`), add `ManifestRemotes` record |
| `DatasetSyncOrchestrator.cs` | Accept optional `IDatasetTransport` for pre-pull and post-push steps (Phase 4 wire-up, not in scope here; stub injection point) |

### Modifications to `src/DistSharp.Core/HuggingFace/HuggingFaceClient.cs`

Add three methods:

| Method | HTTP call |
|---|---|
| `ListFilesAsync(repoId, branch, ct)` | `GET /api/datasets/{repoId}/tree/{branch}` (paginated) |
| `DownloadFileAsync(repoId, branch, remotePath, localPath, progress, ct)` | `GET /datasets/{repoId}/resolve/{branch}/{path}` |
| `CommitFilesAsync(repoId, branch, operations, message, ct)` | `POST /api/datasets/{repoId}/preupload/{branch}` + LFS batch upload + `POST /api/datasets/{repoId}/commit/{branch}` |

`HuggingFaceDatasetTransport` wraps `HuggingFaceClient`; it does not duplicate HTTP logic. `HuggingFaceClient` remains the single place that knows HF API URLs.

### New files in `src/DistSharp.Cli/Commands/`

| File | Purpose |
|---|---|
| `DatasetPullCommandHandler.cs` | Bind flags → `DatasetPullService.PullAsync` → Spectre progress display |
| `DatasetPullCommandOptions.cs` | Options record for pull |
| `DatasetPushCommandHandler.cs` | Bind flags → `DatasetPushService.PushAsync` → output summary |
| `DatasetPushCommandOptions.cs` | Options record for push |
| `DatasetMergeCommandHandler.cs` | Bind flags → `DatasetMergeService.MergeAsync` → summary table; exit 4 on unresolved conflicts |
| `DatasetMergeCommandOptions.cs` | Options record for merge |

---

## 7. CLI Surface

### `dataset pull`

```
distsharp dataset pull <repo-or-url> [OPTIONS]

Arguments:
  <repo-or-url>            HF repo-id (e.g. my-org/my-dataset) or git URL.
                           May be omitted if the target directory already has
                           a remote recorded in its manifest.

Options:
  --into <dir>             Local directory to pull into. Created if absent.
                           Default: ./{repo-name}
  --branch <branch>        Remote branch. Default: main (or manifest default).
  --hf-repo <repo>         Explicit HF repo-id (overrides positional arg).
  --git-remote <url>       Explicit git URL (overrides positional arg).
  --hf-token <token>       HF API token. Overrides HF_TOKEN env var.
  --full-history           For git transport: clone without --depth 1.
  --no-create              Do not auto-create the HF repo if it is missing.
  --dry-run                Show what would be fetched; do not write files.
  --force                  Overwrite local changes without confirmation.
```

Exit codes: 0 success, 1 transport error, 2 bad arguments, 3 divergence (not applicable to pull; reserved).

### `dataset push`

```
distsharp dataset push <dir> [OPTIONS]

Arguments:
  <dir>                    Local dataset directory. Required.

Options:
  --hf-repo <repo>         HF repo-id to push to. Overrides manifest remotes.hf.
  --git-remote <url>       Git URL to push to. Overrides manifest remotes.git.
  --branch <branch>        Target branch. Default: main (or manifest default).
  --message <msg>          Commit message. Default: "distsharp dataset push <timestamp>"
  --hf-token <token>       HF API token. Overrides HF_TOKEN env var.
  --force                  Skip divergence check; overwrite remote.
  --no-create              Do not auto-create HF repo if missing.
  --dry-run                Show what would be pushed; do not upload.
```

Exit codes: 0 success, 1 transport error, 2 bad arguments, 3 remote has diverged (use --force or pull first).

### `dataset merge`

```
distsharp dataset merge <dir-a> <dir-b> [OPTIONS]

Arguments:
  <dir-a>                  "Ours" — the first (preferred) input snapshot.
  <dir-b>                  "Theirs" — the second input snapshot.

Options:
  --out <dir>              Output directory. Created if absent.
                           Default: <dir-a>-merged
  --strategy <policy>      Conflict policy: newer (default), ours, theirs,
                           regenerate, prompt.
  --resolve-conflicts      Instead of merging, promote files from
                           _distsharp/conflicts/ and clear conflict markers.
                           Ignores <dir-b> and --strategy.
  --dry-run                Show the merge plan; do not write files.
```

Exit codes: 0 success, 1 error, 2 bad arguments, 4 unresolved conflicts remain (--strategy prompt produced conflict files).

---

## 8. Effort

| Phase | Scope | Estimate | Dependencies |
|---|---|---|---|
| **2a: IDatasetTransport + HF transport** | Interface, `HuggingFaceClient` extensions, `HuggingFaceDatasetTransport`, `DatasetPullService`, `DatasetPushService`, `dataset pull` + `dataset push` CLI | 3 days | Phase 1 (shipped) |
| **2b: Git transport** | `GitDatasetTransport`, `GitHelper` extensions, LFS `.gitattributes` injection | 1.5 days | Phase 2a (IDatasetTransport available) |
| **3: Merge** | `DatasetMergeService`, all 5 policies, conflict file I/O, `MergeResult`, `dataset merge` CLI + `--resolve-conflicts` | 2 days | Phase 1 only |

**Total: ~6.5 working days.** Phases 2a, 2b, and 3 are independent of each other after Phase 1; they can be built in parallel by different contributors.

The fastest useful delivery is Phase 3 (merge) first — it has no HTTP or process-spawn complexity and unblocks multi-machine workflows that already have manual pull/push via `export`.

---

## 9. Risks & Open Questions

1. **(BLOCKING) Divergence policy on `dataset push`.** The spec fails-fast (exit 3) when the remote is ahead. An alternative is auto-pull + auto-merge + push. The auto-merge approach is more ergonomic but requires choosing a strategy silently, which can destroy data. **Decision needed: fail-fast (safe) or auto-pull-merge-push (convenient)?** Current default in this spec: fail-fast. Auto behaviour can be enabled by a future `--auto-merge-strategy` flag.

2. **(BLOCKING) Auto-create HF repo on first push.** The existing `export --hf-repo` creates the repo automatically. Should `dataset push` match that behaviour? The spec says yes (opt-out via `--no-create`). If the answer is no, the default is a helpful error message. **Confirm with product.**

3. **(BLOCKING) `dataset clone` vs. `pull --into`.** The spec exposes only `pull --into <dir>`. A `dataset clone <url> <dir>` alias (mirrors `git clone` UX) could be added as a zero-implementation alias. **Decision: alias or not?** Current spec: no alias, keep the surface small.

4. HF partial-file PATCH for JSONL rows. The HF commit endpoint requires whole-file content (base64 inline or LFS). There is no row-level PATCH. **Resolution: always rewrite the whole JSONL file on push.** This is consistent with the parent spec §11 ("JSONL line edits … rewriting the whole file is acceptable for now"). No change needed.

5. `prompt` policy UX for non-interactive environments. The spec writes conflict files and exits 4. In a TTY, the user is left to resolve manually. **Future work:** interactive per-conflict diff+choice when `--interactive` is passed and `stdout` is a TTY. Not in scope for Phase 3.

6. Git history bloat for Parquet datasets. Committing a new Parquet snapshot every day balloons history. The spec recommends `git checkout --orphan` periodically or using HF (LFS deduplication). This is a documentation/guidance issue, not a code issue — no transport changes needed.

7. Concurrent pushes from two machines. The divergence check catches the second push. The first push wins; the second operator must pull, merge, and re-push. The `newer` strategy in `dataset merge` gives a sensible default.

8. `HuggingFaceClient` refactor collision. The Phase 7 export design (`2026-05-16-phase7-export-design.md`) describes `HuggingFaceClient` using the older single-file upload endpoint (`/api/datasets/{repo}/upload/{revision}/{path}`). Phase 2 adds the multi-file commit endpoint methods alongside, without removing the existing method. If Phase 7 and Phase 2 land from separate branches, the merge is additive and conflict-free. No coordination required beyond standard PR review.

9. ONNX provider (`2026-05-18-onnx-provider-design.md`) uses `HuggingFaceModelCache` (separate class in `DistSharp.Providers/Onnx/`). It does not touch `HuggingFaceClient`. No collision.

---

## 10. Out of Scope

- **`dataset sync` Phase 4.** The orchestrator (`DatasetSyncOrchestrator`) already ships (Phase 1). Wiring it to `IDatasetTransport` for pre-pull and post-push is Phase 4 of the parent spec; the transport injection point is stubbed here but not implemented.
- **Streaming row-level push.** The unit of a push commit is a full snapshot, not individual rows.
- **Argilla / Label Studio integration.** Curation tooling is a separate concern.
- **Federated pull across multiple HF repos.** Achievable by composing `pull` + `merge`; no new primitive.
- **Pull-request creation on HF or GitHub.** URLs are printed; PR creation is not automated.
- **Repo deletion or branch management.** The transport is append/overwrite only.
- **Interactive TTY merge (`--interactive` flag for `prompt` policy).** Future work.
- **`dataset clone` alias.** `pull --into <dir>` covers the use case. An alias can be added later with zero implementation cost if demand exists.
- **LibGit2Sharp integration.** Evaluated and rejected for Phase 2; re-evaluate if full git semantics are needed.
