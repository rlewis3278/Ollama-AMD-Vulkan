#Requires -Version 5.1
Set-StrictMode -Version Latest

if (-not $Script:ToolkitRoot) {
    $Script:ToolkitRoot = $PSScriptRoot
}

$Script:ConfigDir = Join-Path $env:USERPROFILE '.ollama-amd-vulkan'
$Script:BackupFile = Join-Path $Script:ConfigDir 'env-backup.json'
$Script:ManagedVars = @(
    'OLLAMA_VULKAN',
    'HIP_VISIBLE_DEVICES',
    'GGML_VK_VISIBLE_DEVICES',
    'ROCR_VISIBLE_DEVICES',
    'CUDA_VISIBLE_DEVICES',
    'OLLAMA_NUM_GPU',
    'OLLAMA_IGPU_ENABLE'
)

$Script:ModeDefinitions = @{}
$Script:DeviceMap = $null
$Script:OllamaAppPath = Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama app.exe'
$Script:OllamaHost = 'http://localhost:11434'
$Script:ApiTimeoutSec = 90
$Script:LogAction = $null
$Script:UiPumpAction = $null
$Script:ExtensionsLoaded = $false

function Get-SafeCollectionCount {
    param($Value)

    if ($null -eq $Value) { return 0 }
    if ($Value -is [System.Collections.ICollection]) { return $Value.Count }
    return @($Value).Count
}

function ConvertTo-ToolkitJson {
    param(
        $InputObject,
        [int]$Depth = 10
    )

    if (-not ('System.Web.Script.Serialization.JavaScriptSerializer' -as [type])) {
        Add-Type -AssemblyName System.Web.Extensions
    }

    $serializer = New-Object System.Web.Script.Serialization.JavaScriptSerializer
    $serializer.MaxJsonLength = 67108864
    $serializer.RecursionLimit = $Depth
    return $serializer.Serialize($InputObject)
}

