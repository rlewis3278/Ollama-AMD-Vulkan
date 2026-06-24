#Requires -Version 5.1
Set-StrictMode -Version Latest

if (-not $Script:ToolkitRoot) {
    $Script:ToolkitRoot = $PSScriptRoot
}

if (-not (Get-Variable -Name 'HashtableMetaProps' -Scope Script -ErrorAction SilentlyContinue)) {
    $Script:HashtableMetaProps = @(
        'Count', 'Keys', 'Values', 'IsReadOnly', 'IsFixedSize', 'SyncRoot', 'IsSynchronized'
    )
}

$Script:ModelDescriptionsFile = Join-Path $Script:ConfigDir 'model-descriptions.json'
$Script:ModelDescriptionStoreCache = $null

function Clear-ModelDescriptionStoreCache {
    $Script:ModelDescriptionStoreCache = $null
}
$Script:OllamaLibraryBaseUrl = 'https://ollama.com/library'
$Script:DescriptionCacheDays = 7
$Script:ListDescriptionMaxChars = 72
$Script:DescriptionSummarizerModels = @(
    'llama3.2:3b',
    'phi4-mini:latest',
    'qwen2.5-coder:7b',
    'phi4-mini-reasoning:latest'
)

function New-EmptyDescriptionStore {
    return [ordered]@{
        Version     = 1
        LastUpdated = (Get-Date).ToString('o')
        Models      = [ordered]@{}
    }
}

function ConvertTo-DescriptionHashtable {
    param($Store)

    $models = [ordered]@{}
    if ($Store.Models) {
        if ($Store.Models -is [System.Collections.IDictionary]) {
            foreach ($key in @($Store.Models.Keys)) {
                if ($key -in $Script:HashtableMetaProps) { continue }
                $models[$key] = $Store.Models[$key]
            }
        }
        else {
            foreach ($prop in $Store.Models.PSObject.Properties) {
                if ($prop.Name -in $Script:HashtableMetaProps) { continue }
                $models[$prop.Name] = $prop.Value
            }
        }
    }
    return [ordered]@{
        Version     = if ($Store.Version) { [int]$Store.Version } else { 1 }
        LastUpdated = if ($Store.LastUpdated) { [string]$Store.LastUpdated } else { (Get-Date).ToString('o') }
        Models      = $models
    }
}

function Get-ModelDescriptionStore {
    if ($Script:ModelDescriptionStoreCache) {
        return $Script:ModelDescriptionStoreCache
    }

    Ensure-ConfigDirectory
    if (-not (Test-Path -LiteralPath $Script:ModelDescriptionsFile)) {
        $Script:ModelDescriptionStoreCache = New-EmptyDescriptionStore
        return $Script:ModelDescriptionStoreCache
    }

    try {
        $data = Get-Content -LiteralPath $Script:ModelDescriptionsFile -Raw | ConvertFrom-Json
        $Script:ModelDescriptionStoreCache = ConvertTo-DescriptionHashtable -Store $data
        return $Script:ModelDescriptionStoreCache
    }
    catch {
        Write-ToolkitLog "Description store corrupt; starting fresh: $($_.Exception.Message)" Yellow
        $Script:ModelDescriptionStoreCache = New-EmptyDescriptionStore
        return $Script:ModelDescriptionStoreCache
    }
}

function Save-ModelDescriptionStore {
    param($Store)

    Ensure-ConfigDirectory
    $normalized = ConvertTo-DescriptionHashtable -Store $Store
    $modelsExport = [ordered]@{}
    foreach ($key in @($normalized.Models.Keys)) {
        $val = $normalized.Models[$key]
        $modelsExport[$key] = [ordered]@{
            ShortDescription     = Get-DescriptionEntryValue -Entry $val -Name 'ShortDescription'
            ListDescription      = Get-DescriptionEntryValue -Entry $val -Name 'ListDescription'
            Readme               = Get-DescriptionEntryValue -Entry $val -Name 'Readme'
            Title                = Get-DescriptionEntryValue -Entry $val -Name 'Title'
            SourceUrl            = Get-DescriptionEntryValue -Entry $val -Name 'SourceUrl'
            LibraryName          = Get-DescriptionEntryValue -Entry $val -Name 'LibraryName'
            FetchedAt            = Get-DescriptionEntryValue -Entry $val -Name 'FetchedAt'
            SummaryModel         = Get-DescriptionEntryValue -Entry $val -Name 'SummaryModel'
            SummaryGeneratedAt   = Get-DescriptionEntryValue -Entry $val -Name 'SummaryGeneratedAt'
            Error                = Get-DescriptionEntryValue -Entry $val -Name 'Error'
        }
    }

    $payload = [ordered]@{
        Version     = 1
        LastUpdated = (Get-Date).ToString('o')
        Models      = $modelsExport
    }
    $payload | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Script:ModelDescriptionsFile -Encoding UTF8
    Clear-ModelDescriptionStoreCache
}

