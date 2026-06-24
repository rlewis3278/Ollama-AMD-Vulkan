#Requires -Version 5.1
Set-StrictMode -Version Latest

if (-not $Script:ToolkitRoot) {
    $Script:ToolkitRoot = $PSScriptRoot
}

if (-not (Get-Command Invoke-ToolkitWebSearch -ErrorAction SilentlyContinue)) {
    . (Join-Path $Script:ToolkitRoot 'Ollama-Toolkit.Core.ps1')
}

if (-not (Get-Variable -Name 'HashtableMetaProps' -Scope Script -ErrorAction SilentlyContinue)) {
    $Script:HashtableMetaProps = @(
        'Count', 'Keys', 'Values', 'IsReadOnly', 'IsFixedSize', 'SyncRoot', 'IsSynchronized'
    )
}

$Script:ModelPerformanceFile = Join-Path $Script:ConfigDir 'model-performance-ratings.json'
$Script:ModelPerformanceStoreCache = $null

function Clear-ModelPerformanceStoreCache {
    $Script:ModelPerformanceStoreCache = $null
}
$Script:PerformanceCacheDays = 14
$Script:ListPerformanceMaxChars = 64
$Script:PerformanceSearchResultLimit = 5

if (-not (Get-Variable -Name 'EnableAiPerformanceRatings' -Scope Script -ErrorAction SilentlyContinue)) {
    $Script:EnableAiPerformanceRatings = $false
}

function Test-AiPerformanceRatingsEnabled {
    return [bool]$Script:EnableAiPerformanceRatings
}

function New-EmptyPerformanceStore {
    return [ordered]@{
        Version     = 1
        LastUpdated = (Get-Date).ToString('o')
        Models      = [ordered]@{}
    }
}

