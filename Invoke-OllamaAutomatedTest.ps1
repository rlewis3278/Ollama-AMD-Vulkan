#Requires -Version 5.1
<#
.SYNOPSIS
    Fully automated Ollama benchmark across compute modes with Ollama restart and report screenshots.

.DESCRIPTION
    For each mode (CPU, APU, GPU, Hybrid):
      1. Applies persistent environment variables
      2. Stops all Ollama processes
      3. Restarts Ollama and waits for the REST API
      4. Runs warm-up + timed benchmark via Ollama API
      5. Records metrics and captures per-mode status screenshots

    Generates HTML + JSON report and captures a full report screenshot.

.PARAMETER ModelName
    Model to benchmark. Defaults to the first suitable 7B-class local model.

.PARAMETER Modes
    Modes to test. Default: CPU, APU, GPU, Hybrid.

.PARAMETER NumPredict
    Tokens to generate per timed run. Default: 64 (faster automated suite).

.PARAMETER Runs
    Timed runs per mode to average. Default: 2.

.PARAMETER OutputDir
    Report output directory. Default: .\reports\<timestamp>

.PARAMETER OllamaHost
    Ollama API URL. Default: http://localhost:11434

.PARAMETER ApiTimeoutSec
    Seconds to wait for Ollama API after restart. Default: 180.

.PARAMETER SkipScreenshots
    Skip all PNG screenshot capture.

.PARAMETER SkipFinalScreenshots
    Skip report-window and fullscreen screenshots (per-model HTML/JSON still generated).

.EXAMPLE
    .\Invoke-OllamaAutomatedTest.ps1 -ModelName "qwen2.5-coder:7b"

.EXAMPLE
    .\Invoke-OllamaAutomatedTest.ps1 -ModelName "llama3.2:3b" -Modes GPU,APU -Runs 1
