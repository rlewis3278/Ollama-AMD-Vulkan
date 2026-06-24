#Requires -Version 5.1
<#
.SYNOPSIS
    Manage Ollama compute modes (CPU / APU / dGPU / Hybrid) via Vulkan on AMD Windows systems.

.DESCRIPTION
    Sets persistent (User) or session-only environment variables for Ollama Vulkan backend.
    Supports interactive menu and CLI automation.

.PARAMETER Mode
    Target compute mode: CPU, APU, GPU, or Hybrid.

.PARAMETER Scope
    User (persistent) or Process (current session only). Default: User.

.PARAMETER Force
    Skip confirmation prompts.

.PARAMETER ShowStatus
    Display current mode and environment variables, then exit.

.PARAMETER ListDevices
    Run Vulkan device discovery helper.

.PARAMETER RestoreBackup
    Restore environment variables from the most recent backup.

.PARAMETER RunBenchmark
    Launch the benchmark script after showing current status.

.PARAMETER RestartOllama
    Stop and restart Ollama after applying a mode so changes take effect immediately.

.PARAMETER ApiTimeoutSec
    Seconds to wait for the Ollama REST API when using -RestartOllama. Default: 90.

.EXAMPLE
    .\Ollama-AMD-Vulkan-Manager.ps1

.EXAMPLE
    .\Ollama-AMD-Vulkan-Manager.ps1 -Mode Hybrid -Force

.EXAMPLE
    .\Ollama-AMD-Vulkan-Manager.ps1 -Mode GPU -Force -RestartOllama

.EXAMPLE
    .\Ollama-AMD-Vulkan-Manager.ps1 -Mode GPU -Scope Process