function Get-DescriptionEntryValue {
    param(
        $Entry,
        [string]$Name
    )

    if ($null -eq $Entry) { return $null }
    if ($Entry -is [System.Collections.IDictionary]) {
        if ($Entry.Contains($Name)) { return $Entry[$Name] }
        return $null
    }
    $prop = $Entry.PSObject.Properties[$Name]
    if ($prop) { return $prop.Value }
    return $null
}

function Get-OllamaLibraryNameCandidates {
    param([string]$ModelName)

    $candidates = New-Object System.Collections.Generic.List[string]
    [void]$candidates.Add($ModelName)

    if ($ModelName -match '^(.+):[^:]+$') {
        [void]$candidates.Add($Matches[1])
    }

    $base = ($ModelName -split ':', 2)[0]
    if ($base -and $base -ne $ModelName) {
        [void]$candidates.Add($base)
    }

    return @($candidates | Select-Object -Unique)
}

function ConvertFrom-OllamaLibraryHtml {
    param(
        [string]$Html,
        [string]$SourceUrl,
        [string]$LibraryName
    )

    $title = ''
    if ($Html -match '<title>([^<]+)</title>') {
        $title = [System.Net.WebUtility]::HtmlDecode($Matches[1]).Trim()
    }

    $short = ''
    if ($Html -match 'meta name="description" content="([^"]*)"') {
        $short = [System.Net.WebUtility]::HtmlDecode($Matches[1]).Trim()
    }

    $readme = ''
    if ($Html -match '(?s)<h2[^>]*>\s*Readme\s*</h2>.*?<div[^>]*\bprose\b[^>]*>(.*?)</div>\s*</div>') {
        $block = $Matches[1]
        $block = $block -replace '(?s)<script.*?</script>', ''
        $block = $block -replace '(?s)<style.*?</style>', ''
        $block = $block -replace '<br\s*/?>', "`n"
        $block = $block -replace '</p>', "`n"
        $block = $block -replace '</h[1-6]>', "`n"
        $block = $block -replace '</li>', "`n"
        $block = $block -replace '<[^>]+>', ''
        $readme = [System.Net.WebUtility]::HtmlDecode($block)
        $readme = ($readme -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ }) -join "`r`n"
        if ($readme.Length -gt 12000) {
            $readme = $readme.Substring(0, 12000) + "`r`n`r`n[Truncated for local storage]"
        }
    }

    return [ordered]@{
        Title                = $title
        ShortDescription     = $short
        ListDescription      = $null
        Readme               = $readme
        SourceUrl            = $SourceUrl
        LibraryName          = $LibraryName
        FetchedAt            = (Get-Date).ToString('o')
        SummaryModel         = $null
        SummaryGeneratedAt   = $null
        Error                = $null
    }
}

function Get-OllamaModelDescriptionFromWeb {
    param([string]$ModelName)

    $lastError = $null
    foreach ($candidate in (Get-OllamaLibraryNameCandidates -ModelName $ModelName)) {
        $url = "$Script:OllamaLibraryBaseUrl/$candidate"
        try {
            $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 30
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) {
                $parsed = ConvertFrom-OllamaLibraryHtml -Html $response.Content -SourceUrl $url -LibraryName $candidate
                if ($parsed.ShortDescription -or $parsed.Readme) {
                    return $parsed
                }
                $lastError = "No description content found at $url"
            }
        }
        catch {
            $lastError = $_.Exception.Message
        }
        Invoke-ToolkitUiPump
    }

    return [ordered]@{
        Title                = $ModelName
        ShortDescription     = ''
        ListDescription      = $null
        Readme               = ''
        SourceUrl            = "$Script:OllamaLibraryBaseUrl/$ModelName"
        LibraryName          = $ModelName
        FetchedAt            = (Get-Date).ToString('o')
        SummaryModel         = $null
        SummaryGeneratedAt   = $null
        Error                = if ($lastError) { $lastError } else { 'Description not found on ollama.com' }
    }
}

