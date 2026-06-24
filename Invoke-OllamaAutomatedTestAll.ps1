#Requires -Version 5.1
<#
.SYNOPSIS
    Run automated Ollama benchmarks for every locally downloaded model.

.DESCRIPTION
    Discovers all models via GET /api/tags, then for each model runs the full
    Invoke-OllamaAutomatedTest.ps1 suite (all modes, Ollama restart, per-model reports).
    Produces a master HTML/JSON/CSV summary and a fullscreen report screenshot.

.PARAMETER Modes
    Compute modes to test per model. Default: CPU, APU, GPU, Hybrid.

.PARAMETER NumPredict
    Tokens per timed run. Default: 32.

.PARAMETER Runs
    Timed runs per mode. Default: 1.

.PARAMETER OutputDir
    Master report directory. Default: .\reports\all-models-<timestamp>

.PARAMETER SkipScreenshots
    Skip all screenshots including per-mode cards.

.PARAMETER ApiTimeoutSec
    API wait timeout after Ollama restart. Default: 300 (large models need longer).

.EXAMPLE
    .\Invoke-OllamaAutomatedTestAll.ps1

.EXAMPLE
    .\Invoke-OllamaAutomatedTestAll.ps1 -NumPredict 64 -Runs 2
#>
[CmdletBinding()]
param(
    [string[]]$Modes = @('CPU', 'APU', 'GPU', 'Hybrid'),
    [int]$NumPredict = 32,
    [int]$Runs = 1,
    [string]$OutputDir,
    [switch]$SkipScreenshots,
    [int]$ApiTimeoutSec = 300,
    [int]$NumCtx = 8192
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Script:RootDir = $PSScriptRoot
$Script:Timestamp = (Get-Date).ToString('yyyy-MM-dd_HHmmss')
$Script:OllamaHost = 'http://localhost:11434'
$Script:OllamaApp = Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama app.exe'

if (-not $OutputDir) {
    $OutputDir = Join-Path $Script:RootDir (Join-Path 'reports' "all-models-$Script:Timestamp")
}

if ([System.Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    $staArgs = @('-STA', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $MyInvocation.MyCommand.Path)
    foreach ($key in $PSBoundParameters.Keys) {
        $value = $PSBoundParameters[$key]
        if ($value -is [switch] -and $value) { $staArgs += "-$key" }
        elseif ($null -ne $value) {
            if ($value -is [array]) {
                $staArgs += "-$key"
                $staArgs += (($value | ForEach-Object { "$_" }) -join ',')
            }
            else {
                $staArgs += "-$key"
                $staArgs += "$value"
            }
        }
    }
    & powershell.exe @staArgs
    exit $LASTEXITCODE
}

if ($Modes.Count -eq 1 -and $Modes[0] -match ',') {
    $Modes = @($Modes[0].Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Write-Log {
    param([string]$Message, [ConsoleColor]$Color = [ConsoleColor]::Gray)
    $stamp = (Get-Date).ToString('HH:mm:ss')
    $prev = $Host.UI.RawUI.ForegroundColor
    $Host.UI.RawUI.ForegroundColor = $Color
    Write-Host "[$stamp] $Message"
    $Host.UI.RawUI.ForegroundColor = $prev
    Add-Content -LiteralPath $script:LogFile -Value "[$stamp] $Message" -Encoding UTF8
}

function Get-SafeDirName {
    param([string]$Name)
    $safe = $Name -replace '[:\\/<>|"?*]', '_'
    return $safe.TrimEnd('.')
}

function Start-OllamaIfNeeded {
    $uri = "$Script:OllamaHost/api/tags"
    try {
        $null = Invoke-RestMethod -Uri $uri -TimeoutSec 5
        return
    }
    catch { }

    if (-not (Test-Path -LiteralPath $Script:OllamaApp)) {
        throw "Ollama not running and app not found: $Script:OllamaApp"
    }

    Write-Log 'Starting Ollama...' -Color Yellow
    Start-Process -FilePath $Script:OllamaApp | Out-Null
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline) {
        try {
            $null = Invoke-RestMethod -Uri $uri -TimeoutSec 5
            Write-Log 'Ollama API ready.' -Color Green
            return
        }
        catch { Start-Sleep -Seconds 2 }
    }
    throw 'Ollama API did not become ready.'
}

function Get-AllLocalModels {
    $uri = "$Script:OllamaHost/api/tags"
    $response = Invoke-RestMethod -Uri $uri -TimeoutSec 30
    if (-not $response.models) { return @() }
    return @(
        $response.models |
            Where-Object {
                -not $_.PSObject.Properties['remote_host'] -and
                $_.details.format -eq 'gguf' -and
                $_.size -gt 1MB
            } |
            Sort-Object -Property size, name
    )
}

function ConvertTo-MasterHtml {
    param($MasterData)

    $modelSections = ''
    foreach ($entry in $MasterData.ModelReports) {
        $rows = ''
        foreach ($r in $entry.Results) {
            $cls = if ($r.Status -eq 'Success') { 'ok' } else { 'fail' }
            $best = ''
            if ($entry.Winner -and $r.Mode -eq $entry.Winner.Mode -and $r.Status -eq 'Success') {
                $best = ' class="best"'
            }
            $rows += "<tr class=`"$cls`"><td$best>$($r.Mode)</td><td>$($r.Status)</td><td>$($r.Generation_tps)</td><td>$($r.TTFT_ms)</td><td>$($r.VRAM_MB)</td></tr>"
        }
        $winnerText = if ($entry.Winner) { "$($entry.Winner.Mode) @ $($entry.Winner.Generation_tps) tok/s" } else { 'N/A' }
        $modelSections += @"
  <h2>$($entry.Model) <span class="dim">($($entry.Quantization), $([math]::Round($entry.SizeGB,2)) GB)</span></h2>
  <div class="meta">Fastest: <span class="winner">$winnerText</span> | Report: <a href="$($entry.RelativeReportDir)/report.html">report.html</a></div>
  <table>
    <thead><tr><th>Mode</th><th>Status</th><th>Gen tok/s</th><th>TTFT ms</th><th>VRAM MB</th></tr></thead>
    <tbody>$rows</tbody>
  </table>
"@
    }

    $summaryRows = ''
    foreach ($entry in $MasterData.ModelReports) {
        foreach ($mode in $MasterData.Modes) {
            $r = $entry.Results | Where-Object { $_.Mode -eq $mode } | Select-Object -First 1
            $val = if ($r -and $r.Status -eq 'Success') { $r.Generation_tps } else { '-' }
            $summaryRows += "<tr><td>$($entry.Model)</td><td>$mode</td><td>$val</td></tr>"
        }
    }

    return @"
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <title>Ollama All-Models Benchmark Report</title>
  <style>
    body { font-family: Segoe UI, Arial, sans-serif; background: #14141a; color: #eee; margin: 24px; }
    h1, h2 { color: #5ab4ff; }
    .dim { color: #888; font-size: 0.85em; font-weight: normal; }
    .meta { background: #1e1e28; padding: 12px; border-radius: 8px; margin: 8px 0 16px; }
    table { width: 100%; border-collapse: collapse; margin-bottom: 28px; }
    th, td { border: 1px solid #333; padding: 8px; text-align: left; }
    th { background: #243040; }
    tr.ok td { background: #16251c; }
    tr.fail td { background: #2a1717; }
    td.best { color: #7dffa8; font-weight: bold; }
    .winner { color: #7dffa8; font-weight: bold; }
    a { color: #5ab4ff; }
  </style>
</head>
<body>
  <h1>Ollama AMD Vulkan - All Models Benchmark</h1>
  <div class="meta">
    <div><strong>Completed:</strong> $($MasterData.CompletedAt)</div>
    <div><strong>Models tested:</strong> $($MasterData.ModelReports.Count)</div>
    <div><strong>Modes:</strong> $($MasterData.Modes -join ', ')</div>
    <div><strong>Runs/mode:</strong> $($MasterData.Runs) | <strong>Tokens/run:</strong> $($MasterData.NumPredict)</div>
    <div><strong>Duration:</strong> $($MasterData.DurationMin) minutes</div>
    <div><strong>Task Manager:</strong> GPU 0 = 6700S, GPU 1 = 680M | <strong>Vulkan:</strong> APU=0, GPU=1</div>
  </div>
  <h2>Per-Model Results</h2>
  $modelSections
</body>
</html>
"@
}

function Save-MasterScreenshot {
    param([string]$HtmlPath, [string]$PngPath)

    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing

    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'Ollama All-Models Benchmark Report'
    $form.WindowState = 'Maximized'
    $form.StartPosition = 'CenterScreen'
    $form.TopMost = $true

    $browser = New-Object System.Windows.Forms.WebBrowser
    $browser.Dock = 'Fill'
    $browser.ScriptErrorsSuppressed = $true
    $fileUri = [Uri]::new((Resolve-Path -LiteralPath $HtmlPath).Path).AbsoluteUri
    $browser.Navigate($fileUri)
    $form.Controls.Add($browser)

    $state = @{ Done = $false }
    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = 4000
    $timer.Add_Tick({
        if (-not $state.Done) {
            $state.Done = $true
            $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
            $bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
            $bmp.Save($PngPath, [System.Drawing.Imaging.ImageFormat]::Png)
            $g.Dispose(); $bmp.Dispose()
            $timer.Stop(); $form.Close()
        }
    })
    $form.Add_Shown({ $timer.Start() })
    [void]$form.ShowDialog()
    $timer.Dispose(); $form.Dispose()
}

# --- Main ---
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$script:LogFile = Join-Path $OutputDir 'all-models.log'
Set-Content -LiteralPath $script:LogFile -Value "All-models benchmark started $(Get-Date -Format o)" -Encoding UTF8

$singleTest = Join-Path $Script:RootDir 'Invoke-OllamaAutomatedTest.ps1'
if (-not (Test-Path -LiteralPath $singleTest)) {
    throw "Missing: $singleTest"
}

Write-Log '=== ALL MODELS AUTOMATED BENCHMARK ===' -Color Cyan
Write-Log "Output: $OutputDir" -Color Cyan

Start-OllamaIfNeeded
$allModels = Get-AllLocalModels
if ($allModels.Count -eq 0) {
    throw 'No local models found in Ollama.'
}

Write-Log ("Found {0} model(s):" -f $allModels.Count) -Color Yellow
foreach ($m in $allModels) {
    $gb = [math]::Round($m.size / 1GB, 2)
    Write-Log "  - $($m.name) ($gb GB, $($m.details.quantization_level))" -Color White
}

$suiteStarted = Get-Date
$modelReports = @()
$failures = @()

$modelIndex = 0
foreach ($model in $allModels) {
    $modelIndex++
    $name = $model.name
    $safe = Get-SafeDirName -Name $name
    $modelDir = Join-Path $OutputDir $safe

    Write-Log '' 
    Write-Log "========== MODEL $modelIndex/$($allModels.Count): $name ==========" -Color Cyan

    $childArgs = @(
        '-STA', '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', $singleTest,
        '-ModelName', $name,
        '-OutputDir', $modelDir,
        '-NumPredict', $NumPredict,
        '-Runs', $Runs,
        '-ApiTimeoutSec', $ApiTimeoutSec,
        '-NumCtx', $NumCtx,
        '-Modes', (($Modes | ForEach-Object { "$_" }) -join ','),
        '-SkipFinalScreenshots'
    )
    if ($SkipScreenshots) { $childArgs += '-SkipScreenshots' }

    try {
        $null = & powershell.exe @childArgs 2>&1 | ForEach-Object { Write-Log "  $_" }
        if ($LASTEXITCODE -ne 0) {
            throw "Child exit code $LASTEXITCODE"
        }

        $jsonPath = Join-Path $modelDir 'report.json'
        if (-not (Test-Path -LiteralPath $jsonPath)) {
            throw 'report.json not produced'
        }

        $report = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
        $modelReports += [pscustomobject]@{
            Model              = $name
            Quantization       = $report.Quantization
            SizeGB             = $model.size / 1GB
            Winner             = $report.Winner
            Results            = @($report.Results)
            RelativeReportDir  = $safe
            OutputDir          = $modelDir
            Status             = 'Success'
            Error              = $null
        }
        if ($report.Winner -and $report.Winner.PSObject.Properties['Mode']) {
            Write-Log "Model $name complete. Fastest: $($report.Winner.Mode) @ $($report.Winner.Generation_tps) tok/s" -Color Green
        }
        else {
            Write-Log "Model $name complete (no successful mode winner)." -Color Yellow
        }
    }
    catch {
        $msg = $_.Exception.Message
        $failures += [pscustomobject]@{ Model = $name; Error = $msg }
        if ($modelReports | Where-Object { $_.Model -eq $name }) {
            Write-Log "Model $name parse error (report already recorded): $msg" -Color Red
            continue
        }
        $modelReports += [pscustomobject]@{
            Model              = $name
            Quantization       = $model.details.quantization_level
            SizeGB             = $model.size / 1GB
            Winner             = $null
            Results            = @()
            RelativeReportDir  = $safe
            OutputDir          = $modelDir
            Status             = 'Failed'
            Error              = $msg
        }
        Write-Log "Model $name FAILED: $msg" -Color Red
    }
}

$completedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
$durationMin = [math]::Round(((Get-Date) - $suiteStarted).TotalMinutes, 2)

$masterPayload = @{
    CompletedAt  = $completedAt
    DurationMin  = $durationMin
    Modes        = @($Modes)
    Runs         = $Runs
    NumPredict   = $NumPredict
    ModelReports = @($modelReports | ForEach-Object {
        @{
            Model             = $_.Model
            Quantization      = $_.Quantization
            SizeGB            = $_.SizeGB
            Winner            = $_.Winner
            Results           = @($_.Results)
            RelativeReportDir = $_.RelativeReportDir
            Status            = $_.Status
            Error             = $_.Error
        }
    })
    Failures = @($failures)
}

$masterJson = Join-Path $OutputDir 'master-report.json'
$masterHtml = Join-Path $OutputDir 'master-report.html'
$masterPng = Join-Path $OutputDir 'master-report.png'
$masterCsv = Join-Path $OutputDir 'master-results.csv'

$masterPayload | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $masterJson -Encoding UTF8

$csvLines = @('Model,Quantization,Mode,Status,Generation_tps,PromptEval_tps,TTFT_ms,VRAM_MB,Notes')
foreach ($entry in $modelReports) {
    if ($entry.Results.Count -eq 0) {
        $csvLines += ('"{0}","{1}","","","Failed","","","","{2}"' -f $entry.Model, $entry.Quantization, ($entry.Error -replace '"', '""'))
        continue
    }
    foreach ($r in $entry.Results) {
        $notes = if ($r.Notes) { $r.Notes } else { '' }
        $csvLines += ('"{0}","{1}","{2}","{3}",{4},{5},{6},{7},"{8}"' -f `
            $entry.Model, $entry.Quantization, $r.Mode, $r.Status, `
            $r.Generation_tps, $r.PromptEval_tps, $r.TTFT_ms, $r.VRAM_MB, ($notes -replace '"', '""'))
    }
}
Set-Content -LiteralPath $masterCsv -Value $csvLines -Encoding UTF8

$masterData = [pscustomobject]@{
    CompletedAt  = $completedAt
    DurationMin  = $durationMin
    Modes        = $Modes
    Runs         = $Runs
    NumPredict   = $NumPredict
    ModelReports = $modelReports
}
$html = ConvertTo-MasterHtml -MasterData $masterData
Set-Content -LiteralPath $masterHtml -Value $html -Encoding UTF8

if (-not $SkipScreenshots) {
    Write-Log 'Capturing master report screenshot...' -Color Cyan
    Save-MasterScreenshot -HtmlPath $masterHtml -PngPath $masterPng
}

Write-Log ''
Write-Log '========== ALL MODELS BENCHMARK COMPLETE ==========' -Color Cyan
Write-Log "Models  : $($allModels.Count) tested, $($failures.Count) failed"
Write-Log "Duration: $durationMin minutes"
Write-Log "Master  : $masterHtml" -Color Green
if (-not $SkipScreenshots) {
    Write-Log "Screenshot: $masterPng" -Color Green
}

Write-Log ''
Write-Log 'Fastest mode per model:'
foreach ($entry in $modelReports) {
    if ($entry.Winner) {
        Write-Log ("  {0,-30} {1,6} @ {2,6} tok/s" -f $entry.Model, $entry.Winner.Mode, $entry.Winner.Generation_tps) -Color Green
    }
    else {
        Write-Log ("  {0,-30} FAILED" -f $entry.Model) -Color Red
    }
}

return $masterPayload