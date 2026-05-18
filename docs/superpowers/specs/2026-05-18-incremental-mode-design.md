# DistSharp — Incremental Mode: Decision Spec

**Date:** 2026-05-18
**Status:** Decision: deprecate, see README.
**Author:** agent
**Supersedes:** roadmap item "Incremental mode — only re-generate rows for files changed since the last run"

---

## 1. Decision

**Option A — Deprecate the incremental-mode roadmap item. `dataset sync` already solves this; incremental mode as a separate feature should not be built.**

### Justification

The roadmap item predates `dataset sync`. Its stated goal — "only re-generate rows for files changed since the last run" — is precisely what `dataset sync` does, only better:

- `dataset sync` triggers on symbol *body-hash* change, not on file mtime or git-diff. Body-hash is strictly more accurate: it ignores whitespace-only changes and comment edits that don't affect generated output, and it catches in-memory edits that haven't been committed.
- DistSharp's unit of generation is the Roslyn `ExtractedSymbol`, not the file. A file can contain dozens of symbols; incremental-by-file would regenerate all of them when only one changed.
- The manifest written by every `generate` run is the state store "incremental mode" would have had to invent anyway. It already exists.

The only argument for Option C (file-mtime/git-diff trigger, no manifest) would be a CI flow that cannot persist a manifest between runs. That scenario collapses on inspection: without prior state there is nothing to skip against; you would run a full generate. If the manifest *is* persisted (e.g. committed to the dataset repo or restored from a CI cache), `dataset sync` already handles the case correctly.

**Rejected paths are documented in §2 (comparison table) and §3 (rejected reasoning).**

---

## 2. Comparison table

| Dimension | `dataset sync` (today) | Hypothetical incremental mode (Option C) |
|---|---|---|
| **Trigger granularity** | Symbol body-hash (per-symbol, content-addressed) | File mtime *or* `git diff` (per-file, structural) |
| **Skip accuracy** | Exact — only skips if the symbol body is byte-for-byte unchanged after normalisation | False negatives possible (file touched but relevant symbols unchanged); false positives impossible but irrelevant |
| **False regenerations** | None — a whitespace-only change produces the same body-hash and is skipped | Present — any file change, even a comment edit, marks all symbols in that file as dirty |
| **State store required** | Yes — `_distsharp/manifest.json` (auto-written by `generate`) | Yes — either a manifest or a file-mtime/hash cache. Needs inventing |
| **CI without persistent state** | Full regenerate (sync falls back gracefully — no manifest ⟹ all rows are "new") | Same — no previous state, no skipping |
| **CLI surface** | `distsharp dataset sync <dir> --solution <sln>` | Would add `distsharp generate --incremental` |
| **Transport** | Optional `--push` / `--pull` for remote dataset repos | Would be generate-local only |
| **Orphan handling** | Yes (`drop` / `keep` / `archive`) | Not defined |
| **Merge / conflict policy** | Planned (Phase 2 of sync design) | Out of scope |
| **Implementation cost** | Already shipped (Phase 1) | Estimated 3–5 days for a weaker feature |
| **Trigger reliability across branch switches** | High — body-hash is stable regardless of branch | Low — file mtime is reset by `git checkout`; git-diff misses uncommitted edits |
| **What it does better** | Everything above | Nothing — no dimension where file-level triggering outperforms body-hash at the symbol level |

**Verdict:** `dataset sync` is a strict superset of what incremental mode would have delivered. Incremental mode would have been a weaker feature that duplicated state management already in the manifest.

---

## 3. Rejected paths

### Option B — `generate --incremental` as a thin wrapper around `dataset sync`

Rejected because:

1. `generate` is a forward-only batch command. Teaching it about manifests, datasets, and sync semantics blurs the command model. Users who want incremental behaviour should reach for `dataset sync`, not a flag on `generate`.
2. "Wrapper" implies a simpler interface to the same thing. But `dataset sync` already has a simple interface (`--dry-run`, `--orphan-policy`). Adding a `--incremental` flag to `generate` would be a parallel entry point with no new capability — confusion without value.
3. "Only adds rows, never modifies" (the restricted semantics mooted in the task) is strictly weaker than sync's full new+stale+unchanged+orphan diff. It would leave stale rows in the dataset indefinitely, which is a correctness regression.

### Option C — Separate incremental mode with file-mtime / git-diff trigger

Rejected because:

