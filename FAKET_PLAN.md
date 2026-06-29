# Faket — Modernization & Fork Plan

A fork of [fsprojects/Paket](https://github.com/fsprojects/Paket) (forked at v10.3.1) with four
objectives:

1. **Target .NET 10 only** — drop `net461`, `netstandard2.0`, `netcoreapp2.1`, the C# bootstrapper,
   ILRepack merging, mono, and all the conditional-compilation cruft that supports them.
2. **System.Text.Json** instead of Newtonsoft.Json.
3. **Nix-friendly lock file** — write package content hashes into `paket.lock` so Nix can pin
   packages reproducibly.
4. **Compose multiple dependency files** — an `include` directive so `paket.dependencies` can be
   split/shared and merged.
5. **`faket migrate`** — one-shot migration of an existing Paket setup to Faket for straightforward cases.

Each phase is independently shippable. Recommended order: 1 → 2 → 3 → 4 → 5 (rename last).

---

## Baseline facts (verified in the v10.3.1 clone)

- **~31k lines** of F# in `src/`, 5 projects (`Paket.Core`, `Paket`, `FSharp.DependencyManager.Paket`,
  `LockFileComparer`, + C# `Paket.Bootstrapper`).
- TFMs today: Core `net461;netstandard2.0`; CLI `net461;net9`; bootstrapper `net461;net9` (+`netcoreapp2.1`
  global-tool); FSI dep mgr `netstandard2.0`; LockFileComparer `net9`.
- JSON: **100% Newtonsoft.Json**, 8 files. Zero `System.Text.Json` today.
- Hashing utilities **already exist**: `Utils.getSha512File` / `getSha512Stream`
  (`src/Paket.Core/Common/Utils.fs:711`) — base64 SHA512, matching NuGet's `.nupkg.sha512`.
- Lock file stores **no hashes** today.
- Dependency files already support **groups** with a `CombineWith` merge — the foundation for compose.

---

## Phase 1 — Collapse to .NET 10 (mostly deletion)

**Project files → single `<TargetFramework>net10.0</TargetFramework>`:**
- `src/Paket.Core/Paket.Core.fsproj` — also remove `DefineConstants` blocks (lines ~4, ~21), the
  net461-only `app.config` include (~113) and `System.*` references (~117–121).
- `src/Paket/Paket.fsproj` — drop `net461`, delete `src/Paket/App.config`.
- `src/FSharp.DependencyManager.Paket/...fsproj` — `netstandard2.0` → `net10.0`.
- `src/LockFileComparer/...fsproj` — `net9` → `net10.0`.
- Tests/integration tests — drop `net461`, bump to `net10.0`.

**Delete outright:**
- `src/Paket.Bootstrapper/` (whole project) + `tests/Paket.Bootstrapper.Tests/` + their solution entries.
- `src/Paket.Core/app.config`, `src/Paket/App.config` (binding redirects — irrelevant on .NET 10).
- `.travis.yml`, `appveyor.yml`, `install.sh`, `build.cmd` (mono cert import path).
- ILRepack `MergePaketTool` target (`build.fsx` lines ~284–326) and bootstrapper publish/upload steps
  (~210–224, ~443, ~660). On .NET 10 we ship `dotnet pack` global tool + a self-contained publish; no
  assembly merging.

**Conditional-compilation cleanup (keep the modern arm, delete the rest):**
- Symbols to eliminate: `DOTNETCORE`, `NETSTANDARD1_5`/`1_6`/`2_0`, `NO_BOOTSTRAPPER`,
  `NO_CONFIGURATIONMANAGER`, `CUSTOM_WEBPROXY`, `NO_MAXCONNECTIONPERSERVER`, `USE_WEB_CLIENT_FOR_UPLOAD`,
  all mono detection.
- Heaviest files: `Common/Utils.fs` (~21 directives, `isMonoRuntime`), `Common/NetUtils.fs` (~21,
  proxy/WebClient/HTTP), `Versioning/Requirements.fs` (~10), `Dependencies/RemoteUpload.fs` (~7),
  `Common/ProcessHelper.fs` (mono `.exe` wrapping). Net effect is a large simplification.

**Build system:** `build.fsx` is FAKE-based and dated. Decision needed (see Open Questions) — either
trim it to the .NET 10 targets or replace with a thin script + `dotnet` CLI. Update test runner refs
that still say `netcoreapp3.0` (`build.fsx` ~251, ~254).

*Risk:* low. Almost entirely deletion. Biggest care points are `NetUtils.fs` proxy handling and
`ProcessHelper.fs` — verify the modern branch is self-sufficient once the `#if` is gone.

---

## Phase 2 — Newtonsoft.Json → System.Text.Json

8 files, all in `Paket.Core` unless noted:
`Dependencies/NuGetV2.fs`, `Dependencies/NuGetV3.fs`, `Dependencies/RemoteDownload.fs`,
`Dependencies/NuGetCache.fs`, `Installation/RestoreProcess.fs`, `PaketConfigFiles/RuntimeGraph.fs`,
`Versioning/CredentialProviders.fs`, and `src/LockFileComparer/Program.fs`.

Approach:
- DOM traversal (`JObject`/`JArray`/`JToken` over NuGet V3 responses) → `JsonNode`/`JsonDocument`.
  Not a find-replace; STJ access patterns differ (`node["x"]?.GetValue<string>()`).
- `[<JsonProperty("...")>]`-attributed record types (e.g. the V3 `Catalog` type, `NuGetV3.fs:607`) →
  `[<JsonPropertyName("...")>]` with `System.Text.Json.Serialization`.
- Watch behavioral gaps vs. Newtonsoft: case-insensitive property matching
  (`PropertyNameCaseInsensitive = true`), lenient number/`null` handling, trailing commas/comments
  (`AllowTrailingCommas`, `ReadCommentHandling`). Set these on a shared `JsonSerializerOptions`.
- Remove `Newtonsoft.Json` from `paket.references` / `paket.dependencies` / `paket.lock` after.

*Risk:* medium. NuGet feed responses are the real test surface — integration tests against the V3 API
are the safety net. Do this phase with those tests runnable.

---

## Phase 3 — Nix-friendly content hashes in paket.lock

**Goal:** every resolved package records a verifiable content hash so a `faket → nix` consumer can pin it.

**Design (keep lockfile format backward-compatible, parser tolerant):**

1. **Model** — add `ContentHash : string option` to `ResolvedPackage`
   (`Dependencies/PackageResolver.fs:59`). `option` so old lock files still parse.
2. **Serialize** — extend the per-package option list (`PaketConfigFiles/LockFile.fs:139–157`); the
   format is `Name (version) - key: value, key: value`. Append `sha512: <base64>` when present. The
   existing `key: value` settings infra means the parser already tolerates unknown keys gracefully.
3. **Parse** — read it back at `LockFile.fs:~502` (there's a literal `// TODO: write stuff into the
   lockfile and read it here` marker there). Extract `sha512:` into the new field.
4. **Source the hash, cheapest-first:**
   - **(a)** Reuse the `.nupkg.sha512` NuGet already writes into the global package cache — free, no
     re-hash. This is base64 SHA512, exactly NuGet's authoritative value.
   - **(b)** Else read server-provided `packageHash`/`packageHashAlgorithm` from the V3 catalog (not
     currently parsed — add fields to the `Catalog` type, `NuGetV3.fs:607`).
   - **(c)** Else compute `Utils.getSha512File targetFileName` at the download chokepoint
     (`Dependencies/NuGet.fs:~994`, right after the tmp→final `File.Move`).
5. **Nix export** — store the canonical NuGet SHA512 in the lock, and add a `faket nix` command that
   emits whatever format Nix tooling wants (SRI `sha256-…`/`sha512-…`, or a `deps.nix`/`deps.json`).
   Decoupling the lock format from Nix's exact expectations keeps the lock clean and the export flexible.

*Risk:* low–medium. Format change is additive and old locks keep working. Main correctness concern: make
sure the hash recorded is of the *as-downloaded .nupkg* (what Nix re-fetches and verifies), which (a)/(c)
both guarantee.

---

## Phase 4 — Compose multiple `paket.dependencies` files (`include`)

**Today:** no include/import of dependency files exists. `import_targets` is a per-package option;
`external_lock` references resolved lock outputs, not source files. The composition primitive that *does*
exist is **groups** with `DependenciesGroup.CombineWith` (`Dependencies/DependenciesTypes.fs:48–60`),
which already merges sources (distinct), caches, packages, options, and remote files.

**Design:**
1. New top-level directive, e.g. `include ./shared/common.paket.dependencies` (valid at file scope and
   inside a `group`). Parsed in `DependenciesFileParser.parseLine` (`DependenciesFileParser.fs:490–585`),
   alongside the existing `Group`/`Remote`/`Package` cases.
2. In `DependenciesFile.ReadFromFile` (`PaketConfigFiles/DependenciesFile.fs:814–824`), resolve includes
   **recursively** relative to the including file, parse each, and merge group maps via the existing
   `CombineWith`. This reuses the proven merge semantics — minimal new resolution logic.
3. **Cycle detection** (A includes B includes A) — track a visited set of canonical paths; error on cycle.
4. **Source/package dedup** — sources are already `distinct`; duplicate packages already warn on parse
   (existing behavior). Keep that.
5. **Resolution & lock** — the merged in-memory `DependenciesFile` resolves to a **single** `paket.lock`
   as today. Composition is a parse-time merge; the resolver is untouched.

**The real wrinkle — editing semantics.** `DependenciesFile` tracks original text lines for in-place
edits (`paket add`/`remove`). With includes, "which file does `paket add` write to?" is ambiguous.
Proposal: edits always target the **root** file; included files are read-only from the CLI's edit
commands (document this; error clearly if a user tries to remove a package that lives in an include).

*Risk:* medium — the feature itself is small, but the edit-path interaction with the line-tracking model
needs care and tests. Parsing/merging is low-risk (built on `CombineWith`).

---

## Phase 5 — Rebrand Paket → Faket

Last, once it builds and tests pass: project/assembly/package ids, CLI exe name, namespaces (or keep
`Paket` namespaces internally to ease upstream merges — decision needed), README/docs, NuGet package
metadata, `git init` fresh history (or keep upstream history for attribution). Keep `LICENSE.txt`
(Paket is MIT) with upstream attribution.

---

## Phase 6 — `faket migrate` (Paket → Faket)

**Why it's mostly easy:** Faket keeps Paket's *file formats* (`paket.dependencies`/`lock`/`references`
parse unchanged). So migration is overwhelmingly about the **tooling + MSBuild integration** layer, not
the dependency data. What a typical Paket consumer repo carries today:

- `.paket/Paket.Restore.targets` — the 36KB MSBuild integration (shipped from
  `src/Paket.Core/embedded/Paket.Restore.targets`). It locates/invokes paket three ways:
  `PaketBootStrapperExePath` → `paket.bootstrapper.exe` (legacy), `PaketExePath` → `paket.exe`, and
  `dotnet paket` (local tool).
- Often a legacy `.paket/paket.bootstrapper.exe` and/or `.paket/paket.exe`.
- A `.config/dotnet-tools.json` with a `paket` tool entry.
- `paket.dependencies`, `paket.lock`, `paket.references`.

**What `faket migrate` does (dry-run by default, `--apply` to write, idempotent):**

1. **Detect** a Paket setup (`.paket/` + dependency files); print a plan and a "supported vs. needs-manual"
   classification before touching anything.
2. **Tooling:** rewrite `.config/dotnet-tools.json` — replace the `paket` tool/command with `faket` (or
   add Faket alongside). Remove `.paket/paket.bootstrapper.exe` and `.paket/paket.exe` and the
   bootstrapper code paths.
3. **MSBuild:** drop in Faket's `Faket.Restore.targets` (the rebranded embedded targets) and remove the
   old `Paket.Restore.targets` / `paket.targets`; rewrite project `<Import>` lines that point at the old
   targets. This is just re-running `faket install` plus cleanup of the legacy bootstrapper-based import.
4. **Lock hashes (ties to Phase 3):** offer to backfill `sha512:` into the existing `paket.lock` without
   a full re-resolve (read from the global cache `.nupkg.sha512`, else fetch+hash). Opt-in flag.
5. **Filenames:** by default keep `paket.*` names (zero-churn, drop-in). Optional `--rename` to move to
   `faket.*` (requires Faket to read both conventions — see Open Questions).
6. **Bail loudly on non-straightforward cases** rather than guessing: custom bootstrapper args / magic
   mode, non-standard targets locations, hand-edited restore targets, `paket.local` overrides, GAC/`gist`
   exotica. Report them with file:line and a "do this by hand" note. **No silent partial migration.**

**Depends on:** Phase 1 (no bootstrapper), Phase 5 (the `faket` name + `Faket.Restore.targets`), and
Phase 3 for the optional hash backfill. Implement as an Argu verb in `src/Paket/Commands.fs` reusing the
existing install/restore plumbing (`InstallProcess.fs`, `RestoreProcess.fs`, `Environment.fs`).

- ✅ Gate: integration scenarios under `integrationtests/scenarios/` for (a) bootstrapper-based repo,
  (b) `dotnet tool` paket repo, (c) `paket.exe` repo — each migrates and then `faket restore` +
  `dotnet build` succeed; (d) an exotic repo is correctly *refused* with a clear report;
  (e) re-running `faket migrate` is a no-op.

## Open questions / decisions for later

- **Build system:** trim `build.fsx` (FAKE) vs. replace with `dotnet` CLI + thin script.
- **Namespace rename:** rename to `Faket.*` (clean) vs. keep `Paket.*` internally (easier to pull
  upstream fixes).
- **Git history:** fresh `git init` vs. preserve upstream history.
- **Nix export format:** confirm what the target Nix tooling (nixpkgs `fetchNuGet`, `dotnet2nix`,
  custom) actually consumes before finalizing the `faket nix` output shape.
- **Compose edit semantics:** confirm "edits target root file only" is acceptable.
- **Filenames:** keep `paket.dependencies`/`lock`/`references` for drop-in compatibility, or rename to
  `faket.*`? If renaming is offered, Faket must read **both** conventions (prefer `faket.*`, fall back to
  `paket.*`). This gates `faket migrate --rename`.

## Suggested first slice

Phase 1 on its own is a satisfying, low-risk, high-impact PR: the project shrinks dramatically, the build
graph simplifies to one TFM, and everything after it is easier. I'd start there.

---
---

# Implementation breakdown (step-by-step)

Each phase below is an ordered checklist. "✅ Gate" lines are the verification that must pass before
moving on. Commit at each gate.

## Phase 0 — Fork bootstrap (prerequisite, ~½ day)

1. Copy the working tree to the fork location (decide where; default `../Faket`).
2. `git init` fresh, or `git remote rename origin upstream` + add new `origin` (decision: fresh vs.
   preserved history — keep upstream as `upstream` either way so you can pull fixes).
3. Install the .NET 10 SDK; update `global.json` `sdk.version` → a `10.0.x` and keep
   `rollForward: latestMinor`.
4. Commit the untouched baseline as the fork's first commit so every later diff is reviewable.
- ✅ Gate: `dotnet --version` reports 10.x; repo builds *as-is* under the net9 targets (baseline green).

## Phase 1 — Collapse to net10.0

Do in this sub-order so the build never stays broken longer than one step.

1. **Delete the bootstrapper surface first** (it's self-contained):
   - Remove `src/Paket.Bootstrapper/` and `tests/Paket.Bootstrapper.Tests/`.
   - Remove both from `Paket.sln`.
   - Remove bootstrapper targets/refs in `build.fsx` (~210–224, ~443, ~660), `README.md`,
     `docs/content/bootstrapper.md`, `install.sh`.
   - ✅ Gate: `dotnet build Paket.sln` still succeeds (bootstrapper was leaf).
2. **Retarget projects** one at a time, building after each:
   - `FSharp.DependencyManager.Paket` `netstandard2.0`→`net10.0`; then `LockFileComparer` `net9`→`net10.0`.
   - `Paket.Core`: TFMs→`net10.0`; delete the `Choose`/`When` net461 `DefineConstants`,
     `app.config` include, and `System.*` reference block.
   - `Paket`: TFMs→`net10.0`; delete `App.config`.
   - Test + integration-test projects → `net10.0`.
3. **Strip conditional compilation** — work file-by-file, keeping the non-net461/modern arm:
   - Order by density: `NetUtils.fs`, `Utils.fs`, `Requirements.fs`, `RemoteUpload.fs`, `ProcessHelper.fs`,
     `Constants.fs`, then the rest.
   - For each `#if SYMBOL / #else / #endif`: keep the branch that compiled under net9, delete the other.
     Symbols to treat as **false**: `NET461`, `USE_WEB_CLIENT_FOR_UPLOAD`, `NO_MAXCONNECTIONPERSERVER`,
     `NETSTANDARD*`. As **true**: `DOTNETCORE`, `NO_BOOTSTRAPPER`, `NO_CONFIGURATIONMANAGER`,
     `CUSTOM_WEBPROXY`.
   - Delete mono logic entirely: `isMonoRuntime` (`Utils.fs`), mono path/arg wrapping (`ProcessHelper.fs`),
     mono home (`Constants.fs`).
   - Build after each file — F# fails fast on a wrong arm.
4. **Delete dead infra:** `.travis.yml`, `appveyor.yml`, `build.cmd`, mono cert block in `build.sh`,
   `install.sh` mono `exec`.
5. **Build system:** minimal pass now — trim `build.fsx` to net10 targets, remove the ILRepack
   `MergePaketTool` target, fix `netcoreapp3.0` test refs (~251, ~254). (Full FAKE-vs-CLI decision can
   come later; just get it green.)
6. **Packaging:** replace ILRepack output with `dotnet pack` global tool (`PackAsTool=true`,
   `ToolCommandName=faket` later) and/or `dotnet publish` self-contained.
- ✅ Gate: `dotnet build` clean; `dotnet test tests/Paket.Tests` green; a `dotnet pack` produces a
  runnable tool; `grep -rn "#if" src` shows only intentional `DEBUG`.

## Phase 2 — System.Text.Json

1. Add a shared `JsonSerializerOptions` (case-insensitive, `AllowTrailingCommas`,
   `ReadCommentHandling = Skip`) in a small `Common/Json.fs` helper to match Newtonsoft leniency.
2. Convert record DTOs: `[<JsonProperty("x")>]`→`[<JsonPropertyName("x")>]`
   (`open System.Text.Json.Serialization`). Primary site: `Catalog` and friends in `NuGetV3.fs`.
3. Convert DOM traversal file-by-file in this order (easiest→hairiest):
   `CredentialProviders.fs`, `RuntimeGraph.fs`, `NuGetCache.fs`, `RemoteDownload.fs`,
   `RestoreProcess.fs`, `NuGetV2.fs`, `NuGetV3.fs`, then `LockFileComparer/Program.fs`.
   - `JObject o → o["k"]`, `JArray → EnumerateArray()`, `(string)token → token.GetValue<string>()`,
     null-tolerant access via `?.`.
4. Remove the `Newtonsoft.Json` line from `paket.dependencies` + each `paket.references`; re-resolve so
   it leaves `paket.lock`.
- ✅ Gate: build clean; unit tests green; **integration tests that hit the V3 API green** (this is the
  real proof); `grep -rn Newtonsoft src` empty.

## Phase 3 — Nix content hashes

1. Add `ContentHash : string option` to `ResolvedPackage` (`PackageResolver.fs:59`); thread the
   default `None` through every constructor/copy site (compiler will list them).
2. **Read existing hashes (no new download):** in the cache/restore path, after a package is in the
   global folder, read its sibling `<id>.<version>.nupkg.sha512`; populate `ContentHash`.
3. **Fallback compute:** at the download chokepoint `NuGet.fs:~994` (post tmp→final `File.Move`), if no
   `.sha512` present, `Utils.getSha512File targetFileName`.
4. **Serialize:** in `LockFile.fs:139–157`, append `, sha512: <value>` to the per-package option string
   when `ContentHash.IsSome`.
5. **Parse:** at the `// TODO` marker `LockFile.fs:~502`, pull `sha512:` out of the parsed key/value
   options into `ContentHash` (unknown keys already tolerated → backward compatible).
6. **(Optional) server hash:** add `packageHash`/`packageHashAlgorithm` to the V3 `Catalog`
   (`NuGetV3.fs:607`) to prefill without touching disk.
7. **`faket nix` command:** new Argu verb in `src/Paket/Commands.fs` + handler that reads `paket.lock`
   and emits the Nix consumer format (confirm target format first — see Open Questions). Convert base64
   SHA512 → SRI (`sha512-<base64>`) or `deps.nix`/`deps.json` as needed.
- ✅ Gate: round-trip test (write lock with hash → re-read → `ContentHash` preserved); old hash-less lock
  still parses; a sample `faket nix` output verifies against an actually-downloaded `.nupkg`.

## Phase 4 — `include` directive

1. **Parser:** add an `Include of string` case in `DependenciesFileParser.parseLine`
   (`DependenciesFileParser.fs:490–585`), recognizing `include <path>` at file scope and within groups.
2. **Resolution:** in `DependenciesFile.ReadFromFile` (`DependenciesFile.fs:814–824`), after parsing,
   resolve each include path relative to the including file, recursively `ReadFromFile`, and fold the
   included group maps into the current via `DependenciesGroup.CombineWith`
   (`DependenciesTypes.fs:48–60`).
3. **Cycle detection:** thread a `Set<canonical path>` through the recursion; raise a clear error on
   re-entry.
4. **Edit semantics:** make `paket add`/`remove` operate only on the root file's tracked lines; if a
   target package resolves to an included file, error with a message pointing at that file. Document the
   rule.
5. **Lock:** none needed — merge is parse-time; resolver and `paket.lock` are unchanged.
- ✅ Gate: unit tests for (a) basic include merge, (b) include inside a group, (c) cycle → error,
  (d) duplicate source dedup, (e) `paket add` writes root only; integration scenario under
  `integrationtests/scenarios/`.

## Phase 5 — Rebrand to Faket

1. Decide namespace strategy (Open Questions). If renaming: assembly names, root namespaces, NuGet
   package ids, `ToolCommandName=faket`, CLI banner/help.
2. Update `README.md`, `docs/`, `CLAUDE.md`, file headers.
3. NuGet metadata (authors, repo url, description) + keep `LICENSE.txt` with upstream attribution.
4. Tag `v0.1.0` (or continue from upstream version with a `-faket` suffix — decision).
- ✅ Gate: `dotnet pack` yields `Faket`-branded packages; `faket --help` runs; full test suite green.

## Rough sequencing

| Phase | Risk | Rough size | Depends on |
|-------|------|-----------|------------|
| 0 bootstrap | low | ½ day | — |
| 1 net10 collapse | low | 2–4 days | 0 |
| 2 STJ | medium | 2–3 days | 1 |
| 3 nix hashes | low–med | 1–2 days | 1 (2 helps) |
| 4 include | medium | 2–3 days | 1 |
| 5 rebrand | low | 1 day | 1–4 |
| 6 migrate | medium | 2–3 days | 1, 5 (3 optional) |
