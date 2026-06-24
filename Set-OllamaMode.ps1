#Requires -Version 5.1
<#
.SYNOPSIS
    Set Ollama compute mode for everyday Llama/Ollama use and restart Ollama.

.DESCRIPTION
    One-command workflow: applies persistent (User) environment variables for the
    chosen CPU / APU / GPU / Hybrid mode, stops Ollama, restarts it, and waits
    for the REST API so you can run models immediately.

.PARAMETER Mode
    Compute mode: CPU, APU, GPU, or Hybrid.

.PARAMETER NoRestart
    Apply mode variables only; do not stop/restart Ollama.

.PARAMETER ShowStatus
    Show current mode and exit without making changes.

.EXAMPLE
    .\Set-OllamaMode.ps1 GPU

.EXAMPLE
    .\Set-OllamaMode.ps1 -Mode CPU

.EXAMPLE
    .\Set-OllamaMode.ps1 -ShowStatus
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('CPU', 'APU', 'GPU', 'Hybrid')]
    [string]$Mode,

    [switch]$NoRestart,

    [switch]$ShowStatus
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$manager = Join-Path $PSScriptRoot 'Ollama-AMD-Vulkan-Manager.ps1'
if (-not (Test-Path -LiteralPath $manager)) {
    throw "Manager script not found: $manager"
}

if ($ShowStatus) {
    & $manager -ShowStatus
    exit $LASTEXITCODE
}

if (-not $Mode) {
    Write-Host ''
    Write-Host 'Set Ollama compute mode for everyday use' -ForegroundColor Cyan
    Write-Host ''
    Write-Host '  CPU    - CPU inference only'
    Write-Host '  APU    - Radeon 680M iGPU (Vulkan)'
    Write-Host '  GPU    - RX 6700S discrete GPU (Vulkan) [recommended for most models]'
    Write-Host '  Hybrid - Both GPUs (Vulkan)'
    Write-Host ''
    $Mode = Read-Host 'Enter mode (CPU/APU/GPU/Hybrid)'
    if ($Mode -notmatch '^(CPU|APU|GPU|Hybrid)$') {
        Write-Host 'Invalid mode.' -ForegroundColor Red
        exit 1
    }
}

$restartArgs = @{}
if (-not $NoRestart) {
    $restartArgs['RestartOllama'] = $true
}

& $manager -Mode $Mode -Force @restartArgs
exit $LASTEXITCODE