#>
[CmdletBinding()]
param(
    [string]$ModelName,
    [string[]]$Modes = @('CPU', 'APU', 'GPU', 'Hybrid'),
    [int]$NumPredict = 64,
    [int]$Runs = 2,
    [string]$OutputDir,
    [string]$OllamaHost = 'http://localhost:11434',
    [int]$ApiTimeoutSec = 180,
    [int]$NumCtx = 8192,
    [switch]$SkipScreenshots,
    [switch]$SkipFinalScreenshots
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# WinForms/WebBrowser screenshots require an STA thread.
if ([System.Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    $staArgs = @(
        '-STA', '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', $MyInvocation.MyCommand.Path
    )
    foreach ($key in $PSBoundParameters.Keys) {
        $value = $PSBoundParameters[$key]
        if ($value -is [switch] -and $value) {
            $staArgs += "-$key"
        }
        elseif ($null -ne $value) {
            if ($value -is [array]) {
                $joined = ($value | ForEach-Object { "$_" }) -join ','
                $staArgs += "-$key"
                $staArgs += $joined
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

$Script:OllamaAppPath = Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama app.exe'
$Script:RootDir = $PSScriptRoot
$Script:ReportTimestamp = (Get-Date).ToString('yyyy-MM-dd_HHmmss')

if (-not $OutputDir) {
    $OutputDir = Join-Path $Script:RootDir (Join-Path 'reports' $Script:ReportTimestamp)
}

function Write-Log {
    param(
        [string]$Message,
        [ValidateSet('Info', 'Success', 'Warning', 'Error', 'Step')]
        [string]$Level = 'Info'
    )

    $color = switch ($Level) {
        'Success' { [ConsoleColor]::Green }
        'Warning' { [ConsoleColor]::Yellow }
        'Error'   { [ConsoleColor]::Red }
        'Step'    { [ConsoleColor]::Cyan }
        default   { [ConsoleColor]::Gray }
    }

    $stamp = (Get-Date).ToString('HH:mm:ss')
    $previous = $Host.UI.RawUI.ForegroundColor
    $Host.UI.RawUI.ForegroundColor = $color
    Write-Host "[$stamp] $Message"
    $Host.UI.RawUI.ForegroundColor = $previous
}

function Import-ToolkitHelpers {
    $vulkanHelper = Join-Path $Script:RootDir 'Get-VulkanDevices.ps1'
    if (-not (Test-Path -LiteralPath $vulkanHelper)) {
        throw "Missing helper: $vulkanHelper"
    }
    . $vulkanHelper
    $vulkanInfo = Resolve-VulkanInfoPath -ExplicitPath ''
    $report = Get-VulkanDeviceReport -VulkanInfoExe $vulkanInfo
    return $report.DeviceMap
}

function Get-ModeDefinitions {
    param($DeviceMap)

    return @{
        CPU = @{
            Label     = 'CPU Only'
            Variables = @{
                OLLAMA_VULKAN           = '0'
                HIP_VISIBLE_DEVICES     = '-1'
                GGML_VK_VISIBLE_DEVICES = '-1'
                ROCR_VISIBLE_DEVICES    = '-1'
                OLLAMA_IGPU_ENABLE      = '0'
            }
        }
        APU = @{
            Label     = "APU / iGPU ($($DeviceMap.ApuName))"
            Variables = @{
                OLLAMA_VULKAN           = '1'
                HIP_VISIBLE_DEVICES     = '-1'
                GGML_VK_VISIBLE_DEVICES = $DeviceMap.ApuVulkanIndex
                ROCR_VISIBLE_DEVICES    = '-1'
                OLLAMA_NUM_GPU          = '999'
                OLLAMA_IGPU_ENABLE      = '1'
            }
        }
        GPU = @{
            Label     = "dGPU ($($DeviceMap.GpuName))"
            Variables = @{
                OLLAMA_VULKAN           = '1'
                HIP_VISIBLE_DEVICES     = '-1'
                GGML_VK_VISIBLE_DEVICES = $DeviceMap.GpuVulkanIndex
                ROCR_VISIBLE_DEVICES    = '-1'
                OLLAMA_IGPU_ENABLE      = '0'
            }
        }
        Hybrid = @{
            Label     = 'Hybrid (iGPU + dGPU)'
            Variables = @{
                OLLAMA_VULKAN           = '1'
                HIP_VISIBLE_DEVICES     = '-1'
                GGML_VK_VISIBLE_DEVICES = $DeviceMap.HybridVulkanValue
                ROCR_VISIBLE_DEVICES    = '-1'
                OLLAMA_IGPU_ENABLE      = '1'
            }
        }
    }
}

function Set-ComputeMode {
    param(
        [string]$ModeName,
        $ModeDefinitions
    )

    $definition = $ModeDefinitions[$ModeName]
    if (-not $definition) {
        throw "Unknown mode: $ModeName"
    }

    Write-Log "Applying mode: $($definition.Label)" -Level Step
    foreach ($entry in $definition.Variables.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'User')
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        Write-Log "  $($entry.Key)=$($entry.Value)"
    }
    [Environment]::SetEnvironmentVariable('CUDA_VISIBLE_DEVICES', $null, 'User')
    [Environment]::SetEnvironmentVariable('CUDA_VISIBLE_DEVICES', $null, 'Process')
}

function Stop-OllamaProcesses {
    Write-Log 'Stopping Ollama processes...' -Level Step

    foreach ($name in @('ollama', 'ollama app')) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
            Write-Log "  Stopping PID $($_.Id) ($($_.ProcessName))"
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        }
    }

    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline) {
        $remaining = @(Get-Process -Name 'ollama', 'ollama app' -ErrorAction SilentlyContinue)
        if ($remaining.Count -eq 0) {
            Write-Log 'Ollama stopped.' -Level Success
            Start-Sleep -Seconds 2
            return
        }
        Start-Sleep -Milliseconds 500
    }

    throw 'Timed out waiting for Ollama processes to exit.'
}

function Start-OllamaApplication {
    if (-not (Test-Path -LiteralPath $Script:OllamaAppPath)) {
        throw "Ollama app not found: $Script:OllamaAppPath"
    }

    Write-Log 'Starting Ollama...' -Level Step
    Start-Process -FilePath $Script:OllamaAppPath | Out-Null
}

function Test-OllamaApiReady {
    $uri = ($OllamaHost.TrimEnd('/')) + '/api/tags'
    try {
        $null = Invoke-RestMethod -Method Get -Uri $uri -TimeoutSec 5
        return $true
    }
    catch {
        return $false
    }
}

function Wait-OllamaApiReady {
    param([switch]$AutoStart)

    Write-Log "Waiting for Ollama API (timeout ${ApiTimeoutSec}s)..." -Level Step
    $deadline = (Get-Date).AddSeconds($ApiTimeoutSec)
    $attempt = 0
    $started = $false

    while ((Get-Date) -lt $deadline) {
        $attempt++
        if (Test-OllamaApiReady) {
            Write-Log "Ollama API ready after $attempt attempt(s)." -Level Success
            Start-Sleep -Seconds 3
            return
        }

        if ($AutoStart -and -not $started -and $attempt -ge 3) {
            $running = @(Get-Process -Name 'ollama', 'ollama app' -ErrorAction SilentlyContinue)
            if ($running.Count -eq 0) {
                Write-Log 'Ollama not running; starting application...' -Level Warning
                Start-OllamaApplication
                $started = $true
            }
        }

        Start-Sleep -Seconds 2
    }

    throw "Ollama API not reachable at $OllamaHost after ${ApiTimeoutSec}s."
}

function Restart-OllamaForMode {
    param([string]$ModeName)

    Stop-OllamaProcesses
    Start-OllamaApplication
    Wait-OllamaApiReady
    Write-Log "Ollama restarted for mode: $ModeName" -Level Success
}

function Get-LocalModels {
    $uri = ($OllamaHost.TrimEnd('/')) + '/api/tags'
    $response = Invoke-RestMethod -Method Get -Uri $uri -TimeoutSec 30
    if (-not $response.models) { return @() }
    return @($response.models | Sort-Object -Property name)
}

function Resolve-DefaultModel {
    param(
        [string]$RequestedModel,
        $Models
    )

    if ($RequestedModel) {
        $match = $Models | Where-Object { $_.name -eq $RequestedModel }
        if (-not $match) {
            throw "Model '$RequestedModel' not found locally. Pull it first."
        }
        return $RequestedModel
    }

    $preferred = @(
        'qwen2.5-coder:7b',
        'qwen2.5:7b',
        'llama3.1:8b',
        'mistral:7b',
        'llama3.2:3b'
    )
    foreach ($name in $preferred) {
        if ($Models | Where-Object { $_.name -eq $name }) {
            Write-Log "Auto-selected model: $name" -Level Warning
            return $name
        }
    }

    if ($Models.Count -eq 0) {
        throw 'No local models found.'
    }

    $fallback = $Models[0].name
    Write-Log "Auto-selected first local model: $fallback" -Level Warning
    return $fallback
}

$Script:AutomatedShortPrompt = 'Summarize in one paragraph how AMD Vulkan GPU selection affects local LLM inference speed on Windows 11.'

function Invoke-ModeBenchmark {
    param(
        [string]$ModeName,
        [string]$SelectedModel,
        [string]$CsvPath,
        [string]$BenchmarkPrompt = $Script:AutomatedShortPrompt
    )

    $benchmarkScript = Join-Path $Script:RootDir 'Test-OllamaBenchmark.ps1'
    if (-not (Test-Path -LiteralPath $benchmarkScript)) {
        throw "Benchmark script not found: $benchmarkScript"
    }

    $args = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $benchmarkScript,
        '-ModelName', $SelectedModel,
        '-Mode', $ModeName,
        '-NumPredict', $NumPredict,
        '-Runs', $Runs,
        '-OutputCsv', $CsvPath,
        '-OllamaHost', $OllamaHost,
        '-Prompt', $BenchmarkPrompt,
        '-NumCtx', $NumCtx,
        '-Force'
    )

    Write-Log "Running benchmark: mode=$ModeName model=$SelectedModel runs=$Runs tokens=$NumPredict" -Level Step
    $output = & powershell.exe @args 2>&1
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Log "  $_" }

    if ($exitCode -ne 0) {
        throw "Benchmark failed for mode $ModeName (exit $exitCode)."
    }

    if (-not (Test-Path -LiteralPath $CsvPath)) {
        throw "Benchmark CSV not created: $CsvPath"
    }

    $rows = Import-Csv -LiteralPath $CsvPath
    return $rows | Select-Object -Last 1
}

