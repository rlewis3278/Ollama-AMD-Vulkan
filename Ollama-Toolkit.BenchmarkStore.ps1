#Requires -Version 5.1
Set-StrictMode -Version Latest

if (-not $Script:ToolkitRoot) {
    $Script:ToolkitRoot = $PSScriptRoot
}

$Script:ModelProfilesFile = Join-Path $Script:ConfigDir 'model-profiles.json'
$Script:GuiTestReportsDir = Join-Path $Script:ToolkitRoot (Join-Path 'reports' 'gui-tests')

function Get-ToolkitLocalOllamaModels {
    param(
        [int]$TimeoutSec = 3,
        [int]$CacheTtlSec = 15,
        [switch]$ForceRefresh
    )

    $models = Get-OllamaTagsApiModels -TimeoutSec $TimeoutSec -TtlSec $CacheTtlSec -ForceRefresh:$ForceRefresh
    return @(
        $models |
            Where-Object {
                -not $_.PSObject.Properties['remote_host'] -and
                $_.details.format -eq 'gguf' -and
                $_.size -gt 1MB
            } |
            Sort-Object -Property size, name
    )
}

function Get-RecommendedBenchmarkNumCtx {
    param([double]$SizeGB)

    if ($SizeGB -ge 18) { return 4096 }
    if ($SizeGB -ge 8) { return 6144 }
    return 8192
}

function New-EmptyModelProfileStore {
    return [ordered]@{
        Version     = 1
        LastUpdated = (Get-Date).ToString('o')
        Models      = [ordered]@{}
    }
}

$Script:HashtableMetaProps = @(
    'Count', 'Keys', 'Values', 'IsReadOnly', 'IsFixedSize', 'SyncRoot', 'IsSynchronized'
)

function ConvertTo-ProfileHashtable {
    param($Store)

    $models = [ordered]@{}
    if ($Store.Models) {
        if ($Store.Models -is [System.Collections.IDictionary]) {
            foreach ($key in @($Store.Models.Keys)) {
                if ($key -in $Script:HashtableMetaProps) { continue }
                $models[$key] = $Store.Models[$key]
            }
        }
        else {
            foreach ($prop in $Store.Models.PSObject.Properties) {
                if ($prop.Name -in $Script:HashtableMetaProps) { continue }
                $models[$prop.Name] = $prop.Value
            }
        }
    }
    return [ordered]@{
        Version     = if ($Store.Version) { [int]$Store.Version } else { 1 }
        LastUpdated = if ($Store.LastUpdated) { [string]$Store.LastUpdated } else { (Get-Date).ToString('o') }
        Models      = $models
    }
}

function Get-ModelProfileStore {
    Ensure-ConfigDirectory
    if (-not (Test-Path -LiteralPath $Script:ModelProfilesFile)) {
        return New-EmptyModelProfileStore
    }

    try {
        $data = Get-Content -LiteralPath $Script:ModelProfilesFile -Raw | ConvertFrom-Json
        return ConvertTo-ProfileHashtable -Store $data
    }
    catch {
        Write-ToolkitLog "Profile store corrupt; starting fresh: $($_.Exception.Message)" Yellow
        return New-EmptyModelProfileStore
    }
}

function Save-ModelProfileStore {
    param($Store)

    Ensure-ConfigDirectory
    $normalized = ConvertTo-ProfileHashtable -Store $Store
    $modelsExport = [ordered]@{}
    foreach ($key in @($normalized.Models.Keys)) {
        $val = $normalized.Models[$key]
        $resultsExport = [ordered]@{}
        if ($val.Results) {
            if ($val.Results -is [System.Collections.IDictionary]) {
                foreach ($mode in @($val.Results.Keys)) {
                    $resultsExport[$mode] = $val.Results[$mode]
                }
            }
            else {
                foreach ($rp in $val.Results.PSObject.Properties) {
                    $resultsExport[$rp.Name] = $rp.Value
                }
            }
        }
        $modelsExport[$key] = [ordered]@{
            BestMode     = $val.BestMode
            BestTps      = $val.BestTps
            Quantization = $val.Quantization
            NumCtx       = $val.NumCtx
            NumPredict   = $val.NumPredict
            Runs         = $val.Runs
            LastTested   = $val.LastTested
            Results      = $resultsExport
            ReportPath   = $val.ReportPath
            OutputDir    = $val.OutputDir
            Digest       = $val.Digest
        }
    }

    $payload = [ordered]@{
        Version     = 1
        LastUpdated = (Get-Date).ToString('o')
        Models      = $modelsExport
    }
    $payload | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Script:ModelProfilesFile -Encoding UTF8
}