1. File-mtime is unreliable across branch switches, build-system touches, and OS-level operations. `git checkout` resets mtimes. Build artifact generation can touch source files.
2. `git diff` against a fixed ref (e.g. `HEAD` or a tag) only captures committed changes — uncommitted work-in-progress is invisible. The manifest's body-hash catches these cases.
3. Both trigger mechanisms operate at file granularity, not symbol granularity. A 500-symbol file with one changed method would mark 499 symbols as dirty unnecessarily.
4. Either mechanism requires a separate state cache. The manifest is a strictly better state cache for this problem and already exists.
5. The one cited use case — "CI flow that doesn't persist a manifest between runs" — does not require a special mode. `dataset sync` with no existing manifest already behaves as a full regenerate. Add manifest persistence to the CI cache (one `actions/cache` entry) and it becomes incremental automatically.

---

## 4. Chosen path design

**No new code is required.** The decision is:

1. **Remove** the roadmap item from `README.md` (the parenthetical note already acknowledges the overlap).
2. **Add** a cross-reference in the `dataset sync` section of the README pointing to this decision record.
3. **Preserve** this spec as a permanent decision record so the rationale is not lost.

### Migration story for anyone expecting incremental mode

If you were waiting for incremental mode, `dataset sync` is the feature:

```bash
# First time: seed the manifest for an existing dataset (no LLM calls)
distsharp dataset migrate ./my-dataset --solution ./MyApp.sln

# Subsequent runs: regenerate only what changed
distsharp dataset sync ./my-dataset --solution ./MyApp.sln

# Preview without spending tokens
distsharp dataset sync ./my-dataset --solution ./MyApp.sln --dry-run
```

If you never ran `generate` before, just run `generate` once — it writes the manifest automatically, and every subsequent `dataset sync` is incremental.

### What tells a future contributor not to re-add it?

Two gates:

1. This spec file exists at a permanent path (`docs/superpowers/specs/2026-05-18-incremental-mode-design.md`) and is referenced from the README roadmap section. Any search for "incremental" in the docs will surface it.
2. The roadmap item in `README.md` should be edited to read: ~~Incremental mode~~ — *superseded by `dataset sync`; see [`docs/superpowers/specs/2026-05-18-incremental-mode-design.md`](docs/superpowers/specs/2026-05-18-incremental-mode-design.md).*

---

## 5. Components & file layout

None. No new production code is required.

The only repository change is a README edit (see §4 — not a deliverable of this spec, just the follow-on action).

---

## 6. CLI surface

No new flags. Existing `dataset sync` surface is sufficient:

```bash
distsharp dataset sync <dataset-dir> \
  --solution <sln>                     # required
  [--orphan-policy drop|keep|archive]  # default: drop
  [--dry-run]                          # print plan, no LLM calls
```

If `--incremental` shorthand on `generate` is ever requested for ergonomic reasons, it should be a forwarding alias to `dataset sync` with `--push` omitted — not a new implementation.

---

## 7. Effort

**Near-zero** — this is Option A.

| Task | Estimate |
|---|---|
| Write this spec | Done |
| Edit README to strike through roadmap item and add cross-reference | 15 minutes |
| **Total** | **15 minutes** |

The alternative (Option C) was estimated at 3–5 days for a feature strictly weaker than what already ships.

---

## 8. Risks & open questions

1. **(BLOCKING) README roadmap edit** — Someone needs to update the roadmap item in `README.md` to mark incremental mode as superseded and link this spec. This is a trivial edit but should be done before the next release so the shipped README does not advertise a feature that will never arrive in its described form.

2. **Discoverability of `dataset sync` for incremental use cases** — The `dataset sync` docs in `README.md` lead with "regenerating only rows whose source symbol has changed since the dataset was last produced." That framing already covers what incremental mode promised. No additional discoverability work is needed unless user confusion is observed.

3. **CI cache configuration guidance** — The argument that `dataset sync` covers the CI case assumes the manifest is persisted between CI runs. This is not automatic. A brief note in the docs (or the README's `dataset sync` section) pointing at a cache-restore snippet would close this gap. Not blocking — the sync command already degrades gracefully to a full regenerate when no manifest exists.

4. **`generate --incremental` ergonomics** — Some users may reach for `generate --incremental` as muscle memory from other tools. If this becomes a support friction point, add it as an alias that prints a deprecation notice and invokes `dataset sync`. Not needed now.

---

## 9. Out of scope

- Implementing any variant of incremental mode.
- Changing the `generate` command.
- File-mtime or git-diff trigger logic.
- CI integration documentation beyond a brief mention.
- Changes to `dataset sync` itself — Phase 1 already implements the required semantics. Phase 2 (pull/push transports) is a separate workstream.