function Initialize-ScreenshotAssemblies {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
}

function Save-Screenshot {
    param(
        [string]$Path,
        [System.Drawing.Rectangle]$Region
    )

    Initialize-ScreenshotAssemblies
    $bitmap = New-Object System.Drawing.Bitmap $Region.Width, $Region.Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($Region.Location, [System.Drawing.Point]::Empty, $Region.Size)
    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    $bitmap.Dispose()
    Write-Log "Screenshot saved: $Path" -Level Success
}

function Show-StatusCardAndCapture {
    param(
        [string]$Title,
        [string]$Body,
        [string]$OutputPath
    )

    Initialize-ScreenshotAssemblies

    $form = New-Object System.Windows.Forms.Form
    $form.Text = $Title
    $form.Size = New-Object System.Drawing.Size 900, 520
    $form.StartPosition = 'CenterScreen'
    $form.TopMost = $true
    $form.BackColor = [System.Drawing.Color]::FromArgb(24, 24, 28)

    $titleLabel = New-Object System.Windows.Forms.Label
    $titleLabel.Dock = 'Top'
    $titleLabel.Height = 56
    $titleLabel.Text = $Title
    $titleLabel.ForeColor = [System.Drawing.Color]::FromArgb(90, 180, 255)
    $titleLabel.Font = New-Object System.Drawing.Font('Segoe UI', 16, [System.Drawing.FontStyle]::Bold)
    $titleLabel.Padding = New-Object System.Windows.Forms.Padding 20, 12, 20, 0
    $form.Controls.Add($titleLabel)

    $bodyLabel = New-Object System.Windows.Forms.Label
    $bodyLabel.Dock = 'Fill'
    $bodyLabel.Text = $Body
    $bodyLabel.ForeColor = [System.Drawing.Color]::White
    $bodyLabel.Font = New-Object System.Drawing.Font('Consolas', 11)
    $bodyLabel.Padding = New-Object System.Windows.Forms.Padding 20
    $form.Controls.Add($bodyLabel)

    $captureState = @{ Done = $false }
    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = 1200
    $timer.Add_Tick({
        if (-not $captureState.Done) {
            $captureState.Done = $true
            $bounds = $form.Bounds
            Save-Screenshot -Path $OutputPath -Region $bounds
            $timer.Stop()
            $form.Close()
        }
    })

    $form.Add_Shown({ $timer.Start() })
    [void]$form.ShowDialog()
    $timer.Dispose()
    $form.Dispose()
}

