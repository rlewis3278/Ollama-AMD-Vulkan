#Requires -Version 5.1
<#
.SYNOPSIS
    Publish OllamaToolkit.App and stage a versioned release folder for git.

.DESCRIPTION
    1. Runs Publish-OllamaToolkitApp.ps1 (framework-dependent win-x64)
    2. Copies output to releases/v<Version>/
    3. Writes VERSION.txt with build metadata
    4. Creates releases/OllamaToolkit.App-v<Version>-win-x64.zip for end-user download
    5. Optionally builds a self-contained single-file exe zip for GitHub Releases

.PARAMETER Version
    Release version folder name (default: read from OllamaToolkit.App.csproj).

.PARAMETER SkipSelfContained
    Skip building the large self-contained zip (used for GitHub Release assets).
#>
param(
    [string]$Version = '',
    [switch]$SkipSelfContained
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $ProjectRoot 'src\OllamaToolkit.App\OllamaToolkit.App.csproj'
$publishScript = Join-Path $ProjectRoot 'scripts\Publish-OllamaToolkitApp.ps1'
$publishDir = Join-Path $ProjectRoot 'publish\OllamaToolkit.App'

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$csproj = Get-Content -LiteralPath $projectFile
    $Version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = '1.0.0'
    }
}

$releaseDir = Join-Path $ProjectRoot "releases\v$Version"
$zipPath = Join-Path $ProjectRoot "releases\OllamaToolkit.App-v$Version-win-x64.zip"
$selfContainedZip = Join-Path $ProjectRoot "releases\OllamaToolkit.App-v$Version-win-x64-selfcontained.zip"

& $publishScript -ProjectRoot $ProjectRoot

$gitHash = ''
Push-Location $ProjectRoot
try {
    $gitHash = (& git rev-parse --short HEAD 2>$null)
    if ($LASTEXITCODE -ne 0) { $gitHash = 'unknown' }
}
finally {
    Pop-Location
}

if (Test-Path -LiteralPath $releaseDir) {
    Remove-Item -LiteralPath $releaseDir -Recurse -Force
}
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
Copy-Item -Path (Join-Path $publishDir '*') -Destination $releaseDir -Recurse -Force

$versionText = @(
    "OllamaToolkit.App v$Version"
    "Built: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    "Git: $gitHash"
    "Runtime: .NET 8 Desktop Runtime required (framework-dependent build)"
    "Download: releases/OllamaToolkit.App-v$Version-win-x64.zip"
) -join [Environment]::NewLine

Set-Content -LiteralPath (Join-Path $releaseDir 'VERSION.txt') -Value $versionText -Encoding UTF8

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $releaseDir '*') -DestinationPath $zipPath -Force

if (-not $SkipSelfContained) {
    $scStaging = Join-Path $ProjectRoot 'publish\OllamaToolkit.App-selfcontained'
    if (Test-Path -LiteralPath $scStaging) {
        Remove-Item -LiteralPath $scStaging -Recurse -Force
    }

    Write-Host 'Building self-contained single-file exe (for GitHub Release asset)...'
    Push-Location $ProjectRoot
    try {
        & dotnet publish $projectFile -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:DebugType=embedded -o $scStaging
        if ($LASTEXITCODE -ne 0) {
            throw "Self-contained publish failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }

    $scExe = Join-Path $scStaging 'OllamaToolkit.App.exe'
    if (-not (Test-Path -LiteralPath $scExe)) {
        throw "Self-contained exe not found: $scExe"
    }

    if (Test-Path -LiteralPath $selfContainedZip) {
        Remove-Item -LiteralPath $selfContainedZip -Force
    }
    Compress-Archive -Path $scExe -DestinationPath $selfContainedZip -Force
}

Write-Host 'Release staged for git.'
Write-Host "  Folder: $releaseDir"
Write-Host "  Zip:    $zipPath"
if (-not $SkipSelfContained) {
    Write-Host "  Self-contained zip (GitHub Release): $selfContainedZip"
}