function Copy-DescriptionEntry {
    param($Entry)

    return [ordered]@{
        Title                = Get-DescriptionEntryValue -Entry $Entry -Name 'Title'
        ShortDescription     = Get-DescriptionEntryValue -Entry $Entry -Name 'ShortDescription'
        ListDescription      = Get-DescriptionEntryValue -Entry $Entry -Name 'ListDescription'
        Readme               = Get-DescriptionEntryValue -Entry $Entry -Name 'Readme'
        SourceUrl            = Get-DescriptionEntryValue -Entry $Entry -Name 'SourceUrl'
        LibraryName          = Get-DescriptionEntryValue -Entry $Entry -Name 'LibraryName'
        FetchedAt            = Get-DescriptionEntryValue -Entry $Entry -Name 'FetchedAt'
        SummaryModel         = Get-DescriptionEntryValue -Entry $Entry -Name 'SummaryModel'
        SummaryGeneratedAt   = Get-DescriptionEntryValue -Entry $Entry -Name 'SummaryGeneratedAt'
        Error                = Get-DescriptionEntryValue -Entry $Entry -Name 'Error'
    }
}

function Get-DescriptionSummarizerModel {
    param(
        [string]$ExcludeModelName,
        [string[]]$LocalModelNames
    )

    $localNames = if ($LocalModelNames) {
        @($LocalModelNames | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }
    else {
        @(Get-ToolkitLocalOllamaModels | ForEach-Object { $_.name })
    }
    if ((Get-SafeCollectionCount $localNames) -eq 0) { return $null }

    foreach ($preferred in $Script:DescriptionSummarizerModels) {
        if ($preferred -eq $ExcludeModelName) { continue }
        if ($localNames -contains $preferred) { return $preferred }
    }

    $smallest = Get-ToolkitLocalOllamaModels |
        Where-Object { $_.name -ne $ExcludeModelName } |
        Sort-Object -Property size |
        Select-Object -First 1
    if ($smallest) { return $smallest.name }
    return $null
}

function Normalize-ListDescriptionText {
    param(
        [string]$Text,
        [int]$MaxLength
    )

    if ([string]::IsNullOrWhiteSpace($Text)) { return '' }

    $normalized = ($Text -replace '[\r\n]+', ' ' -replace '\s+', ' ').Trim()
    $normalized = $normalized.Trim('"', '''', '`')
    if ($normalized.Length -le $MaxLength) { return $normalized }
    return $normalized.Substring(0, $MaxLength - 3) + '...'
}

function Invoke-ToolkitOllamaGenerate {
    param(
        [string]$Model,
        [string]$Prompt,
        [int]$MaxPredict = 96,
        [int]$NumCtx = 4096
    )

    $uri = ($Script:OllamaHost.TrimEnd('/')) + '/api/generate'
    $body = @{
        model   = $Model
        prompt  = $Prompt
        stream  = $false
        options = @{
            num_predict = $MaxPredict
            num_ctx     = $NumCtx
            temperature = 0.2
        }
    }

    $response = Invoke-ToolkitRestJson -Uri $uri -Body $body -TimeoutSec 120
    return [string]$response.response
}

function New-ListDescriptionViaLlm {
    param(
        [string]$ModelName,
        $Entry,
        [int]$MaxChars = 0
    )

    if ($MaxChars -le 0) {
        $MaxChars = if ($Script:ListDescriptionMaxChars -gt 0) { $Script:ListDescriptionMaxChars } else { 72 }
    }

    $sourceText = ''
    if ($Entry.ShortDescription) { $sourceText = $Entry.ShortDescription.Trim() }
    if ($Entry.Readme) {
        $readmeSnippet = $Entry.Readme
        if ($readmeSnippet.Length -gt 1800) {
            $readmeSnippet = $readmeSnippet.Substring(0, 1800)
        }
        if ($sourceText) { $sourceText += "`n`n" }
        $sourceText += $readmeSnippet
    }
    if (-not $sourceText) { return $null }

    if (-not (Test-OllamaApiReady)) {
        Write-ToolkitLog 'Ollama API unavailable; using truncated description for list column.' Yellow
        return Normalize-ListDescriptionText -Text $Entry.ShortDescription -MaxLength $MaxChars
    }

    $summarizer = Get-DescriptionSummarizerModel -ExcludeModelName $ModelName
    if (-not $summarizer) {
        Write-ToolkitLog "No local model available to summarize '$ModelName'; using truncated text." Yellow
        return Normalize-ListDescriptionText -Text $Entry.ShortDescription -MaxLength $MaxChars
    }

    $prompt = @"
Write one concise phrase describing this AI model for a single-line table cell.
Rules:
- Maximum $MaxChars characters
- Plain text only (no quotes, labels, markdown, or line breaks)
- Focus on what the model is best for

Model name: $ModelName
Source material:
$sourceText

Summary:
"@

    try {
        Write-ToolkitLog "Summarizing '$ModelName' with '$summarizer'..." Cyan
        $raw = Invoke-ToolkitOllamaGenerate -Model $summarizer -Prompt $prompt -MaxPredict 96
        Invoke-ToolkitUiPump
        $summary = Normalize-ListDescriptionText -Text $raw -MaxLength $MaxChars
        if ($summary) {
            $Entry.SummaryModel = $summarizer
            $Entry.SummaryGeneratedAt = (Get-Date).ToString('o')
            return $summary
        }
    }
    catch {
        Write-ToolkitLog "LLM summary failed for '$ModelName': $($_.Exception.Message)" Yellow
    }

    return Normalize-ListDescriptionText -Text $Entry.ShortDescription -MaxLength $MaxChars
}

function Test-DescriptionNeedsRefresh {
    param(
        $Entry,
        [switch]$Force
    )

    if ($Force) { return $true }
    if (-not $Entry) { return $true }
    if ($Entry.Error -and -not $Entry.ShortDescription -and -not $Entry.Readme) { return $true }
    if (-not $Entry.FetchedAt) { return $true }

    try {
        $fetched = [datetime]$Entry.FetchedAt
        return ((Get-Date) - $fetched).TotalDays -ge $Script:DescriptionCacheDays
    }
    catch {
        return $true
    }
}

function Test-ListDescriptionNeedsRefresh {
    param(
        $Entry,
        [switch]$Force
    )

    if ($Force) { return $true }
    if (-not $Entry) { return $true }
    if (-not (Get-DescriptionEntryValue -Entry $Entry -Name 'ListDescription')) { return $true }
    if (-not (Get-DescriptionEntryValue -Entry $Entry -Name 'SummaryGeneratedAt')) { return $true }

    try {
        $generated = [datetime](Get-DescriptionEntryValue -Entry $Entry -Name 'SummaryGeneratedAt')
        return ((Get-Date) - $generated).TotalDays -ge $Script:DescriptionCacheDays
    }
    catch {
        return $true
    }
}

function Update-ModelDescriptionCache {
    param(
        [string]$ModelName,
        [switch]$Force,
        [switch]$SkipListSummary
    )

    $store = Get-ModelDescriptionStore
    if (-not $store.Models -or $store.Models -isnot [System.Collections.IDictionary]) {
        $store.Models = [ordered]@{}
    }

    $existing = $null
    if ($store.Models.Contains($ModelName)) {
        $existing = $store.Models[$ModelName]
    }

    $needsWeb = Test-DescriptionNeedsRefresh -Entry $existing -Force:$Force
    $needsSummary = if ($SkipListSummary) {
        $false
    }
    else {
        Test-ListDescriptionNeedsRefresh -Entry $existing -Force:$Force
    }

    if (-not $needsWeb -and -not $needsSummary) {
        return [pscustomobject]@{
            Model   = $ModelName
            Updated = $false
            Entry   = $existing
        }
    }

    if ($needsWeb) {
        Write-ToolkitLog "Fetching ollama.com description for '$ModelName'..." Cyan
        $entry = Get-OllamaModelDescriptionFromWeb -ModelName $ModelName
    }
    else {
        $entry = Copy-DescriptionEntry -Entry $existing
    }

    if ($needsSummary -and ($entry.ShortDescription -or $entry.Readme)) {
        $entry.ListDescription = New-ListDescriptionViaLlm -ModelName $ModelName -Entry $entry
    }
    elseif (-not $entry.ListDescription -and $entry.ShortDescription) {
        $entry.ListDescription = Normalize-ListDescriptionText -Text $entry.ShortDescription `
            -MaxLength $Script:ListDescriptionMaxChars
    }

    $store.Models[$ModelName] = $entry
    Save-ModelDescriptionStore -Store $store

    return [pscustomobject]@{
        Model   = $ModelName
        Updated = $true
        Entry   = $entry
    }
}

function Sync-LocalModelDescriptions {
    param(
        [switch]$Force,
        [string[]]$ModelNames
    )

    $ModelNames = @($ModelNames | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ((Get-SafeCollectionCount $ModelNames) -eq 0) {
        $ModelNames = @(Get-ToolkitLocalOllamaModels | ForEach-Object { $_.name })
    }

    $updated = 0
    $skipped = 0
    $results = @()

    foreach ($name in $ModelNames) {
        $result = Update-ModelDescriptionCache -ModelName $name -Force:$Force
        $results += $result
        if ($result.Updated) { $updated++ } else { $skipped++ }
        Invoke-ToolkitUiPump
    }

    return [pscustomobject]@{
        Updated = $updated
        Skipped = $skipped
        Total   = (Get-SafeCollectionCount $ModelNames)
        Results = $results
    }
}

function Get-ModelDescriptionEntry {
    param([string]$ModelName)

    $store = Get-ModelDescriptionStore
    if (-not $store.Models -or -not $store.Models.Contains($ModelName)) {
        return $null
    }
    return $store.Models[$ModelName]
}

function Get-ModelListDescription {
    param([string]$ModelName)

    $entry = Get-ModelDescriptionEntry -ModelName $ModelName
    if (-not $entry) { return '-' }

    $listDesc = Get-DescriptionEntryValue -Entry $entry -Name 'ListDescription'
    if ($listDesc) { return [string]$listDesc }

    return '-'
}

function Get-ModelDescriptionAbbrev {
    param([string]$ModelName)

    $entry = Get-ModelDescriptionEntry -ModelName $ModelName
    if (-not $entry) { return '-' }
    $listDesc = Get-DescriptionEntryValue -Entry $entry -Name 'ListDescription'
    if ($listDesc) { return [string]$listDesc }
    $shortDesc = Get-DescriptionEntryValue -Entry $entry -Name 'ShortDescription'
    if ($shortDesc) {
        return Normalize-ListDescriptionText -Text $shortDesc `
            -MaxLength $Script:ListDescriptionMaxChars
    }
    if (Get-DescriptionEntryValue -Entry $entry -Name 'Error') { return '(not available)' }
    return '-'
}

function Get-ModelDescriptionDisplayText {
    param(
        [string]$ModelName,
        [ValidateSet('Short', 'Full')]
        [string]$Format = 'Short'
    )

    $entry = Get-ModelDescriptionEntry -ModelName $ModelName
    if (-not $entry) {
        return "No description cached for '$ModelName'.`r`nClick 'Refresh Descriptions' to pull from ollama.com."
    }

    $lines = New-Object System.Collections.Generic.List[string]
    if ($entry.Title) { $lines.Add($entry.Title) }
    if ($entry.ShortDescription) { $lines.Add(''); $lines.Add($entry.ShortDescription) }
    if ($entry.LibraryName) { $lines.Add(''); $lines.Add("Library: $($entry.LibraryName)") }
    if ($entry.SourceUrl) { $lines.Add($entry.SourceUrl) }
    if ($entry.FetchedAt) { $lines.Add("Fetched: $($entry.FetchedAt)") }
    if ($entry.Error -and -not $entry.ShortDescription) {
        $lines.Add('')
        $lines.Add("Error: $($entry.Error)")
    }

    if ($Format -eq 'Full' -and $entry.Readme) {
        $lines.Add('')
        $lines.Add('--- Readme ---')
        $lines.Add($entry.Readme)
    }

    return ($lines -join "`r`n")
}

function Get-MissingDescriptionModels {
    $local = @(Get-ToolkitLocalOllamaModels | ForEach-Object { $_.name })
    $store = Get-ModelDescriptionStore
    $missing = @()

    foreach ($name in $local) {
        $needs = $true
        if ($store.Models -and $store.Models.Contains($name)) {
            $entry = $store.Models[$name]
            $needsWeb = Test-DescriptionNeedsRefresh -Entry $entry
            $needsSummary = Test-ListDescriptionNeedsRefresh -Entry $entry
            $needs = $needsWeb -or $needsSummary
        }
        if ($needs) { $missing += $name }
    }
    return @($missing)
}