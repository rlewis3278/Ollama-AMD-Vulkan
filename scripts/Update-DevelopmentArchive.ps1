#Requires -Version 5.1
<#
.SYNOPSIS
    Syncs agent activity into DEVELOPMENT-ARCHIVE-SESSION.log and DEVELOPMENT-ARCHIVE-COMMANDS.txt.

.DESCRIPTION
    Reads Grok session updates.jsonl (if present), appends new shell commands and tool events
    since the last run. Safe to run repeatedly; uses .archive-state.json for cursor position.

.PARAMETER SessionUpdatesPath
    Path to updates.jsonl. Defaults to the known greenfield session file.

.PARAMETER Entry
    Manual log entry: "CATEGORY|detail" (e.g. "REASONING|Fixed CLI import-reports").

.PARAMETER ProjectRoot
    Ollama-AMD-Vulkan repo root.
#>
param(
    [string]$SessionUpdatesPath = '',
    [string[]]$Entry = @(),
    [string]$ProjectRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Split-Path -Parent $PSScriptRoot
}

$sessionLog = Join-Path $ProjectRoot 'DEVELOPMENT-ARCHIVE-SESSION.log'
$commandsFile = Join-Path $ProjectRoot 'DEVELOPMENT-ARCHIVE-COMMANDS.txt'
$stateFile = Join-Path $ProjectRoot '.archive-state.json'

if ([string]::IsNullOrWhiteSpace($SessionUpdatesPath)) {
    $defaultSession = 'C:\Users\lewis\.grok\sessions\C%3A%5CUsers%5Clewis\019ef98d-ebd9-7ea0-b305-ee7496b813b8\updates.jsonl'
    if (Test-Path -LiteralPath $defaultSession) {
        $SessionUpdatesPath = $defaultSession
    }
}

function Write-SessionLine {
    param([string]$Category, [string]$Detail)
    $stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    $line = "[$stamp] [$Category] $Detail"
    Add-Content -LiteralPath $sessionLog -Value $line -Encoding UTF8
    Write-Host $line
}

function Get-ArchiveState {
    if (-not (Test-Path -LiteralPath $stateFile)) {
        return [ordered]@{ UpdatesLine = 0; LastRun = $null }
    }
    return (Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json)
}

function Save-ArchiveState {
    param($State)
    $State.LastRun = (Get-Date).ToString('o')
    $State | ConvertTo-Json | Set-Content -LiteralPath $stateFile -Encoding UTF8
}

# Manual entries from agent or CI
foreach ($e in $Entry) {
    if ($e -match '^([^|]+)\|(.+)$') {
        Write-SessionLine -Category $Matches[1].Trim() -Detail $Matches[2].Trim()
    }
    else {
        Write-SessionLine -Category 'NOTE' -Detail $e
    }
}

$state = Get-ArchiveState
$startLine = [int]$state.UpdatesLine

if ($SessionUpdatesPath -and (Test-Path -LiteralPath $SessionUpdatesPath)) {
    $lines = Get-Content -LiteralPath $SessionUpdatesPath
    $existingCommands = @{}
    if (Test-Path -LiteralPath $commandsFile) {
        Get-Content -LiteralPath $commandsFile | ForEach-Object {
            if ($_ -notmatch '^\s*#' -and $_.Trim()) { $existingCommands[$_.Trim()] = $true }
        }
    }

    for ($i = $startLine; $i -lt $lines.Count; $i++) {
        $raw = $lines[$i]
        if ([string]::IsNullOrWhiteSpace($raw)) { continue }

        try {
        $obj = $raw | ConvertFrom-Json
        $update = $obj.params.update
        if (-not $update) { continue }

        if ($update.sessionUpdate -eq 'tool_call') {
            $title = [string]$update.title
            $rawIn = $update.rawInput
            $cmd = $null
            $desc = $null
            if ($rawIn) {
                if ($rawIn.PSObject.Properties['command']) { $cmd = [string]$rawIn.command }
                if ($rawIn.PSObject.Properties['description']) { $desc = [string]$rawIn.description }
            }
            if ($title -eq 'Shell' -and $cmd) {
                Write-SessionLine -Category 'SHELL' -Detail $cmd
                if (-not $existingCommands.ContainsKey($cmd)) {
                    Add-Content -LiteralPath $commandsFile -Value $cmd -Encoding UTF8
                    $existingCommands[$cmd] = $true
                }
            }
            elseif ($title) {
                $detail = if ($desc) { $desc } else { $title }
                Write-SessionLine -Category 'TOOL' -Detail ($title + ' - ' + $detail)
            }
        }
        elseif ($update.sessionUpdate -eq 'tool_call_update' -and $update.PSObject.Properties['status'] -and $update.status -eq 'completed') {
            $rawOut = $null
            if ($update.PSObject.Properties['rawOutput']) { $rawOut = $update.rawOutput }
            if ($rawOut -and $rawOut.PSObject.Properties['command']) {
                $cmd = [string]$rawOut.command
                $exit = $null
                if ($rawOut.PSObject.Properties['exit_code']) { $exit = $rawOut.exit_code }
                if ($null -ne $exit -and $exit -ne 0) {
                    Write-SessionLine -Category 'SHELL_FAIL' -Detail "exit=$exit $cmd"
                }
            }
        }
        } catch {
            # Skip malformed or partial jsonl lines
        }
    }

    $state.UpdatesLine = $lines.Count
    Save-ArchiveState -State $state
    Write-SessionLine -Category 'SYNC' -Detail "Processed updates.jsonl lines $startLine..$($lines.Count - 1)"
}
else {
    if ($Entry.Count -eq 0) {
        Write-SessionLine -Category 'SYNC' -Detail 'No session updates.jsonl path; manual entries only.'
    }
    Save-ArchiveState -State $state
}

Write-SessionLine -Category 'SYNC' -Detail ('Archive sync complete. Session log: ' + $sessionLog)