function ConvertFrom-BenchmarkReport {
    param(
        [string]$ReportPath,
        [int]$NumCtx = 8192
    )

    if (-not (Test-Path -LiteralPath $ReportPath)) { return $null }
    $report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
    if (-not $report.Model) { return $null }

    $results = @{}
    foreach ($row in @($report.Results)) {
        $results[$row.Mode] = [ordered]@{
            Status         = $row.Status
            Generation_tps = [double]$row.Generation_tps
            PromptEval_tps = [double]$row.PromptEval_tps
            TTFT_ms        = [double]$row.TTFT_ms
            VRAM_MB        = [double]$row.VRAM_MB
            Notes          = if ($row.Notes) { [string]$row.Notes } else { '' }
            Error          = if ($row.Error) { [string]$row.Error } else { '' }
        }
    }

    $winner = $null
    if ($report.Winner -and $report.Winner.Mode) {
        $winner = [ordered]@{
            Mode           = [string]$report.Winner.Mode
            Generation_tps = [double]$report.Winner.Generation_tps
            TTFT_ms        = [double]$report.Winner.TTFT_ms
            VRAM_MB        = [double]$report.Winner.VRAM_MB
        }
    }

    return [ordered]@{
        Model         = [string]$report.Model
        Quantization  = if ($report.Quantization) { [string]$report.Quantization } else { 'unknown' }
        BestMode      = if ($winner) { $winner.Mode } else { $null }
        BestTps       = if ($winner) { $winner.Generation_tps } else { 0 }
        NumCtx        = $NumCtx
        NumPredict    = if ($report.NumPredict) { [int]$report.NumPredict } else { 32 }
        Runs          = if ($report.Runs) { [int]$report.Runs } else { 1 }
        LastTested    = if ($report.CompletedAt) { [string]$report.CompletedAt } else { (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') }
        Results       = $results
        ReportPath    = (Resolve-Path -LiteralPath $ReportPath).Path
        OutputDir     = if ($report.OutputDir) { [string]$report.OutputDir } else { Split-Path -Parent $ReportPath }
    }
}

function Update-ModelProfileFromReport {
    param(
        [string]$ReportPath,
        [int]$NumCtx = 8192,
        [string]$ModelDigest = ''
    )

    $profile = ConvertFrom-BenchmarkReport -ReportPath $ReportPath -NumCtx $NumCtx
    if (-not $profile) { return $null }

    $store = Get-ModelProfileStore
    if (-not $store.Models -or $store.Models -isnot [System.Collections.IDictionary]) {
        $store.Models = [ordered]@{}
    }

    $entry = [ordered]@{
        BestMode      = $profile.BestMode
        BestTps       = $profile.BestTps
        Quantization  = $profile.Quantization
        NumCtx        = $profile.NumCtx
        NumPredict    = $profile.NumPredict
        Runs          = $profile.Runs
        LastTested    = $profile.LastTested
        Results       = $profile.Results
        ReportPath    = $profile.ReportPath
        OutputDir     = $profile.OutputDir
        Digest        = $ModelDigest
    }

    $store.Models[$profile.Model] = $entry
    Save-ModelProfileStore -Store $store
    return $profile
}

function Get-ModelProfileEntry {
    param([string]$ModelName)

    $store = Get-ModelProfileStore
    if (-not $store.Models -or -not $store.Models.Contains($ModelName)) { return $null }
    $val = $store.Models[$ModelName]

    return [pscustomobject]@{
        Model        = $ModelName
        BestMode     = $val.BestMode
        BestTps      = [double]$val.BestTps
        Quantization = $val.Quantization
        NumCtx       = [int]$val.NumCtx
        LastTested   = $val.LastTested
        Results      = $val.Results
        ReportPath   = $val.ReportPath
        Status       = if ($val.BestMode) { 'Tested' } else { 'Failed' }
        NeedsRetest  = $false
    }
}

function Get-AllModelProfileSummaries {
    $store = Get-ModelProfileStore
    $local = Get-ToolkitLocalOllamaModels
    $summaries = @()

    foreach ($model in $local) {
        $name = $model.name
        $sizeGb = [math]::Round($model.size / 1GB, 2)
        $digest = if ($model.digest) { $model.digest } else { '' }
        $val = $null
        if ($store.Models -and $store.Models.Contains($name)) {
            $val = $store.Models[$name]
        }

        $status = 'Untested'
        $bestMode = ''
        $bestTps = 0
        $lastTested = ''
        $needsRetest = $true

        if ($val) {
            if ($val.Digest -and $digest -and $val.Digest -ne $digest) {
                $status = 'Updated - retest needed'
                $needsRetest = $true
            }
            elseif ($val.BestMode) {
                $status = 'Tested'
                $bestMode = $val.BestMode
                $bestTps = [double]$val.BestTps
                $lastTested = $val.LastTested
                $needsRetest = $false
            }
            else {
                $status = 'Test failed'
                $needsRetest = $true
            }
        }

        $summaries += [pscustomobject]@{
            Model          = $name
            SizeGB         = $sizeGb
            Quantization   = $model.details.quantization_level
            ParameterSize  = $model.details.parameter_size
            Digest         = $digest
            Status         = $status
            BestMode       = $bestMode
            BestTps        = $bestTps
            LastTested     = $lastTested
            NeedsRetest    = $needsRetest
            RecommendedCtx = (Get-RecommendedBenchmarkNumCtx -SizeGB $sizeGb)
        }
    }

    return $summaries
}

function Get-UntestedLocalModels {
    return @(Get-AllModelProfileSummaries | Where-Object { $_.NeedsRetest })
}

function Import-ToolkitBenchmarkReports {
    param(
        [string]$ReportsRoot = (Join-Path $Script:ToolkitRoot 'reports')
    )

    if (-not (Test-Path -LiteralPath $ReportsRoot)) { return 0 }

    $latestByModel = @{}
    $reportFiles = Get-ChildItem -LiteralPath $ReportsRoot -Filter 'report.json' -Recurse -File -ErrorAction SilentlyContinue
    foreach ($file in $reportFiles) {
        try {
            $report = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
            if (-not $report.Model -or -not $report.CompletedAt) { continue }
            $key = $report.Model
            if (-not $latestByModel.ContainsKey($key) -or $report.CompletedAt -gt $latestByModel[$key].CompletedAt) {
                $latestByModel[$key] = [pscustomobject]@{
                    CompletedAt = $report.CompletedAt
                    Path        = $file.FullName
                    NumCtx      = 8192
                }
            }
        }
        catch { }
    }

    $imported = 0
    foreach ($entry in $latestByModel.Values) {
        if (Update-ModelProfileFromReport -ReportPath $entry.Path -NumCtx $entry.NumCtx) {
            $imported++
        }
    }
    return $imported
}

function Get-SafeReportDirName {
    param([string]$Name)
    return ($Name -replace '[:\\/<>|"?*]', '_').TrimEnd('.')
}

function New-GuiTestOutputDir {
    param([string]$ModelName)

    $stamp = (Get-Date).ToString('yyyy-MM-dd_HHmmss')
    $safe = Get-SafeReportDirName -Name $ModelName
    $dir = Join-Path $Script:GuiTestReportsDir "$stamp`_$safe"
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    return $dir
}

function Get-OllamaCliPath {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe'),
        (Join-Path $env:ProgramFiles 'Ollama\ollama.exe')
    )
    foreach ($path in $candidates) {
        if (Test-Path -LiteralPath $path) { return $path }
    }
    return 'ollama'
}