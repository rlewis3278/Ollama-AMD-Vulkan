#Requires -Version 5.1
<#
.SYNOPSIS
    Benchmark locally downloaded Ollama models via the REST API.

.DESCRIPTION
    Discovers models from GET /api/tags, runs warm-up and timed generations using
    POST /api/generate (stream: false), and logs metrics to CSV.

.PARAMETER ModelName
    Model tag to benchmark (e.g. qwen2.5-coder:7b). Interactive selection if omitted.

.PARAMETER Mode
    Label for the current compute mode in CSV output. Auto-detected if omitted.

.PARAMETER Prompt
    Custom benchmark prompt.

.PARAMETER NumPredict
    Maximum tokens to generate. Default: 128.

.PARAMETER Warmup
    Run a warm-up generation before recording metrics. Default: true.

.PARAMETER OutputCsv
    CSV output path. Default: .\ollama-benchmark-results.csv in script directory.

.PARAMETER OllamaHost
    Ollama API base URL. Default: http://localhost:11434

.PARAMETER Force
    Skip confirmation prompts.

.PARAMETER ListModels
    List locally downloaded models and exit.

.PARAMETER CompareModes
    Print a step-by-step workflow to benchmark all four compute modes.

.PARAMETER Runs
    Number of timed benchmark runs to average (warm-up still runs once). Default: 1.

.EXAMPLE
    .\Test-OllamaBenchmark.ps1

.EXAMPLE
    .\Test-OllamaBenchmark.ps1 -ModelName "qwen2.5-coder:7b" -Mode GPU -Force

.EXAMPLE
    .\Test-OllamaBenchmark.ps1 -CompareModes -ModelName "qwen2.5-coder:7b"
#>
[CmdletBinding(DefaultParameterSetName = 'Interactive')]
param(
    [string]$ModelName,

    [ValidateSet('CPU', 'APU', 'GPU', 'Hybrid', 'Custom')]
    [string]$Mode,

    [string]$Prompt,
    [int]$NumPredict = 128,
    [switch]$Warmup,
    [string]$OutputCsv,
    [string]$OllamaHost = 'http://localhost:11434',
    [switch]$Force,

    [Parameter(ParameterSetName = 'List')]
    [switch]$ListModels,

    [Parameter(ParameterSetName = 'Compare')]
    [switch]$CompareModes,

    [int]$Runs = 1,

    [int]$NumCtx = 8192
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $PSBoundParameters.ContainsKey('Warmup')) {
    $Warmup = $true
}

if ($Runs -lt 1) {
    throw 'Runs must be at least 1.'
}

$Script:DefaultPrompt = @'
You are a senior systems engineer. Explain how Vulkan device selection affects large language model inference on a Windows 11 laptop with an AMD Ryzen 9 6900HX APU and Radeon RX 6700S discrete GPU. Cover CPU-only, iGPU-only, dGPU-only, and hybrid scheduling. Include practical benchmarking methodology and power/thermal considerations.
'@

function Import-VulkanHelper {
    $helperPath = Join-Path $PSScriptRoot 'Get-VulkanDevices.ps1'
    if (-not (Test-Path -LiteralPath $helperPath)) {
        return $null
    }
    . $helperPath
    $vulkanInfo = Resolve-VulkanInfoPath -ExplicitPath ''
    $report = Get-VulkanDeviceReport -VulkanInfoExe $vulkanInfo
    return $report.DeviceMap
}

function Write-ColorLine {
    param(
        [string]$Text,
        [ConsoleColor]$ForegroundColor = [ConsoleColor]::Gray
    )
    $previous = $Host.UI.RawUI.ForegroundColor
    $Host.UI.RawUI.ForegroundColor = $ForegroundColor
    Write-Host $Text
    $Host.UI.RawUI.ForegroundColor = $previous
}

function Invoke-OllamaApi {
    param(
        [string]$Method,
        [string]$Path,
        [object]$Body = $null
    )

    $uri = ($OllamaHost.TrimEnd('/')) + $Path
    $params = @{
        Method      = $Method
        Uri         = $uri
        TimeoutSec  = 3600
        ErrorAction = 'Stop'
    }

    if ($null -ne $Body) {
        $params.ContentType = 'application/json'
        $params.Body = ($Body | ConvertTo-Json -Depth 6 -Compress)
    }

    $maxAttempts = 3
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        try {
            return Invoke-RestMethod @params
        }
        catch {
            if ($attempt -ge $maxAttempts) { throw }
            Start-Sleep -Seconds (2 * $attempt)
        }
    }
}