function ConvertTo-PerformanceHashtable {
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

function Get-ModelPerformanceStore {
    if ($Script:ModelPerformanceStoreCache) {
        return $Script:ModelPerformanceStoreCache
    }

    Ensure-ConfigDirectory
    if (-not (Test-Path -LiteralPath $Script:ModelPerformanceFile)) {
        $Script:ModelPerformanceStoreCache = New-EmptyPerformanceStore
        return $Script:ModelPerformanceStoreCache
    }

    try {
        $data = Get-Content -LiteralPath $Script:ModelPerformanceFile -Raw | ConvertFrom-Json
        $Script:ModelPerformanceStoreCache = ConvertTo-PerformanceHashtable -Store $data
        return $Script:ModelPerformanceStoreCache
    }
    catch {
        Write-ToolkitLog "Performance store corrupt; starting fresh: $($_.Exception.Message)" Yellow
        $Script:ModelPerformanceStoreCache = New-EmptyPerformanceStore
        return $Script:ModelPerformanceStoreCache
    }
}

function Get-PerformanceEntryValue {
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

function Save-ModelPerformanceStore {
    param($Store)

    Ensure-ConfigDirectory
    $normalized = ConvertTo-PerformanceHashtable -Store $Store
    $modelsExport = [ordered]@{}
    foreach ($key in @($normalized.Models.Keys)) {
        $val = $normalized.Models[$key]
        $modelsExport[$key] = [ordered]@{
            Rating         = Get-PerformanceEntryValue -Entry $val -Name 'Rating'
            LibraryName    = Get-PerformanceEntryValue -Entry $val -Name 'LibraryName'
            FetchedAt      = Get-PerformanceEntryValue -Entry $val -Name 'FetchedAt'
            SummaryModel   = Get-PerformanceEntryValue -Entry $val -Name 'SummaryModel'
            SearchQueries  = Get-PerformanceEntryValue -Entry $val -Name 'SearchQueries'
            Error          = Get-PerformanceEntryValue -Entry $val -Name 'Error'
        }
    }

    $payload = [ordered]@{
        Version     = 1
        LastUpdated = (Get-Date).ToString('o')
        Models      = $modelsExport
    }
    $payload | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Script:ModelPerformanceFile -Encoding UTF8
    Clear-ModelPerformanceStoreCache
}

function Normalize-PerformanceRatingText {
    param(
        [string]$Text,
        [int]$MaxLength = 0
    )

    if ($MaxLength -le 0) {
        $MaxLength = $Script:ListPerformanceMaxChars
    }
    if ([string]::IsNullOrWhiteSpace($Text)) { return '' }

    $normalized = ($Text -replace '[\r\n]+', ' ' -replace '\s+', ' ').Trim()
    $normalized = $normalized.Trim('"', '''', '`')
    if ($normalized.Length -le $MaxLength) { return $normalized }
    return $normalized.Substring(0, $MaxLength - 3) + '...'
}

function Get-ModelPerformanceSearchQueries {
    param([string]$LibraryName)

    $safe = ($LibraryName -replace '[^\w\.\-:+]', ' ').Trim()
    return @(
        "$safe LLM benchmark MMLU GPQA performance rating",
        "$safe artificial analysis intelligence index benchmark",
        "$safe ollama model benchmark scores"
    )
}

function Get-ModelPerformanceSearchSnippets {
    param([string]$LibraryName)

    $queries = Get-ModelPerformanceSearchQueries -LibraryName $LibraryName
    $snippets = New-Object System.Collections.Generic.List[string]
    foreach ($query in $queries) {
        foreach ($hit in @(Invoke-ToolkitWebSearch -Query $query)) {
            if ((Get-SafeCollectionCount $snippets) -ge 12) { break }
            if ($hit -and -not ($snippets -contains $hit)) {
                [void]$snippets.Add($hit)
            }
        }
        Invoke-ToolkitUiPump
        if ((Get-SafeCollectionCount $snippets) -ge 8) { break }
    }
    return @($snippets)
}

function New-ModelPerformanceRatingViaLlm {
    param(
        [string]$ModelName,
        [string]$LibraryName,
        [string[]]$SearchSnippets,
        [int]$MaxChars = 0
    )

    if ($MaxChars -le 0) {
        $MaxChars = $Script:ListPerformanceMaxChars
    }
    if ((Get-SafeCollectionCount $SearchSnippets) -eq 0) { return $null }

    if (-not (Test-OllamaApiReady)) {
        Write-ToolkitLog 'Ollama API unavailable; cannot synthesize performance rating.' Yellow
        return $null
    }

    $summarizer = Get-DescriptionSummarizerModel -ExcludeModelName $ModelName
    if (-not $summarizer) {
        Write-ToolkitLog "No local model available to rate '$ModelName' performance." Yellow
        return $null
    }

    $source = ($SearchSnippets | Select-Object -First 8) -join "`n`n"
    if ($source.Length -gt 2800) {
        $source = $source.Substring(0, 2800)
    }

    $prompt = @"
Using ONLY the web search snippets below, write one concise performance rating for this AI model.
Rules:
- Maximum $MaxChars characters
- Plain text only (no quotes, labels, markdown, or line breaks)
- Include benchmark scores or tiers when present (e.g. MMLU, GPQA, HumanEval, Artificial Analysis)
- If snippets disagree, summarize the typical range; do not invent numbers
- If no scores are found, give a short qualitative tier (e.g. "Strong coding · mid-size")

Model: $ModelName
Library: $LibraryName
Web search snippets:
$source

Performance rating:
"@

    try {
        Write-ToolkitLog "Rating '$ModelName' performance with '$summarizer'..." Cyan
        $raw = Invoke-ToolkitOllamaGenerate -Model $summarizer -Prompt $prompt -MaxPredict 96
        Invoke-ToolkitUiPump
        return Normalize-PerformanceRatingText -Text $raw -MaxLength $MaxChars
    }
    catch {
        Write-ToolkitLog "LLM performance rating failed for '$ModelName': $($_.Exception.Message)" Yellow
        return $null
    }
}

function Test-PerformanceNeedsRefresh {
    param(
        $Entry,
        [switch]$Force
    )

    if ($Force) { return $true }
    if (-not $Entry) { return $true }
    if (-not (Get-PerformanceEntryValue -Entry $Entry -Name 'FetchedAt')) { return $true }

    $rating = Get-PerformanceEntryValue -Entry $Entry -Name 'Rating'
    if ($rating) {
        try {
            $fetched = [datetime](Get-PerformanceEntryValue -Entry $Entry -Name 'FetchedAt')
            return ((Get-Date) - $fetched).TotalDays -ge $Script:PerformanceCacheDays
        }
        catch {
            return $true
        }
    }

    $error = Get-PerformanceEntryValue -Entry $Entry -Name 'Error'
    if ($error) {
        try {
            $fetched = [datetime](Get-PerformanceEntryValue -Entry $Entry -Name 'FetchedAt')
            return ((Get-Date) - $fetched).TotalDays -ge 1
        }
        catch {
            return $true
        }
    }

    return $true
}

function Update-ModelPerformanceRatingCache {
    param(
        [string]$ModelName,
        [switch]$Force
    )

    if (-not (Test-AiPerformanceRatingsEnabled)) {
        return [pscustomobject]@{
            Model   = $ModelName
            Updated = $false
            Entry   = $null
        }
    }

    $store = Get-ModelPerformanceStore
    if (-not $store.Models -or $store.Models -isnot [System.Collections.IDictionary]) {
        $store.Models = [ordered]@{}
    }

    $existing = $null
    if ($store.Models.Contains($ModelName)) {
        $existing = $store.Models[$ModelName]
    }

    if (-not (Test-PerformanceNeedsRefresh -Entry $existing -Force:$Force)) {
        return [pscustomobject]@{
            Model   = $ModelName
            Updated = $false
            Entry   = $existing
        }
    }

    $libraryName = $ModelName
    foreach ($candidate in (Get-OllamaLibraryNameCandidates -ModelName $ModelName)) {
        $libraryName = $candidate
        break
    }

    Write-ToolkitLog "Searching web for '$ModelName' performance ratings..." Cyan
    $queries = Get-ModelPerformanceSearchQueries -LibraryName $libraryName
    $snippets = @(Get-ModelPerformanceSearchSnippets -LibraryName $libraryName)
    $rating = $null
    $summaryModel = $null
    $errorText = $null

    if ((Get-SafeCollectionCount $snippets) -gt 0) {
        $rating = New-ModelPerformanceRatingViaLlm -ModelName $ModelName `
            -LibraryName $libraryName -SearchSnippets $snippets
        if ($rating) {
            $summaryModel = Get-DescriptionSummarizerModel -ExcludeModelName $ModelName
        }
        else {
            $errorText = 'LLM could not synthesize a performance rating from search results.'
        }
    }
    else {
        $errorText = 'No benchmark search results found on the web.'
    }

    $entry = [ordered]@{
        Rating        = $rating
        LibraryName   = $libraryName
        FetchedAt     = (Get-Date).ToString('o')
        SummaryModel  = $summaryModel
        SearchQueries = $queries
        Error         = if ($rating) { $null } else { $errorText }
    }

    $store.Models[$ModelName] = $entry
    Save-ModelPerformanceStore -Store $store

    return [pscustomobject]@{
        Model   = $ModelName
        Updated = $true
        Entry   = $entry
    }
}

function Sync-ModelPerformanceRatings {
    param(
        [switch]$Force,
        [string[]]$ModelNames
    )

    if (-not (Test-AiPerformanceRatingsEnabled)) {
        return [pscustomobject]@{
            Updated = 0
            Skipped = 0
            Total   = 0
            Results = @()
        }
    }

    $modelNames = @($ModelNames)
    if ((Get-SafeCollectionCount $modelNames) -eq 0) {
        $modelNames = @(Get-ToolkitLocalOllamaModels | ForEach-Object { $_.name })
    }

    $updated = 0
    $skipped = 0
    $results = @()

    foreach ($name in $modelNames) {
        $result = Update-ModelPerformanceRatingCache -ModelName $name -Force:$Force
        $results += $result
        if ($result.Updated) { $updated++ } else { $skipped++ }
        Invoke-ToolkitUiPump
    }

    return [pscustomobject]@{
        Updated = $updated
        Skipped = $skipped
        Total   = (Get-SafeCollectionCount $modelNames)
        Results = $results
    }
}

function Get-TaggedModelPerformanceEntries {
    param(
        [string]$LibraryName,
        $StoreModels
    )

    if (-not $StoreModels -or [string]::IsNullOrWhiteSpace($LibraryName)) { return @() }

    $prefix = "${LibraryName}:"
    $matches = @()
    foreach ($key in @($StoreModels.Keys)) {
        if ($key -in $Script:HashtableMetaProps) { continue }
        $keyText = [string]$key
        if ($keyText.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            $matches += [pscustomobject]@{
                Key   = $keyText
                Entry = $StoreModels[$key]
            }
        }
    }
    return $matches
}

function Select-PreferredModelPerformanceEntry {
    param($Entries)

    if (-not $Entries -or @($Entries).Count -eq 0) { return $null }

    $latest = @($Entries | Where-Object { $_.Key -match ':latest$' } | Select-Object -First 1)
    if ((Get-SafeCollectionCount $latest) -gt 0) { return $latest[0].Entry }

    $rated = @($Entries | Where-Object {
        Get-PerformanceEntryValue -Entry $_.Entry -Name 'Rating'
    } | Select-Object -First 1)
    if ((Get-SafeCollectionCount $rated) -gt 0) { return $rated[0].Entry }

    $entries = @($Entries)
    if ((Get-SafeCollectionCount $entries) -eq 0) { return $null }
    return $entries[0].Entry
}

function Get-ModelPerformanceEntry {
    param([string]$ModelName)

    if ([string]::IsNullOrWhiteSpace($ModelName)) { return $null }

    $store = Get-ModelPerformanceStore
    if (-not $store.Models) { return $null }

    if ($store.Models.Contains($ModelName)) {
        return $store.Models[$ModelName]
    }

    foreach ($candidate in (Get-OllamaLibraryNameCandidates -ModelName $ModelName)) {
        if ($store.Models.Contains($candidate)) {
            return $store.Models[$candidate]
        }
    }

    foreach ($candidate in (Get-OllamaLibraryNameCandidates -ModelName $ModelName)) {
        $tagged = @(Get-TaggedModelPerformanceEntries -LibraryName $candidate -StoreModels $store.Models)
        $preferred = Select-PreferredModelPerformanceEntry -Entries $tagged
        if ($preferred) { return $preferred }
    }

    return $null
}

function Get-ModelPerformanceRating {
    param([string]$ModelName)

    if (-not (Test-AiPerformanceRatingsEnabled)) { return '-' }

    $entry = Get-ModelPerformanceEntry -ModelName $ModelName
    if (-not $entry) { return '-' }

    $rating = Get-PerformanceEntryValue -Entry $entry -Name 'Rating'
    if ($rating) { return [string]$rating }

    $error = Get-PerformanceEntryValue -Entry $entry -Name 'Error'
    if ($error) { return '(pending)' }

    return '-'
}

function Get-MissingPerformanceRatingModels {
    param([string[]]$ModelNames)

    if (-not (Test-AiPerformanceRatingsEnabled)) { return @() }

    $modelNames = @($ModelNames)
    if ((Get-SafeCollectionCount $modelNames) -eq 0) {
        $modelNames = @(Get-ToolkitLocalOllamaModels | ForEach-Object { $_.name })
    }

    $missing = @()
    foreach ($name in $modelNames) {
        $entry = Get-ModelPerformanceEntry -ModelName $name
        if (-not $entry -or (Test-PerformanceNeedsRefresh -Entry $entry)) {
            $missing += $name
        }
    }
    return @($missing)
}

function Get-MissingCatalogPerformanceModels {
    param(
        [string[]]$ModelNames,
        [int]$MaxCount = 40
    )

    if (-not (Test-AiPerformanceRatingsEnabled)) { return @() }

    $modelNames = @($ModelNames)
    if ((Get-SafeCollectionCount $modelNames) -eq 0) {
        if (-not (Get-Command Get-LibraryCatalogStoreItems -ErrorAction SilentlyContinue)) {
            return @()
        }
        $modelNames = @(Get-LibraryCatalogStoreItems | ForEach-Object {
            [string](Get-LibraryCatalogEntryValue -Entry $_ -Name 'Name')
        } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }

    $missing = @()
    foreach ($name in $modelNames) {
        $entry = Get-ModelPerformanceEntry -ModelName $name
        if (-not $entry -or (Test-PerformanceNeedsRefresh -Entry $entry)) {
            $missing += $name
            if ($MaxCount -gt 0 -and (Get-SafeCollectionCount $missing) -ge $MaxCount) { break }
        }
    }
    return @($missing)
}