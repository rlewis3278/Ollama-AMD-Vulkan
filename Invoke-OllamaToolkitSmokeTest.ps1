#Requires -Version 5.1
<#
.SYNOPSIS
    Build, publish, launch OllamaToolkit.App, and run an automated smoke test matrix.

.DESCRIPTION
    Verifies startup, AI Settings summarizer binding, diagnostics cleanliness, and core UI presence.
    Writes results.json under reports/smoke-<timestamp>/.

.PARAMETER SkipPublish
    Use existing publish/OllamaToolkit.App without rebuilding.

.PARAMETER StartupTimeoutSec
    Seconds to wait for MainWindow loaded in diagnostics log.
#>
[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [int]$StartupTimeoutSec = 120,
    [int]$PostStartupSec = 15
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Script:Root = $PSScriptRoot
$Script:PublishDir = Join-Path $Script:Root 'publish\OllamaToolkit.App'
$Script:ExePath = Join-Path $Script:PublishDir 'OllamaToolkit.App.exe'
$Script:ConfigDir = Join-Path $env:USERPROFILE '.ollama-amd-vulkan'
$Script:DiagnosticsLog = Join-Path $Script:ConfigDir 'toolkit-diagnostics.log'
$Script:AiSettingsFile = Join-Path $Script:ConfigDir 'ai-settings.json'
$Script:Timestamp = (Get-Date).ToString('yyyy-MM-dd_HHmmss')
$Script:OutputDir = Join-Path $Script:Root (Join-Path 'reports' "smoke-$Script:Timestamp")
$Script:Results = [System.Collections.Generic.List[object]]::new()

function Add-TestResult {
    param(
        [string]$Name,
        [bool]$Passed,
        [string]$Detail = ''
    )
    $Script:Results.Add([pscustomobject]@{
            Name   = $Name
            Passed = $Passed
            Detail = $Detail
        })
    $status = if ($Passed) { 'PASS' } else { 'FAIL' }
    if ($Detail) {
        Write-Host "[$status] $Name - $Detail"
    }
    else {
        Write-Host "[$status] $Name"
    }
}

function Wait-ForLogLine {
    param(
        [string]$Path,
        [string]$Pattern,
        [int]$TimeoutSec,
        [long]$StartOffset = 0
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $Path) {
            $text = Get-Content -LiteralPath $Path -Raw -ErrorAction SilentlyContinue
            if ($text -and $text.Length -gt $StartOffset) {
                $slice = $text.Substring([int]$StartOffset)
                if ($slice -match $Pattern) {
                    return $true
                }
            }
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Get-NewDiagnosticErrors {
    param(
        [string]$Path,
        [long]$StartOffset
    )
    if (-not (Test-Path -LiteralPath $Path)) {
        return @()
    }
    $text = Get-Content -LiteralPath $Path -Raw -ErrorAction SilentlyContinue
    if (-not $text -or $text.Length -le $StartOffset) {
        return @()
    }
    $slice = $text.Substring([int]$StartOffset)
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($match in [regex]::Matches($slice, '(?m)^\[[^\]]+\]\s*\[Error\]\s*(.+)$')) {
        $lines.Add($match.Value.Trim())
    }
    return $lines.ToArray()
}

function Get-MainWindowElement {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        'Ollama AMD Vulkan Manager')
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
}

function Get-DescendantByName {
    param(
        $Parent,
        [string]$Name,
        [string]$ControlTypeName = ''
    )
    $nameCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    if ([string]::IsNullOrWhiteSpace($ControlTypeName)) {
        return $Parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond)
    }
    $type = [System.Windows.Automation.ControlType]::$ControlTypeName
    $typeCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        $type)
    $and = New-Object System.Windows.Automation.AndCondition($nameCond, $typeCond)
    return $Parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $and)
}

function Invoke-TabSelect {
    param(
        $Window,
        [string]$TabHeader
    )
    $tab = Get-DescendantByName -Parent $Window -Name $TabHeader -ControlTypeName 'TabItem'
    if ($null -eq $tab) {
        return $false
    }
    $pattern = $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pattern.Select()
    Start-Sleep -Milliseconds 800
    return $true
}

New-Item -ItemType Directory -Path $Script:OutputDir -Force | Out-Null

if (-not $SkipPublish) {
    $publishScript = Join-Path $Script:Root 'scripts\Publish-OllamaToolkitApp.ps1'
    if (-not (Test-Path -LiteralPath $publishScript)) {
        throw "Publish script not found: $publishScript"
    }
    & $publishScript -ProjectRoot $Script:Root
    Add-TestResult -Name 'Publish' -Passed ($LASTEXITCODE -eq 0) -Detail 'Publish-OllamaToolkitApp.ps1'
}
else {
    Add-TestResult -Name 'Publish' -Passed (Test-Path -LiteralPath $Script:ExePath) -Detail 'Skipped publish'
}

Add-TestResult -Name 'PublishedExeExists' -Passed (Test-Path -LiteralPath $Script:ExePath) -Detail $Script:ExePath

