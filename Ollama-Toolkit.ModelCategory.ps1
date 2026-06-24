#Requires -Version 5.1
Set-StrictMode -Version Latest

if (-not $Script:ToolkitRoot) {
    $Script:ToolkitRoot = $PSScriptRoot
}

if (-not (Get-Command Invoke-ToolkitWebSearch -ErrorAction SilentlyContinue)) {
    . (Join-Path $Script:ToolkitRoot 'Ollama-Toolkit.Core.ps1')
}
if (-not (Get-Command Invoke-ToolkitOllamaGenerate -ErrorAction SilentlyContinue)) {
    . (Join-Path $Script:ToolkitRoot 'Ollama-Toolkit.ModelCatalog.ps1')
}

if (-not (Get-Variable -Name 'HashtableMetaProps' -Scope Script -ErrorAction SilentlyContinue)) {
    $Script:HashtableMetaProps = @(
        'Count', 'Keys', 'Values', 'IsReadOnly', 'IsFixedSize', 'SyncRoot', 'IsSynchronized'
    )
}

$Script:ModelUsageCategoryFile = Join-Path $Script:ConfigDir 'model-usage-categories.json'
$Script:ModelUsageCategoryStoreCache = $null

function Clear-ModelUsageCategoryStoreCache {
    $Script:ModelUsageCategoryStoreCache = $null
}
$Script:UsageCategoryCacheDays = 30
$Script:UsageCategorySearchResultLimit = 8
$Script:UsageCategoryMaxSnippets = 20

function Get-OllamaLibraryUsageCategories {
    if (Get-Command Get-OllamaLibrarySpecializationOrder -ErrorAction SilentlyContinue) {
        return @((Get-OllamaLibrarySpecializationOrder).Keys)
    }
    return @(
        'General Chat', 'Coding', 'Reasoning', 'Vision', 'Multimodal',
        'Embedding', 'Tools', 'Domain Specific', 'Other'
    )
}

function New-EmptyUsageCategoryStore {
    return [ordered]@{
        Version     = 1
        LastUpdated = (Get-Date).ToString('o')
        Models      = [ordered]@{}
    }
}

