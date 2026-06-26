#Requires -Version 5.1
<#
.SYNOPSIS
    Creates or updates a Desktop shortcut for Ollama AMD Vulkan Manager.
#>
param(
    [string]$ProjectRoot = '',
    [string]$ShortcutName = 'Ollama AMD Vulkan Manager.lnk'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Split-Path -Parent $PSScriptRoot
}

$exePath = Join-Path $ProjectRoot 'publish\OllamaToolkit.App\OllamaToolkit.App.exe'
$iconPath = Join-Path $ProjectRoot 'src\OllamaToolkit.App\Assets\ollama_gui_manager_icon.ico'
$desktop = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktop $ShortcutName

if (-not (Test-Path -LiteralPath $exePath)) {
    Write-Error "Published app not found. Run scripts/Publish-OllamaToolkitApp.ps1 first: $exePath"
    exit 1
}

if (-not (Test-Path -LiteralPath $iconPath)) {
    Write-Error "App icon not found: $iconPath"
    exit 1
}

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = Split-Path -Parent $exePath
$shortcut.IconLocation = "$iconPath,0"
$shortcut.Description = 'Launch Ollama AMD Vulkan Manager'
$shortcut.Save()

Write-Host "Desktop shortcut created:"
Write-Host "  $shortcutPath"
Write-Host "  Target: $exePath"