function Get-LocalOllamaModels {
    $response = Invoke-OllamaApi -Method 'GET' -Path '/api/tags'
    if (-not $response.models) {
        return @()
    }
    return @($response.models | Sort-Object -Property name)
}

function Show-ModelList {
    param($Models)

    if ($Models.Count -eq 0) {
        Write-ColorLine 'No local models found. Pull a model first, e.g. ollama pull qwen2.5:7b' Yellow
        return
    }

    Write-ColorLine ''
    Write-ColorLine '=== Downloaded Ollama Models ===' Cyan
    foreach ($model in $Models) {
        $quant = $model.details.quantization_level
        $params = $model.details.parameter_size
        $sizeGb = [math]::Round($model.size / 1GB, 2)
        Write-ColorLine ("  {0} ({1}, {2}, {3} GB)" -f $model.name, $params, $quant, $sizeGb) White
    }
    Write-Host ''
}

function Select-OllamaModelInteractive {
    param($Models)

    if ($Models.Count -eq 0) {
        throw 'No local models found. Pull a model first, e.g. ollama pull qwen2.5:7b'
    }

    Show-ModelList -Models $Models

    while ($true) {
        $selection = Read-Host 'Enter model number or full model name'
        if ($selection -match '^\d+$') {
            $index = [int]$selection - 1
            if ($index -ge 0 -and $index -lt $Models.Count) {
                return $Models[$index].name
            }
        }
        else {
            $match = $Models | Where-Object { $_.name -eq $selection }
            if ($match) {
                return $match.name
            }
        }
        Write-ColorLine 'Invalid selection. Try again.' Red
    }
}

function Get-DetectedBenchmarkMode {
    param($DeviceMap)

    $vk = [Environment]::GetEnvironmentVariable('GGML_VK_VISIBLE_DEVICES', 'User')
    $ollamaVk = [Environment]::GetEnvironmentVariable('OLLAMA_VULKAN', 'User')

    if ($ollamaVk -eq '0' -or $vk -eq '-1') { return 'CPU' }
    if ($DeviceMap) {
        if ($vk -eq $DeviceMap.ApuVulkanIndex) { return 'APU' }
        if ($vk -eq $DeviceMap.GpuVulkanIndex) { return 'GPU' }
        if ($vk -eq $DeviceMap.HybridVulkanValue) { return 'Hybrid' }
    }
    else {
        if ($vk -eq '0') { return 'APU' }
        if ($vk -eq '1') { return 'GPU' }
        if ($vk -eq '0,1' -or $vk -eq '1,0') { return 'Hybrid' }
    }
    return 'Custom'
}

function Get-LongOrZero {
    param($Value)
    if ($null -eq $Value) { return [long]0 }
    return [long]$Value
}

function Get-StringOrEmpty {
    param($Value)
    if ($null -eq $Value) { return '' }
    return [string]$Value
}

function Convert-NanosecondsToMilliseconds {
    param($Nanoseconds)
    if ($null -eq $Nanoseconds -or [long]$Nanoseconds -le 0) { return 0 }
    return [math]::Round([long]$Nanoseconds / 1000000.0, 2)
}

function Get-TokensPerSecond {
    param(
        [long]$TokenCount,
        [long]$DurationNs
    )
    if ($TokenCount -le 0 -or $DurationNs -le 0) { return 0 }
    return [math]::Round($TokenCount / ($DurationNs / 1000000000.0), 2)
}

function Get-ModelQuantization {
    param($Models, [string]$Name)
    $model = $Models | Where-Object { $_.name -eq $Name } | Select-Object -First 1
    if ($model -and $model.details.quantization_level) {
        return $model.details.quantization_level
    }
    return 'unknown'
}

function Get-OllamaVramUsageMb {
    try {
        $ps = Invoke-OllamaApi -Method 'GET' -Path '/api/ps'
        if (-not $ps.models) { return 0 }
        $total = ($ps.models | Measure-Object -Property size_vram -Sum).Sum
        return [math]::Round($total / 1MB, 2)
    }
    catch {
        return 0
    }
}

