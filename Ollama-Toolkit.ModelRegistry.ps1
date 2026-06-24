#Requires -Version 5.1
Set-StrictMode -Version Latest

if (-not $Script:ToolkitRoot) {
    $Script:ToolkitRoot = $PSScriptRoot
}

if (-not (Get-Command ConvertTo-ToolkitJson -ErrorAction SilentlyContinue)) {
    . (Join-Path $Script:ToolkitRoot 'Ollama-Toolkit.Core.ps1')
}
if (-not (Get-Command Invoke-ToolkitOllamaGenerate -ErrorAction SilentlyContinue)) {
    . (Join-Path $Script:ToolkitRoot 'Ollama-Toolkit.ModelCatalog.ps1')
}

$Script:OllamaLibraryCatalogUrl = 'https://ollama.com/library'
if (-not $Script:OllamaLibraryBaseUrl) {
    $Script:OllamaLibraryBaseUrl = 'https://ollama.com/library'
}
$Script:LibraryCatalogCacheMinutes = 30
$Script:LibraryCatalogCache = @{}
$Script:ModelTagsCache = @{}
$Script:LibraryFileSizeCache = @{}
$Script:LibraryCatalogStoreFile = Join-Path $Script:ConfigDir 'library-catalog-store.json'
$Script:LibraryCatalogStoreDays = 7

function Get-AllInstalledOllamaModels {
    param(
        [int]$TimeoutSec = 3,
        [switch]$ForceRefresh
    )

    $models = Get-OllamaTagsApiModels -TimeoutSec $TimeoutSec -ForceRefresh:$ForceRefresh
    return @($models | Sort-Object -Property name)
}

function Get-InstalledModelNameSet {
    $set = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    try {
        foreach ($model in @(Get-AllInstalledOllamaModels)) {
            if ($model.name) { [void]$set.Add([string]$model.name) }
        }
    }
    catch {
        Write-ToolkitLog "Installed model lookup failed: $($_.Exception.Message)" Yellow
    }
    return $set
}