function Invoke-ToolkitRestJson {
    param(
        [string]$Method = 'Post',
        [string]$Uri,
        $Body,
        [int]$TimeoutSec = 120
    )

    $json = ConvertTo-ToolkitJson -InputObject $Body
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    return Invoke-RestMethod -Method $Method -Uri $Uri `
        -Body $bytes -ContentType 'application/json; charset=utf-8' -TimeoutSec $TimeoutSec
}

function Invoke-ToolkitUiPump {
    if ($null -ne $Script:UiPumpAction) {
        & $Script:UiPumpAction
    }
}

function Write-ToolkitLog {
    param(
        [string]$Text,
        [ConsoleColor]$ForegroundColor = [ConsoleColor]::Gray
    )

    if ($null -ne $Script:LogAction) {
        & $Script:LogAction $Text $ForegroundColor
        return
    }

    $previous = $Host.UI.RawUI.ForegroundColor
    $Host.UI.RawUI.ForegroundColor = $ForegroundColor
    Write-Host $Text
    $Host.UI.RawUI.ForegroundColor = $previous
}

function Initialize-ModeDefinitions {
    $helperPath = Join-Path $Script:ToolkitRoot 'Get-VulkanDevices.ps1'
    if (-not (Test-Path -LiteralPath $helperPath)) {
        throw "Vulkan helper not found: $helperPath"
    }

    . $helperPath
    $vulkanInfo = Resolve-VulkanInfoPath -ExplicitPath ''
    $report = Get-VulkanDeviceReport -VulkanInfoExe $vulkanInfo
    $Script:DeviceMap = $report.DeviceMap
    $map = $Script:DeviceMap

    $Script:ModeDefinitions = @{
        CPU = @{
            Label       = 'CPU Only'
            ShortLabel  = 'CPU'
            Description = 'Disable Vulkan and HIP; force CPU inference.'
            Variables   = @{
                OLLAMA_VULKAN           = '0'
                HIP_VISIBLE_DEVICES     = '-1'
                GGML_VK_VISIBLE_DEVICES = '-1'
                ROCR_VISIBLE_DEVICES    = '-1'
                OLLAMA_IGPU_ENABLE      = '0'
            }
        }
        APU = @{
            Label       = "APU / iGPU Only ($($map.ApuName))"
            ShortLabel  = 'APU (680M)'
            Description = "Vulkan on integrated GPU Vulkan index $($map.ApuVulkanIndex) only. Requires system Vulkan loader + OLLAMA_IGPU_ENABLE on Windows."
            Requires680MWorkaround = $true
            Variables   = @{
                OLLAMA_VULKAN           = '1'
                HIP_VISIBLE_DEVICES     = '-1'
                GGML_VK_VISIBLE_DEVICES = $map.ApuVulkanIndex
                ROCR_VISIBLE_DEVICES    = '-1'
                OLLAMA_NUM_GPU          = '999'
                OLLAMA_IGPU_ENABLE      = '1'
            }
        }
        GPU = @{
            Label       = "Discrete GPU Only ($($map.GpuName))"
            ShortLabel  = 'GPU (6700S)'
            Description = "Vulkan on discrete GPU Vulkan index $($map.GpuVulkanIndex) only."
            Variables   = @{
                OLLAMA_VULKAN           = '1'
                HIP_VISIBLE_DEVICES     = '-1'
                GGML_VK_VISIBLE_DEVICES = $map.GpuVulkanIndex
                ROCR_VISIBLE_DEVICES    = '-1'
                OLLAMA_IGPU_ENABLE      = '0'
            }
        }
        Hybrid = @{
            Label       = 'Hybrid (iGPU + dGPU)'
            ShortLabel  = 'Hybrid'
            Description = "Vulkan on both GPUs (Vulkan indices $($map.HybridVulkanValue)) for split scheduling."
            Variables   = @{
                OLLAMA_VULKAN           = '1'
                HIP_VISIBLE_DEVICES     = '-1'
                GGML_VK_VISIBLE_DEVICES = $map.HybridVulkanValue
                ROCR_VISIBLE_DEVICES    = '-1'
                OLLAMA_IGPU_ENABLE      = '1'
            }
        }
    }
}

function Ensure-ConfigDirectory {
    if (-not (Test-Path -LiteralPath $Script:ConfigDir)) {
        New-Item -ItemType Directory -Path $Script:ConfigDir -Force | Out-Null
    }
}

function Initialize-ToolkitExtensions {
    if ($Script:ExtensionsLoaded) { return }
    $Script:ExtensionsLoaded = $true
}

function Get-680MVulkanWorkaroundStatus {
    Initialize-ToolkitExtensions
    if (Get-Command Get-OllamaVulkanWorkaroundStatus -ErrorAction SilentlyContinue) {
        return Get-OllamaVulkanWorkaroundStatus
    }

    return [pscustomobject]@{
        Active = $false
        BundledPresent = $true
        BackupPresent = $false
        NeedsReapply = $false
    }
}

function Ensure-680MVulkanWorkaround {
    param([switch]$SkipConfirmation)

    Initialize-ToolkitExtensions
    if (-not (Get-Command Enable-OllamaVulkanSystemLoader -ErrorAction SilentlyContinue)) {
        throw 'Vulkan workaround helper not found.'
    }

    $status = Get-OllamaVulkanWorkaroundStatus
    if ($status.Active -and -not $status.NeedsReapply) {
        Write-ToolkitLog '680M Vulkan workaround already active.' DarkGray
        return $status
    }

    Write-ToolkitLog 'Applying 680M Vulkan workaround (system loader)...' Cyan
    return Enable-OllamaVulkanSystemLoader -SkipConfirmation:$SkipConfirmation
}

function Get-EnvSnapshot {
    param(
        [ValidateSet('User', 'Process')]
        [string]$TargetScope = 'User'
    )

    $snapshot = [ordered]@{}
    foreach ($name in $Script:ManagedVars) {
        if ($TargetScope -eq 'User') {
            $snapshot[$name] = [Environment]::GetEnvironmentVariable($name, 'User')
        }
        else {
            $snapshot[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        }
    }
    return $snapshot
}

function Save-EnvBackup {
    param([hashtable]$Snapshot)

    Ensure-ConfigDirectory
    $payload = [ordered]@{
        Timestamp = (Get-Date).ToString('o')
        Variables = $Snapshot
    }
    $payload | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $Script:BackupFile -Encoding UTF8
    Write-ToolkitLog "Backup saved: $Script:BackupFile" DarkGray
}

function Restore-EnvBackup {
    if (-not (Test-Path -LiteralPath $Script:BackupFile)) {
        throw "No backup file found at $Script:BackupFile"
    }

    $backup = Get-Content -LiteralPath $Script:BackupFile -Raw | ConvertFrom-Json
    foreach ($property in $backup.Variables.PSObject.Properties) {
        $name = $property.Name
        $value = $property.Value
        if ([string]::IsNullOrWhiteSpace($value)) {
            [Environment]::SetEnvironmentVariable($name, $null, 'User')
            [Environment]::SetEnvironmentVariable($name, $null, 'Process')
        }
        else {
            [Environment]::SetEnvironmentVariable($name, $value, 'User')
            [Environment]::SetEnvironmentVariable($name, $value, 'Process')
        }
    }

    Write-ToolkitLog "Restored environment variables from backup ($($backup.Timestamp))." Green
}

function Test-EnvValueMatchesMode {
    param(
        [string]$Name,
        [string]$Expected,
        [string]$Current
    )

    if ($Current -eq $Expected) { return $true }

    # Older toolkit versions cleared OLLAMA_IGPU_ENABLE for CPU/GPU instead of setting it to 0.
    if ($Name -eq 'OLLAMA_IGPU_ENABLE' -and $Expected -eq '0' -and [string]::IsNullOrWhiteSpace($Current)) {
        return $true
    }

    return $false
}

function Get-DetectedMode {
    $userSnapshot = Get-EnvSnapshot -TargetScope 'User'

    foreach ($modeName in @('CPU', 'APU', 'GPU', 'Hybrid')) {
        $definition = $Script:ModeDefinitions[$modeName]
        $matchesMode = $true
        foreach ($entry in $definition.Variables.GetEnumerator()) {
            $current = $userSnapshot[$entry.Key]
            if (-not (Test-EnvValueMatchesMode -Name $entry.Key -Expected $entry.Value -Current $current)) {
                $matchesMode = $false
                break
            }
        }
        if ($matchesMode) {
            return $modeName
        }
    }

    return 'Custom/Unknown'
}

function Get-OllamaProcesses {
    $names = @('ollama', 'ollama app')
    return @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $names -contains $_.ProcessName })
}

function Stop-OllamaProcesses {
    param(
        [int]$TimeoutSec = 45,
        [switch]$Quick
    )

    if ($Quick) { $TimeoutSec = [math]::Min($TimeoutSec, 5) }

    Write-ToolkitLog 'Stopping Ollama processes...' Cyan

    foreach ($name in @('ollama', 'ollama app')) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
            Write-ToolkitLog ("  Stopping PID {0} ({1})" -f $_.Id, $_.ProcessName) DarkGray
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        }
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $remaining = @(Get-Process -Name 'ollama', 'ollama app' -ErrorAction SilentlyContinue)
        if ((Get-SafeCollectionCount $remaining) -eq 0) {
            Write-ToolkitLog 'Ollama stopped.' Green
            if (-not $Quick) { Start-Sleep -Seconds 2 }
            return
        }
        Start-Sleep -Milliseconds $(if ($Quick) { 150 } else { 500 })
        if (-not $Quick) { Invoke-ToolkitUiPump }
    }

    if ($Quick) {
        Write-ToolkitLog 'Ollama stop timed out during GUI exit; continuing shutdown.' Yellow
        return
    }

    throw 'Timed out waiting for Ollama processes to exit.'
}

function Start-OllamaApplication {
    if (-not (Test-Path -LiteralPath $Script:OllamaAppPath)) {
        throw "Ollama app not found: $Script:OllamaAppPath"
    }

    Write-ToolkitLog 'Starting Ollama...' Cyan
    Start-Process -FilePath $Script:OllamaAppPath | Out-Null
}

$Script:OllamaApiReadyCache = @{
    Ready     = $false
    CheckedAt = [datetime]::MinValue
}
$Script:OllamaTagsCache = @{
    Models    = $null
    CheckedAt = [datetime]::MinValue
}

function Clear-OllamaToolkitRuntimeCaches {
    $Script:OllamaApiReadyCache.CheckedAt = [datetime]::MinValue
    $Script:OllamaTagsCache.CheckedAt = [datetime]::MinValue
}

function Test-OllamaApiReady {
    $uri = ($Script:OllamaHost.TrimEnd('/')) + '/api/tags'
    try {
        $null = Invoke-RestMethod -Method Get -Uri $uri -TimeoutSec 5
        return $true
    }
    catch {
        return $false
    }
}

function Test-OllamaApiReadyCached {
    param([int]$TtlSec = 10)

    $age = ((Get-Date) - $Script:OllamaApiReadyCache.CheckedAt).TotalSeconds
    if ($age -lt $TtlSec) {
        return [bool]$Script:OllamaApiReadyCache.Ready
    }

    $ready = Test-OllamaApiReady
    $Script:OllamaApiReadyCache.Ready = $ready
    $Script:OllamaApiReadyCache.CheckedAt = Get-Date
    return $ready
}

function Get-OllamaTagsApiModels {
    param(
        [int]$TimeoutSec = 3,
        [int]$TtlSec = 15,
        [switch]$ForceRefresh
    )

    if (-not $ForceRefresh -and $null -ne $Script:OllamaTagsCache.Models) {
        $age = ((Get-Date) - $Script:OllamaTagsCache.CheckedAt).TotalSeconds
        if ($age -lt $TtlSec) {
            return @($Script:OllamaTagsCache.Models)
        }
    }

    $uri = ($Script:OllamaHost.TrimEnd('/')) + '/api/tags'
    try {
        $response = Invoke-RestMethod -Method Get -Uri $uri -TimeoutSec $TimeoutSec
        $models = if ($response.models) { @($response.models) } else { @() }
        if ((Get-SafeCollectionCount $models) -gt 0) {
            $Script:OllamaApiReadyCache.Ready = $true
            $Script:OllamaApiReadyCache.CheckedAt = Get-Date
        }
    }
    catch {
        $models = @()
    }

    $Script:OllamaTagsCache.Models = $models
    $Script:OllamaTagsCache.CheckedAt = Get-Date
    return $models
}

function Wait-OllamaApiReady {
    param(
        [switch]$AutoStart,
        [int]$TimeoutSec
    )

    if (-not $PSBoundParameters.ContainsKey('TimeoutSec')) {
        $TimeoutSec = if ($Script:ApiTimeoutSec) { $Script:ApiTimeoutSec } else { 90 }
    }

    Write-ToolkitLog ("Waiting for Ollama API (timeout {0}s)..." -f $TimeoutSec) Cyan
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $attempt = 0
    $started = $false

    while ((Get-Date) -lt $deadline) {
        $attempt++
        if (Test-OllamaApiReady) {
            Write-ToolkitLog ("Ollama API ready after {0} attempt(s)." -f $attempt) Green
            Start-Sleep -Seconds 3
            return
        }

        if ($AutoStart -and -not $started -and $attempt -ge 3) {
            $running = @(Get-Process -Name 'ollama', 'ollama app' -ErrorAction SilentlyContinue)
            if ((Get-SafeCollectionCount $running) -eq 0) {
                Write-ToolkitLog 'Ollama not running; starting application...' Yellow
                Start-OllamaApplication
                $started = $true
            }
        }

        $waitSlice = 500
        $waited = 0
        while ($waited -lt 2000) {
            Start-Sleep -Milliseconds $waitSlice
            $waited += $waitSlice
            Invoke-ToolkitUiPump
        }
    }

    throw "Ollama API not reachable at $($Script:OllamaHost) after ${TimeoutSec}s."
}

function Restart-Ollama {
    param([string]$ModeLabel = '')

    Stop-OllamaProcesses
    Start-OllamaApplication
    Wait-OllamaApiReady -AutoStart
    if ($ModeLabel) {
        Write-ToolkitLog ("Ollama restarted. Active mode: {0}" -f $ModeLabel) Green
    }
    else {
        Write-ToolkitLog 'Ollama restarted.' Green
    }
}

function Get-OllamaToolkitStatus {
    [CmdletBinding()]
    param(
        [Parameter(ValueFromPipeline = $false)]
        [switch]$UseCachedApi
    )

    $processes = @(Get-OllamaProcesses)
    $detected = Get-DetectedMode
    $definition = $Script:ModeDefinitions[$detected]

    $workaround = Get-680MVulkanWorkaroundStatus
    $apiReady = if ($UseCachedApi) { Test-OllamaApiReadyCached } else { Test-OllamaApiReady }

    return [pscustomobject]@{
        DetectedMode      = $detected
        ModeLabel         = if ($definition) { $definition.Label } else { $detected }
        ShortLabel        = if ($definition) { $definition.ShortLabel } else { $detected }
        DeviceMap         = $Script:DeviceMap
        UserVariables     = (Get-EnvSnapshot -TargetScope 'User')
        ProcessVariables  = (Get-EnvSnapshot -TargetScope 'Process')
        OllamaRunning     = ((Get-SafeCollectionCount $processes) -gt 0)
        OllamaProcessCount = (Get-SafeCollectionCount $processes)
        ApiReady          = $apiReady
        ModeDefinitions   = $Script:ModeDefinitions
        VulkanWorkaround  = $workaround
    }
}

function Apply-OllamaToolkitMode {
    param(
        [ValidateSet('CPU', 'APU', 'GPU', 'Hybrid')]
        [string]$TargetMode,

        [ValidateSet('User', 'Process')]
        [string]$TargetScope = 'User',

        [switch]$RestartOllama,

        [switch]$SuppressRestartGuidance,

        [switch]$Apply680MWorkaround,

        [switch]$Skip680MWorkaround
    )

    $definition = $Script:ModeDefinitions[$TargetMode]
    if (-not $definition) {
        throw "Unknown mode: $TargetMode"
    }

    $requiresWorkaround = $false
    if ($definition.ContainsKey('Requires680MWorkaround')) {
        $requiresWorkaround = [bool]$definition.Requires680MWorkaround
    }
    $shouldApplyWorkaround = $requiresWorkaround -and -not $Skip680MWorkaround

    if ($shouldApplyWorkaround) {
        Ensure-680MVulkanWorkaround -SkipConfirmation | Out-Null
    }

    if ($TargetScope -eq 'User') {
        $snapshot = Get-EnvSnapshot -TargetScope 'User'
        Save-EnvBackup -Snapshot $snapshot
    }

    foreach ($entry in $definition.Variables.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, $TargetScope)
        if ($TargetScope -eq 'User') {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
    }

    if ($TargetMode -ne 'APU') {
        if ($TargetScope -eq 'User') {
            [Environment]::SetEnvironmentVariable('OLLAMA_NUM_GPU', $null, 'User')
            [Environment]::SetEnvironmentVariable('OLLAMA_NUM_GPU', $null, 'Process')
        }
        else {
            [Environment]::SetEnvironmentVariable('OLLAMA_NUM_GPU', $null, 'Process')
        }
    }

    if ($TargetScope -eq 'User') {
        [Environment]::SetEnvironmentVariable('CUDA_VISIBLE_DEVICES', $null, 'User')
        [Environment]::SetEnvironmentVariable('CUDA_VISIBLE_DEVICES', $null, 'Process')
    }
    else {
        [Environment]::SetEnvironmentVariable('CUDA_VISIBLE_DEVICES', $null, 'Process')
    }

    Write-ToolkitLog ("Mode '{0}' applied successfully." -f $definition.Label) Green

    if ($RestartOllama) {
        if ($TargetScope -eq 'Process') {
            Write-ToolkitLog 'Note: Ollama reads User-scope variables at startup. Session-only changes require manual restart from this shell.' Yellow
        }
        Restart-Ollama -ModeLabel $definition.Label
    }
    elseif (-not $SuppressRestartGuidance) {
        Show-OllamaRestartGuidance
    }

    return [pscustomobject]@{
        Mode      = $TargetMode
        Label     = $definition.Label
        Scope     = $TargetScope
        Restarted = [bool]$RestartOllama
    }
}

function Show-OllamaRestartGuidance {
    $processes = @(Get-OllamaProcesses)
    if ((Get-SafeCollectionCount $processes) -eq 0) {
        Write-ToolkitLog 'Ollama is not currently running. Start it after applying changes.' Yellow
        return
    }

    Write-ToolkitLog '' Gray
    Write-ToolkitLog 'WARNING: Ollama is running and must be restarted for changes to take effect.' Red
    foreach ($proc in $processes) {
        Write-ToolkitLog ("  PID {0} : {1}" -f $proc.Id, $proc.ProcessName) Yellow
    }
    Write-ToolkitLog '' Gray
    Write-ToolkitLog 'Recommended restart steps:' Cyan
    Write-ToolkitLog '  1. Right-click Ollama tray icon -> Quit Ollama' Gray
    Write-ToolkitLog '  2. Start Ollama from the Start Menu' Gray
    Write-ToolkitLog '  3. Verify mode with the status panel or Set-OllamaMode.ps1 -ShowStatus' Gray
}

function Show-Status {
    param([switch]$SkipRestartGuidance)

    $detected = Get-DetectedMode
    $userSnapshot = Get-EnvSnapshot -TargetScope 'User'
    $processSnapshot = Get-EnvSnapshot -TargetScope 'Process'

    Write-ToolkitLog '' Gray
    Write-ToolkitLog '=== Ollama AMD Vulkan Manager - Status ===' Cyan
    Write-ToolkitLog ("Detected persistent mode: {0}" -f $detected) Yellow
    if ($Script:DeviceMap) {
        Write-ToolkitLog "Vulkan indices: APU=$($Script:DeviceMap.ApuVulkanIndex) (680M), GPU=$($Script:DeviceMap.GpuVulkanIndex) (6700S)" DarkGray
        Write-ToolkitLog 'Task Manager: GPU 0 = 6700S, GPU 1 = 680M (differs from Vulkan numbering)' DarkGray
    }
    Write-ToolkitLog '' Gray

    Write-ToolkitLog 'Persistent (User) variables:' Gray
    foreach ($name in $Script:ManagedVars) {
        $value = $userSnapshot[$name]
        if ([string]::IsNullOrWhiteSpace($value)) { $value = '(not set)' }
        Write-ToolkitLog ("  {0,-28} {1}" -f $name, $value) DarkGray
    }

    Write-ToolkitLog '' Gray
    Write-ToolkitLog 'Current session (Process) variables:' Gray
    foreach ($name in $Script:ManagedVars) {
        $value = $processSnapshot[$name]
        if ([string]::IsNullOrWhiteSpace($value)) { $value = '(not set)' }
        Write-ToolkitLog ("  {0,-28} {1}" -f $name, $value) DarkGray
    }

    if (-not $SkipRestartGuidance) {
        Show-OllamaRestartGuidance
    }
}

# Load extensions at file scope so functions remain visible to the host script.
if ($Script:ToolkitRoot) {
    $extensionPath = Join-Path $Script:ToolkitRoot 'Ollama-VulkanWorkaround.ps1'
    if ((Test-Path -LiteralPath $extensionPath) -and -not $Script:ExtensionsLoaded) {
        . $extensionPath
        $Script:ExtensionsLoaded = $true
    }
}