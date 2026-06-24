#Requires -Version 5.1
<#
.SYNOPSIS
    Lists Vulkan physical devices and helps identify AMD iGPU vs dGPU indices.

.DESCRIPTION
    Parses vulkaninfo output to enumerate GPUs with index, name, type, and driver info.
    Designed for AMD Ryzen APU + discrete Radeon laptop configurations on Windows 11.

    IMPORTANT: Vulkan/Ollama indices (GPU0, GPU1 from vulkaninfo) are what
    GGML_VK_VISIBLE_DEVICES uses. Windows Task Manager may number GPUs differently
    (e.g. Task Manager GPU 0 = RX 6700S, GPU 1 = 680M while Vulkan GPU0 = 680M).

.PARAMETER Json
    Output results as JSON instead of formatted text.

.PARAMETER VulkanInfoPath
    Path to vulkaninfo.exe. Auto-detected from PATH if omitted.

.EXAMPLE
    .\Get-VulkanDevices.ps1

.EXAMPLE
    .\Get-VulkanDevices.ps1 -Json
#>
[CmdletBinding()]
param(
    [switch]$Json,
    [string]$VulkanInfoPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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

function Resolve-VulkanInfoPath {
    param([string]$ExplicitPath)

    if ($ExplicitPath -and (Test-Path -LiteralPath $ExplicitPath)) {
        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    $command = Get-Command vulkaninfo -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $commonPaths = @(
        "$env:SystemRoot\System32\vulkaninfo.exe",
        "$env:SystemRoot\SysWOW64\vulkaninfo.exe"
    )
    foreach ($path in $commonPaths) {
        if (Test-Path -LiteralPath $path) {
            return $path
        }
    }

    throw "vulkaninfo.exe not found. Install the Vulkan SDK or ensure AMD drivers are installed."
}

function Get-VulkanDeviceTypeLabel {
    param([string]$DeviceType)

    switch -Regex ($DeviceType) {
        'INTEGRATED_GPU' { return 'Integrated (iGPU / APU)' }
        'DISCRETE_GPU'   { return 'Discrete (dGPU)' }
        'CPU'            { return 'CPU' }
        'VIRTUAL_GPU'    { return 'Virtual' }
        default          { return $DeviceType }
    }
}

function Get-VulkanDevicesFromVulkanInfo {
    param([string]$VulkanInfoExe)

    $previousEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        # vulkaninfo emits benign loader warnings to stderr on some AMD systems.
        $rawOutput = @(cmd /c "`"$VulkanInfoExe`" --summary 2>nul")
    }
    finally {
        $ErrorActionPreference = $previousEap
    }

    if (-not $rawOutput -or $rawOutput.Count -eq 0) {
        throw "vulkaninfo returned no output."
    }

    $devices = New-Object System.Collections.Generic.List[object]
    $current = $null

    foreach ($line in $rawOutput) {
        if ($line -match '^GPU(\d+):') {
            if ($null -ne $current) {
                $devices.Add($current)
            }
            $current = [ordered]@{
                Index      = [int]$Matches[1]
                Name       = $null
                DeviceType = $null
                VendorId   = $null
                DeviceId   = $null
                DriverName = $null
                DriverInfo = $null
                ApiVersion = $null
            }
            continue
        }

        if ($null -eq $current) { continue }

        if ($line -match '^\s*deviceName\s*=\s*(.+)$') {
            $current.Name = $Matches[1].Trim()
        }
        elseif ($line -match '^\s*deviceType\s*=\s*(.+)$') {
            $current.DeviceType = $Matches[1].Trim()
        }
        elseif ($line -match '^\s*vendorID\s*=\s*(.+)$') {
            $current.VendorId = $Matches[1].Trim()
        }
        elseif ($line -match '^\s*deviceID\s*=\s*(.+)$') {
            $current.DeviceId = $Matches[1].Trim()
        }
        elseif ($line -match '^\s*driverName\s*=\s*(.+)$') {
            $current.DriverName = $Matches[1].Trim()
        }
        elseif ($line -match '^\s*driverInfo\s*=\s*(.+)$') {
            $current.DriverInfo = $Matches[1].Trim()
        }
        elseif ($line -match '^\s*apiVersion\s*=\s*(.+)$') {
            $current.ApiVersion = $Matches[1].Trim()
        }
    }

    if ($null -ne $current) {
        $devices.Add($current)
    }

    if ($devices.Count -eq 0) {
        throw "No Vulkan devices parsed from vulkaninfo output."
    }

    return $devices
}

function Get-OllamaDeviceMap {
    param($Devices)

    $integrated = $Devices | Where-Object { $_.DeviceType -match 'INTEGRATED' } | Select-Object -First 1
    $discrete = $Devices | Where-Object { $_.DeviceType -match 'DISCRETE' } | Select-Object -First 1

    if (-not $integrated) {
        $integrated = $Devices | Where-Object { $_.Name -match '680m|radeon\(tm\) graphics' } | Select-Object -First 1
    }
    if (-not $discrete) {
        $discrete = $Devices | Where-Object { $_.Name -match '6700' } | Select-Object -First 1
    }

    if (-not $integrated -or -not $discrete) {
        throw 'Could not identify both integrated and discrete Vulkan devices.'
    }

    $sortedIndices = @($Devices | ForEach-Object { $_.Index } | Sort-Object) -join ','

    return [pscustomobject]@{
        ApuVulkanIndex     = [string]$integrated.Index
        GpuVulkanIndex     = [string]$discrete.Index
        HybridVulkanValue  = $sortedIndices
        ApuName            = $integrated.Name
        GpuName            = $discrete.Name
        ApuDeviceId        = $integrated.DeviceId
        GpuDeviceId        = $discrete.DeviceId
        TaskManagerNote    = 'Task Manager GPU 0 is often the RX 6700S and GPU 1 is the 680M on this laptop. Ollama uses Vulkan indices above, not Task Manager labels.'
    }
}

function Get-AmdGpuHint {
    param(
        [string]$DeviceName,
        [string]$DeviceType,
        [int]$Index,
        $DeviceMap
    )

    if ($Index -eq [int]$DeviceMap.ApuVulkanIndex) {
        return "Radeon 680M iGPU (APU) - Ollama APU mode: GGML_VK_VISIBLE_DEVICES=$($DeviceMap.ApuVulkanIndex)"
    }
    if ($Index -eq [int]$DeviceMap.GpuVulkanIndex) {
        return "RX 6700S (dGPU) - Ollama GPU mode: GGML_VK_VISIBLE_DEVICES=$($DeviceMap.GpuVulkanIndex)"
    }

    $name = $DeviceName.ToLowerInvariant()
    if ($name -match '6700s') {
        return "RX 6700S detected at Vulkan index $Index."
    }
    if ($name -match '680m|radeon\(tm\) graphics|ryzen') {
        return "680M iGPU detected at Vulkan index $Index."
    }
    if ($DeviceType -match 'INTEGRATED') {
        return "Integrated GPU at Vulkan index $Index."
    }
    if ($DeviceType -match 'DISCRETE') {
        return "Discrete GPU at Vulkan index $Index."
    }
    return $null
}

function Show-VulkanDeviceTable {
    param(
        $Devices,
        $DeviceMap
    )

    Write-ColorLine ''
    Write-ColorLine '=== Vulkan Physical Devices (Ollama uses these indices) ===' Cyan
    Write-ColorLine ''

    foreach ($device in $Devices) {
        $typeLabel = Get-VulkanDeviceTypeLabel -DeviceType $device.DeviceType
        $hint = Get-AmdGpuHint -DeviceName $device.Name -DeviceType $device.DeviceType -Index $device.Index -DeviceMap $DeviceMap

        Write-ColorLine ("Vulkan GPU{0}: {1}" -f $device.Index, $device.Name) Yellow
        Write-ColorLine ("    Type   : {0}" -f $typeLabel) White
        Write-ColorLine ("    Vendor : {0}  Device: {1}" -f $device.VendorId, $device.DeviceId) DarkGray
        Write-ColorLine ("    Driver : {0}" -f $device.DriverInfo) DarkGray
        if ($hint) {
            Write-ColorLine ("    Hint   : {0}" -f $hint) Green
        }
        Write-Host ''
    }

    Write-ColorLine 'Ollama mode mapping (auto-detected for this system):' Cyan
    Write-ColorLine '  CPU only  : GGML_VK_VISIBLE_DEVICES=-1  (and OLLAMA_VULKAN=0)' White
    Write-ColorLine ("  APU only  : GGML_VK_VISIBLE_DEVICES=$($DeviceMap.ApuVulkanIndex)   ($($DeviceMap.ApuName))") White
    Write-ColorLine ("  dGPU only : GGML_VK_VISIBLE_DEVICES=$($DeviceMap.GpuVulkanIndex)   ($($DeviceMap.GpuName))") White
    Write-ColorLine ("  Hybrid    : GGML_VK_VISIBLE_DEVICES=$($DeviceMap.HybridVulkanValue) (both GPUs)") White
    Write-Host ''
    Write-ColorLine 'Task Manager vs Vulkan numbering:' Yellow
    Write-ColorLine '  Task Manager GPU 0 = RX 6700S (dGPU)' White
    Write-ColorLine '  Task Manager GPU 1 = Radeon 680M (iGPU)' White
    Write-ColorLine '  Ollama GGML_VK_VISIBLE_DEVICES uses Vulkan indices, NOT Task Manager GPU numbers.' White
    Write-ColorLine ("  On this system: Vulkan GPU$($DeviceMap.ApuVulkanIndex) = 680M, Vulkan GPU$($DeviceMap.GpuVulkanIndex) = 6700S") DarkGray
    Write-Host ''
}

function Get-VulkanDeviceReport {
    param([string]$VulkanInfoExe)

    $devices = Get-VulkanDevicesFromVulkanInfo -VulkanInfoExe $VulkanInfoExe
    $deviceMap = Get-OllamaDeviceMap -Devices $devices

    $enriched = foreach ($device in $devices) {
        [pscustomobject]@{
            Index           = $device.Index
            Name            = $device.Name
            DeviceType      = $device.DeviceType
            DeviceTypeLabel = Get-VulkanDeviceTypeLabel -DeviceType $device.DeviceType
            VendorId        = $device.VendorId
            DeviceId        = $device.DeviceId
            DriverName      = $device.DriverName
            DriverInfo      = $device.DriverInfo
            ApiVersion      = $device.ApiVersion
            OllamaHint      = Get-AmdGpuHint -DeviceName $device.Name -DeviceType $device.DeviceType -Index $device.Index -DeviceMap $deviceMap
        }
    }

    return [pscustomobject]@{
        Devices   = $devices
        DeviceMap = $deviceMap
        Enriched  = $enriched
    }
}

# Only run when invoked directly, not when dot-sourced by other scripts.
if ($MyInvocation.InvocationName -ne '.') {
    try {
        $vulkanInfo = Resolve-VulkanInfoPath -ExplicitPath $VulkanInfoPath
        $report = Get-VulkanDeviceReport -VulkanInfoExe $vulkanInfo

        if ($Json) {
            [pscustomobject]@{
                Devices   = $report.Enriched
                DeviceMap = $report.DeviceMap
            } | ConvertTo-Json -Depth 4
        }
        else {
            Show-VulkanDeviceTable -Devices $report.Devices -DeviceMap $report.DeviceMap
        }
    }
    catch {
        Write-ColorLine "ERROR: $($_.Exception.Message)" Red
        exit 1
    }
}