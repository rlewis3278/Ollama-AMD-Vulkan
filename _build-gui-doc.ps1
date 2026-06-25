#Requires -Version 5.1
$ErrorActionPreference = 'Stop'

$outPath = Join-Path $env:USERPROFILE 'Desktop\Ollama AMD Vulkan GUI Reference.docx'

function Add-Heading {
    param($Doc, [string]$Text, [int]$Level = 1)
    $style = switch ($Level) {
        1 { -2 }  # wdStyleHeading1
        2 { -3 }
        3 { -4 }
        default { -2 }
    }
    $p = $Doc.Content.Paragraphs.Add()
    $p.Range.Text = $Text
    $p.Range.Style = $style
    $p.Range.InsertParagraphAfter() | Out-Null
}

function Add-Body {
    param($Doc, [string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return }
    $p = $Doc.Content.Paragraphs.Add()
    $p.Range.Text = $Text
    $p.Range.Style = -1
    $p.Range.InsertParagraphAfter() | Out-Null
}

function Add-BulletList {
    param($Doc, [string[]]$Items)
    foreach ($item in $Items) {
        $p = $Doc.Content.Paragraphs.Add()
        $p.Range.Text = $item
        $p.Range.ListFormat.ApplyBulletDefault() | Out-Null
        $p.Range.InsertParagraphAfter() | Out-Null
    }
    $Doc.Content.Paragraphs.Add().Range.ListFormat.RemoveNumbers() | Out-Null
}

function Add-TableFromRows {
    param($Doc, [string[]]$Headers, [object[]]$Rows)
    $colCount = $Headers.Count
    $rowCount = $Rows.Count + 1
    $range = $Doc.Content.Paragraphs.Add().Range
    $table = $Doc.Tables.Add($range, $rowCount, $colCount)
    $table.Style = 'Grid Table 4 - Accent 1'
    for ($c = 0; $c -lt $colCount; $c++) {
        $table.Cell(1, $c + 1).Range.Text = $Headers[$c]
        $table.Cell(1, $c + 1).Range.Bold = $true
    }
    for ($r = 0; $r -lt $Rows.Count; $r++) {
        $row = $Rows[$r]
        if ($row -is [string[]]) {
            for ($c = 0; $c -lt $colCount; $c++) {
                $table.Cell($r + 2, $c + 1).Range.Text = [string]$row[$c]
            }
        }
        else {
            $table.Cell($r + 2, 1).Range.Text = [string]$row
        }
    }
    $table.Range.InsertParagraphAfter() | Out-Null
}

$word = New-Object -ComObject Word.Application
$word.Visible = $false
$doc = $word.Documents.Add()

try {
    Add-Heading $doc 'Ollama AMD Vulkan Manager' 1
    Add-Heading $doc 'GUI Layout and Architecture Reference' 2
    Add-Body $doc 'Handoff document for continuing development in a new session.'
    Add-Body $doc "Project path: C:\Users\lewis\Ollama-AMD-Vulkan"
    Add-Body $doc 'Git repository: https://github.com/rlewis3278/Ollama-AMD-Vulkan'
    Add-Body $doc 'Latest stable commits: ce56315 (initial), bad4770 (startup freeze fixes).'
    Add-Body $doc 'Generated: June 23, 2026'

    Add-Heading $doc 'Purpose' 1
    Add-Body $doc 'Windows PowerShell toolkit for an AMD Ryzen 9 6900HX + Radeon 680M iGPU + RX 6700S laptop running Ollama with the Vulkan backend. The GUI lets you:'
    Add-BulletList $doc @(
        'Switch Ollama compute modes (CPU / APU / GPU / Hybrid)'
        'View installed models and benchmark results'
        'Browse, download, and remove models from ollama.com'
        'Run automated 4-mode benchmarks to find the best mode per model'
        'Launch models in their best-known mode'
    )
    Add-Body $doc 'Hardware note: Vulkan GPU 0 = 680M (iGPU), Vulkan GPU 1 = 6700S (dGPU). Task Manager uses the opposite numbering. The toolkit auto-detects Vulkan indices via Get-VulkanDevices.ps1.'

    Add-Heading $doc 'Entry Points' 1
    Add-TableFromRows $doc @('File', 'Role') @(
        , @('Ollama-Mode-GUI.ps1', 'Main WinForms GUI (STA required)')
        , @('Ollama-Mode-GUI.cmd / .vbs', 'Double-click launchers')
        , @('Set-OllamaMode.ps1', 'CLI one-shot mode switch')
        , @('Ollama-AMD-Vulkan-Manager.ps1', 'CLI/interactive mode manager')
        , @('Invoke-OllamaAutomatedTest.ps1', 'Single-model 4-mode benchmark (spawned by GUI)')
        , @('Invoke-OllamaAutomatedTestAll.ps1', 'Batch benchmark all models')
        , @('Test-OllamaBenchmark.ps1', 'Standalone REST API benchmark')
        , @('Get-VulkanDevices.ps1', 'Vulkan device index discovery')
        , @('Ollama-VulkanWorkaround.ps1', '680M system Vulkan loader fix')
    )

    Add-Heading $doc 'Module Architecture' 1
    Add-Body $doc 'Ollama-Mode-GUI.ps1 dot-sources these modules in order:'
    Add-BulletList $doc @(
        'Ollama-Toolkit.Core.ps1 — Modes, env vars, Ollama API, restart'
        'Ollama-Toolkit.GuiAiActivity.ps1 — AI activity log file and tail reader'
        'Ollama-Toolkit.BenchmarkStore.ps1 — Model profiles, local models, report import'
        'Ollama-Toolkit.ModelCatalog.ps1 — Per-model descriptions from ollama.com'
        'Ollama-Toolkit.ModelRegistry.ps1 — ollama.com catalog, pull/remove, search'
        'Ollama-Toolkit.GuiTesting.ps1 — Benchmark queue, launch-in-best-mode'
    )
    Add-Body $doc 'Orphan files (on disk, NOT loaded by GUI): Ollama-Toolkit.ModelPerformance.ps1 and Ollama-Toolkit.ModelCategory.ps1 (leftover from removed web-search feature).'

    Add-Heading $doc 'User Config and Data' 1
    Add-Body $doc 'Location: %USERPROFILE%\.ollama-amd-vulkan\'
    Add-TableFromRows $doc @('File', 'Contents') @(
        , @('env-backup.json', 'Snapshot of managed env vars before mode changes')
        , @('model-profiles.json', 'Benchmark results per model (best mode, tok/s, per-mode results)')
        , @('model-descriptions.json', 'Cached ollama.com descriptions per installed model')
        , @('library-catalog-store.json', 'Cached ollama.com model catalog (~234 models)')
        , @('registry-split-layout.json', 'Model Library tab splitter position')
        , @('gui-ai-activity.log', 'Background task / AI activity log')
        , @('vulkan-workaround.json', '680M Vulkan workaround state')
    )
    Add-Body $doc 'Project-local (gitignored): reports/ folder for HTML/JSON/CSV benchmark output; *.log runtime logs.'

    Add-Heading $doc 'GUI Shell Layout' 1
    Add-Body $doc 'Top: Alert bar (hidden unless untested models exist).'
    Add-Body $doc 'Middle: Tab control with 6 tabs (detailed below).'
    Add-Body $doc 'Bottom footer: Hint text (left) and AI status button (right).'
    Add-Body $doc 'Visual theme: Dark charcoal panels, red accent, green = active/success, yellow = warning.'
    Add-Body $doc 'On exit: Stops all background timers/jobs and stops Ollama processes (Stop-OllamaOnGuiExit).'

    Add-Heading $doc 'Tab 1: Compute Modes' 1
    Add-Body $doc 'Purpose: Switch Ollama environment variables and optionally restart Ollama.'
    Add-Heading $doc 'Layout' 2
    Add-BulletList $doc @(
        'Vulkan device label — detected indices for 680M and 6700S'
        'Status panel — current mode, Ollama/API status, 680M workaround status'
        '2x2 mode cards — CPU, APU (680M), GPU (6700S), Hybrid (both GPUs)'
        'Checkbox — Restart Ollama after applying mode (default: checked)'
        'Bottom split — env var box (top) and modes log (bottom)'
        'Toolbar — Refresh, Restart Ollama, Restore Backup, 680M Fix, Vulkan Info'
    )
    Add-Heading $doc 'Behavior' 2
    Add-Body $doc 'Clicking a card calls Invoke-ModeSelection, which starts a GUI worker job running Apply-OllamaToolkitMode. Managed env vars: OLLAMA_VULKAN, HIP_VISIBLE_DEVICES, GGML_VK_VISIBLE_DEVICES, ROCR_VISIBLE_DEVICES, CUDA_VISIBLE_DEVICES, OLLAMA_NUM_GPU, OLLAMA_IGPU_ENABLE.'

    Add-Heading $doc 'Tab 2: Models and Launch' 1
    Add-Body $doc 'Purpose: View locally installed models, benchmark status, descriptions; launch or test models.'
    Add-Heading $doc 'ListView columns' 2
    Add-Body $doc 'Model, Size, Param Size, Best Mode, tok/s, Status, Last Tested, Description'
    Add-Heading $doc 'Toolbar buttons' 2
    Add-TableFromRows $doc @('Button', 'Action') @(
        , @('Launch (Best Mode)', 'Apply best benchmark mode and ollama run in new cmd window')
        , @('Test Selected', 'Queue 4-mode benchmark for selected models')
        , @('Test Untested', 'Benchmark all models marked NeedsRetest')
        , @('Refresh', 'Force API refresh of model list')
        , @('Import Reports', 'Import reports/**/report.json into profiles')
        , @('Refresh Descriptions', 'Background sync of ollama.com descriptions')
        , @('Full Description', 'Dialog with full readme/description')
        , @('ollama.com', 'Open model page in browser')
    )
    Add-Body $doc 'Requires Ollama API running for the list to populate. Row colors: green = tested, yellow = needs retest. Yellow alert banner at top appears when untested models exist.'

    Add-Heading $doc 'Tab 3: Model Library' 1
    Add-Body $doc 'Purpose: Browse ollama.com catalog, download/remove models, search.'
    Add-Heading $doc 'Layout' 2
    Add-Body $doc 'Horizontal split (resizable, ratio saved): Installed Models (top) and Available on ollama.com (bottom).'
    Add-Body $doc 'Installed columns: Model, Size, Param Size, Type, Modified, Description'
    Add-Body $doc 'Catalog columns: Model, Size, Param Size, Installed, Date Updated, Description'
    Add-Heading $doc 'Toolbar' 2
    Add-TableFromRows $doc @('Control', 'Action') @(
        , @('Variant combo', 'Tags for selected catalog model (e.g. llama3.2:latest)')
        , @('Search', 'Filter catalog by name/description/tags')
        , @('Download', 'ollama pull via background worker')
        , @('Remove', 'Delete selected installed model')
        , @('Refresh Installed', 'Re-fetch from Ollama /api/tags')
        , @('Refresh Catalog', 'Re-fetch catalog from web and refresh cache')
    )
    Add-Body $doc 'Catalog is lazy-loaded when tab is first opened. Loads from cache first (~234 models). Large catalogs load in batches of 35 per timer tick to avoid UI freeze.'

    Add-Heading $doc 'Tab 4: Testing Suite' 1
    Add-Body $doc 'Purpose: Run and monitor 4-mode benchmark runs.'
    Add-BulletList $doc @(
        'Options: Model combo, num_ctx (2048-32768), tokens (8-256, default 32)'
        'Status label and marquee progress bar while busy'
        'Log box with live benchmark output'
        'Buttons: Test Selected Model, Test All Models, Test Untested, Stop, Clear Log'
    )
    Add-Body $doc 'Flow: Start-ModelBenchmark spawns Invoke-OllamaAutomatedTest.ps1 per model, tests CPU/APU/GPU/Hybrid, writes report.json to reports/gui-tests/, imports to model-profiles.json. One model at a time in queue.'

    Add-Heading $doc 'Tab 5: Test Results' 1
    Add-Body $doc 'Sortable DataGridView: Model, SizeGB, ParameterSize, Status, BestMode, BestTps, CPU, APU, GPU, Hybrid (tok/s or FAIL), LastTested, NumCtx.'
    Add-Body $doc 'Toolbar: Open Reports Folder, Import Reports, Refresh, Launch Selected.'

    Add-Heading $doc 'Tab 6: AI Activity' 1
    Add-Body $doc 'Monitors background tasks (downloads, description sync, catalog refresh, workers). Shows status line, scrolling log from gui-ai-activity.log, and Clear Log button.'
    Add-Body $doc 'Footer AI button states: AI Inactive (API down), AI Busy (background jobs), AI Active (local summarizer ready).'

    Add-Heading $doc 'Background Architecture' 1
    Add-Heading $doc 'Key timers' 2
    Add-TableFromRows $doc @('Timer', 'Interval', 'Purpose') @(
        , @('GuiPostShowTimer', '1 ms', 'One-shot post-show init')
        , @('GuiDeferredRefreshTimer', '1.5 s', 'Full refresh after benchmark import')
        , @('descStartupTimer', '8 s', 'Deferred ForceApiRefresh')
        , @('statusTimer', '60 s', 'Periodic refresh and new-model prompts')
        , @('testPollTimer', '500 ms', 'Tail benchmark log')
        , @('RegistryCatalogLoadTimer', '40 ms', 'Batched catalog list load')
        , @('GuiWorkerTimer', '250 ms', 'Poll general GUI worker job')
    )
    Add-Heading $doc 'Background jobs' 2
    Add-TableFromRows $doc @('Job', 'Work') @(
        , @('GuiWorkerJob', 'Mode apply, restart, pull, remove, import, etc.')
        , @('RegistryBackgroundJob', 'Fetch/sort ollama.com catalog')
        , @('DescriptionBackgroundJob', 'Fetch descriptions for installed models')
        , @('BenchmarkImportJob', 'Scan reports/ for report.json on startup')
    )

    Add-Heading $doc 'Key Workflows' 1
    Add-BulletList $doc @(
        'Switch mode: Click card -> worker sets User env vars -> optional Ollama restart'
        'Launch in best mode: Requires benchmark profile -> apply best mode -> restart -> ollama run'
        'Download: Select catalog row -> pick variant -> Download -> worker pulls with progress logging'
        'Benchmark: Queue -> Invoke-OllamaAutomatedTest.ps1 -> 4 modes -> report.json -> model-profiles.json'
    )

    Add-Heading $doc 'What Was Removed (rollback state)' 1
    Add-BulletList $doc @(
        'Web search providers for performance ratings and categories'
        'Performance and Category columns in Models, Model Library, and Test Results grids'
        'Automatic background metadata sync on startup (descriptions on demand only)'
    )

    Add-Heading $doc 'Known Constraints' 1
    Add-BulletList $doc @(
        'Ollama must be running for Models and Launch tab to populate'
        'StrictMode Latest throughout — null property access causes crashes'
        'API calls on refresh can block UI thread briefly (startup uses fast cached refresh first)'
        'Model Library catalog loads when tab is opened, not at app startup'
        'On GUI close, Ollama processes are stopped'
        'Git rollback: git log and git checkout <commit> -- . in project folder'
    )

    Add-Heading $doc 'Managed Environment Variables (per mode)' 1
    Add-TableFromRows $doc @('Mode', 'Key behavior') @(
        , @('CPU', 'GPU devices hidden/disabled')
        , @('APU', 'GGML_VK_VISIBLE_DEVICES=0, OLLAMA_IGPU_ENABLE=1')
        , @('GPU', 'GGML_VK_VISIBLE_DEVICES=1')
        , @('Hybrid', 'GGML_VK_VISIBLE_DEVICES=0,1, multi-GPU scheduling')
    )

    Add-Heading $doc 'Suggested Starting Point for New Session' 1
    Add-BulletList $doc @(
        'Core debug path: Ollama-Mode-GUI.ps1 -> Refresh-GuiStatus -> Get-AllModelProfileSummaries -> Get-ToolkitLocalOllamaModels'
        'Empty Models tab: Usually Ollama API not running — check footer AI status'
        'Model Library empty until tab opened — by design (lazy load)'
        'Latest stable commit: bad4770 on master'
        'Test launch: .\Ollama-Mode-GUI.ps1 or double-click Ollama-Mode-GUI.cmd'
    )

    if (Test-Path -LiteralPath $outPath) {
        Remove-Item -LiteralPath $outPath -Force
    }
    $doc.SaveAs2($outPath, 16) | Out-Null  # wdFormatDocumentDefault = docx
    Write-Host "Saved: $outPath"
}
finally {
    if ($doc) { $doc.Close($false) | Out-Null }
    if ($word) { $word.Quit() | Out-Null }
    [System.Runtime.InteropServices.Marshal]::ReleaseComObject($word) | Out-Null
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}