#>
[CmdletBinding(DefaultParameterSetName = 'Interactive')]
param(
    [Parameter(ParameterSetName = 'SetMode')]
    [ValidateSet('CPU', 'APU', 'GPU', 'Hybrid')]
    [string]$Mode,

    [Parameter(ParameterSetName = 'SetMode')]
    [ValidateSet('User', 'Process')]
    [string]$Scope = 'User',

    [Parameter(ParameterSetName = 'SetMode')]
    [switch]$Force,

    [Parameter(ParameterSetName = 'Status')]
    [switch]$ShowStatus,

    [Parameter(ParameterSetName = 'Devices')]
    [switch]$ListDevices,

    [Parameter(ParameterSetName = 'Restore')]
    [switch]$RestoreBackup,

    [Parameter(ParameterSetName = 'Benchmark')]
    [switch]$RunBenchmark,

    [Parameter(ParameterSetName = 'SetMode')]
    [switch]$RestartOllama,

    [Parameter(ParameterSetName = 'SetMode')]
    [int]$ApiTimeoutSec = 90
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Script:ToolkitRoot = $PSScriptRoot
. (Join-Path $PSScriptRoot 'Ollama-Toolkit.Core.ps1')

function Write-ColorLine {
    param(
        [string]$Text,
        [ConsoleColor]$ForegroundColor = [ConsoleColor]::Gray
    )
    Write-ToolkitLog -Text $Text -ForegroundColor $ForegroundColor
}

function Set-OllamaMode {
    param(
        [ValidateSet('CPU', 'APU', 'GPU', 'Hybrid')]
        [string]$TargetMode,

        [ValidateSet('User', 'Process')]
        [string]$TargetScope,

        [switch]$SkipConfirmation,

        [switch]$RestartOllama,

        [switch]$SuppressRestartGuidance
    )

    $definition = $Script:ModeDefinitions[$TargetMode]
    $currentMode = Get-DetectedMode

    Write-ColorLine ''
    Write-ColorLine ("Target mode : {0}" -f $definition.Label) Cyan
    Write-ColorLine ("Description : {0}" -f $definition.Description) White
    Write-ColorLine ("Scope       : {0}" -f $TargetScope) White
    Write-ColorLine ("Current     : {0}" -f $currentMode) DarkGray
    Write-Host ''

    Write-ColorLine 'Variables to apply:' Yellow
    foreach ($entry in $definition.Variables.GetEnumerator()) {
        Write-ColorLine ("  {0}={1}" -f $entry.Key, $entry.Value) White
    }
    Write-Host ''

    if (-not $SkipConfirmation) {
        $answer = Read-Host 'Apply these settings? [Y/N]'
        if ($answer -notmatch '^(y|yes)$') {
            Write-ColorLine 'Cancelled. No changes made.' Yellow
            return
        }
    }

    Apply-OllamaToolkitMode -TargetMode $TargetMode -TargetScope $TargetScope `
        -RestartOllama:$RestartOllama -SuppressRestartGuidance:$SuppressRestartGuidance | Out-Null

    if ($RestartOllama) {
        Show-Status -SkipRestartGuidance
    }
}

function Invoke-InteractiveModeChange {
    param(
        [ValidateSet('CPU', 'APU', 'GPU', 'Hybrid')]
        [string]$TargetMode
    )

    Set-OllamaMode -TargetMode $TargetMode -TargetScope User -SuppressRestartGuidance

    $restartAnswer = Read-Host 'Restart Ollama now so the mode is active? [Y/n]'
    if ($restartAnswer -match '^(|y|yes)$') {
        Restart-Ollama -ModeLabel $Script:ModeDefinitions[$TargetMode].Label
        Show-Status -SkipRestartGuidance
    }
    else {
        Show-OllamaRestartGuidance
    }
}

function Invoke-BenchmarkScript {
    $benchmark = Join-Path $PSScriptRoot 'Test-OllamaBenchmark.ps1'
    if (-not (Test-Path -LiteralPath $benchmark)) {
        Write-ColorLine "Benchmark script not found: $benchmark" Red
        return
    }
    & $benchmark
}

function Show-InteractiveMenu {
    while ($true) {
        Write-ColorLine ''
        Write-ColorLine '=== Ollama AMD Vulkan Manager ===' Cyan
        Write-ColorLine ("Current persistent mode: {0}" -f (Get-DetectedMode)) Yellow
        if ($Script:DeviceMap) {
            Write-ColorLine ("Vulkan: APU index $($Script:DeviceMap.ApuVulkanIndex) (680M), GPU index $($Script:DeviceMap.GpuVulkanIndex) (6700S)") DarkGray
            Write-ColorLine 'Task Manager: GPU 0 = 6700S, GPU 1 = 680M' DarkGray
        }
        Write-Host ''
        Write-ColorLine '  1) CPU Only' White
        Write-ColorLine '  2) APU / iGPU Only (Radeon 680M)' White
        Write-ColorLine '  3) Discrete GPU Only (RX 6700S)' White
        Write-ColorLine '  4) Hybrid (iGPU + dGPU)' White
        Write-ColorLine '  5) Show current status' White
        Write-ColorLine '  6) List Vulkan devices' White
        Write-ColorLine '  7) Restore from backup' White
        Write-ColorLine '  8) Apply mode to current session only' White
        Write-ColorLine '  9) Run benchmark tool' White
        Write-ColorLine ' 10) Run automated full test (all modes + report)' White
        Write-ColorLine ' 11) Run automated test for ALL downloaded models' White
        Write-ColorLine '  0) Exit' DarkGray
        Write-Host ''

        $choice = Read-Host 'Select an option'
        switch ($choice) {
            '1' { Invoke-InteractiveModeChange -TargetMode CPU }
            '2' { Invoke-InteractiveModeChange -TargetMode APU }
            '3' { Invoke-InteractiveModeChange -TargetMode GPU }
            '4' { Invoke-InteractiveModeChange -TargetMode Hybrid }
            '5' { Show-Status }
            '6' {
                $helper = Join-Path $PSScriptRoot 'Get-VulkanDevices.ps1'
                if (Test-Path -LiteralPath $helper) {
                    & $helper
                }
                else {
                    Write-ColorLine "Helper not found: $helper" Red
                }
            }
            '7' {
                try {
                    Restore-EnvBackup
                    Show-OllamaRestartGuidance
                }
                catch {
                    Write-ColorLine $_.Exception.Message Red
                }
            }
            '8' {
                Write-ColorLine ''
                Write-ColorLine 'Session-only mode (not persisted):' Cyan
                Write-ColorLine '  a) CPU  b) APU  c) GPU  d) Hybrid' White
                $sessionChoice = Read-Host 'Choose session mode [a/b/c/d]'
                $sessionMode = switch ($sessionChoice.ToLowerInvariant()) {
                    'a' { 'CPU' }
                    'b' { 'APU' }
                    'c' { 'GPU' }
                    'd' { 'Hybrid' }
                    default { $null }
                }
                if ($sessionMode) {
                    Set-OllamaMode -TargetMode $sessionMode -TargetScope Process -SkipConfirmation
                }
                else {
                    Write-ColorLine 'Invalid session mode selection.' Red
                }
            }
            '9' { Invoke-BenchmarkScript }
            '10' {
                $auto = Join-Path $PSScriptRoot 'Invoke-OllamaAutomatedTest.ps1'
                if (Test-Path -LiteralPath $auto) {
                    & $auto
                }
                else {
                    Write-ColorLine "Automated test script not found: $auto" Red
                }
            }
            '11' {
                $autoAll = Join-Path $PSScriptRoot 'Invoke-OllamaAutomatedTestAll.ps1'
                if (Test-Path -LiteralPath $autoAll) {
                    & $autoAll
                }
                else {
                    Write-ColorLine "All-models test script not found: $autoAll" Red
                }
            }
            '0' { return }
            default { Write-ColorLine 'Invalid selection.' Red }
        }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        $Script:ApiTimeoutSec = $ApiTimeoutSec
        Ensure-ConfigDirectory
        Initialize-ModeDefinitions

        if ($RestoreBackup) {
            Restore-EnvBackup
            Show-OllamaRestartGuidance
            return
        }

        if ($ShowStatus) {
            Show-Status
            return
        }

        if ($ListDevices) {
            $helper = Join-Path $PSScriptRoot 'Get-VulkanDevices.ps1'
            & $helper
            return
        }

        if ($RunBenchmark) {
            Show-Status
            Invoke-BenchmarkScript
            return
        }

        if ($PSCmdlet.ParameterSetName -eq 'SetMode') {
            Set-OllamaMode -TargetMode $Mode -TargetScope $Scope -SkipConfirmation:$Force -RestartOllama:$RestartOllama
            return
        }

        Show-InteractiveMenu
    }
    catch {
        Write-ColorLine "ERROR: $($_.Exception.Message)" Red
        exit 1
    }
}