function Invoke-OllamaBenchmarkRun {
    param(
        [string]$Model,
        [string]$BenchmarkPrompt,
        [int]$MaxPredict,
        [switch]$IsWarmup,
        [int]$RunNumber = 1
    )

    if ($IsWarmup) {
        Write-ColorLine "Warm-up run starting for '$Model'..." Cyan
    }
    else {
        Write-ColorLine ("Benchmark run {0} starting for '{1}'..." -f $RunNumber, $Model) Cyan
    }

    $body = [ordered]@{
        model   = $Model
        prompt  = $BenchmarkPrompt
        stream  = $false
        options = [ordered]@{
            num_predict = $MaxPredict
            num_ctx     = $NumCtx
            temperature = 0.2
        }
    }

    $started = Get-Date
    $response = Invoke-OllamaApi -Method 'POST' -Path '/api/generate' -Body $body
    $ended = Get-Date

    $promptEvalCount = Get-LongOrZero -Value $response.prompt_eval_count
    $promptEvalDuration = Get-LongOrZero -Value $response.prompt_eval_duration
    $evalCount = Get-LongOrZero -Value $response.eval_count
    $evalDuration = Get-LongOrZero -Value $response.eval_duration
    $totalDuration = Get-LongOrZero -Value $response.total_duration
    $loadDuration = Get-LongOrZero -Value $response.load_duration

    $promptTps = Get-TokensPerSecond -TokenCount $promptEvalCount -DurationNs $promptEvalDuration
    $genTps = Get-TokensPerSecond -TokenCount $evalCount -DurationNs $evalDuration
    $ttftMs = Convert-NanosecondsToMilliseconds -Nanoseconds ($loadDuration + $promptEvalDuration)
    $totalMs = Convert-NanosecondsToMilliseconds -Nanoseconds $totalDuration
    $wallMs = [math]::Round(($ended - $started).TotalMilliseconds, 2)

    if (-not $IsWarmup) {
        Write-ColorLine ("  Prompt eval : {0} tok @ {1} tok/s" -f $promptEvalCount, $promptTps) White
        Write-ColorLine ("  Generation  : {0} tok @ {1} tok/s" -f $evalCount, $genTps) White
        Write-ColorLine ("  TTFT (est.) : {0} ms" -f $ttftMs) Yellow
        Write-ColorLine ("  Total (API) : {0} ms | Wall clock: {1} ms" -f $totalMs, $wallMs) DarkGray
    }
    else {
        Write-ColorLine '  Warm-up complete.' DarkGray
    }

    return [pscustomobject]@{
        PromptEvalCount    = $promptEvalCount
        PromptEvalDuration = $promptEvalDuration
        EvalCount          = $evalCount
        EvalDuration       = $evalDuration
        TotalDuration      = $totalDuration
        LoadDuration       = $loadDuration
        PromptEval_tps     = $promptTps
        Generation_tps     = $genTps
        TTFT_ms            = $ttftMs
        Total_ms           = $totalMs
        Wall_ms            = $wallMs
        ResponseLength     = (Get-StringOrEmpty -Value $response.response).Length
    }
}

function Initialize-CsvFile {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        $header = 'Timestamp,Mode,Model,Quantization,PromptEval_tps,Generation_tps,TTFT_ms,VRAM_MB,Notes'
        Set-Content -LiteralPath $Path -Value $header -Encoding UTF8
    }
}

function Escape-CsvField {
    param([string]$Value)
    if ($null -eq $Value) { return '""' }
    return '"' + ($Value -replace '"', '""') + '"'
}

function Add-BenchmarkCsvRow {
    param(
        [string]$Path,
        [pscustomobject]$Row
    )

    $line = (@(
        (Escape-CsvField -Value $Row.Timestamp),
        (Escape-CsvField -Value $Row.Mode),
        (Escape-CsvField -Value $Row.Model),
        (Escape-CsvField -Value $Row.Quantization),
        $Row.PromptEval_tps,
        $Row.Generation_tps,
        $Row.TTFT_ms,
        $Row.VRAM_MB,
        (Escape-CsvField -Value $Row.Notes)
    ) -join ',')
    Add-Content -LiteralPath $Path -Value $line -Encoding UTF8
}

