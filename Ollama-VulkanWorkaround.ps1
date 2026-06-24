#Requires -Version 5.1
<#
.SYNOPSIS
    Enable/disable the Windows Vulkan system-loader workaround for AMD iGPU (680M) support.

.DESCRIPTION
    Ollama ships a bundled vulkan-1.dll that often fails to enumerate AMD GPUs on Windows,
    especially integrated RDNA2 parts like the Radeon 680M. Renaming the bundled loader
    forces Ollama to use C:\Windows\System32\vulkan-1.dll, which vulkaninfo already uses.

    Reference: https://github.com/ollama/ollama/issues/16677

.PARAMETER Enable
    Apply the workaround (rename bundled loader to .bak).

.PARAMETER Disable
    Restore the bundled Ollama Vulkan loader.

.PARAMETER Status
    Show whether the workaround is active.

.PARAMETER Force
    Skip confirmation prompts.

.EXAMPLE
    .\Ollama-VulkanWorkaround.ps1 -Enable

.EXAMPLE
    .\Ollama-VulkanWorkaround.ps1 -Status
#>
[CmdletBinding(DefaultParameterSetName = 'Status')]
param(
    [Parameter(ParameterSetName = 'Enable')]
    [switch]$Enable,

    [Parameter(ParameterSetName = 'Disable')]
    [switch]$Disable,

    [Parameter(ParameterSetName = 'Status')]
    [switch]$Status,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Script:OllamaVulkanDir = Join-Path $env:LOCALAPPDATA 'Programs\Ollama\lib\ollama\vulkan'
$Script:BundledLoader = Join-Path $Script:OllamaVulkanDir 'vulkan-1.dll'
$Script:BundledBackup = Join-Path $Script:OllamaVulkanDir 'vulkan-1.dll.bak'
$Script:SystemLoader = Join-Path $env:SystemRoot 'System32\vulkan-1.dll'
$Script:ToolkitConfigDir = Join-Path $env:USERPROFILE '.ollama-amd-vulkan'
$Script:WorkaroundStateFile = Join-Path $Script:ToolkitConfigDir 'vulkan-workaround.json'

function Write-WorkaroundLog {
    param(
        [string]$Message,
        [ConsoleColor]$Color = [ConsoleColor]::Gray
    )
    $previous = $Host.UI.RawUI.ForegroundColor
    $Host.UI.RawUI.ForegroundColor = $Color
    Write-Host $Message
    $Host.UI.RawUI.ForegroundColor = $previous
}

function Get-OllamaVulkanWorkaroundStatus {
    $bundledPresent = Test-Path -LiteralPath $Script:BundledLoader
    $backupPresent = Test-Path -LiteralPath $Script:BundledBackup
    $systemPresent = Test-Path -LiteralPath $Script:SystemLoader

    $active = (-not $bundledPresent) -and $backupPresent

    $bundledVersion = $null
    $systemVersion = $null
    if ($bundledPresent) {
        $bundledVersion = (Get-Item -LiteralPath $Script:BundledLoader).VersionInfo.FileVersion
    }
    elseif ($backupPresent) {
        $bundledVersion = (Get-Item -LiteralPath $Script:BundledBackup).VersionInfo.FileVersion
    }
    if ($systemPresent) {
        $systemVersion = (Get-Item -LiteralPath $Script:SystemLoader).VersionInfo.FileVersion
    }

    $state = $null
    if (Test-Path -LiteralPath $Script:WorkaroundStateFile) {
        $state = Get-Content -LiteralPath $Script:WorkaroundStateFile -Raw | ConvertFrom-Json
    }

    return [pscustomobject]@{
        Active            = $active
        BundledPresent    = $bundledPresent
        BackupPresent     = $backupPresent
        SystemLoaderPresent = $systemPresent
        BundledVersion    = $bundledVersion
        SystemVersion     = $systemVersion
        BundledPath       = $Script:BundledLoader
        BackupPath        = $Script:BundledBackup
        SystemPath        = $Script:SystemLoader
        NeedsReapply      = $bundledPresent -and $state -and $state.Active
        LastApplied       = if ($state) { $state.Timestamp } else { $null }
    }
}

function Save-WorkaroundState {
    param([bool]$Active)

    if (-not (Test-Path -LiteralPath $Script:ToolkitConfigDir)) {
        New-Item -ItemType Directory -Path $Script:ToolkitConfigDir -Force | Out-Null
    }

    $payload = [ordered]@{
        Active    = $Active
        Timestamp = (Get-Date).ToString('o')
        BundledPath = $Script:BundledLoader
        BackupPath  = $Script:BundledBackup
        SystemPath  = $Script:SystemLoader
    }
    $payload | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $Script:WorkaroundStateFile -Encoding UTF8
}

function Enable-OllamaVulkanSystemLoader {
    param([switch]$SkipConfirmation)

    $status = Get-OllamaVulkanWorkaroundStatus

    if ($status.Active) {
        Write-WorkaroundLog '680M Vulkan workaround is already active (bundled loader disabled).' Green
        return $status
    }

    if (-not (Test-Path -LiteralPath $Script:OllamaVulkanDir)) {
        throw "Ollama Vulkan folder not found: $Script:OllamaVulkanDir"
    }
    if (-not (Test-Path -LiteralPath $Script:BundledLoader)) {
        throw "Bundled vulkan-1.dll not found at: $Script:BundledLoader"
    }
    if (-not (Test-Path -LiteralPath $Script:SystemLoader)) {
        throw "System vulkan-1.dll not found at: $Script:SystemLoader"
    }

    Write-WorkaroundLog ''
    Write-WorkaroundLog '=== Enable 680M / AMD Vulkan System Loader Workaround ===' Cyan
    Write-WorkaroundLog "Bundled loader : $($status.BundledPath)" Gray
    Write-WorkaroundLog "Bundled version: $($status.BundledVersion)" Gray
    Write-WorkaroundLog "System loader  : $($status.SystemPath)" Gray
    Write-WorkaroundLog "System version : $($status.SystemVersion)" Gray
    Write-WorkaroundLog ''
    Write-WorkaroundLog 'Ollama will be pointed at the Windows system Vulkan loader instead of its bundled DLL.' Yellow
    Write-WorkaroundLog 'Note: Ollama updates may restore the bundled DLL - re-run -Enable after updates.' Yellow
    Write-WorkaroundLog ''

    if (-not $SkipConfirmation) {
        $answer = Read-Host 'Apply workaround? [Y/N]'
        if ($answer -notmatch '^(y|yes)$') {
            Write-WorkaroundLog 'Cancelled.' Yellow
            return $status
        }
    }

    if (Test-Path -LiteralPath $Script:BundledBackup) {
        Remove-Item -LiteralPath $Script:BundledBackup -Force
    }

    Rename-Item -LiteralPath $Script:BundledLoader -NewName 'vulkan-1.dll.bak'
    Save-WorkaroundState -Active $true

    $updated = Get-OllamaVulkanWorkaroundStatus
    Write-WorkaroundLog 'Workaround applied. Restart Ollama, then use APU mode.' Green
    return $updated
}

function Disable-OllamaVulkanSystemLoader {
    param([switch]$SkipConfirmation)

    $status = Get-OllamaVulkanWorkaroundStatus

    if ($status.BundledPresent) {
        Write-WorkaroundLog 'Bundled loader is already in place (workaround not active).' Yellow
        Save-WorkaroundState -Active $false
        return $status
    }

    if (-not (Test-Path -LiteralPath $Script:BundledBackup)) {
        throw "Backup not found: $Script:BundledBackup"
    }

    Write-WorkaroundLog ''
    Write-WorkaroundLog '=== Restore Ollama Bundled Vulkan Loader ===' Cyan
    Write-WorkaroundLog "Restore from: $($status.BackupPath)" Gray
    Write-WorkaroundLog ''

    if (-not $SkipConfirmation) {
        $answer = Read-Host 'Restore bundled loader? [Y/N]'
        if ($answer -notmatch '^(y|yes)$') {
            Write-WorkaroundLog 'Cancelled.' Yellow
            return $status
        }
    }

    Rename-Item -LiteralPath $Script:BundledBackup -NewName 'vulkan-1.dll'
    Save-WorkaroundState -Active $false

    $updated = Get-OllamaVulkanWorkaroundStatus
    Write-WorkaroundLog 'Bundled Vulkan loader restored.' Green
    return $updated
}

function Show-OllamaVulkanWorkaroundStatus {
    $status = Get-OllamaVulkanWorkaroundStatus

    Write-WorkaroundLog ''
    Write-WorkaroundLog '=== 680M / AMD Vulkan Workaround Status ===' Cyan
    if ($status.Active) {
        Write-WorkaroundLog 'Status: ACTIVE (using system Vulkan loader)' Green
    }
    else {
        Write-WorkaroundLog 'Status: NOT ACTIVE (using Ollama bundled loader)' Yellow
    }

    if ($status.NeedsReapply) {
        Write-WorkaroundLog 'WARNING: Ollama update likely restored bundled loader. Re-run -Enable.' Red
    }

    Write-WorkaroundLog "Bundled DLL present : $($status.BundledPresent)" Gray
    Write-WorkaroundLog "Backup (.bak) present: $($status.BackupPresent)" Gray
    Write-WorkaroundLog "System loader present: $($status.SystemLoaderPresent)" Gray
    Write-WorkaroundLog "Bundled version      : $($status.BundledVersion)" Gray
    Write-WorkaroundLog "System version       : $($status.SystemVersion)" Gray
    if ($status.LastApplied) {
        Write-WorkaroundLog "Last applied         : $($status.LastApplied)" DarkGray
    }
    Write-WorkaroundLog ''
    Write-WorkaroundLog 'After enabling, set APU mode and restart Ollama:' Gray
    Write-WorkaroundLog '  .\Set-OllamaMode.ps1 APU' Gray
    Write-WorkaroundLog 'APU mode also sets OLLAMA_IGPU_ENABLE=1 (required for 680M).' DarkGray
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        if ($Enable) {
            Enable-OllamaVulkanSystemLoader -SkipConfirmation:$Force | Out-Null
        }
        elseif ($Disable) {
            Disable-OllamaVulkanSystemLoader -SkipConfirmation:$Force | Out-Null
        }
        else {
            Show-OllamaVulkanWorkaroundStatus
        }
    }
    catch {
        Write-WorkaroundLog "ERROR: $($_.Exception.Message)" Red
        exit 1
    }
}