#Requires -Version 5.1
<#
.SYNOPSIS
    Stops a running OllamaToolkit.App, publishes to publish/OllamaToolkit.App, and verifies UI marker strings.

.DESCRIPTION
    Prevents silent stale publishes (MSB3026 file lock) by stopping the app first, then failing if the
    published DLL does not contain expected strings from the latest AI status / features UI.

.PARAMETER ProjectRoot
    Ollama-AMD-Vulkan repo root. Defaults to parent of this script's directory.
#>
param(
    [string]$ProjectRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Split-Path -Parent $PSScriptRoot
}

$projectFile = Join-Path $ProjectRoot 'src\OllamaToolkit.App\OllamaToolkit.App.csproj'
$publishDir = Join-Path $ProjectRoot 'publish\OllamaToolkit.App'
$dllPath = Join-Path $publishDir 'OllamaToolkit.App.dll'

$requiredMarkers = @(
    'Program AI Inactive',
    'Ollama AI Inactive',
    '1. Description summarization',
    'Developed by Lewisound',
    'Ollama AMD Vulkan Manager'
)

function Test-DllContainsMarker {
    param(
        [byte[]]$DllBytes,
        [string]$Marker
    )

    $utf8 = [Text.Encoding]::UTF8.GetString($DllBytes)
    if ($utf8.Contains($Marker)) {
        return $true
    }

    $utf16 = [Text.Encoding]::Unicode.GetString($DllBytes)
    return $utf16.Contains($Marker)
}

Write-Host 'Stopping OllamaToolkit.App if running...'
$running = Get-Process -Name 'OllamaToolkit.App' -ErrorAction SilentlyContinue
if ($null -ne $running) {
    $running | Stop-Process -Force
    Start-Sleep -Seconds 2
}

Write-Host "Publishing to $publishDir ..."
Push-Location $ProjectRoot
try {
    & dotnet publish $projectFile -c Release -r win-x64 -o $publishDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

if (-not (Test-Path -LiteralPath $dllPath)) {
    Write-Error "Published DLL not found: $dllPath"
    exit 1
}

$dllBytes = [IO.File]::ReadAllBytes($dllPath)
$missing = @()
foreach ($marker in $requiredMarkers) {
    if (-not (Test-DllContainsMarker -DllBytes $dllBytes -Marker $marker)) {
        $missing += $marker
    }
}

if ($missing.Count -gt 0) {
    Write-Error @(
        'Published DLL is missing required marker strings:',
        ($missing | ForEach-Object { "  - $_" })
    ) -join "`n"
    exit 1
}

$dllInfo = Get-Item -LiteralPath $dllPath
$gitHash = ''
try {
    Push-Location $ProjectRoot
    $gitHash = (& git rev-parse --short HEAD 2>$null)
    if ($LASTEXITCODE -ne 0) { $gitHash = '' }
}
finally {
    Pop-Location
}

Write-Host 'Publish verification passed.'
Write-Host "  DLL: $($dllInfo.FullName)"
Write-Host "  LastWriteTime: $($dllInfo.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))"
if (-not [string]::IsNullOrWhiteSpace($gitHash)) {
    Write-Host "  Git: $gitHash"
}
foreach ($marker in $requiredMarkers) {
    Write-Host "  FOUND: $marker"
}