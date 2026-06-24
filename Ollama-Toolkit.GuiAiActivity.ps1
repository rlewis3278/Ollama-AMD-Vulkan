#Requires -Version 5.1
Set-StrictMode -Version Latest

if (-not $Script:ConfigDir) {
    $Script:ConfigDir = Join-Path $env:USERPROFILE '.ollama-amd-vulkan'
}

$Script:GuiAiActivityFile = Join-Path $Script:ConfigDir 'gui-ai-activity.log'
$Script:GuiAiActivityMaxLines = 4000

function Initialize-GuiAiActivityLog {
    Ensure-ConfigDirectory
    $header = "=== AI Activity Log started $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ==="
    Set-Content -LiteralPath $Script:GuiAiActivityFile -Value $header -Encoding UTF8
}

function Write-GuiAiActivity {
    param(
        [string]$Message,
        [string]$Category = 'AI'
    )

    if ([string]::IsNullOrWhiteSpace($Message)) { return }

    Ensure-ConfigDirectory
    if (-not (Test-Path -LiteralPath $Script:GuiAiActivityFile)) {
        Initialize-GuiAiActivityLog
    }

    $line = "[$(Get-Date -Format 'HH:mm:ss.fff')] [$Category] $Message"
    Add-Content -LiteralPath $Script:GuiAiActivityFile -Value $line -Encoding UTF8

    try {
        $lines = @(Get-Content -LiteralPath $Script:GuiAiActivityFile -Encoding UTF8)
        if ((Get-SafeCollectionCount $lines) -gt $Script:GuiAiActivityMaxLines) {
            $trimmed = @($lines | Select-Object -Last ([int]($Script:GuiAiActivityMaxLines * 0.85)))
            Set-Content -LiteralPath $Script:GuiAiActivityFile -Value $trimmed -Encoding UTF8
        }
    }
    catch { }
}

function Read-GuiAiActivityTail {
    param([ref]$LastPosition)

    if (-not (Test-Path -LiteralPath $Script:GuiAiActivityFile)) { return @() }

    try {
        $stream = [System.IO.File]::Open($Script:GuiAiActivityFile, [System.IO.FileMode]::Open, `
            [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            if ($LastPosition.Value -gt $stream.Length) { $LastPosition.Value = 0 }
            $stream.Position = $LastPosition.Value
            $reader = New-Object System.IO.StreamReader($stream)
            $text = $reader.ReadToEnd()
            $reader.Dispose()
            $LastPosition.Value = $stream.Position
        }
        finally {
            $stream.Dispose()
        }

        if ([string]::IsNullOrEmpty($text)) { return @() }
        return @($text -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }
    catch {
        return @()
    }
}

function Clear-MetadataStoreCaches {
    if (Get-Command Clear-ModelDescriptionStoreCache -ErrorAction SilentlyContinue) {
        Clear-ModelDescriptionStoreCache
    }
}