function ConvertTo-UsageCategoryHashtable {
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

function Get-ModelUsageCategoryStore {
    if ($Script:ModelUsageCategoryStoreCache) {
        return $Script:ModelUsageCategoryStoreCache
    }

    Ensure-ConfigDirectory
    if (-not (Test-Path -LiteralPath $Script:ModelUsageCategoryFile)) {
        $Script:ModelUsageCategoryStoreCache = New-EmptyUsageCategoryStore
        return $Script:ModelUsageCategoryStoreCache
    }

    try {
        $data = Get-Content -LiteralPath $Script:ModelUsageCategoryFile -Raw | ConvertFrom-Json
        $Script:ModelUsageCategoryStoreCache = ConvertTo-UsageCategoryHashtable -Store $data
        return $Script:ModelUsageCategoryStoreCache
    }
    catch {
        Write-ToolkitLog "Usage category store corrupt; starting fresh: $($_.Exception.Message)" Yellow
        $Script:ModelUsageCategoryStoreCache = New-EmptyUsageCategoryStore
        return $Script:ModelUsageCategoryStoreCache
    }
}

function Get-UsageCategoryEntryValue {
    param(
        $Entry,
        [string]$Name
    )

    if ($null -eq $Entry) { return $null }
    if ($Entry -is [System.Collections.IDictionary]) {
        if ($Entry.Contains($Name)) { return $Entry[$Name] }
        if ($Name -eq 'Category' -and $Entry.Contains('Specialization')) { return $Entry['Specialization'] }
        return $null
    }
    $prop = $Entry.PSObject.Properties[$Name]
    if ($prop) { return $prop.Value }
    if ($Name -eq 'Category') {
        $legacy = $Entry.PSObject.Properties['Specialization']
        if ($legacy) { return $legacy.Value }
    }
    return $null
}

function Save-ModelUsageCategoryStore {
    param($Store)

    Ensure-ConfigDirectory
    $normalized = ConvertTo-UsageCategoryHashtable -Store $Store
    $modelsExport = [ordered]@{}
    foreach ($key in @($normalized.Models.Keys)) {
        $val = $normalized.Models[$key]
        $modelsExport[$key] = [ordered]@{
            Category          = Get-UsageCategoryEntryValue -Entry $val -Name 'Category'
            UsageSummary      = Get-UsageCategoryEntryValue -Entry $val -Name 'UsageSummary'
            LibraryName       = Get-UsageCategoryEntryValue -Entry $val -Name 'LibraryName'
            FetchedAt         = Get-UsageCategoryEntryValue -Entry $val -Name 'FetchedAt'
            SummaryModel      = Get-UsageCategoryEntryValue -Entry $val -Name 'SummaryModel'
            SearchQueries     = Get-UsageCategoryEntryValue -Entry $val -Name 'SearchQueries'
            WebAiClassified   = Get-UsageCategoryEntryValue -Entry $val -Name 'WebAiClassified'
            Error             = Get-UsageCategoryEntryValue -Entry $val -Name 'Error'
        }
    }

    $payload = [ordered]@{
        Version     = 1
        LastUpdated = (Get-Date).ToString('o')
        Models      = $modelsExport
    }
    $payload | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Script:ModelUsageCategoryFile -Encoding UTF8
    Clear-ModelUsageCategoryStoreCache
}

function Normalize-UsageCategoryName {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) { return 'Other' }

    $allowed = @(Get-OllamaLibraryUsageCategories)
    $trimmed = $Text.Trim().Trim('"', '''', '.')
    foreach ($cat in $allowed) {
        if ($trimmed -eq $cat) { return $cat }
    }

    $lower = $trimmed.ToLowerInvariant()
    foreach ($cat in $allowed) {
        if ($lower -eq $cat.ToLowerInvariant()) { return $cat }
    }

    $aliases = [ordered]@{
        'chat'            = 'General Chat'
        'general'           = 'General Chat'
        'assistant'         = 'General Chat'
        'code'              = 'Coding'
        'programming'       = 'Coding'
        'developer'         = 'Coding'
        'reasoning'         = 'Reasoning'
        'math'              = 'Reasoning'
        'thinking'          = 'Reasoning'
        'image'             = 'Vision'
        'visual'            = 'Vision'
        'ocr'               = 'Vision'
        'multimodal'        = 'Multimodal'
        'audio'             = 'Multimodal'
        'embed'             = 'Embedding'
        'embeddings'        = 'Embedding'
        'retrieval'         = 'Embedding'
        'tool use'          = 'Tools'
        'function calling'  = 'Tools'
        'agent'             = 'Tools'
        'domain'            = 'Domain Specific'
        'medical'           = 'Domain Specific'
        'legal'             = 'Domain Specific'
        'finance'           = 'Domain Specific'
    }
    foreach ($alias in $aliases.Keys) {
        if ($lower.Contains($alias)) { return $aliases[$alias] }
    }

    return 'Other'
}

function Get-ModelUsageSearchQueries {
    param(
        [string]$LibraryName,
        [string]$Description = '',
        [string]$CapabilityTags = ''
    )

    $safe = ($LibraryName -replace '[^\w\.\-:+]', ' ').Trim()
    $context = $safe
    if ($Description) {
        $shortDesc = ($Description -replace '\s+', ' ').Trim()
        if ($shortDesc.Length -gt 80) { $shortDesc = $shortDesc.Substring(0, 80) }
        if ($shortDesc) { $context = "$safe $shortDesc" }
    }

    return @(
        "$context ollama model primary use case real world applications",
        "$safe LLM what is it used for coding chat vision embedding",
        "$safe ollama.com library model capabilities and intended usage"
    )
}

function Get-ModelUsageSearchSnippets {
    param(
        [string]$LibraryName,
        [string]$Description = '',
        [string]$CapabilityTags = ''
    )

    $queries = Get-ModelUsageSearchQueries -LibraryName $LibraryName `
        -Description $Description -CapabilityTags $CapabilityTags
    $snippets = New-Object System.Collections.Generic.List[string]

    if ($CapabilityTags) {
        [void]$snippets.Add("ollama.com capability tags: $CapabilityTags")
    }
    if ($Description) {
        $desc = ($Description -replace '\s+', ' ').Trim()
        if ($desc.Length -gt 240) { $desc = $desc.Substring(0, 240) + '...' }
        [void]$snippets.Add("ollama.com listing: $desc")
    }

    foreach ($query in $queries) {
        foreach ($hit in @(Invoke-ToolkitWebSearch -Query $query -MaxResults $Script:UsageCategorySearchResultLimit)) {
            if ((Get-SafeCollectionCount $snippets) -ge $Script:UsageCategoryMaxSnippets) { break }
            if ($hit -and -not ($snippets -contains $hit)) {
                [void]$snippets.Add($hit)
            }
        }
        Invoke-ToolkitUiPump
        if ((Get-SafeCollectionCount $snippets) -ge 12) { break }
    }

    return @($snippets)
}

function New-ModelUsageCategoryViaLlm {
    param(
        [string]$ModelName,
        [string]$LibraryName,
        [string[]]$SearchSnippets,
        [string]$Description = '',
        [string]$CapabilityTags = ''
    )

    if ((Get-SafeCollectionCount $SearchSnippets) -eq 0) { return $null }

    if (-not (Test-OllamaApiReady)) {
        Write-ToolkitLog 'Ollama API unavailable; cannot classify model usage.' Yellow
        return $null
    }

    $summarizer = Get-DescriptionSummarizerModel -ExcludeModelName $ModelName
    if (-not $summarizer) {
        Write-ToolkitLog "No local model available to classify '$ModelName' usage." Yellow
        return $null
    }

    $categories = (Get-OllamaLibraryUsageCategories) -join ', '
    $source = ($SearchSnippets | Select-Object -First 16) -join "`n`n"
    if ($source.Length -gt 4200) {
        $source = $source.Substring(0, 4200)
    }

    $prompt = @"
Using ONLY the evidence below (web search snippets and ollama listing text), determine the PRIMARY real-world usage category for this LLM.

Pick exactly one category from: $categories

Rules:
- Base the category on documented use cases, model card text, and reputable reviews - not just the model name
- Vision = image understanding/generation; Multimodal = multiple input types (e.g. audio+text); Embedding = vector search/RAG embeddings
- Coding = software development; Reasoning = math/logic/thinking models; Tools = agents/function calling
- Domain Specific = specialized fields (medical, legal, finance, etc.)
- General Chat = broad assistant/conversation when no stronger specialty applies
- Reply on two lines exactly:
CATEGORY: <one category from the list>
USAGE: <one sentence, max 120 chars, plain text, describing real-world usage>

Model: $ModelName
Library: $LibraryName
Evidence:
$source

Classification:
"@

    try {
        Write-ToolkitLog "Classifying '$ModelName' usage with '$summarizer' (web search)..." Cyan
        $raw = Invoke-ToolkitOllamaGenerate -Model $summarizer -Prompt $prompt -MaxPredict 120
        Invoke-ToolkitUiPump

        $category = $null
        $usage = $null
        foreach ($line in ($raw -split "`n")) {
            if ($line -match '^\s*CATEGORY\s*:\s*(.+?)\s*$') {
                $category = Normalize-UsageCategoryName -Text $Matches[1]
            }
            elseif ($line -match '^\s*USAGE\s*:\s*(.+?)\s*$') {
                $usage = ($Matches[1] -replace '\s+', ' ').Trim()
                if ($usage.Length -gt 120) { $usage = $usage.Substring(0, 117) + '...' }
            }
        }

        if (-not $category) {
            $category = Normalize-UsageCategoryName -Text $raw
        }

        return [pscustomobject]@{
            Category     = $category
            UsageSummary = $usage
            SummaryModel = $summarizer
        }
    }
    catch {
        Write-ToolkitLog "LLM usage classification failed for '$ModelName': $($_.Exception.Message)" Yellow
        return $null
    }
}

function Test-UsageCategoryNeedsRefresh {
    param(
        $Entry,
        [switch]$Force
    )

    if ($Force) { return $true }
    if (-not $Entry) { return $true }
    if (-not (Get-UsageCategoryEntryValue -Entry $Entry -Name 'FetchedAt')) { return $true }

    $category = Get-UsageCategoryEntryValue -Entry $Entry -Name 'Category'
    if ($category) {
        try {
            $fetched = [datetime](Get-UsageCategoryEntryValue -Entry $Entry -Name 'FetchedAt')
            return ((Get-Date) - $fetched).TotalDays -ge $Script:UsageCategoryCacheDays
        }
        catch {
            return $true
        }
    }

    $error = Get-UsageCategoryEntryValue -Entry $Entry -Name 'Error'
    if ($error) {
        try {
            $fetched = [datetime](Get-UsageCategoryEntryValue -Entry $Entry -Name 'FetchedAt')
            return ((Get-Date) - $fetched).TotalDays -ge 1
        }
        catch {
            return $true
        }
    }

    return $true
}

function Update-ModelUsageCategoryCache {
    param(
        [string]$ModelName,
        [string]$Description = '',
        [string]$CapabilityTags = '',
        [switch]$Force
    )

    $store = Get-ModelUsageCategoryStore
    if (-not $store.Models -or $store.Models -isnot [System.Collections.IDictionary]) {
        $store.Models = [ordered]@{}
    }

    $existing = $null
    if ($store.Models.Contains($ModelName)) {
        $existing = $store.Models[$ModelName]
    }

    if (-not (Test-UsageCategoryNeedsRefresh -Entry $existing -Force:$Force)) {
        return [pscustomobject]@{
            Model   = $ModelName
            Updated = $false
            Entry   = $existing
        }
    }

    $libraryName = $ModelName
    if (Get-Command Get-OllamaLibraryNameCandidates -ErrorAction SilentlyContinue) {
        foreach ($candidate in (Get-OllamaLibraryNameCandidates -ModelName $ModelName)) {
            $libraryName = $candidate
            break
        }
    }

    Write-ToolkitLog "Searching web for '$ModelName' real-world usage..." Cyan
    $queries = Get-ModelUsageSearchQueries -LibraryName $libraryName `
        -Description $Description -CapabilityTags $CapabilityTags
    $snippets = @(Get-ModelUsageSearchSnippets -LibraryName $libraryName `
        -Description $Description -CapabilityTags $CapabilityTags)

    $category = $null
    $usageSummary = $null
    $summaryModel = $null
    $webAiClassified = $false
    $errorText = $null

    if ((Get-SafeCollectionCount $snippets) -gt 0) {
        $result = New-ModelUsageCategoryViaLlm -ModelName $ModelName -LibraryName $libraryName `
            -SearchSnippets $snippets -Description $Description -CapabilityTags $CapabilityTags
        if ($result -and $result.Category) {
            $category = $result.Category
            $usageSummary = $result.UsageSummary
            $summaryModel = $result.SummaryModel
            $webAiClassified = $true
        }
        else {
            $errorText = 'LLM could not classify usage from web search results.'
        }
    }
    else {
        $errorText = 'No usage search results found on the web.'
    }

    if (-not $category -and (Get-Command Resolve-OllamaLibrarySpecialization -ErrorAction SilentlyContinue)) {
        $category = Resolve-OllamaLibrarySpecialization -ModelName $libraryName `
            -Description $Description -Tags $CapabilityTags
        $usageSummary = 'Heuristic fallback (web/AI classification unavailable).'
    }

    $entry = [ordered]@{
        Category          = $category
        UsageSummary      = $usageSummary
        LibraryName       = $libraryName
        FetchedAt         = (Get-Date).ToString('o')
        SummaryModel      = $summaryModel
        SearchQueries     = $queries
        WebAiClassified   = $webAiClassified
        Error             = if ($webAiClassified -or $category) { $null } else { $errorText }
    }

    $store.Models[$ModelName] = $entry
    Save-ModelUsageCategoryStore -Store $store

    return [pscustomobject]@{
        Model   = $ModelName
        Updated = $true
        Entry   = $entry
    }
}

function Sync-ModelUsageCategories {
    param(
        [switch]$Force,
        [string[]]$ModelNames,
        [hashtable]$CatalogContext = @{}
    )

    $modelNames = @($ModelNames)
    if ((Get-SafeCollectionCount $modelNames) -eq 0) {
        $modelNames = @(Get-ToolkitLocalOllamaModels | ForEach-Object { $_.name })
    }

    $updated = 0
    $skipped = 0
    $results = @()

    foreach ($name in $modelNames) {
        $ctx = $null
        if ($CatalogContext.ContainsKey($name)) { $ctx = $CatalogContext[$name] }
        $desc = ''
        $tags = ''
        if ($ctx) {
            $desc = [string]$ctx.Description
            $tags = [string]$ctx.CapabilityTags
        }

        $result = Update-ModelUsageCategoryCache -ModelName $name -Description $desc `
            -CapabilityTags $tags -Force:$Force
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

function Get-TaggedModelUsageCategoryEntries {
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

function Select-PreferredModelUsageCategoryEntry {
    param($Entries)

    if (-not $Entries -or @($Entries).Count -eq 0) { return $null }

    $webAi = @($Entries | Where-Object {
        Get-UsageCategoryEntryValue -Entry $_.Entry -Name 'WebAiClassified'
    } | Select-Object -First 1)
    if ((Get-SafeCollectionCount $webAi) -gt 0) { return $webAi[0].Entry }

    $latest = @($Entries | Where-Object { $_.Key -match ':latest$' } | Select-Object -First 1)
    if ((Get-SafeCollectionCount $latest) -gt 0) { return $latest[0].Entry }

    $entries = @($Entries)
    if ((Get-SafeCollectionCount $entries) -eq 0) { return $null }
    return $entries[0].Entry
}

function Get-ModelUsageCategoryEntry {
    param([string]$ModelName)

    if ([string]::IsNullOrWhiteSpace($ModelName)) { return $null }

    $store = Get-ModelUsageCategoryStore
    if (-not $store.Models) { return $null }

    if ($store.Models.Contains($ModelName)) {
        return $store.Models[$ModelName]
    }

    if (Get-Command Get-OllamaLibraryNameCandidates -ErrorAction SilentlyContinue) {
        foreach ($candidate in (Get-OllamaLibraryNameCandidates -ModelName $ModelName)) {
            if ($store.Models.Contains($candidate)) {
                return $store.Models[$candidate]
            }
        }

        foreach ($candidate in (Get-OllamaLibraryNameCandidates -ModelName $ModelName)) {
            $tagged = @(Get-TaggedModelUsageCategoryEntries -LibraryName $candidate -StoreModels $store.Models)
            $preferred = Select-PreferredModelUsageCategoryEntry -Entries $tagged
            if ($preferred) { return $preferred }
        }
    }

    return $null
}

function Get-ModelUsageCategoryDisplay {
    param(
        [string]$ModelName,
        [string]$FallbackDescription = '',
        [string]$FallbackCapabilityTags = ''
    )

    $entry = Get-ModelUsageCategoryEntry -ModelName $ModelName
    if ($entry) {
        $category = Get-UsageCategoryEntryValue -Entry $entry -Name 'Category'
        if ($category) { return [string]$category }
        if (Get-UsageCategoryEntryValue -Entry $entry -Name 'Error') { return '(pending)' }
    }

    if (Get-Command Resolve-OllamaLibrarySpecialization -ErrorAction SilentlyContinue) {
        $libraryName = $ModelName
        if (Get-Command Get-OllamaLibraryNameCandidates -ErrorAction SilentlyContinue) {
            foreach ($candidate in (Get-OllamaLibraryNameCandidates -ModelName $ModelName)) {
                $libraryName = $candidate
                break
            }
        }
        return Resolve-OllamaLibrarySpecialization -ModelName $libraryName `
            -Description $FallbackDescription -Tags $FallbackCapabilityTags
    }

    return '-'
}

function Get-MissingUsageCategoryModels {
    param(
        [string[]]$ModelNames,
        [int]$MaxCount = 40
    )

    $modelNames = @($ModelNames)
    if ((Get-SafeCollectionCount $modelNames) -eq 0) {
        $modelNames = @(Get-ToolkitLocalOllamaModels | ForEach-Object { $_.name })
        if (Get-Command Get-LibraryCatalogStoreItems -ErrorAction SilentlyContinue) {
            $modelNames += @(Get-LibraryCatalogStoreItems | ForEach-Object {
                [string](Get-LibraryCatalogEntryValue -Entry $_ -Name 'Name')
            })
        }
        $modelNames = @($modelNames | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
    }

    $missing = @()
    foreach ($name in $modelNames) {
        $entry = Get-ModelUsageCategoryEntry -ModelName $name
        if (-not $entry -or (Test-UsageCategoryNeedsRefresh -Entry $entry)) {
            $missing += $name
            if ($MaxCount -gt 0 -and (Get-SafeCollectionCount $missing) -ge $MaxCount) { break }
        }
    }
    return @($missing)
}

function Get-MissingCatalogUsageCategoryModels {
    param(
        [string[]]$ModelNames,
        [int]$MaxCount = 40
    )

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
        $entry = Get-ModelUsageCategoryEntry -ModelName $name
        if (-not $entry -or (Test-UsageCategoryNeedsRefresh -Entry $entry)) {
            $missing += $name
            if ($MaxCount -gt 0 -and (Get-SafeCollectionCount $missing) -ge $MaxCount) { break }
        }
    }
    return @($missing)
}

function New-CatalogUsageContextMap {
    param([object[]]$Items)

    $map = @{}
    foreach ($item in @($Items)) {
        $name = if ($item.Name) { [string]$item.Name } else {
            [string](Get-LibraryCatalogEntryValue -Entry $item -Name 'Name')
        }
        if (-not $name) { continue }
        $map[$name] = [pscustomobject]@{
            Description     = [string](Get-LibraryCatalogEntryValue -Entry $item -Name 'Description')
            CapabilityTags  = [string](Get-LibraryCatalogEntryValue -Entry $item -Name 'Tags')
        }
    }
    return $map
}