function Show-MonitoringGuidance {
    param($DeviceMap)

    Write-ColorLine ''
    Write-ColorLine 'Monitoring tips (Windows):' Cyan
    Write-ColorLine '  - Task Manager GPU 0 = RX 6700S (dGPU)' White
    Write-ColorLine '  - Task Manager GPU 1 = Radeon 680M (iGPU)' White
    if ($DeviceMap) {
        Write-ColorLine ("  - Ollama Vulkan indices: APU=$($DeviceMap.ApuVulkanIndex) (680M), GPU=$($DeviceMap.GpuVulkanIndex) (6700S)") DarkGray
    }
    Write-ColorLine '  - AMD Software -> Performance -> Metrics (VRAM, GPU utilization)' White
    Write-ColorLine '  - Keep laptop plugged in and use the same power plan for fair comparisons' White
    Write-Host ''
}

function Show-CompareModesWorkflow {
    param(
        [string]$SelectedModel,
        $DeviceMap
    )

    $manager = Join-Path $PSScriptRoot 'Ollama-AMD-Vulkan-Manager.ps1'
    $benchmark = Join-Path $PSScriptRoot 'Test-OllamaBenchmark.ps1'
    $modelArg = if ($SelectedModel) { "-ModelName `"$SelectedModel`"" } else { '-ModelName "<your-model>"' }

    Write-ColorLine ''
    Write-ColorLine '=== Four-Mode Benchmark Workflow ===' Cyan
    Write-ColorLine 'Run each block, restarting Ollama between mode changes.' Yellow
    Write-Host ''

    if ($DeviceMap) {
        Write-ColorLine 'Detected Vulkan mapping:' DarkGray
        Write-ColorLine ("  APU  (680M)   -> GGML_VK_VISIBLE_DEVICES=$($DeviceMap.ApuVulkanIndex)") DarkGray
        Write-ColorLine ("  GPU  (6700S)  -> GGML_VK_VISIBLE_DEVICES=$($DeviceMap.GpuVulkanIndex)") DarkGray
        Write-ColorLine ("  Hybrid        -> GGML_VK_VISIBLE_DEVICES=$($DeviceMap.HybridVulkanValue)") DarkGray
        Write-Host ''
    }

    $modes = @('CPU', 'APU', 'GPU', 'Hybrid')
    foreach ($targetMode in $modes) {
        Write-ColorLine "--- $targetMode ---" Yellow
        Write-ColorLine (".\Ollama-AMD-Vulkan-Manager.ps1 -Mode $targetMode -Force") White
        Write-ColorLine '# Quit Ollama from tray, then restart Ollama' DarkGray
        Write-ColorLine (".\Test-OllamaBenchmark.ps1 $modelArg -Mode $targetMode -Force") White
        Write-Host ''
    }

    Write-ColorLine "Results append to: $(Join-Path $PSScriptRoot 'ollama-benchmark-results.csv')" Green
    Write-Host ''
}

function Get-AveragedBenchmarkResult {
    param([object[]]$Results)

    if ($Results.Count -eq 0) {
        throw 'No benchmark results to average.'
    }

    if ($Results.Count -eq 1) {
        return $Results[0]
    }

    $promptTpsSum = [double]0
    $genTpsSum = [double]0
    $ttftSum = [double]0
    $wallSum = [double]0

    foreach ($result in $Results) {
        $promptTpsSum += [double]$result.PromptEval_tps
        $genTpsSum += [double]$result.Generation_tps
        $ttftSum += [double]$result.TTFT_ms
        $wallSum += [double]$result.Wall_ms
    }

    $count = $Results.Count
    $last = $Results[-1]

    return [pscustomobject]@{
        PromptEvalCount    = $last.PromptEvalCount
        PromptEvalDuration = $last.PromptEvalDuration
        EvalCount          = $last.EvalCount
        EvalDuration       = $last.EvalDuration
        TotalDuration      = $last.TotalDuration
        LoadDuration       = $last.LoadDuration
        PromptEval_tps     = [math]::Round($promptTpsSum / $count, 2)
        Generation_tps     = [math]::Round($genTpsSum / $count, 2)
        TTFT_ms            = [math]::Round($ttftSum / $count, 2)
        Total_ms           = $last.Total_ms
        Wall_ms            = [math]::Round($wallSum / $count, 2)
        ResponseLength     = $last.ResponseLength
        RunCount           = $count
    }
}

function Invoke-SingleBenchmark {
    param(
        [string]$SelectedModel,
        [string]$BenchmarkMode,
        [string]$BenchmarkPrompt,
        [int]$MaxPredict,
        [bool]$DoWarmup,
        [int]$RunCount,
        [string]$CsvPath,
        [string]$Quantization,
        $DeviceMap
    )

    if ($DoWarmup) {
        $null = Invoke-OllamaBenchmarkRun -Model $SelectedModel -BenchmarkPrompt $BenchmarkPrompt -MaxPredict 32 -IsWarmup
        Start-Sleep -Seconds 2
    }

    $runResults = @()
    for ($i = 1; $i -le $RunCount; $i++) {
        $runResults += Invoke-OllamaBenchmarkRun -Model $SelectedModel -BenchmarkPrompt $BenchmarkPrompt -MaxPredict $MaxPredict -RunNumber $i
        if ($i -lt $RunCount) {
            Start-Sleep -Seconds 1
        }
    }

    $result = Get-AveragedBenchmarkResult -Results $runResults
    $vramMb = Get-OllamaVramUsageMb

    $vkNote = ''
    if ($DeviceMap) {
        $vkNote = "vulkan_apu=$($DeviceMap.ApuVulkanIndex); vulkan_gpu=$($DeviceMap.GpuVulkanIndex); "
    }

    $notes = "{0}prompt_tokens={1}; gen_tokens={2}; wall_ms={3}; runs={4}" -f `
        $vkNote, $result.PromptEvalCount, $result.EvalCount, $result.Wall_ms, $RunCount

    if ($RunCount -gt 1) {
        $notes += '; averaged=true'
    }

    $csvRow = [pscustomobject]@{
        Timestamp      = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        Mode           = $BenchmarkMode
        Model          = $SelectedModel
        Quantization   = $Quantization
        PromptEval_tps = $result.PromptEval_tps
        Generation_tps = $result.Generation_tps
        TTFT_ms        = $result.TTFT_ms
        VRAM_MB        = $vramMb
        Notes          = $notes
    }

    Initialize-CsvFile -Path $CsvPath
    Add-BenchmarkCsvRow -Path $CsvPath -Row $csvRow

    Write-ColorLine ''
    Write-ColorLine 'Benchmark complete. Results logged to CSV.' Green
    if ($RunCount -gt 1) {
        Write-ColorLine ("  Averaged over {0} runs" -f $RunCount) Green
    }
    Write-ColorLine ("  Generation speed: {0} tok/s" -f $result.Generation_tps) Green
    Write-ColorLine ("  TTFT (estimated): {0} ms" -f $result.TTFT_ms) Green
    Write-ColorLine ("  VRAM (api/ps)    : {0} MB" -f $vramMb) Green
    Write-Host ''

    return $csvRow
}

function Show-BenchmarkInteractiveMenu {
    param(
        $Models,
        $DeviceMap
    )

    while ($true) {
        Write-ColorLine ''
        Write-ColorLine '=== Ollama Vulkan Benchmark ===' Cyan
        Write-ColorLine ("Detected mode: {0}" -f (Get-DetectedBenchmarkMode -DeviceMap $DeviceMap)) Yellow
        Write-Host ''
        Write-ColorLine '  1) Run benchmark (select model)' White
        Write-ColorLine '  2) List downloaded models' White
        Write-ColorLine '  3) Show four-mode comparison workflow' White
        Write-ColorLine '  4) Run benchmark with custom run count' White
        Write-ColorLine '  0) Exit' DarkGray
        Write-Host ''

        $choice = Read-Host 'Select an option'
        switch ($choice) {
            '1' {
                $selected = Select-OllamaModelInteractive -Models $Models
                $mode = Get-DetectedBenchmarkMode -DeviceMap $DeviceMap
                Show-MonitoringGuidance -DeviceMap $DeviceMap
                $confirm = Read-Host 'Start benchmark? [Y/N]'
                if ($confirm -match '^(y|yes)$') {
                    $null = Invoke-SingleBenchmark `
                        -SelectedModel $selected `
                        -BenchmarkMode $mode `
                        -BenchmarkPrompt $Script:DefaultPrompt `
                        -MaxPredict 128 `
                        -DoWarmup $true `
                        -RunCount 1 `
                        -CsvPath (Join-Path $PSScriptRoot 'ollama-benchmark-results.csv') `
                        -Quantization (Get-ModelQuantization -Models $Models -Name $selected) `
                        -DeviceMap $DeviceMap
                }
            }
            '2' { Show-ModelList -Models $Models }
            '3' {
                $modelForGuide = Read-Host 'Model name for workflow examples (optional, press Enter to skip)'
                if ([string]::IsNullOrWhiteSpace($modelForGuide)) { $modelForGuide = $null }
                Show-CompareModesWorkflow -SelectedModel $modelForGuide -DeviceMap $DeviceMap
            }
            '4' {
                $selected = Select-OllamaModelInteractive -Models $Models
                $runInput = Read-Host 'Number of timed runs to average [default 3]'
                $runCount = 3
                if ($runInput -match '^\d+$' -and [int]$runInput -gt 0) {
                    $runCount = [int]$runInput
                }
                $mode = Get-DetectedBenchmarkMode -DeviceMap $DeviceMap
                $null = Invoke-SingleBenchmark `
                    -SelectedModel $selected `
                    -BenchmarkMode $mode `
                    -BenchmarkPrompt $Script:DefaultPrompt `
                    -MaxPredict 128 `
                    -DoWarmup $true `
                    -RunCount $runCount `
                    -CsvPath (Join-Path $PSScriptRoot 'ollama-benchmark-results.csv') `
                    -Quantization (Get-ModelQuantization -Models $Models -Name $selected) `
                    -DeviceMap $DeviceMap
            }
            '0' { return }
            default { Write-ColorLine 'Invalid selection.' Red }
        }
    }
}

try {
    if (-not $OutputCsv) {
        $OutputCsv = Join-Path $PSScriptRoot 'ollama-benchmark-results.csv'
    }

    if (-not $Prompt) {
        $Prompt = $Script:DefaultPrompt
    }

    $deviceMap = Import-VulkanHelper
    $models = Get-LocalOllamaModels

    if ($ListModels) {
        Show-ModelList -Models $models
        return
    }

    if ($CompareModes) {
        if (-not $ModelName) {
            if ($models.Count -gt 0) {
                Show-ModelList -Models $models
                $ModelName = Read-Host 'Enter model name for workflow examples (optional)'
                if ([string]::IsNullOrWhiteSpace($ModelName)) { $ModelName = $null }
            }
        }
        Show-CompareModesWorkflow -SelectedModel $ModelName -DeviceMap $deviceMap
        return
    }

    if ($PSCmdlet.ParameterSetName -eq 'Interactive' -and -not $ModelName -and -not $Force) {
        if ($models.Count -eq 0) {
            throw 'No local models found. Pull a model first, e.g. ollama pull qwen2.5:7b'
        }
        Show-BenchmarkInteractiveMenu -Models $models -DeviceMap $deviceMap
        return
    }

    Write-ColorLine ''
    Write-ColorLine '=== Ollama Vulkan Benchmark ===' Cyan
    Write-ColorLine "API host: $OllamaHost" DarkGray

    if (-not $ModelName) {
        $ModelName = Select-OllamaModelInteractive -Models $models
    }
    else {
        $exists = $models | Where-Object { $_.name -eq $ModelName }
        if (-not $exists) {
            throw "Model '$ModelName' is not in local /api/tags list. Pull it first or choose another model."
        }
    }

    if (-not $Mode) {
        $Mode = Get-DetectedBenchmarkMode -DeviceMap $deviceMap
    }

    $quantization = Get-ModelQuantization -Models $models -Name $ModelName

    Write-ColorLine ''
    Write-ColorLine ("Model : {0}" -f $ModelName) Yellow
    Write-ColorLine ("Mode  : {0}" -f $Mode) Yellow
    Write-ColorLine ("Quant : {0}" -f $quantization) Yellow
    Write-ColorLine ("Runs  : {0}" -f $Runs) Yellow
    Write-ColorLine ("CSV   : {0}" -f $OutputCsv) DarkGray
    Show-MonitoringGuidance -DeviceMap $deviceMap

    if (-not $Force) {
        $confirm = Read-Host 'Start benchmark? [Y/N]'
        if ($confirm -notmatch '^(y|yes)$') {
            Write-ColorLine 'Cancelled.' Yellow
            return
        }
    }

    $csvRow = Invoke-SingleBenchmark `
        -SelectedModel $ModelName `
        -BenchmarkMode $Mode `
        -BenchmarkPrompt $Prompt `
        -MaxPredict $NumPredict `
        -DoWarmup $Warmup `
        -RunCount $Runs `
        -CsvPath $OutputCsv `
        -Quantization $quantization `
        -DeviceMap $deviceMap

    return $csvRow
}
catch {
    Write-ColorLine "ERROR: $($_.Exception.Message)" Red
    Write-ColorLine 'Ensure Ollama is running and reachable at the configured host.' Yellow
    exit 1
}

exit 0