function Test-LibraryModelInstalled {
    param(
        [string]$LibraryName,
        $InstalledNames
    )

    if ($null -eq $InstalledNames) { return $false }
    if ($InstalledNames.Contains($LibraryName)) { return $true }
    foreach ($name in $InstalledNames) {
        if ($name.StartsWith("${LibraryName}:", [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

function Format-OllamaModelSize {
    param([object]$SizeBytes)

    $size = [double]$SizeBytes
    if ($size -le 0) { return '-' }
    if ($size -ge 1GB) { return ('{0:N1} GB' -f ($size / 1GB)) }
    if ($size -ge 1MB) { return ('{0:N0} MB' -f ($size / 1MB)) }
    return ('{0:N0} B' -f $size)
}

function Get-OllamaModelParameterSizeDisplay {
    param($Model)

    if ($Model.details -and $Model.details.parameter_size) {
        return [string]$Model.details.parameter_size
    }

    $name = [string]$Model.name
    if ($name -match ':([^:]+)$') {
        $tag = $Matches[1]
        if ($tag -eq 'latest') { return 'latest' }
        if ($tag -match '^(\d+(?:\.\d+)?)([bkmBKM])') {
            return ('{0}{1}' -f $Matches[1], $Matches[2]).ToUpperInvariant()
        }
    }

    return '-'
}

function Get-OllamaModelTagDisplay {
    param($Model)

    $name = [string]$Model.name
    if ($name -match '^([^:]+):(.+)$') {
        return $Matches[2]
    }
    if ($name) { return 'latest' }
    return '-'
}

function Convert-OllamaFileSizeLabelToBytes {
    param([string]$Label)

    $text = ($Label -replace '\s+', '').ToUpperInvariant()
    if ($text -match '^([\d.]+)GB$') { return [double]$Matches[1] * 1GB }
    if ($text -match '^([\d.]+)MB$') { return [double]$Matches[1] * 1MB }
    if ($text -match '^([\d.]+)KB$') { return [double]$Matches[1] * 1KB }
    return $null
}

function Format-OllamaLibraryFileSizeRange {
    param([string[]]$SizeLabels)

    $SizeLabels = @($SizeLabels)
    if ((Get-SafeCollectionCount $SizeLabels) -eq 0) { return '-' }

    $bytes = New-Object System.Collections.Generic.List[double]
    foreach ($label in $SizeLabels) {
        $b = Convert-OllamaFileSizeLabelToBytes -Label $label
        if ($null -ne $b) { [void]$bytes.Add($b) }
    }

    if ((Get-SafeCollectionCount $bytes) -eq 0) {
        return (($SizeLabels | Select-Object -Unique) -join ' ')
    }

    $min = ($bytes | Measure-Object -Minimum).Minimum
    $max = ($bytes | Measure-Object -Maximum).Maximum
    $minText = Format-OllamaModelSize -SizeBytes $min
    $maxText = Format-OllamaModelSize -SizeBytes $max
    if ($minText -eq $maxText) { return $minText }
    return "$minText-$maxText"
}

function Test-OllamaLibraryCanonicalTagName {
    param(
        [string]$LibraryName,
        [string]$TagName
    )

    if ($TagName -eq "$LibraryName`:latest") { return $true }
    if ($TagName -match "^$([regex]::Escape($LibraryName)):(\d+(?:\.\d+)?[bkm])$") { return $true }
    return $false
}

function Parse-OllamaLibraryTagFileSizesFromHtml {
    param(
        [string]$Html,
        [string]$LibraryName
    )

    if ([string]::IsNullOrWhiteSpace($Html) -or [string]::IsNullOrWhiteSpace($LibraryName)) {
        return @()
    }

    $escaped = [regex]::Escape($LibraryName)
    $sizes = New-Object System.Collections.Generic.List[string]
    $blocks = [regex]::Split($Html, '(?=href="/library/' + $escaped + ':)')
    foreach ($block in $blocks) {
        if ($block -notmatch 'href="/library/(' + $escaped + ':[^"]+)"') { continue }

        $tag = $Matches[1].Trim()
        if (-not (Test-OllamaLibraryCanonicalTagName -LibraryName $LibraryName -TagName $tag)) {
            continue
        }

        $chunk = $block
        if ($chunk.Length -gt 2500) { $chunk = $chunk.Substring(0, 2500) }
        if ($chunk -match '([\d.]+\s*(?:GB|MB))(?:\s*</|\s+\d+K|\s+&bull;|\s+context)') {
            $size = ($Matches[1] -replace '\s+', ' ').Trim().ToUpperInvariant()
            if ($size -notin $sizes) { [void]$sizes.Add($size) }
        }
    }

    return @($sizes)
}

function Get-OllamaLibraryFileSizeLabelFromWeb {
    param(
        [string]$LibraryName,
        [switch]$Force
    )

    if ([string]::IsNullOrWhiteSpace($LibraryName)) { return '-' }
    if (-not $Script:LibraryFileSizeCache) { $Script:LibraryFileSizeCache = @{} }
    if (-not $Force -and $Script:LibraryFileSizeCache.ContainsKey($LibraryName)) {
        return [string]$Script:LibraryFileSizeCache[$LibraryName]
    }

    $url = "$Script:OllamaLibraryBaseUrl/$LibraryName/tags"
    try {
        $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 45
        $sizes = @(Parse-OllamaLibraryTagFileSizesFromHtml -Html $response.Content -LibraryName $LibraryName)
        $label = Format-OllamaLibraryFileSizeRange -SizeLabels $sizes
    }
    catch {
        $label = '-'
    }

    $Script:LibraryFileSizeCache[$LibraryName] = $label
    Invoke-ToolkitUiPump
    return $label
}

function Update-LibraryCatalogFileSizes {
    param(
        [object[]]$Items,
        [switch]$Force
    )

    foreach ($item in @($Items)) {
        $name = [string]$item.Name
        if (-not $name) { continue }

        $current = [string](Get-LibraryCatalogEntryValue -Entry $item -Name 'FileSize')
        if (-not $Force -and $current -and $current -ne '-') { continue }

        $fileSize = Get-OllamaLibraryFileSizeLabelFromWeb -LibraryName $name -Force:$Force
        if ($item.PSObject.Properties['FileSize']) {
            $item.FileSize = $fileSize
        }
        else {
            $item | Add-Member -NotePropertyName FileSize -NotePropertyValue $fileSize -Force
        }
    }
}

function Format-OllamaLibraryParameterSizeLabel {
    param([string[]]$SizeTokens)

    $SizeTokens = @($SizeTokens)
    if ((Get-SafeCollectionCount $SizeTokens) -eq 0) { return '-' }

    $parsed = New-Object System.Collections.Generic.List[object]
    foreach ($token in $SizeTokens) {
        $raw = $token.Trim().ToLower()
        if ($raw -match '^(\d+(?:\.\d+)?)([bkm])$') {
            [void]$parsed.Add([pscustomobject]@{
                    Raw   = $raw
                    Value = [double]$Matches[1]
                    Unit  = $Matches[2]
                })
            continue
        }
        if ($raw -match '^(\d+)x(\d+(?:\.\d+)?)([bkm])$') {
            [void]$parsed.Add([pscustomobject]@{
                    Raw   = $raw
                    Value = [double]$Matches[2]
                    Unit  = $Matches[3]
                })
        }
    }

    $parsedCount = Get-SafeCollectionCount $parsed
    if ($parsedCount -eq 0) {
        return (($SizeTokens | ForEach-Object { $_.ToUpper() }) -join ' ')
    }
    if ($parsedCount -eq 1) { return $parsed[0].Raw.ToUpper() }

    $sameUnit = @($parsed | Where-Object { $_.Unit -eq 'b' } | Sort-Object Value)
    if ((Get-SafeCollectionCount $sameUnit) -ge 2) {
        $min = $sameUnit[0].Raw.ToUpper()
        $max = $sameUnit[-1].Raw.ToUpper()
        if ($min -eq $max) { return $min }
        return "$min-$max"
    }

    return (($parsed | ForEach-Object { $_.Raw.ToUpper() }) -join ' ')
}

function Get-OllamaLibrarySpecializationOrder {
    return [ordered]@{
        'General Chat'    = 1
        'Coding'          = 2
        'Reasoning'       = 3
        'Vision'          = 4
        'Multimodal'      = 5
        'Embedding'       = 6
        'Tools'           = 7
        'Domain Specific' = 8
        'Other'           = 9
    }
}

function Resolve-OllamaLibrarySpecialization {
    param(
        [string]$ModelName,
        [string]$Description,
        [string]$Tags
    )

    $text = ("$ModelName $Description $Tags").ToLowerInvariant()
    if ($text -match 'embed') { return 'Embedding' }
    if ($text -match 'vision|llava|multimodal|ocr|moondream') { return 'Vision' }
    if ($text -match 'coder|code|sql|starcoder|codestral|codegemma') { return 'Coding' }
    if ($text -match 'reason|thinking|r1|math|deepscaler') { return 'Reasoning' }
    if ($text -match 'tool|agent|function') { return 'Tools' }
    if ($text -match 'medical|legal|finance|sql') { return 'Domain Specific' }
    if ($text -match 'audio|translate') { return 'Multimodal' }
    return 'General Chat'
}

function New-EmptyLibraryCatalogStore {
    return [ordered]@{
        Version           = 1
        LastUpdated       = (Get-Date).ToString('o')
        CatalogFetchedAt  = $null
        SortGeneratedAt   = $null
        SummaryModel      = $null
        Items             = @()
    }
}

function Get-LibraryCatalogEntryValue {
    param(
        $Entry,
        [string]$Name
    )

    if ($null -eq $Entry) { return $null }
    if ($Entry -is [System.Collections.IDictionary]) {
        if ($Entry.Contains($Name)) { return $Entry[$Name] }
        if ($Name -eq 'Category' -and $Entry.Contains('Specialization')) { return $Entry['Specialization'] }
        if ($Name -eq 'ParameterSize' -and $Entry.Contains('Size')) { return $Entry['Size'] }
        if ($Name -eq 'FileSize' -and $Entry.Contains('Size') -and [string]$Entry['Size'] -match '(?:GB|MB|KB)\b') {
            return $Entry['Size']
        }
        return $null
    }
    $prop = $Entry.PSObject.Properties[$Name]
    if ($prop) { return $prop.Value }

    if ($Name -eq 'Category') {
        $legacySpec = $Entry.PSObject.Properties['Specialization']
        if ($legacySpec) { return $legacySpec.Value }
    }

    if ($Name -eq 'ParameterSize') {
        $legacy = $Entry.PSObject.Properties['Size']
        if ($legacy) { return $legacy.Value }
    }
    if ($Name -eq 'FileSize') {
        $legacy = $Entry.PSObject.Properties['Size']
        if ($legacy -and [string]$legacy.Value -match '(?:GB|MB|KB)\b') {
            return $legacy.Value
        }
    }

    return $null
}

function Get-LibraryCatalogStore {
    Ensure-ConfigDirectory
    if (-not (Test-Path -LiteralPath $Script:LibraryCatalogStoreFile)) {
        return New-EmptyLibraryCatalogStore
    }

    try {
        $data = Get-Content -LiteralPath $Script:LibraryCatalogStoreFile -Raw | ConvertFrom-Json
        return [ordered]@{
            Version          = if ($data.Version) { [int]$data.Version } else { 1 }
            LastUpdated      = if ($data.LastUpdated) { [string]$data.LastUpdated } else { $null }
            CatalogFetchedAt = if ($data.CatalogFetchedAt) { [string]$data.CatalogFetchedAt } else { $null }
            SortGeneratedAt  = if ($data.SortGeneratedAt) { [string]$data.SortGeneratedAt } else { $null }
            SummaryModel     = if ($data.SummaryModel) { [string]$data.SummaryModel } else { $null }
            Items            = if ($data.Items) { @($data.Items) } else { @() }
        }
    }
    catch {
        Write-ToolkitLog "Library catalog store corrupt; starting fresh: $($_.Exception.Message)" Yellow
        return New-EmptyLibraryCatalogStore
    }
}

function Save-LibraryCatalogStore {
    param($Store)

    Ensure-ConfigDirectory
    $itemsExport = @(
        foreach ($item in @($Store.Items)) {
            [ordered]@{
                Name           = [string](Get-LibraryCatalogEntryValue -Entry $item -Name 'Name')
                Description    = [string](Get-LibraryCatalogEntryValue -Entry $item -Name 'Description')
                Tags           = [string](Get-LibraryCatalogEntryValue -Entry $item -Name 'Tags')
                ParameterSize  = [string](Get-LibraryCatalogEntryValue -Entry $item -Name 'ParameterSize')
                FileSize       = [string](Get-LibraryCatalogEntryValue -Entry $item -Name 'FileSize')
                Category       = [string](Get-LibraryCatalogEntryValue -Entry $item -Name 'Category')
                Specialization = [string](Get-LibraryCatalogEntryValue -Entry $item -Name 'Specialization')
                SortOrder      = [int](Get-LibraryCatalogEntryValue -Entry $item -Name 'SortOrder')
                AiSorted       = [bool](Get-LibraryCatalogEntryValue -Entry $item -Name 'AiSorted')
            }
        }
    )

    $payload = [ordered]@{
        Version          = 1
        LastUpdated      = (Get-Date).ToString('o')
        CatalogFetchedAt = $Store.CatalogFetchedAt
        SortGeneratedAt  = $Store.SortGeneratedAt
        SummaryModel     = $Store.SummaryModel
        Items            = $itemsExport
    }
    $payload | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Script:LibraryCatalogStoreFile -Encoding UTF8
}

function Test-LibraryCatalogStoreStale {
    param([switch]$ForceWeb)

    if ($ForceWeb) { return $true }

    $store = Get-LibraryCatalogStore
    if ((Get-SafeCollectionCount $store.Items) -eq 0) { return $true }
    if (-not $store.CatalogFetchedAt) { return $true }

    try {
        $fetched = [datetime]$store.CatalogFetchedAt
        return ((Get-Date) - $fetched).TotalDays -ge $Script:LibraryCatalogStoreDays
    }
    catch {
        return $true
    }
}

function Get-LibraryCatalogStoreItems {
    $store = Get-LibraryCatalogStore
    if ((Get-SafeCollectionCount $store.Items) -eq 0) { return @() }
    return @($store.Items | Sort-Object -Property SortOrder, Name)
}

function Filter-LibraryCatalogItems {
    param(
        [object[]]$Items,
        [string]$Search
    )

    $items = @($Items)
    if ((Get-SafeCollectionCount $items) -eq 0) { return @() }
    if ([string]::IsNullOrWhiteSpace($Search)) { return $items }

    $q = $Search.Trim().ToLowerInvariant()
    return @(
        $items | Where-Object {
            $name = [string]$_.Name
            $desc = [string]$_.Description
            $tags = [string]$_.Tags
            $category = [string](Get-LibraryCatalogEntryValue -Entry $_ -Name 'Category')
            if (-not $category) { $category = [string]$_.Specialization }
            $paramSize = [string](Get-LibraryCatalogEntryValue -Entry $_ -Name 'ParameterSize')
            $fileSize = [string](Get-LibraryCatalogEntryValue -Entry $_ -Name 'FileSize')
            ($name.ToLowerInvariant().Contains($q)) -or
            ($desc.ToLowerInvariant().Contains($q)) -or
            ($tags.ToLowerInvariant().Contains($q)) -or
            ($category.ToLowerInvariant().Contains($q)) -or
            ($paramSize.ToLowerInvariant().Contains($q)) -or
            ($fileSize.ToLowerInvariant().Contains($q))
        }
    )
}

function Get-RegistryCatalogDisplayItems {
    param([string]$Search = '')

    return @(Filter-LibraryCatalogItems -Items (Get-LibraryCatalogStoreItems) -Search $Search)
}

function Update-LibraryCatalogCategoryFields {
    param([string[]]$ModelNames)

    if (-not (Get-Command Get-ModelUsageCategoryEntry -ErrorAction SilentlyContinue)) {
        return 0
    }

    $store = Get-LibraryCatalogStore
    if ((Get-SafeCollectionCount $store.Items) -eq 0) { return 0 }

    $nameSet = $null
    if ((Get-SafeCollectionCount $ModelNames) -gt 0) {
        $nameSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($n in $ModelNames) {
            if ($n) { [void]$nameSet.Add([string]$n) }
        }
    }

    $categoryOrder = Get-OllamaLibrarySpecializationOrder
    $changed = 0
    $updatedItems = New-Object System.Collections.Generic.List[object]

    foreach ($item in @($store.Items)) {
        $name = [string](Get-LibraryCatalogEntryValue -Entry $item -Name 'Name')
        if ($nameSet -and -not $nameSet.Contains($name)) {
            [void]$updatedItems.Add($item)
            continue
        }

        $usageEntry = Get-ModelUsageCategoryEntry -ModelName $name
        if (-not $usageEntry) {
            [void]$updatedItems.Add($item)
            continue
        }

        $category = [string](Get-UsageCategoryEntryValue -Entry $usageEntry -Name 'Category')
        if (-not $category) {
            [void]$updatedItems.Add($item)
            continue
        }
        if (-not $categoryOrder.Contains($category)) { $category = 'Other' }

        $aiSorted = [bool](Get-UsageCategoryEntryValue -Entry $usageEntry -Name 'WebAiClassified')
        $current = [string](Get-LibraryCatalogEntryValue -Entry $item -Name 'Category')
        if (-not $current) { $current = [string](Get-LibraryCatalogEntryValue -Entry $item -Name 'Specialization') }

        if ($current -eq $category -and `
            [bool](Get-LibraryCatalogEntryValue -Entry $item -Name 'AiSorted') -eq $aiSorted) {
            [void]$updatedItems.Add($item)
            continue
        }

        [void]$updatedItems.Add([pscustomobject]@{
                Name           = $name
                Description    = [string](Get-LibraryCatalogEntryValue -Entry $item -Name 'Description')
                Tags           = [string](Get-LibraryCatalogEntryValue -Entry $item -Name 'Tags')
                ParameterSize  = [string](Get-LibraryCatalogEntryValue -Entry $item -Name 'ParameterSize')
                FileSize       = [string](Get-LibraryCatalogEntryValue -Entry $item -Name 'FileSize')
                Category       = $category
                Specialization = $category
                SortOrder      = [int]$categoryOrder[$category]
                AiSorted       = $aiSorted
            })
        $changed++
    }

    if ($changed -gt 0) {
        $store.Items = @($updatedItems | Sort-Object -Property SortOrder, Name)
        Save-LibraryCatalogStore -Store $store
    }

    return $changed
}

function Get-LibraryCatalogDescriptionText {
    param(
        [string]$LibraryName,
        [string]$FallbackDescription
    )

    $listDesc = Get-ModelListDescription -ModelName $LibraryName
    if ($listDesc -and $listDesc -ne '-') { return $listDesc }

    if ($FallbackDescription) { return $FallbackDescription }
    return '-'
}

function Get-LibraryCatalogDateUpdatedDisplay {
    param([string]$LibraryName)

    if ([string]::IsNullOrWhiteSpace($LibraryName)) { return '-' }

    $latest = $null
    $candidates = @()

    if (Get-Command Get-ModelDescriptionEntry -ErrorAction SilentlyContinue) {
        $descEntry = Get-ModelDescriptionEntry -ModelName $LibraryName
        if ($descEntry) {
            $fetchedAt = if (Get-Command Get-DescriptionEntryValue -ErrorAction SilentlyContinue) {
                Get-DescriptionEntryValue -Entry $descEntry -Name 'FetchedAt'
            }
            else { $descEntry.FetchedAt }
            if ($fetchedAt) { $candidates += [string]$fetchedAt }
        }
    }

    foreach ($value in $candidates) {
        try {
            $parsed = [datetime]$value
            if (-not $latest -or $parsed -gt $latest) { $latest = $parsed }
        }
        catch { }
    }

    if (-not $latest) { return '-' }
    return $latest.ToString('yyyy-MM-dd HH:mm')
}

function Get-CatalogSpecializationPromptLine {
    param($Item)

    $desc = if ($Item.Description) { [string]$Item.Description } else { '-' }
    if ($desc.Length -gt 96) { $desc = $desc.Substring(0, 93) + '...' }

    $tags = if ($Item.Tags) { [string]$Item.Tags } else { '-' }
    if ($tags.Length -gt 48) { $tags = $tags.Substring(0, 45) + '...' }

    return "- $($Item.Name): $desc [tags: $tags]"
}

function New-CatalogSpecializationPrompt {
    param(
        [object[]]$Items,
        [string]$CategoryList
    )

    $lines = ($Items | ForEach-Object { Get-CatalogSpecializationPromptLine -Item $_ }) -join "`n"
    return @"
Classify each Ollama model by its primary specialization.
Use exactly one category from: $CategoryList

Reply with one line per model using this exact format:
modelname|Category

Models:
$lines
"@
}

function Read-CatalogSpecializationResponse {
    param([string]$Raw)

    $map = @{}
    foreach ($line in ($Raw -split "`n")) {
        if ($line -match '^\s*-?\s*([^|]+?)\s*\|\s*(.+?)\s*$') {
            $name = $Matches[1].Trim().TrimStart('-').Trim()
            $cat = $Matches[2].Trim()
            if ($name) { $map[$name] = $cat }
        }
    }
    return $map
}

function Invoke-OllamaLibraryCatalogSpecializationBatch {
    param(
        [object[]]$Items,
        [string]$SummarizerModel,
        [string]$CategoryList,
        [int]$NumCtx = 4096
    )

    $map = @{}
    $items = @($Items)
    if ((Get-SafeCollectionCount $items) -eq 0) { return $map }

    $prompt = New-CatalogSpecializationPrompt -Items $items -CategoryList $CategoryList
    $maxPredict = [math]::Min(1800, [math]::Max(120, (Get-SafeCollectionCount $items) * 24))
    $raw = Invoke-ToolkitOllamaGenerate -Model $SummarizerModel -Prompt $prompt `
        -MaxPredict $maxPredict -NumCtx $NumCtx
    Invoke-ToolkitUiPump
    return Read-CatalogSpecializationResponse -Raw $raw
}

function Add-CatalogSpecializationMapEntries {
    param(
        [hashtable]$Target,
        [hashtable]$Source
    )

    foreach ($key in $Source.Keys) {
        $Target[$key] = $Source[$key]
    }
}

function Invoke-OllamaLibraryCatalogSpecializationMap {
    param(
        [object[]]$Items,
        [string]$SummarizerModel
    )

    $map = @{}
    $Items = @($Items)
    if ((Get-SafeCollectionCount $Items) -eq 0) { return $map }

    $categoryList = (Get-OllamaLibrarySpecializationOrder).Keys -join ', '
    $batchSize = 15

    function Invoke-CatalogSpecializationBatchWithRetry {
        param(
            [object[]]$BatchItems,
            [int]$Depth = 0
        )

        $BatchItems = @($BatchItems)
        if ((Get-SafeCollectionCount $BatchItems) -eq 0) { return @{} }

        $lastError = 'Unknown error'
        $ctxOptions = @(4096, 8192)
        foreach ($numCtx in $ctxOptions) {
            try {
                return Invoke-OllamaLibraryCatalogSpecializationBatch -Items $BatchItems `
                    -SummarizerModel $SummarizerModel -CategoryList $categoryList -NumCtx $numCtx
            }
            catch {
                $lastError = $_.Exception.Message
            }
        }

        $batchCount = Get-SafeCollectionCount $BatchItems
        if ($batchCount -eq 1) {
            Write-ToolkitLog "Catalog specialization failed for '$($BatchItems[0].Name)': $lastError" Yellow
            return @{}
        }

        if ($Depth -ge 4) {
            Write-ToolkitLog "Catalog specialization batch failed after retries ($batchCount models): $lastError" Yellow
            return @{}
        }

        $mid = [math]::Ceiling($batchCount / 2)
        $left = Invoke-CatalogSpecializationBatchWithRetry -BatchItems @($BatchItems[0..($mid - 1)]) -Depth ($Depth + 1)
        $right = Invoke-CatalogSpecializationBatchWithRetry -BatchItems @($BatchItems[$mid..($batchCount - 1)]) -Depth ($Depth + 1)
        $merged = @{}
        Add-CatalogSpecializationMapEntries -Target $merged -Source $left
        Add-CatalogSpecializationMapEntries -Target $merged -Source $right
        return $merged
    }

    $itemCount = Get-SafeCollectionCount $Items
    for ($offset = 0; $offset -lt $itemCount; $offset += $batchSize) {
        $end = [math]::Min($offset + $batchSize - 1, $itemCount - 1)
        $batch = @($Items[$offset..$end])
        $partial = Invoke-CatalogSpecializationBatchWithRetry -BatchItems $batch
        Add-CatalogSpecializationMapEntries -Target $map -Source $partial
    }

    return $map
}

function Get-LibraryCatalogCategoryDisplay {
    param(
        $Item,
        [hashtable]$CachedSpecMap = @{}
    )

    $name = [string](Get-LibraryCatalogEntryValue -Entry $Item -Name 'Name')
    if (-not $name -and $Item.Name) { $name = [string]$Item.Name }

    $description = [string](Get-LibraryCatalogEntryValue -Entry $Item -Name 'Description')
    $capabilityTags = [string](Get-LibraryCatalogEntryValue -Entry $Item -Name 'Tags')

    if ($CachedSpecMap.ContainsKey($name)) {
        return [pscustomobject]@{
            Category  = [string]$CachedSpecMap[$name]
            AiSorted  = $true
        }
    }

    if (Get-Command Get-ModelUsageCategoryDisplay -ErrorAction SilentlyContinue) {
        $usageEntry = $null
        if (Get-Command Get-ModelUsageCategoryEntry -ErrorAction SilentlyContinue) {
            $usageEntry = Get-ModelUsageCategoryEntry -ModelName $name
        }
        $display = Get-ModelUsageCategoryDisplay -ModelName $name `
            -FallbackDescription $description -FallbackCapabilityTags $capabilityTags
        if ($display -and $display -ne '-' -and $display -ne '(pending)') {
            $aiSorted = $false
            if ($usageEntry -and (Get-UsageCategoryEntryValue -Entry $usageEntry -Name 'WebAiClassified')) {
                $aiSorted = $true
            }
            return [pscustomobject]@{
                Category = $display
                AiSorted = $aiSorted
            }
        }
    }

    $heuristic = Resolve-OllamaLibrarySpecialization -ModelName $name `
        -Description $description -Tags $capabilityTags
    return [pscustomobject]@{
        Category = $heuristic
        AiSorted = $false
    }
}

function Sort-OllamaLibraryCatalogBySpecialization {
    param(
        [object[]]$Items,
        [hashtable]$CachedSpecMap = @{},
        [string]$SummaryModel = ''
    )

    $Items = @($Items)
    if ((Get-SafeCollectionCount $Items) -eq 0) { return @() }

    $categoryOrder = Get-OllamaLibrarySpecializationOrder
    $enriched = New-Object System.Collections.Generic.List[object]

    foreach ($item in $Items) {
        $name = [string]$item.Name
        $resolved = Get-LibraryCatalogCategoryDisplay -Item $item -CachedSpecMap $CachedSpecMap
        $category = [string]$resolved.Category
        if (-not $categoryOrder.Contains($category)) { $category = 'Other' }

        [void]$enriched.Add([pscustomobject]@{
                Name           = $name
                Description    = [string]$item.Description
                Tags           = [string]$item.Tags
                ParameterSize  = [string](Get-LibraryCatalogEntryValue -Entry $item -Name 'ParameterSize')
                FileSize       = [string](Get-LibraryCatalogEntryValue -Entry $item -Name 'FileSize')
                Category       = $category
                Specialization = $category
                SortOrder      = [int]$categoryOrder[$category]
                AiSorted       = [bool]$resolved.AiSorted
            })
    }

    return @($enriched | Sort-Object -Property SortOrder, Name)
}

function Update-EnrichedLibraryCatalog {
    param(
        [switch]$ForceWeb,
        [switch]$ForceReclassify
    )

    $store = Get-LibraryCatalogStore
    $cachedSpecMap = @{}
    if (-not $ForceReclassify) {
        foreach ($item in @($store.Items)) {
            $name = [string](Get-LibraryCatalogEntryValue -Entry $item -Name 'Name')
            if (-not $name) { continue }
            $cachedCategory = [string](Get-LibraryCatalogEntryValue -Entry $item -Name 'Category')
            if (-not $cachedCategory) {
                $cachedCategory = [string](Get-LibraryCatalogEntryValue -Entry $item -Name 'Specialization')
            }
            if ([bool](Get-LibraryCatalogEntryValue -Entry $item -Name 'AiSorted') -and $cachedCategory) {
                $cachedSpecMap[$name] = $cachedCategory
                continue
            }
            if (Get-Command Get-ModelUsageCategoryEntry -ErrorAction SilentlyContinue) {
                $usageEntry = Get-ModelUsageCategoryEntry -ModelName $name
                if ($usageEntry -and (Get-UsageCategoryEntryValue -Entry $usageEntry -Name 'WebAiClassified')) {
                    $cat = Get-UsageCategoryEntryValue -Entry $usageEntry -Name 'Category'
                    if ($cat) { $cachedSpecMap[$name] = [string]$cat }
                }
            }
        }
    }

    $webItems = @()
    if ($ForceWeb -or (Test-LibraryCatalogStoreStale)) {
        $webItems = @(Get-OllamaLibraryCatalog -Force)
        $store.CatalogFetchedAt = (Get-Date).ToString('o')
    }
    elseif ((Get-SafeCollectionCount $store.Items) -gt 0) {
        $cachedCount = Get-SafeCollectionCount $store.Items
        return [pscustomobject]@{
            Items     = @(Get-LibraryCatalogStoreItems)
            Updated   = $false
            FromCache = $true
            Message   = "Using cached catalog ($cachedCount models)."
        }
    }
    else {
        $webItems = @(Get-OllamaLibraryCatalog)
        $store.CatalogFetchedAt = (Get-Date).ToString('o')
    }

    $webItemCount = Get-SafeCollectionCount $webItems
    $cachedItemCount = Get-SafeCollectionCount $store.Items
    if ($webItemCount -eq 0 -and $cachedItemCount -gt 0) {
        return [pscustomobject]@{
            Items     = @(Get-LibraryCatalogStoreItems)
            Updated   = $false
            FromCache = $true
            Message   = "Using cached catalog ($cachedItemCount models)."
        }
    }

    if ($webItemCount -gt 0) {
        Write-ToolkitLog "Fetching download sizes for $webItemCount catalog model(s)..." Cyan
        Update-LibraryCatalogFileSizes -Items $webItems -Force:$ForceWeb
    }

    $sorted = @(Sort-OllamaLibraryCatalogBySpecialization -Items $webItems `
        -CachedSpecMap $cachedSpecMap -SummaryModel $store.SummaryModel)
    $store.Items = $sorted
    $store.LastUpdated = (Get-Date).ToString('o')
    if ((Get-SafeCollectionCount $sorted) -gt 0) {
        $store.SortGeneratedAt = (Get-Date).ToString('o')
    }
    $activeSummarizer = Get-DescriptionSummarizerModel -ExcludeModelName ''
    if ($activeSummarizer) { $store.SummaryModel = $activeSummarizer }
    Save-LibraryCatalogStore -Store $store

    $sortedCount = Get-SafeCollectionCount $sorted
    $newAi = Get-SafeCollectionCount @($sorted | Where-Object { $_.AiSorted })
    $message = "Catalog updated: $sortedCount models ($newAi AI-classified)."

    return [pscustomobject]@{
        Items     = $sorted
        Updated   = $true
        FromCache = $false
        Message   = $message
    }
}

function Get-OllamaModelInstallType {
    param($Model)

    if ($Model.PSObject.Properties['remote_host'] -and $Model.remote_host) { return 'cloud' }
    if ($Model.details -and $Model.details.format -eq 'gguf') { return 'local' }
    return 'other'
}

function ConvertFrom-OllamaLibraryListingHtml {
    param([string]$Html)

    $results = New-Object System.Collections.Generic.List[object]
    $seen = New-Object System.Collections.Generic.HashSet[string]

    $blocks = [regex]::Split($Html, '(?=<li x-test-model)')
    foreach ($block in $blocks) {
        if ($block -notmatch 'x-test-model') { continue }
        if ($block -notmatch 'href="/library/([^"/?#]+)"') { continue }

        $name = $Matches[1].Trim()
        if ($name -match ':') { continue }
        if (-not $seen.Add($name)) { continue }

        $description = ''
        if ($block -match '<p class="max-w-lg break-words text-neutral-800 text-md">([^<]*)</p>') {
            $description = [System.Net.WebUtility]::HtmlDecode($Matches[1]).Trim()
        }

        $capabilityList = New-Object System.Collections.Generic.List[string]
        $sizeList = New-Object System.Collections.Generic.List[string]
        foreach ($tm in [regex]::Matches($block, 'x-test-(capability|size)\s+class="[^"]*">([^<]+)</span>')) {
            $kind = $tm.Groups[1].Value
            $tag = $tm.Groups[2].Value.Trim().ToLower()
            if (-not $tag) { continue }
            if ($kind -eq 'size') {
                if ($tag -notin $sizeList) { [void]$sizeList.Add($tag) }
            }
            elseif ($tag -notin $capabilityList) {
                [void]$capabilityList.Add($tag)
            }
        }

        [void]$results.Add([pscustomobject]@{
                Name          = $name
                Description   = $description
                Tags          = ($capabilityList -join ' ')
                ParameterSize = (Format-OllamaLibraryParameterSizeLabel -SizeTokens @($sizeList))
                FileSize      = '-'
            })
    }

    return [object[]]$results.ToArray()
}

function Get-OllamaLibraryCatalog {
    param(
        [string]$Search = '',
        [switch]$Force
    )

    $cacheKey = if ([string]::IsNullOrWhiteSpace($Search)) { '__all__' } else { $Search.Trim().ToLowerInvariant() }
    if (-not $Force -and $Script:LibraryCatalogCache.ContainsKey($cacheKey)) {
        $cached = $Script:LibraryCatalogCache[$cacheKey]
        if (((Get-Date) - $cached.FetchedAt).TotalMinutes -lt $Script:LibraryCatalogCacheMinutes) {
            return @($cached.Items)
        }
    }

    $url = $Script:OllamaLibraryCatalogUrl
    if (-not [string]::IsNullOrWhiteSpace($Search)) {
        $encoded = [uri]::EscapeDataString($Search.Trim())
        $url = "$Script:OllamaLibraryCatalogUrl`?q=$encoded"
    }

    Write-ToolkitLog "Loading ollama.com library catalog..." Cyan
    $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 60
    $items = @(ConvertFrom-OllamaLibraryListingHtml -Html $response.Content)
    $Script:LibraryCatalogCache[$cacheKey] = [pscustomobject]@{
        FetchedAt = Get-Date
        Items     = $items
    }
    Invoke-ToolkitUiPump
    return $items
}

function Get-OllamaModelTagsFromWeb {
    param(
        [string]$LibraryName,
        [switch]$Force
    )

    if ([string]::IsNullOrWhiteSpace($LibraryName)) { return @() }
    if (-not $Force -and $Script:ModelTagsCache.ContainsKey($LibraryName)) {
        return @($Script:ModelTagsCache[$LibraryName])
    }

    $url = "$Script:OllamaLibraryBaseUrl/$LibraryName/tags"
    try {
        $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 45
    }
    catch {
        return @("$LibraryName`:latest")
    }

    $escaped = [regex]::Escape($LibraryName)
    $pattern = 'href="/library/(' + $escaped + ':[^"]+)"'
    $tags = New-Object System.Collections.Generic.List[string]
    foreach ($m in [regex]::Matches($response.Content, $pattern)) {
        $tag = $m.Groups[1].Value.Trim()
        if ($tag -and $tag -notin $tags) { [void]$tags.Add($tag) }
    }

    if ((Get-SafeCollectionCount $tags) -eq 0) {
        [void]$tags.Add("$LibraryName`:latest")
    }

    $sorted = @(
        $tags |
            Sort-Object @{
                Expression = {
                    if ($_ -match ':latest$') { 0 }
                    elseif ($_ -notmatch '-instruct-q|-text-q|-fp16') { 1 }
                    else { 2 }
                }
            }, @{
                Expression = { $_ }
            }
    )

    $Script:ModelTagsCache[$LibraryName] = $sorted
    Invoke-ToolkitUiPump
    return $sorted
}

function Invoke-OllamaPullModel {
    param(
        [string]$ModelName,
        [scriptblock]$OnProgress
    )

    if ([string]::IsNullOrWhiteSpace($ModelName)) {
        throw 'Model name is required.'
    }

    $uri = ($Script:OllamaHost.TrimEnd('/')) + '/api/pull'
    $bytes = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-ToolkitJson -InputObject @{
        name   = $ModelName
        stream = $true
    }))

    $request = [System.Net.HttpWebRequest]::Create($uri)
    $request.Method = 'POST'
    $request.ContentType = 'application/json'
    $request.Timeout = 7200000
    $request.ReadWriteTimeout = 7200000

    $reqStream = $request.GetRequestStream()
    $reqStream.Write($bytes, 0, $bytes.Length)
    $reqStream.Close()

    $response = $request.GetResponse()
    $reader = New-Object System.IO.StreamReader($response.GetResponseStream())

    try {
        while (-not $reader.EndOfStream) {
            $line = $reader.ReadLine()
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $chunk = $line | ConvertFrom-Json
            if ($OnProgress) { & $OnProgress $chunk }
            Invoke-ToolkitUiPump
            if ($chunk.error) {
                throw [string]$chunk.error
            }
        }
    }
    finally {
        $reader.Close()
        $response.Close()
    }
}

function Remove-OllamaLocalModel {
    param([string]$ModelName)

    if ([string]::IsNullOrWhiteSpace($ModelName)) {
        throw 'Model name is required.'
    }

    $uri = ($Script:OllamaHost.TrimEnd('/')) + '/api/delete'
    $bytes = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-ToolkitJson -InputObject @{ name = $ModelName }))
    $null = Invoke-RestMethod -Method Delete -Uri $uri -Body $bytes -ContentType 'application/json; charset=utf-8' -TimeoutSec 120
    Invoke-ToolkitUiPump
}