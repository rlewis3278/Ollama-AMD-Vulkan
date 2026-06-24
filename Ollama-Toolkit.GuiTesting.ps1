#Requires -Version 5.1
Set-StrictMode -Version Latest

if (-not $Script:ToolkitRoot) {
    $Script:ToolkitRoot = $PSScriptRoot
}

$Script:GuiTestRunner = @{
    Process       = $null
    LogPath       = $null
    OutputDir     = $null
    ModelName     = $null
    NumCtx        = 8192
    Queue         = @()
    RunningAll    = $false
    StopRequested = $false
    OnComplete    = $null
}

function Start-GuiBenchmarkProcess {
    param(
        [string]$ModelName,
        [string]$OutputDir,
        [int]$NumCtx = 8192,
        [int]$NumPredict = 32,
        [int]$Runs = 1,
        [int]$ApiTimeoutSec = 900
    )

    $testScript = Join-Path $Script:ToolkitRoot 'Invoke-OllamaAutomatedTest.ps1'
    if (-not (Test-Path -LiteralPath $testScript)) {
        throw "Benchmark script not found: $testScript"
    }

    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    $logPath = Join-Path $OutputDir 'gui-test.log'
    if (Test-Path -LiteralPath $logPath) { Remove-Item -LiteralPath $logPath -Force }

    $args = @(
        '-STA', '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', $testScript,
        '-ModelName', $ModelName,
        '-OutputDir', $OutputDir,
        '-NumCtx', $NumCtx,
        '-NumPredict', $NumPredict,
        '-Runs', $Runs,
        '-ApiTimeoutSec', $ApiTimeoutSec,
        '-SkipScreenshots',
        '-SkipFinalScreenshots'
    )

    $argText = ($args | ForEach-Object {
        if ($_ -match '\s') { "`"$_`"" } else { $_ }
    }) -join ' '

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'powershell.exe'
    $psi.Arguments = $argText
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.WorkingDirectory = $Script:ToolkitRoot

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    $proc.EnableRaisingEvents = $true

    $stdoutAction = {
        if (-not [string]::IsNullOrEmpty($EventArgs.Data)) {
            Add-Content -LiteralPath $Event.MessageData -Value $EventArgs.Data -Encoding UTF8
        }
    }
    $stderrAction = {
        if (-not [string]::IsNullOrEmpty($EventArgs.Data)) {
            Add-Content -LiteralPath $Event.MessageData -Value $EventArgs.Data -Encoding UTF8
        }
    }

    $null = Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived -Action $stdoutAction -MessageData $logPath
    $null = Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived -Action $stderrAction -MessageData $logPath

    [void]$proc.Start()
    $proc.BeginOutputReadLine()
    $proc.BeginErrorReadLine()

    $Script:GuiTestRunner.Process = $proc
    $Script:GuiTestRunner.LogPath = $logPath
    $Script:GuiTestRunner.OutputDir = $OutputDir
    $Script:GuiTestRunner.ModelName = $ModelName
    $Script:GuiTestRunner.NumCtx = $NumCtx
    $Script:GuiTestRunner.StopRequested = $false

    Write-ToolkitLog "Started benchmark for '$ModelName' (num_ctx=$NumCtx)" Cyan
    Write-ToolkitLog "Log: $logPath" DarkGray
    return $proc
}

function Stop-GuiBenchmarkProcess {
    $proc = $Script:GuiTestRunner.Process
    if ($proc -and -not $proc.HasExited) {
        $Script:GuiTestRunner.StopRequested = $true
        Write-ToolkitLog 'Stopping benchmark process...' Yellow
        try {
            $proc.Kill()
        }
        catch {
            Write-ToolkitLog "Stop failed: $($_.Exception.Message)" Red
        }
    }
    $Script:GuiTestRunner.Process = $null
}

function Test-GuiBenchmarkProcessComplete {
    $proc = $Script:GuiTestRunner.Process
    if (-not $proc) { return $true }
    return $proc.HasExited
}

function Complete-GuiBenchmarkProcess {
    param([scriptblock]$OnDone)

    $proc = $Script:GuiTestRunner.Process
    if ($proc -and -not $proc.HasExited) { return $false }

    $modelName = $Script:GuiTestRunner.ModelName
    $outputDir = $Script:GuiTestRunner.OutputDir
    $numCtx = $Script:GuiTestRunner.NumCtx
    $exitCode = if ($proc) { $proc.ExitCode } else { -1 }
    $stopped = $Script:GuiTestRunner.StopRequested

    if ($proc) {
        try { $proc.Dispose() } catch { }
    }
    $Script:GuiTestRunner.Process = $null

    $profile = $null
    $reportPath = Join-Path $outputDir 'report.json'
    if ((-not $stopped) -and (Test-Path -LiteralPath $reportPath)) {
        $local = Get-ToolkitLocalOllamaModels | Where-Object { $_.name -eq $modelName } | Select-Object -First 1
        $digest = if ($local -and $local.digest) { $local.digest } else { '' }
        $profile = Update-ModelProfileFromReport -ReportPath $reportPath -NumCtx $numCtx -ModelDigest $digest
        if ($profile -and $profile.BestMode) {
            Write-ToolkitLog ("'{0}' complete - best mode: {1} @ {2} tok/s" -f $modelName, $profile.BestMode, $profile.BestTps) Green
        }
        else {
            Write-ToolkitLog ("'{0}' complete - no successful mode (exit {1})" -f $modelName, $exitCode) Yellow
        }
    }
    elseif ($stopped) {
        Write-ToolkitLog "Benchmark for '$modelName' was cancelled." Yellow
    }
    else {
        Write-ToolkitLog ("Benchmark for '$modelName' failed (exit {0})." -f $exitCode) Red
    }

    if ((Get-SafeCollectionCount $Script:GuiTestRunner.Queue) -gt 0) {
        $next = @($Script:GuiTestRunner.Queue)[0]
        $Script:GuiTestRunner.Queue = @($Script:GuiTestRunner.Queue | Select-Object -Skip 1)
        $outDir = New-GuiTestOutputDir -ModelName $next.Model
        Start-GuiBenchmarkProcess -ModelName $next.Model -OutputDir $outDir `
            -NumCtx $next.NumCtx -NumPredict $next.NumPredict -Runs $next.Runs | Out-Null
        return $true
    }

    $Script:GuiTestRunner.RunningAll = $false
    if ($OnDone) { & $OnDone $profile }
    if ($Script:GuiTestRunner.OnComplete) { & $Script:GuiTestRunner.OnComplete }
    return $true
}

function Start-GuiBenchmarkQueue {
    param(
        [array]$Models,
        [int]$NumPredict = 32,
        [int]$Runs = 1,
        [scriptblock]$OnComplete
    )

    if ($Script:GuiTestRunner.Process -and -not $Script:GuiTestRunner.Process.HasExited) {
        throw 'A benchmark is already running.'
    }

    $Models = @($Models)
    if ((Get-SafeCollectionCount $Models) -eq 0) {
        throw 'No models selected for testing.'
    }

    $queue = @()
    foreach ($item in $Models) {
        if ($item -is [string]) {
            $summary = Get-AllModelProfileSummaries | Where-Object { $_.Model -eq $item } | Select-Object -First 1
            $ctx = if ($summary) { $summary.RecommendedCtx } else { 8192 }
            $queue += [pscustomobject]@{ Model = $item; NumCtx = $ctx; NumPredict = $NumPredict; Runs = $Runs }
        }
        else {
            $queue += $item
        }
    }

    $Script:GuiTestRunner.Queue = @($queue | Select-Object -Skip 1)
    $Script:GuiTestRunner.RunningAll = ((Get-SafeCollectionCount $queue) -gt 1)
    $Script:GuiTestRunner.OnComplete = $OnComplete

    $first = $queue[0]
    $outDir = New-GuiTestOutputDir -ModelName $first.Model
    Start-GuiBenchmarkProcess -ModelName $first.Model -OutputDir $outDir `
        -NumCtx $first.NumCtx -NumPredict $first.NumPredict -Runs $first.Runs | Out-Null
}

function Read-GuiBenchmarkLogTail {
    param(
        [ref]$LastPosition
    )

    $logPath = $Script:GuiTestRunner.LogPath
    if (-not $logPath -or -not (Test-Path -LiteralPath $logPath)) { return @() }

    $stream = [System.IO.File]::Open($logPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $stream.Position = $LastPosition.Value
        $reader = New-Object System.IO.StreamReader($stream)
        $lines = @()
        while (-not $reader.EndOfStream) {
            $line = $reader.ReadLine()
            if ($null -ne $line) { $lines += $line }
        }
        $LastPosition.Value = $stream.Position
        return @($lines)
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Launch-OllamaModelInBestMode {
    param(
        [string]$ModelName,
        [switch]$SkipConfirmation
    )

    $summary = Get-AllModelProfileSummaries | Where-Object { $_.Model -eq $ModelName } | Select-Object -First 1
    if (-not $summary) {
        throw "Model '$ModelName' not found locally."
    }

    if ($summary.NeedsRetest -or -not $summary.BestMode) {
        throw "Model '$ModelName' has no benchmark profile. Run a test first."
    }

    $mode = $summary.BestMode
    if (-not $SkipConfirmation) {
        $msg = "Launch '$ModelName' using best mode $($summary.BestMode) ($([math]::Round($summary.BestTps,2)) tok/s)?`n`nOllama will restart with the correct environment."
        $answer = [System.Windows.Forms.MessageBox]::Show(
            $msg,
            'Launch Model',
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Question
        )
        if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return $false }
    }

    Write-ToolkitLog "Applying best mode '$mode' for '$ModelName'..." Cyan
    Apply-OllamaToolkitMode -TargetMode $mode -TargetScope User -RestartOllama -SuppressRestartGuidance | Out-Null

    $ollamaCli = Get-OllamaCliPath
    Write-ToolkitLog "Launching: ollama run $ModelName" Green
    Start-Process -FilePath 'cmd.exe' -ArgumentList @('/k', "`"$ollamaCli`" run `"$ModelName`"") | Out-Null
    return $true
}

function Format-ModelResultsGridRow {
    param($Summary)

    $store = Get-ModelProfileStore
    $cpu = '-'; $apu = '-'; $gpu = '-'; $hybrid = '-'
    if ($store.Models -and $store.Models.Contains($Summary.Model)) {
        $entry = $store.Models[$Summary.Model]
        if ($entry.Results) {
            foreach ($mode in @('CPU', 'APU', 'GPU', 'Hybrid')) {
                $r = $null
                if ($entry.Results -is [System.Collections.IDictionary]) {
                    if ($entry.Results.Contains($mode)) { $r = $entry.Results[$mode] }
                }
                else {
                    $rp = $entry.Results.PSObject.Properties | Where-Object { $_.Name -eq $mode } | Select-Object -First 1
                    if ($rp) { $r = $rp.Value }
                }
                if ($r -and $r.Status -eq 'Success') {
                    $val = [string][math]::Round([double]$r.Generation_tps, 2)
                    switch ($mode) {
                        'CPU' { $cpu = $val }
                        'APU' { $apu = $val }
                        'GPU' { $gpu = $val }
                        'Hybrid' { $hybrid = $val }
                    }
                }
                elseif ($r -and $r.Status -eq 'Failed') {
                    switch ($mode) {
                        'CPU' { $cpu = 'FAIL' }
                        'APU' { $apu = 'FAIL' }
                        'GPU' { $gpu = 'FAIL' }
                        'Hybrid' { $hybrid = 'FAIL' }
                    }
                }
            }
        }
    }

    return [pscustomobject]@{
        Model          = $Summary.Model
        SizeGB         = $Summary.SizeGB
        ParameterSize  = if ($Summary.ParameterSize) { $Summary.ParameterSize } else { '-' }
        Status         = $Summary.Status
        BestMode   = if ($Summary.BestMode) { $Summary.BestMode } else { '-' }
        BestTps    = if ($Summary.BestTps) { [math]::Round($Summary.BestTps, 2) } else { 0 }
        CPU        = $cpu
        APU        = $apu
        GPU        = $gpu
        Hybrid     = $hybrid
        LastTested = if ($Summary.LastTested) { $Summary.LastTested } else { '-' }
        NumCtx     = $Summary.RecommendedCtx
    }
}