function Get-FastestMode {
    param($Results)
    $valid = $Results | Where-Object { $_.Status -eq 'Success' -and $_.Generation_tps -gt 0 }
    if (-not $valid) { return $null }
    return ($valid | Sort-Object -Property { [double]$_.Generation_tps } -Descending | Select-Object -First 1)
}

function ConvertTo-HtmlReport {
    param(
        $ReportData,
        [string]$ScreenshotRelativePath
    )

    $rowsHtml = ''
    foreach ($row in $ReportData.Results) {
        $statusClass = if ($row.Status -eq 'Success') { 'ok' } else { 'fail' }
        $rowsHtml += @"
        <tr class="$statusClass">
            <td>$($row.Mode)</td>
            <td>$($row.Status)</td>
            <td>$($row.Generation_tps)</td>
            <td>$($row.PromptEval_tps)</td>
            <td>$($row.TTFT_ms)</td>
            <td>$($row.VRAM_MB)</td>
            <td>$($row.DurationSec)</td>
            <td>$($row.Notes)</td>
        </tr>
"@
    }

    $winner = $ReportData.Winner
    $winnerText = if ($winner) {
        "$($winner.Mode) at $($winner.Generation_tps) tok/s"
    } else {
        'N/A'
    }

    $screenshotBlock = ''
    if ($ScreenshotRelativePath) {
        $screenshotBlock = "<h2>Report Screenshot</h2><img src=`"$ScreenshotRelativePath`" alt=`"Report screenshot`" />"
    }

    return @"
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <title>Ollama Automated Benchmark Report</title>
  <style>
    body { font-family: Segoe UI, Arial, sans-serif; background: #14141a; color: #eee; margin: 24px; }
    h1, h2 { color: #5ab4ff; }
    .meta { background: #1e1e28; padding: 16px; border-radius: 8px; margin-bottom: 20px; }
    table { width: 100%; border-collapse: collapse; margin-top: 12px; }
    th, td { border: 1px solid #333; padding: 10px; text-align: left; }
    th { background: #243040; }
    tr.ok td { background: #16251c; }
    tr.fail td { background: #2a1717; }
    .winner { color: #7dffa8; font-size: 1.2em; font-weight: bold; }
    img { max-width: 100%; border: 1px solid #444; border-radius: 8px; margin-top: 12px; }
    .shots { display: flex; flex-wrap: wrap; gap: 12px; }
    .shots img { width: 420px; }
  </style>
</head>
<body>
  <h1>Ollama AMD Vulkan Automated Benchmark Report</h1>
  <div class="meta">
    <div><strong>Generated:</strong> $($ReportData.CompletedAt)</div>
    <div><strong>Model:</strong> $($ReportData.Model)</div>
    <div><strong>Quantization:</strong> $($ReportData.Quantization)</div>
    <div><strong>Modes tested:</strong> $($ReportData.Modes -join ', ')</div>
    <div><strong>Runs per mode:</strong> $($ReportData.Runs)</div>
    <div><strong>Tokens per run:</strong> $($ReportData.NumPredict)</div>
    <div><strong>Fastest mode:</strong> <span class="winner">$winnerText</span></div>
    <div><strong>Vulkan mapping:</strong> APU index $($ReportData.DeviceMap.ApuVulkanIndex) (680M), GPU index $($ReportData.DeviceMap.GpuVulkanIndex) (6700S)</div>
    <div><strong>Task Manager:</strong> GPU 0 = 6700S, GPU 1 = 680M</div>
  </div>

  <h2>Results</h2>
  <table>
    <thead>
      <tr>
        <th>Mode</th><th>Status</th><th>Gen tok/s</th><th>Prompt tok/s</th>
        <th>TTFT ms</th><th>VRAM MB</th><th>Duration s</th><th>Notes</th>
      </tr>
    </thead>
    <tbody>
      $rowsHtml
    </tbody>
  </table>

  <h2>Per-Mode Screenshots</h2>
  <div class="shots">
$(
    ($ReportData.Results | ForEach-Object {
        if ($_.Screenshot) {
            "    <div><div>$($_.Mode)</div><img src=`"$($_.Screenshot)`" alt=`"$($_.Mode)`" /></div>"
        }
    }) -join "`n"
)
  </div>

  $screenshotBlock
</body>
</html>
"@
}

function Show-HtmlReportAndCapture {
    param(
        [string]$HtmlPath,
        [string]$ScreenshotPath
    )

    Initialize-ScreenshotAssemblies

    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'Ollama Benchmark Report'
    $form.WindowState = [System.Windows.Forms.FormWindowState]::Maximized
    $form.StartPosition = 'CenterScreen'
    $form.TopMost = $true

    $browser = New-Object System.Windows.Forms.WebBrowser
    $browser.Dock = 'Fill'
    $browser.ScriptErrorsSuppressed = $true
    $fileUri = [Uri]::new((Resolve-Path -LiteralPath $HtmlPath).Path).AbsoluteUri
    $browser.Navigate($fileUri)
    $form.Controls.Add($browser)

    $captureState = @{ Done = $false }
    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = 3500
    $timer.Add_Tick({
        if (-not $captureState.Done) {
            $captureState.Done = $true
            $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
            Save-Screenshot -Path $ScreenshotPath -Region $bounds
            $timer.Stop()
            $form.Close()
        }
    })

    $form.Add_Shown({ $timer.Start() })
    [void]$form.ShowDialog()
    $timer.Dispose()
    $form.Dispose()
}

# --- Main ---

$startedAt = Get-Date
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$screenshotDir = Join-Path $OutputDir 'screenshots'
New-Item -ItemType Directory -Path $screenshotDir -Force | Out-Null

$sessionCsv = Join-Path $OutputDir 'session-results.csv'
$jsonPath = Join-Path $OutputDir 'report.json'
$htmlPath = Join-Path $OutputDir 'report.html'
$reportScreenshot = Join-Path $OutputDir 'report-fullscreen.png'
$htmlScreenshot = Join-Path $OutputDir 'report-window.png'

Write-Log '=== Ollama Automated Benchmark Suite ===' -Level Step
Write-Log "Output: $OutputDir"

if ($Modes.Count -eq 1 -and $Modes[0] -match ',') {
    $Modes = @($Modes[0].Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

$validModes = @('CPU', 'APU', 'GPU', 'Hybrid')
foreach ($m in $Modes) {
    if ($validModes -notcontains $m) {
        throw "Invalid mode '$m'. Valid modes: $($validModes -join ', ')"
    }
}

$deviceMap = Import-ToolkitHelpers
$modeDefinitions = Get-ModeDefinitions -DeviceMap $deviceMap

Wait-OllamaApiReady -AutoStart
$models = Get-LocalModels
$selectedModel = Resolve-DefaultModel -RequestedModel $ModelName -Models $models
$quantModel = $models | Where-Object { $_.name -eq $selectedModel } | Select-Object -First 1
$quantization = if ($quantModel -and $quantModel.details.quantization_level) {
    $quantModel.details.quantization_level
} else {
    'unknown'
}

$results = @()

foreach ($mode in $Modes) {
    $modeStarted = Get-Date
    $modeScreenshot = Join-Path $screenshotDir ("mode-$mode.png")
    $resultRow = [ordered]@{
        Mode            = $mode
        Status          = 'Pending'
        Generation_tps  = 0
        PromptEval_tps  = 0
        TTFT_ms         = 0
        VRAM_MB         = 0
        DurationSec     = 0
        Notes           = ''
        Screenshot      = if ($SkipScreenshots) { $null } else { "screenshots/mode-$mode.png" }
        Error           = $null
    }

    try {
        Write-Log "========== MODE: $mode ==========" -Level Step
        Set-ComputeMode -ModeName $mode -ModeDefinitions $modeDefinitions
        Restart-OllamaForMode -ModeName $mode

        $csvRow = Invoke-ModeBenchmark -ModeName $mode -SelectedModel $selectedModel -CsvPath $sessionCsv

        $resultRow.Status = 'Success'
        $resultRow.Generation_tps = [double]$csvRow.Generation_tps
        $resultRow.PromptEval_tps = [double]$csvRow.PromptEval_tps
        $resultRow.TTFT_ms = [double]$csvRow.TTFT_ms
        $resultRow.VRAM_MB = [double]$csvRow.VRAM_MB
        $resultRow.Notes = $csvRow.Notes

        if (-not $SkipScreenshots) {
            $cardBody = @"
Mode: $mode
Model: $selectedModel
Generation: $($csvRow.Generation_tps) tok/s
Prompt Eval: $($csvRow.PromptEval_tps) tok/s
TTFT: $($csvRow.TTFT_ms) ms
VRAM: $($csvRow.VRAM_MB) MB
Status: SUCCESS
"@
            Show-StatusCardAndCapture -Title "Ollama Benchmark - $mode" -Body $cardBody -OutputPath $modeScreenshot
        }
    }
    catch {
        $resultRow.Status = 'Failed'
        $resultRow.Error = $_.Exception.Message
        $resultRow.Notes = $_.Exception.Message
        Write-Log "Mode $mode failed: $($_.Exception.Message)" -Level Error

        if (-not $SkipScreenshots) {
            Show-StatusCardAndCapture -Title "Ollama Benchmark - $mode FAILED" -Body $_.Exception.Message -OutputPath $modeScreenshot
        }
    }
    finally {
        $resultRow.DurationSec = [math]::Round(((Get-Date) - $modeStarted).TotalSeconds, 1)
        $results += [pscustomobject]$resultRow
    }
}

$winner = Get-FastestMode -Results $results
$completedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')

$deviceMapSummary = @{
    ApuVulkanIndex    = $deviceMap.ApuVulkanIndex
    GpuVulkanIndex    = $deviceMap.GpuVulkanIndex
    HybridVulkanValue = $deviceMap.HybridVulkanValue
    ApuName           = $deviceMap.ApuName
    GpuName           = $deviceMap.GpuName
}

$winnerSummary = $null
if ($winner) {
    $winnerSummary = @{
        Mode           = $winner.Mode
        Generation_tps = $winner.Generation_tps
        TTFT_ms        = $winner.TTFT_ms
        VRAM_MB        = $winner.VRAM_MB
    }
}

$resultRows = @($results | ForEach-Object {
    @{
        Mode           = $_.Mode
        Status         = $_.Status
        Generation_tps = $_.Generation_tps
        PromptEval_tps = $_.PromptEval_tps
        TTFT_ms        = $_.TTFT_ms
        VRAM_MB        = $_.VRAM_MB
        DurationSec    = $_.DurationSec
        Notes          = $_.Notes
        Screenshot     = $_.Screenshot
        Error          = $_.Error
    }
})

$reportPayload = @{
    StartedAt    = $startedAt.ToString('o')
    CompletedAt  = $completedAt
    DurationMin  = [math]::Round(((Get-Date) - $startedAt).TotalMinutes, 2)
    Model        = $selectedModel
    Quantization = $quantization
    Modes        = @($Modes)
    Runs         = $Runs
    NumPredict   = $NumPredict
    DeviceMap    = $deviceMapSummary
    Winner       = $winnerSummary
    Results      = $resultRows
    OutputDir    = $OutputDir
}

$reportPayload | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$reportData = [pscustomobject]@{
    StartedAt    = $reportPayload.StartedAt
    CompletedAt  = $reportPayload.CompletedAt
    DurationMin  = $reportPayload.DurationMin
    Model        = $reportPayload.Model
    Quantization = $reportPayload.Quantization
    Modes        = $reportPayload.Modes
    Runs         = $reportPayload.Runs
    NumPredict   = $reportPayload.NumPredict
    DeviceMap    = [pscustomobject]$deviceMapSummary
    Winner       = if ($winnerSummary) { [pscustomobject]$winnerSummary } else { $null }
    Results      = @($results)
    OutputDir    = $OutputDir
}

$html = ConvertTo-HtmlReport -ReportData $reportData -ScreenshotRelativePath 'report-window.png'
Set-Content -LiteralPath $htmlPath -Value $html -Encoding UTF8

Write-Log 'Report files written.' -Level Success
Write-Log "  JSON: $jsonPath"
Write-Log "  HTML: $htmlPath"
Write-Log "  CSV : $sessionCsv"

if (-not $SkipScreenshots -and -not $SkipFinalScreenshots) {
    Write-Log 'Capturing full report screenshot...' -Level Step
    Show-HtmlReportAndCapture -HtmlPath $htmlPath -ScreenshotPath $htmlScreenshot

    Write-Log 'Capturing desktop screenshot...' -Level Step
    Initialize-ScreenshotAssemblies
    $desktop = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    Save-Screenshot -Path $reportScreenshot -Region $desktop
}

Write-Log '' 
Write-Log '========== AUTOMATED BENCHMARK COMPLETE ==========' -Level Step
Write-Log "Model     : $selectedModel"
Write-Log "Duration  : $($reportData.DurationMin) minutes"
if ($winner) {
    Write-Log "Fastest   : $($winner.Mode) ($($winner.Generation_tps) tok/s)" -Level Success
}

Write-Log ''
Write-Log 'Results by mode:'
foreach ($r in $results) {
    if ($r.Status -eq 'Success') {
        Write-Log ("  {0,-7} {1,8} tok/s | TTFT {2,7} ms | VRAM {3,8} MB" -f $r.Mode, $r.Generation_tps, $r.TTFT_ms, $r.VRAM_MB) -Level Success
    }
    else {
        Write-Log ("  {0,-7} FAILED: {1}" -f $r.Mode, $r.Error) -Level Error
    }
}

Write-Log ''
Write-Log "Open report: $htmlPath" -Level Success
if (-not $SkipScreenshots) {
    Write-Log "Screenshot : $htmlScreenshot" -Level Success
}

return [pscustomobject]$reportPayload