$dllPath = Join-Path $Script:PublishDir 'OllamaToolkit.App.dll'
$dllBytes = [IO.File]::ReadAllBytes($dllPath)
$utf16 = [Text.Encoding]::Unicode.GetString($dllBytes)
$hasSummarizerDownload = $utf16.Contains('DownloadSummarizerBtn')
Add-TestResult -Name 'SummarizerDownloadButtonRemoved' -Passed (-not $hasSummarizerDownload) `
    -Detail $(if ($hasSummarizerDownload) { 'DownloadSummarizerBtn still in assembly' } else { 'OK' })

$preferredBefore = $null
if (Test-Path -LiteralPath $Script:AiSettingsFile) {
    try {
        $settingsJson = Get-Content -LiteralPath $Script:AiSettingsFile -Raw | ConvertFrom-Json
        $preferredBefore = $settingsJson.PreferredSummarizerModel
    }
    catch {
        $preferredBefore = $null
    }
}

$logOffset = 0L
if (Test-Path -LiteralPath $Script:DiagnosticsLog) {
    $logOffset = (Get-Item -LiteralPath $Script:DiagnosticsLog).Length
}

$running = Get-Process -Name 'OllamaToolkit.App' -ErrorAction SilentlyContinue
if ($null -ne $running) {
    $running | Stop-Process -Force
    Start-Sleep -Seconds 2
}

$proc = Start-Process -FilePath $Script:ExePath -WorkingDirectory $Script:PublishDir -PassThru
Add-TestResult -Name 'LaunchProcess' -Passed ($null -ne $proc) -Detail "PID $($proc.Id)"

$loaded = Wait-ForLogLine -Path $Script:DiagnosticsLog -Pattern 'MainWindow loaded' `
    -TimeoutSec $StartupTimeoutSec -StartOffset $logOffset
Add-TestResult -Name 'StartupMainWindowLoaded' -Passed $loaded -Detail "${StartupTimeoutSec}s timeout"

Start-Sleep -Seconds $PostStartupSec

$window = $null
$windowDeadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $windowDeadline -and $null -eq $window) {
    try {
        $window = Get-MainWindowElement
    }
    catch {
        $window = $null
    }
    if ($null -eq $window) {
        Start-Sleep -Milliseconds 500
    }
}

Add-TestResult -Name 'MainWindowVisible' -Passed ($null -ne $window) -Detail 'UI Automation'

$tabHeaders = @(
    'Compute Modes',
    'Models & Launch',
    'Model Library',
    'Model Run',
    'Testing Suite',
    'Test Results',
    'AI Settings',
    'AI Features',
    'AI Activity',
    'Diagnostics'
)

foreach ($header in $tabHeaders) {
    $ok = $false
    if ($null -ne $window) {
        $ok = Invoke-TabSelect -Window $window -TabHeader $header
    }
    Add-TestResult -Name "Tab_$($header -replace '[^a-zA-Z0-9]','_')" -Passed $ok -Detail $header
}

if ($null -ne $window) {
    Invoke-TabSelect -Window $window -TabHeader 'AI Settings' | Out-Null
    $refreshBtn = Get-DescendantByName -Parent $window -Name 'Refresh List' -ControlTypeName 'Button'
    $testBtn = Get-DescendantByName -Parent $window -Name 'Test Summarizer' -ControlTypeName 'Button'
    Add-TestResult -Name 'AiSettings_RefreshListButton' -Passed ($null -ne $refreshBtn)
    Add-TestResult -Name 'AiSettings_TestSummarizerButton' -Passed ($null -ne $testBtn)

    $comboType = [System.Windows.Automation.ControlType]::ComboBox
    $comboCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        $comboType)
    $combo = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $comboCond)
    $comboPopulated = $false
    if ($null -ne $combo) {
        $expand = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $expand.Expand()
        Start-Sleep -Milliseconds 500
        $listType = [System.Windows.Automation.ControlType]::ListItem
        $listCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            $listType)
        $items = $combo.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listCond)
        $comboPopulated = $items.Count -ge 1
        $expand.Collapse()
    }
    Add-TestResult -Name 'AiSettings_SummarizerComboPopulated' -Passed $comboPopulated `
        -Detail $(if ($comboPopulated) { "$($items.Count) item(s)" } else { 'empty combo' })
}

if (-not $proc.HasExited) {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

$preferredAfter = $preferredBefore
if (Test-Path -LiteralPath $Script:AiSettingsFile) {
    try {
        $settingsJson = Get-Content -LiteralPath $Script:AiSettingsFile -Raw | ConvertFrom-Json
        $preferredAfter = $settingsJson.PreferredSummarizerModel
    }
    catch {
        $preferredAfter = $null
    }
}

$persisted = [string]::Equals(
    [string]$preferredBefore,
    [string]$preferredAfter,
    [StringComparison]::OrdinalIgnoreCase)
Add-TestResult -Name 'PreferredSummarizerPersisted' -Passed $persisted `
    -Detail "before=$preferredBefore after=$preferredAfter"

$errors = @(Get-NewDiagnosticErrors -Path $Script:DiagnosticsLog -StartOffset $logOffset)
$noErrors = ($errors.Length -eq 0)
Add-TestResult -Name 'DiagnosticsNoNewErrors' -Passed $noErrors `
    -Detail $(if ($noErrors) { 'OK' } else { ($errors | Select-Object -First 3) -join ' | ' })

$allResults = @($Script:Results)
$failedResults = @($allResults | Where-Object { -not $_.Passed })
$failCount = $failedResults.Length
$summary = [pscustomobject]@{
    Timestamp  = $Script:Timestamp
    OutputDir  = $Script:OutputDir
    Total      = $allResults.Length
    Passed     = $allResults.Length - $failCount
    Failed     = $failCount
    Tests      = $Script:Results
}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Script:OutputDir 'results.json') -Encoding UTF8
Copy-Item -LiteralPath $Script:DiagnosticsLog -Destination (Join-Path $Script:OutputDir 'toolkit-diagnostics.log') `
    -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Smoke test complete: $($summary.Passed)/$($summary.Total) passed, $failCount failed."
Write-Host "Results: $(Join-Path $Script:OutputDir 'results.json')"

exit $failCount