# Ollama AMD Vulkan Manager — Agent Development Archive

Complete record of AI-assisted greenfield development from initial planning through Phase 3.
Generated: **2026-06-24** (session `019ef98d-ebd9-7ea0-b305-ee7496b813b8`).

> **Machine-readable full transcript:**  
> `C:\Users\lewis\.grok\sessions\C%3A%5CUsers%5Clewis\019ef98d-ebd9-7ea0-b305-ee7496b813b8\updates.jsonl`  
> **Design plan (living document):**  
> `C:\Users\lewis\.grok\sessions\C%3A%5CUsers%5Clewis\019ef98d-ebd9-7ea0-b305-ee7496b813b8\plan.md`

---

## Table of contents

1. [User request arc](#1-user-request-arc)
2. [Planning phase — reasoning and decisions](#2-planning-phase--reasoning-and-decisions)
3. [Environment and git setup](#3-environment-and-git-setup)
4. [Phase 1 — Initial scaffold (`790e3a5`)](#4-phase-1--initial-scaffold-790e3a5)
5. [Phase 2 — Core toolkit implementation](#5-phase-2--core-toolkit-implementation)
6. [Phase 3 — Catalog libraries and remaining tabs (`ce23914`)](#6-phase-3--catalog-libraries-and-remaining-tabs-ce23914)
7. [Every shell command executed](#7-every-shell-command-executed)
8. [Errors encountered and fixes](#8-errors-encountered-and-fixes)
9. [Agent reasoning log (thought process)](#9-agent-reasoning-log-thought-process)
10. [Files created or modified (by phase)](#10-files-created-or-modified-by-phase)
11. [Remaining plan items](#11-remaining-plan-items)
12. [How to extend this archive](#12-how-to-extend-this-archive)

---

## 1. User request arc

| # | User message (paraphrased) | Agent action |
|---|---------------------------|--------------|
| 1 | Provided GUI layout and architecture reference for existing PowerShell Ollama AMD Vulkan toolkit | Read `Ollama-Mode-GUI.ps1` and companion `.ps1` modules; used as behavioral reference |
| 2 | Plan a **greenfield rebuild** — not patch PowerShell | Produced `plan.md`: C# + WPF (.NET 8), replace in place, git rollback preserved |
| 3 | Iterated plan: AI Settings tab, catalog categorization (metadata only), GUI responsiveness non-negotiable, Model Run in-app chat, 11 AI functions + AI Features toggles | Updated `plan.md` with hard responsiveness rules, AI function specs, tab layouts |
| 4 | **"execute initial build"** | Installed .NET 8 SDK, created solution scaffold, Core/Cli/App shell, first commit |
| 5 | **"continue build autonomously"** | Built BenchmarkStore, BenchmarkRunner, WPF tabs (Compute Modes, Models, Testing, Model Run, AI Activity, AI Features); hit CLI `import-reports` build error at session end |
| 6 | **"continue build autonomously"** (resumed after context compaction) | Fixed CLI; implemented ModelCatalog/ModelCategory/ModelRegistry; wired Model Library, Test Results, AI Settings tabs; committed `ce23914` |
| 7 | **"add a file to archive every command and thought process"** | Created this `DEVELOPMENT-ARCHIVE.md` |

---

## 2. Planning phase — reasoning and decisions

### 2.1 Greenfield definition

**Decision:** New C# + WPF codebase; do **not** incrementally patch PowerShell.

**Reasoning:**
- PowerShell WinForms GUI had startup freeze and threading issues (prior commit `bad4770` addressed PS fixes, but user wanted full rewrite).
- C# gives stronger typing, native WPF virtualization, and cleaner async/await for the responsiveness requirements.
- User data paths under `%USERPROFILE%\.ollama-amd-vulkan\` must remain compatible so existing `model-profiles.json`, reports, and AI caches survive the migration.

### 2.2 Technology stack

| Choice | Alternatives considered | Why chosen |
|--------|------------------------|------------|
| C# + WPF (.NET 8) | Patch PowerShell; WinUI 3; Avalonia | User explicitly chose C# + WPF; WPF is mature on Windows, good DataGrid virtualization |
| 9-project solution | Monolith single project | Mirrors PowerShell module separation (Core, Catalog, Category, Registry, AiAssist, etc.) |
| `BackgroundWorkQueue` (Channel) | `Task.Run` per action; `SemaphoreSlim` | Single worker prevents UI-thread blocking and serializes mode apply / pull / categorize |
| `InvokeAsync` only on dispatcher | `Dispatcher.Invoke` (sync) | Plan mandates no sync dispatcher calls except shutdown |

### 2.3 Non-negotiable: GUI responsiveness

Hard rules written into `plan.md`:

- UI thread never blocks (no sync I/O, no `.Wait()`, no `.Result`).
- Virtualized lists for 20+ rows.
- 30 fps throttled updates for streams and log tails.
- 300 ms debounce on catalog search.
- All JSON and HTTP off UI thread via `BackgroundWorkQueue`.

### 2.4 AI architecture

**Decision:** 11 toolkit AI functions share one `PreferredSummarizerModel` from AI Settings; each gated by `FeatureFlags` in `ai-settings.json`.

**Not toolkit AI:** 4-mode benchmark inference, Model Run user chat, model pull, mode switching.

**Default summarizer preference order** (from PowerShell `Ollama-Toolkit.ModelCatalog.ps1`):
1. `llama3.2:3b`
2. `phi4-mini:latest`
3. `qwen2.5-coder:7b`
4. `phi4-mini-reasoning:latest`

### 2.5 Model Run tab

**Decision:** Launch (Best Mode) opens in-app chat tab; external `cmd /k ollama run` is opt-in via **Open in PowerShell** button only.

---

## 3. Environment and git setup

### 3.1 Pre-build state

- Repo: `C:\Users\lewis\Ollama-AMD-Vulkan`
- Existing branch: `master` with PowerShell toolkit (`ce56315` initial, `bad4770` GUI freeze fix)
- **No .NET SDK** installed initially (`dotnet --version` → "No .NET SDKs were found")

### 3.2 .NET SDK installation

**Command:**
```powershell
winget install Microsoft.DotNet.SDK.8 --accept-package-agreements --accept-source-agreements
```

**Result:** SDK 8.0.422 installed to `C:\Program Files\dotnet\dotnet.exe`

**Verification:**
```powershell
& "C:\Program Files\dotnet\dotnet.exe" --list-sdks
```

### 3.3 Git branches

**Commands:**
```powershell
Set-Location "C:\Users\lewis\Ollama-AMD-Vulkan"
git checkout -b pre-csharp-greenfield-backup   # PowerShell snapshot
git checkout master
git checkout -b csharp-wpf-greenfield        # Active greenfield work
```

| Branch | Purpose |
|--------|---------|
| `master` | Original PowerShell toolkit |
| `pre-csharp-greenfield-backup` | Rollback snapshot before C# work |
| `csharp-wpf-greenfield` | Greenfield C# implementation (active) |

---

## 4. Phase 1 — Initial scaffold (`790e3a5`)

**Commit:** `790e3a5` — `feat: initial C# WPF greenfield scaffold (Core, Cli, App shell)`  
**Date:** 2026-06-24 06:04:52 -0700  
**Diff:** 36 files, +1622 lines

### 4.1 Solution creation commands

```powershell
Set-Location "C:\Users\lewis\Ollama-AMD-Vulkan"
New-Item -ItemType Directory -Path src -Force | Out-Null
dotnet new sln -n OllamaToolkit -o . --force
dotnet new classlib -n OllamaToolkit.Core -o src/OllamaToolkit.Core -f net8.0 --force
dotnet new classlib -n OllamaToolkit.BenchmarkStore -o src/OllamaToolkit.BenchmarkStore -f net8.0 --force
dotnet new classlib -n OllamaToolkit.ModelCatalog -o src/OllamaToolkit.ModelCatalog -f net8.0 --force
dotnet new classlib -n OllamaToolkit.ModelCategory -o src/OllamaToolkit.ModelCategory -f net8.0 --force
dotnet new classlib -n OllamaToolkit.AiAssist -o src/OllamaToolkit.AiAssist -f net8.0 --force
dotnet new classlib -n OllamaToolkit.ModelRegistry -o src/OllamaToolkit.ModelRegistry -f net8.0 --force
dotnet new classlib -n OllamaToolkit.BenchmarkRunner -o src/OllamaToolkit.BenchmarkRunner -f net8.0 --force
dotnet new console -n OllamaToolkit.Cli -o src/OllamaToolkit.Cli -f net8.0 --force
dotnet new wpf -n OllamaToolkit.App -o src/OllamaToolkit.App -f net8.0 --force
```

**Note:** `dotnet new wpf -f net8.0-windows` failed (invalid framework). Used `-f net8.0`; `TargetFramework` set to `net8.0-windows` in csproj.

```powershell
dotnet sln add src/OllamaToolkit.Core/OllamaToolkit.Core.csproj `
  src/OllamaToolkit.BenchmarkStore/OllamaToolkit.BenchmarkStore.csproj `
  src/OllamaToolkit.ModelCatalog/OllamaToolkit.ModelCatalog.csproj `
  src/OllamaToolkit.ModelCategory/OllamaToolkit.ModelCategory.csproj `
  src/OllamaToolkit.AiAssist/OllamaToolkit.AiAssist.csproj `
  src/OllamaToolkit.ModelRegistry/OllamaToolkit.ModelRegistry.csproj `
  src/OllamaToolkit.BenchmarkRunner/OllamaToolkit.BenchmarkRunner.csproj `
  src/OllamaToolkit.Cli/OllamaToolkit.Cli.csproj `
  src/OllamaToolkit.App/OllamaToolkit.App.csproj
```

### 4.2 Phase 1 deliverables

**OllamaToolkit.Core:**
- `ConfigPaths`, `JsonFileHelper`
- `VulkanDeviceDiscovery`, `VulkanDeviceMap` (fallback indices 0/1 if `vulkaninfo` fails)
- `ComputeMode`, `ModeDefinition`, `ModeDefinitionService`, `ModeService`
- `EnvBackupService` (namespace `OllamaToolkit.Core.EnvBackup` — renamed from `Environment` to avoid `System.Environment` conflict)
- `OllamaProcessService`, `OllamaApiClient` (tags, generate, benchmark generate)
- `AiSettingsDocument`, `AiSettingsService`

**OllamaToolkit.Cli:**
- Commands: `set-mode`, `vulkan-devices`, `status`, `help`

**OllamaToolkit.App:**
- Dark theme `Resources/Theme.xaml`
- `MainWindow` shell with placeholder tabs
- `BackgroundWorkQueue`

**Stubs:** ModelCatalog, ModelCategory, ModelRegistry (empty classlibs, compile-only)

**Launchers:** `launchers/Ollama-Mode-GUI.cmd`, `launchers/OllamaToolkit-Cli.cmd`; root `Ollama-Mode-GUI.cmd` delegates

### 4.3 Phase 1 verification

```powershell
dotnet build OllamaToolkit.sln -c Release
dotnet run --project src/OllamaToolkit.Cli/OllamaToolkit.Cli.csproj -c Release -- vulkan-devices
dotnet run --project src/OllamaToolkit.Cli/OllamaToolkit.Cli.csproj -c Release -- status
dotnet publish src/OllamaToolkit.App/OllamaToolkit.App.csproj -c Release -r win-x64 -o publish/OllamaToolkit.App
dotnet publish src/OllamaToolkit.Cli/OllamaToolkit.Cli.csproj -c Release -r win-x64 -o publish/OllamaToolkit.Cli
```

---

## 5. Phase 2 — Core toolkit implementation

**Status:** Built in session between `790e3a5` and `ce23914`; largely uncommitted until Phase 3 commit bundled the work.

### 5.1 Agent reasoning (Phase 2)

1. Read existing PowerShell `model-profiles.json` / `reports/**/report.json` format — BenchmarkStore must be compatible.
2. Implement `ProfileStoreService` against live Ollama `/api/tags` + cached profiles.
3. `AutomatedBenchmarkService`: sequential CPU → APU → GPU → Hybrid; write `report.json`; update profiles.
4. WPF: wire real tabs with `BackgroundWorkQueue`; never block UI thread.
5. `PlainLanguageErrorService` + `AiFeatureKeys` as first AiAssist services.
6. Model Run: streaming chat via `OllamaApiClient.ChatStreamAsync` + `IAsyncEnumerable`.
7. AI Features tab: dynamic checkboxes bound to `ai-settings.json` feature flags.

### 5.2 Phase 2 deliverables

**OllamaToolkit.BenchmarkStore:**
- `Models/BenchmarkModels.cs`
- `ProfileStoreService`, `ReportParser`, `ReportImporter`

**OllamaToolkit.BenchmarkRunner:**
- `AutomatedBenchmarkService`

**OllamaToolkit.AiAssist:**
- `AiFeatureKeys`, `PlainLanguageErrorService`

**OllamaToolkit.App:**
- `AppServices`, `ActivityLogService`, `UiDispatcher`
- Functional tabs: Compute Modes, Models & Launch, Testing Suite, Model Run, AI Activity, AI Features
- Placeholder tabs: Model Library, Test Results, AI Settings

**OllamaToolkit.Core additions:**
- `ToolkitPaths` (reports root, model profiles path)
- `OllamaApiClient.ChatStreamAsync`

### 5.3 Phase 2 build break (end of session)

**Error:** `CS0103: The name 'RunImportReportsAsync' does not exist` in `Program.cs` line 24.

**Cause:** `import-reports` added to command switch but function body not appended (botched edit / duplicate `return 0`).

**Fix (Phase 3):** Added at end of `Program.cs`:
```csharp
static async Task<int> RunImportReportsAsync()
{
    var importer = new ReportImporter();
    var count = await importer.ImportReportsAsync().ConfigureAwait(false);
    Console.WriteLine($"Imported {count} report(s).");
    return 0;
}
```

---

## 6. Phase 3 — Catalog libraries and remaining tabs (`ce23914`)

**Commit:** `ce23914` — `Phase 3: Model Library, Test Results, AI Settings, catalog libraries`  
**Date:** 2026-06-24 06:16:53 -0700  
**Diff:** 40 files, +3354 / -66 lines

### 6.1 Agent reasoning (Phase 3)

1. **Fix build first** — CLI `import-reports` blocking entire solution compile.
2. **Read PowerShell reference** — `Ollama-Toolkit.ModelRegistry.ps1` `ConvertFrom-OllamaLibraryListingHtml` for catalog HTML parsing; `Ollama-Toolkit.ModelCategory.ps1` for batch classification pattern.
3. **Implement libraries before UI** — ModelCatalog → ModelCategory → ModelRegistry → wire AppServices.
4. **Port categorization as metadata-only** — no web search in C# v1; heuristic fallback when no summarizer; batch LLM classify 15 models per `/api/generate`.
5. **Keep UI responsive** — catalog load on tab open via `BackgroundWorkQueue`; 300 ms search debounce; virtualized DataGrids.
6. **AiAssist expansion** — `BenchmarkInsightService` (post-benchmark + failure diagnosis), `LogAnomalyService` (startup scan + manual).
7. **Extend `OllamaApiClient`** — `PullAsync` for model download in Model Library and AI Settings.

### 6.2 New libraries

**OllamaToolkit.ModelCatalog:**
- `OllamaLibraryHtmlParser` — ports PS regex: `x-test-model` blocks, `href="/library/..."`, capability/size spans
- `LibraryCatalogStoreService` — fetch `https://ollama.com/library`, cache to `library-catalog-store.json`
- `DescriptionStoreService` — list description summarization → `model-descriptions.json`
- `SummarizerModelResolver` — user preference → default order → smallest installed model

**OllamaToolkit.ModelCategory:**
- `CategoryNormalizer` — 9 fixed categories + alias map (from PS)
- `UsageCategoryStoreService` — `model-usage-categories.json`
- `CatalogClassificationService` — batch classify, merge category into catalog store

**OllamaToolkit.ModelRegistry:**
- `ModelRegistryService` — unified `CatalogRowViewModel` + `TestResultRowViewModel`

**OllamaToolkit.AiAssist (added):**
- `BenchmarkInsightService` — `model-benchmark-insights.json`
- `LogAnomalyService` — `log-anomalies.json`

**OllamaToolkit.Core (extended):**
- `ConfigPaths` — all AI cache file paths
- `OllamaApiClient.PullAsync` — streaming pull with progress

### 6.3 WPF tabs implemented

| Tab | Features |
|-----|----------|
| **Model Library** | Virtualized grid, search (300 ms debounce), category filter, refresh, categorize all, download selected |
| **Test Results** | Per-mode CPU/APU/GPU/Hybrid columns, AI insight column, detail panel, Launch Selected |
| **AI Settings** | Summarizer combo, download/test, suggested models list, categorization status + shortcuts |
| **AI Activity** | Scan Logs button, anomaly summary banner, 30 s startup auto-scan |

### 6.4 Phase 3 verification

```powershell
cd "C:\Users\lewis\Ollama-AMD-Vulkan"
dotnet build OllamaToolkit.sln -c Release
# Build succeeded: 0 Warning(s), 0 Error(s)

dotnet publish src/OllamaToolkit.App/OllamaToolkit.App.csproj -c Release -r win-x64 -o publish/OllamaToolkit.App
dotnet publish src/OllamaToolkit.Cli/OllamaToolkit.Cli.csproj -c Release -r win-x64 -o publish/OllamaToolkit.Cli

git add src/ BUILD.md
git commit -m "Phase 3: Model Library, Test Results, AI Settings, catalog libraries"
```

---

## 7. Every shell command executed

All unique shell commands run by the agent in this session (chronological groups):

### Git
```powershell
cd "C:\Users\lewis\Ollama-AMD-Vulkan" && git log --oneline -10 2>&1; git status --short 2>&1   # FAILED: PowerShell && syntax
Set-Location "C:\Users\lewis\Ollama-AMD-Vulkan"; git log --oneline -10; git status --short
Set-Location "C:\Users\lewis\Ollama-AMD-Vulkan"; git status --short; git branch -a
Set-Location "C:\Users\lewis\Ollama-AMD-Vulkan"; git checkout -b pre-csharp-greenfield-backup; git checkout master; git checkout -b csharp-wpf-greenfield
Set-Location "C:\Users\lewis\Ollama-AMD-Vulkan"; git add OllamaToolkit.sln src/ launchers/ BUILD.md Ollama-Mode-GUI.cmd .gitignore; git status --short; git commit -m "feat: initial C# WPF greenfield scaffold (Core, Cli, App shell)"
cd "C:\Users\lewis\Ollama-AMD-Vulkan"; git status; git branch --show-current
cd "C:\Users\lewis\Ollama-AMD-Vulkan"; git add src/ BUILD.md; git commit -m "Phase 3: Model Library, Test Results, AI Settings, catalog libraries"
cd "C:\Users\lewis\Ollama-AMD-Vulkan"; git log --oneline --all -20
cd "C:\Users\lewis\Ollama-AMD-Vulkan"; git log --format="=== %h %s (%ci) ===" --stat pre-csharp-greenfield-backup..HEAD
```

### .NET SDK
```powershell
dotnet --version                                    # FAILED: no SDK
where.exe dotnet 2>$null; Get-Command dotnet ...
winget search "Microsoft.DotNet.SDK.8" ...
winget install Microsoft.DotNet.SDK.8 --accept-package-agreements --accept-source-agreements
& "C:\Program Files\dotnet\dotnet.exe" --list-sdks
& "C:\Program Files\dotnet\dotnet.exe" --list-runtimes
```

### Solution scaffold
```powershell
# dotnet new sln, classlib x7, console, wpf (see Phase 1 section)
dotnet sln add [all 9 projects]
# Remove Class1.cs stubs; normalize csproj references
```

### Build / run / publish
```powershell
dotnet build OllamaToolkit.sln -c Release
dotnet build OllamaToolkit.sln -c Release -v q
cd "C:\Users\lewis\Ollama-AMD-Vulkan"; dotnet build
cd "C:\Users\lewis\Ollama-AMD-Vulkan"; dotnet build OllamaToolkit.sln -c Release
dotnet run --project src/OllamaToolkit.Cli/OllamaToolkit.Cli.csproj -c Release -- vulkan-devices
dotnet run --project src/OllamaToolkit.Cli/OllamaToolkit.Cli.csproj -c Release -- status
dotnet run --project src/OllamaToolkit.Cli/OllamaToolkit.Cli.csproj -c Release -- help
dotnet run --project src/OllamaToolkit.Cli/OllamaToolkit.Cli.csproj -c Release -- import-reports
dotnet publish src/OllamaToolkit.App/OllamaToolkit.App.csproj -c Release -r win-x64 -o publish/OllamaToolkit.App
dotnet publish src/OllamaToolkit.Cli/OllamaToolkit.Cli.csproj -c Release -r win-x64 -o publish/OllamaToolkit.Cli
dotnet publish ... --self-contained false -o publish/...   # Phase 1 variant
```

### Archive extraction (this file)
```powershell
Get-ChildItem "...\terminal\*.log" | Sort-Object LastWriteTime
# Parse updates.jsonl for Shell tool commands
```

---

## 8. Errors encountered and fixes

| # | Error | Root cause | Fix |
|---|-------|------------|-----|
| 1 | `No .NET SDKs were found` | Fresh Windows environment | `winget install Microsoft.DotNet.SDK.8` |
| 2 | `dotnet new wpf -f net8.0-windows` invalid | Template alias mismatch | Use `-f net8.0`; set `net8.0-windows` in csproj |
| 3 | `The token '&&' is not a valid statement separator` | PowerShell 5.x syntax | Use `;` instead of `&&` for command chaining |
| 4 | Namespace `Environment` shadows `System.Environment` | C# using conflict | Rename to `EnvBackup`; qualify `System.Environment` |
| 5 | `OllamaApiClient` List/array null coalescing | C# type mismatch | Explicit list construction with null check |
| 6 | WPF `TextBox.AppendText` missing | WPF API difference | Use `Text +=` |
| 7 | Missing `using System.IO` | Implicit usings incomplete in App | Add explicit using |
| 8 | `CS0103 RunImportReportsAsync` | Incomplete CLI edit | Add function body at end of `Program.cs` |
| 9 | `git commit -m "$(cat <<'EOF'..."` failed | Bash heredoc in PowerShell | Single-line `-m "..."` message |
| 10 | `GetTestResultRowsAsync` async lambda | `Func<string,string>` not async | Load insights in MainWindow loop after rows |

---

## 9. Agent reasoning log (thought process)

Chronological decision trail the agent followed:

### Planning (prompt index 1–5)
- User wants full toolkit, not a minimal demo → plan all 9 projects upfront even if stubs.
- Responsiveness is **the** constraint → every feature design starts with "how does this enqueue without blocking UI?"
- AI categorization is metadata-only → explicitly exclude auto-pull of all catalog models.
- Model Run chat is user conversation, not toolkit AI → separate code path from `/api/generate` assist calls.

### Phase 1 execution
- Create git backup branch **before** any C# files — user requested rollback safety.
- Implement Core + Cli first so Vulkan/mode switching is testable without GUI.
- Keep PowerShell sources on disk — removal deferred to later phase per plan.

### Phase 2 execution
- `ReportImporter` must pick latest `report.json` per model by `CompletedAt` — match PS import behavior.
- `BackgroundWorkQueue` single worker — prevents concurrent mode apply + Ollama restart races.
- AI Features toggles save immediately on background thread — no Apply button per plan.
- Footer AI button routes to AI Features when active; Phase 3 changed to route to AI Settings when inactive.

### Phase 3 execution (resumed session)
- Build was broken on CLI only — fix that before any new features.
- ModelCatalog/ModelRegistry PS files are the authoritative reference for HTML parsing and catalog shape.
- Start with heuristic categorization when Ollama down — UI should still show categories.
- Don't over-implement all 11 AI functions in one pass — prioritize catalog, insights, log scan; defer NL search, advisor, comparison, queue priority.
- Chat streaming: append assistant text by replacing suffix after "Assistant: " marker to avoid full TextBox rewrite per token.

### Current (archive request)
- User wants complete audit trail → mine `updates.jsonl`, `chat_history.jsonl`, git log, terminal logs.
- Store in repo so it versions with the code; link to raw session files for byte-level replay.

---

## 10. Files created or modified (by phase)

### Phase 1 (`790e3a5`) — key paths
```
OllamaToolkit.sln
src/OllamaToolkit.Core/**          (ConfigPaths, Modes, Vulkan, Ollama, Settings, EnvBackup)
src/OllamaToolkit.Cli/Program.cs
src/OllamaToolkit.App/**           (shell, Theme.xaml, BackgroundWorkQueue)
launchers/*.cmd, BUILD.md, .gitignore
```

### Phase 2 (uncommitted until `ce23914`) — key paths
```
src/OllamaToolkit.BenchmarkStore/**
src/OllamaToolkit.BenchmarkRunner/AutomatedBenchmarkService.cs
src/OllamaToolkit.AiAssist/AiFeatureKeys.cs, PlainLanguageErrorService.cs
src/OllamaToolkit.App/MainWindow.xaml(.cs)  — 6 functional tabs
src/OllamaToolkit.App/Services/AppServices.cs, ActivityLogService.cs, UiDispatcher.cs
src/OllamaToolkit.Core/ToolkitPaths.cs, OllamaApiClient.cs (chat stream)
```

### Phase 3 (`ce23914`) — key paths
```
src/OllamaToolkit.ModelCatalog/**     (5 new .cs files)
src/OllamaToolkit.ModelCategory/**    (4 new .cs files)
src/OllamaToolkit.ModelRegistry/**    (2 new .cs files)
src/OllamaToolkit.AiAssist/BenchmarkInsightService.cs, LogAnomalyService.cs, Models/
src/OllamaToolkit.Core/ConfigPaths.cs, OllamaApiClient.cs (PullAsync)
src/OllamaToolkit.App/MainWindow.xaml(.cs)  — Model Library, Test Results, AI Settings tabs
src/OllamaToolkit.App/Services/ThrottledUpdater.cs
src/OllamaToolkit.Cli/Program.cs (RunImportReportsAsync)
```

---

## 11. Remaining plan items

From `plan.md` — not yet implemented as of `ce23914`:

- [ ] AI functions 5–9: NL search, model advisor, benchmark settings advisor, queue prioritization, model comparison
- [ ] Category column on Models & Launch grid
- [ ] Ask AI / Compare with AI toolbar buttons
- [ ] Full 30 fps throttling on all streams (partial: chat uses marker-based update; `ThrottledUpdater` created but not fully wired)
- [ ] Remove PowerShell sources
- [ ] Merge `csharp-wpf-greenfield` → `master`
- [ ] Full README rewrite
- [ ] Push `pre-csharp-greenfield-backup` to remote

---

## 12. How to extend this archive

### Append after each development session

1. Add a new `## Phase N` section with commit hash, reasoning, commands, errors.
2. Copy new shell commands from session `updates.jsonl` (filter `"title":"Shell"`).
3. Update Section 11 checkboxes.

### Raw replay files

| File | Contents |
|------|----------|
| `updates.jsonl` | Every tool call, shell output, file read/write event |
| `chat_history.jsonl` | User messages, assistant responses, reasoning summaries |
| `hunk_records.jsonl` | Per-edit line ranges with timestamps |
| `terminal/*.log` | Full stdout/stderr per shell invocation |
| `plan.md` | Living design document |

### Git commit map

```
ce56315  Initial PowerShell toolkit
bad4770  Fix GUI startup freeze (PowerShell)
790e3a5  C# greenfield scaffold
ce23914  Phase 3: catalog libraries + remaining tabs
```

---

*This archive is maintained as part of the Ollama AMD Vulkan greenfield rebuild. Last updated by agent session 2026-06-24.*