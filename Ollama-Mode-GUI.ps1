#Requires -Version 5.1
<#
.SYNOPSIS
    Ollama AMD Vulkan manager with compute modes, benchmark testing, and model launch.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([System.Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    & powershell.exe -STA -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path
    exit $LASTEXITCODE
}

$Script:ToolkitRoot = $PSScriptRoot
. (Join-Path $PSScriptRoot 'Ollama-Toolkit.Core.ps1')
. (Join-Path $PSScriptRoot 'Ollama-Toolkit.GuiAiActivity.ps1')
. (Join-Path $PSScriptRoot 'Ollama-Toolkit.BenchmarkStore.ps1')
. (Join-Path $PSScriptRoot 'Ollama-Toolkit.ModelCatalog.ps1')
. (Join-Path $PSScriptRoot 'Ollama-Toolkit.ModelRegistry.ps1')
. (Join-Path $PSScriptRoot 'Ollama-Toolkit.GuiTesting.ps1')

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$Script:ApiTimeoutSec = 90
Ensure-ConfigDirectory
Initialize-ModeDefinitions

$colors = @{
    Bg          = [System.Drawing.Color]::FromArgb(28, 30, 38)
    Panel       = [System.Drawing.Color]::FromArgb(38, 41, 52)
    PanelBorder = [System.Drawing.Color]::FromArgb(58, 62, 78)
    Text        = [System.Drawing.Color]::FromArgb(232, 234, 240)
    Muted       = [System.Drawing.Color]::FromArgb(156, 163, 175)
    Accent      = [System.Drawing.Color]::FromArgb(237, 28, 36)
    AccentHover = [System.Drawing.Color]::FromArgb(255, 58, 66)
    Active      = [System.Drawing.Color]::FromArgb(34, 197, 94)
    ActiveBg    = [System.Drawing.Color]::FromArgb(22, 58, 38)
    Warning     = [System.Drawing.Color]::FromArgb(250, 204, 21)
    WarningBg   = [System.Drawing.Color]::FromArgb(58, 48, 18)
    Button      = [System.Drawing.Color]::FromArgb(52, 56, 70)
    ButtonHover = [System.Drawing.Color]::FromArgb(68, 72, 88)
    LogBg       = [System.Drawing.Color]::FromArgb(20, 22, 28)
}

function New-ToolkitFont {
    param(
        [float]$Size = 10,
        [System.Drawing.FontStyle]$Style = [System.Drawing.FontStyle]::Regular
    )
    return New-Object System.Drawing.Font('Segoe UI', $Size, $Style)
}

function Enable-UiDoubleBuffering {
    param([System.Windows.Forms.Control]$Control)

    if (-not $Control) { return }

    # ListView/DataGridView column headers disappear when DoubleBuffered is set via reflection.
    $controlType = $Control.GetType()
    $skipDoubleBuffer = (
        [System.Windows.Forms.ListView].IsAssignableFrom($controlType) -or
        [System.Windows.Forms.DataGridView].IsAssignableFrom($controlType)
    )
    if (-not $skipDoubleBuffer) {
        $flags = [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::NonPublic
        $prop = $controlType.GetProperty('DoubleBuffered', $flags)
        if ($prop) {
            $prop.SetValue($Control, $true, $null)
        }
    }
    foreach ($child in $Control.Controls) {
        Enable-UiDoubleBuffering -Control $child
    }
}

function Get-ListViewCellText {
    param(
        [System.Windows.Forms.ListViewItem]$Item,
        [int]$Column
    )

    if ($Column -le 0) { return [string]$Item.Text }
    if ($Item.SubItems.Count -gt $Column) { return [string]$Item.SubItems[$Column].Text }
    return ''
}

function Get-ListViewSortSizeBytes {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    if ($Text -notmatch '(\d+(?:\.\d+)?)\s*([BbKkMmGg]?)\b') { return $null }

    try {
        $value = [double]::Parse(
            $Matches[1],
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        return $null
    }

    switch ($Matches[2].ToUpperInvariant()) {
        'G' { return $value * 1GB }
        'M' { return $value * 1MB }
        'K' { return $value * 1KB }
        'B' { return $value }
        default { return $value }
    }
}

function Get-ListViewSortKey {
    param(
        [System.Windows.Forms.ListViewItem]$Item,
        [int]$Column
    )

    $text = (Get-ListViewCellText -Item $Item -Column $Column).Trim()
    if ($text.Length -eq 0 -or $text -eq '-') {
        return [pscustomobject]@{ Rank = 3; Value = '' }
    }

    $parsedDate = $text -as [datetime]
    if ($null -ne $parsedDate) {
        return [pscustomobject]@{ Rank = 0; Value = $parsedDate.Ticks }
    }

    $sizeBytes = Get-ListViewSortSizeBytes -Text $text
    if ($null -ne $sizeBytes) {
        return [pscustomobject]@{ Rank = 1; Value = $sizeBytes }
    }

    return [pscustomobject]@{ Rank = 2; Value = $text.ToLowerInvariant() }
}

function Sort-ListViewByColumn {
    param(
        [System.Windows.Forms.ListView]$List,
        [int]$Column,
        [System.Windows.Forms.SortOrder]$Order
    )

    if ($Order -eq [System.Windows.Forms.SortOrder]::None -or $List.Items.Count -lt 2) { return }

    $items = @($List.Items | ForEach-Object {
        $key = Get-ListViewSortKey -Item $_ -Column $Column
        [pscustomobject]@{ Item = $_; Rank = $key.Rank; Value = $key.Value }
    })
    $sorted = if ($Order -eq [System.Windows.Forms.SortOrder]::Ascending) {
        $items | Sort-Object Rank, Value | ForEach-Object { $_.Item }
    }
    else {
        $items | Sort-Object Rank, Value -Descending | ForEach-Object { $_.Item }
    }

    $List.BeginUpdate()
    $List.Items.Clear()
    foreach ($item in $sorted) {
        [void]$List.Items.Add($item)
    }
    $List.EndUpdate()
}

function Register-ListViewColumnSorting {
    param([System.Windows.Forms.ListView]$List)

    $List.Tag = @{
        SortColumn = -1
        SortOrder  = [System.Windows.Forms.SortOrder]::None
    }

    $List.Add_ColumnClick({
        param($sender, $e)

        $state = $sender.Tag
        $sortColumn = [int](Get-ListViewSortTagValue -Tag $state -Name 'SortColumn' -Default -1)
        if ($sortColumn -eq $e.Column) {
            $sortOrder = if ((Get-ListViewSortTagValue -Tag $state -Name 'SortOrder') -eq [System.Windows.Forms.SortOrder]::Ascending) {
                [System.Windows.Forms.SortOrder]::Descending
            }
            else {
                [System.Windows.Forms.SortOrder]::Ascending
            }
        }
        else {
            $sortColumn = $e.Column
            $sortOrder = [System.Windows.Forms.SortOrder]::Ascending
        }

        Set-ListViewSortTagValue -Tag $state -Name 'SortColumn' -Value $sortColumn
        Set-ListViewSortTagValue -Tag $state -Name 'SortOrder' -Value $sortOrder
        Sort-ListViewByColumn -List $sender -Column $e.Column -Order $sortOrder
    })
}

function Reapply-ListViewSort {
    param([System.Windows.Forms.ListView]$List)

    if (-not $List.Tag) { return }
    $state = $List.Tag
    $sortColumn = [int](Get-ListViewSortTagValue -Tag $state -Name 'SortColumn' -Default -1)
    $sortOrder = Get-ListViewSortTagValue -Tag $state -Name 'SortOrder' -Default ([System.Windows.Forms.SortOrder]::None)
    if ($sortColumn -lt 0 -or $sortOrder -eq [System.Windows.Forms.SortOrder]::None) { return }

    Sort-ListViewByColumn -List $List -Column $sortColumn -Order $sortOrder
}

function Register-ResultsGridColumnSorting {
    param([System.Windows.Forms.DataGridView]$Grid)

    $Script:ResultsGridRows = @()
    $Script:ResultsGridSortColumn = $null
    $Script:ResultsGridSortAscending = $true

    $Grid.Add_ColumnHeaderMouseClick({
        param($sender, $e)

        if ($e.ColumnIndex -lt 0 -or (Get-SafeCollectionCount $Script:ResultsGridRows) -eq 0) { return }

        $columnName = [string]$sender.Columns[$e.ColumnIndex].Name
        if ($Script:ResultsGridSortColumn -eq $columnName) {
            $Script:ResultsGridSortAscending = -not $Script:ResultsGridSortAscending
        }
        else {
            $Script:ResultsGridSortColumn = $columnName
            $Script:ResultsGridSortAscending = $true
        }

        $sorted = if ($Script:ResultsGridSortAscending) {
            $Script:ResultsGridRows | Sort-Object -Property $columnName
        }
        else {
            $Script:ResultsGridRows | Sort-Object -Property $columnName -Descending
        }

        $sender.DataSource = $null
        $sender.DataSource = @($sorted)
        if ($sender.Columns.Count -gt 0) {
            $sender.Columns['Model'].FillWeight = 180
            $sender.Columns['BestMode'].FillWeight = 70
        }
    })
}

function New-FlatButton {
    param(
        [System.Windows.Forms.Control]$Parent,
        [string]$Text,
        [int]$X,
        [int]$Y,
        [int]$Width,
        [int]$Height,
        [scriptblock]$OnClick,
        [System.Drawing.Color]$BackColor = $colors.Button,
        [System.Drawing.Color]$ForeColor = $colors.Text,
        [System.Drawing.Color]$HoverColor = $colors.ButtonHover
    )

    $button = New-Object System.Windows.Forms.Button
    $button.Text = $Text
    $button.Location = New-Object System.Drawing.Point($X, $Y)
    $button.Size = New-Object System.Drawing.Size($Width, $Height)
    $button.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $button.FlatAppearance.BorderSize = 1
    $button.FlatAppearance.BorderColor = $colors.PanelBorder
    $button.BackColor = $BackColor
    $button.ForeColor = $ForeColor
    $button.Font = New-ToolkitFont
    $button.Cursor = [System.Windows.Forms.Cursors]::Hand
    $button.Tag = @{ NormalColor = $BackColor; HoverColor = $HoverColor }
    $button.Add_MouseEnter({
        $c = $this.Tag
        if ($c -and $c.HoverColor) { $this.BackColor = $c.HoverColor }
    })
    $button.Add_MouseLeave({
        $c = $this.Tag
        if ($c -and $c.NormalColor) { $this.BackColor = $c.NormalColor }
    })
    if ($OnClick) { $button.Add_Click($OnClick) }
    $Parent.Controls.Add($button) | Out-Null
    return $button
}

function Get-ToolbarButtonHeight {
    $font = New-ToolkitFont
    return [math]::Max(34, [System.Windows.Forms.TextRenderer]::MeasureText('Ag', $font).Height + 16)
}

function New-ToolbarFlowRow {
    param(
        [System.Windows.Forms.TableLayoutPanel]$Table,
        [int]$Row
    )

    $flow = New-Object System.Windows.Forms.FlowLayoutPanel
    $flow.Dock = [System.Windows.Forms.DockStyle]::Fill
    $flow.FlowDirection = [System.Windows.Forms.FlowDirection]::LeftToRight
    $flow.WrapContents = $false
    $flow.AutoSize = $false
    $flow.AutoScroll = $true
    $flow.Margin = New-Object System.Windows.Forms.Padding(0)
    $flow.Padding = New-Object System.Windows.Forms.Padding(0)
    [void]$Table.Controls.Add($flow, 0, $Row)
    return $flow
}

function New-ToolbarPanel {
    param([int]$Rows = 1)

    $rowHeight = Get-ToolbarButtonHeight
    $panel = New-Object System.Windows.Forms.Panel
    $panel.BackColor = $colors.Bg
    $panel.Margin = New-Object System.Windows.Forms.Padding(0)
    $panel.Padding = New-Object System.Windows.Forms.Padding(0)
    $panel.Height = ($rowHeight * $Rows) + 8
    $panel.MinimumSize = New-Object System.Drawing.Size(0, $panel.Height)

    $table = New-Object System.Windows.Forms.TableLayoutPanel
    $table.Dock = [System.Windows.Forms.DockStyle]::Fill
    $table.ColumnCount = 1
    $table.RowCount = $Rows
    $table.Margin = New-Object System.Windows.Forms.Padding(0)
    $table.Padding = New-Object System.Windows.Forms.Padding(0, 4, 0, 4)
    for ($i = 0; $i -lt $Rows; $i++) {
        [void]$table.RowStyles.Add((New-Object System.Windows.Forms.RowStyle(
            [System.Windows.Forms.SizeType]::Absolute, $rowHeight)))
    }
    $panel.Controls.Add($table)

    $flowRows = New-Object System.Collections.Generic.List[System.Windows.Forms.FlowLayoutPanel]
    for ($i = 0; $i -lt $Rows; $i++) {
        [void]$flowRows.Add((New-ToolbarFlowRow -Table $table -Row $i))
    }

    return [pscustomobject]@{
        Panel    = $panel
        Table    = $table
        RowHeight = $rowHeight
        Rows     = @($flowRows)
    }
}

function Get-SplitContainerTagValue {
    param(
        $Tag,
        [string]$Name
    )

    if ($null -eq $Tag) { return $null }
    if ($Tag -is [System.Collections.IDictionary]) {
        if ($Tag.Contains($Name)) { return $Tag[$Name] }
        return $null
    }
    $prop = $Tag.PSObject.Properties[$Name]
    if ($prop) { return $prop.Value }
    return $null
}

function Get-ListViewSortTagValue {
    param(
        $Tag,
        [string]$Name,
        $Default = $null
    )

    if ($null -eq $Tag) { return $Default }
    if ($Tag -is [System.Collections.IDictionary]) {
        if ($Tag.Contains($Name)) { return $Tag[$Name] }
        return $Default
    }
    $prop = $Tag.PSObject.Properties[$Name]
    if ($prop) { return $prop.Value }
    return $Default
}

function Set-ListViewSortTagValue {
    param(
        $Tag,
        [string]$Name,
        $Value
    )

    if ($null -eq $Tag) { return }
    if ($Tag -is [System.Collections.IDictionary]) {
        $Tag[$Name] = $Value
        return
    }
    $Tag | Add-Member -NotePropertyName $Name -NotePropertyValue $Value -Force
}

function Initialize-SplitContainerDistance {
    param(
        [System.Windows.Forms.SplitContainer]$Split,
        [int]$PreferredDistance = 0,
        [double]$PreferredRatio = 0
    )

    if (-not $Split) { return }

    $tag = $null
    if ($Split.Tag) {
        if ($Split.Tag -is [System.Collections.IDictionary]) { $tag = $Split.Tag }
        elseif ($Split.Tag -is [hashtable]) { $tag = $Split.Tag }
    }
    if (-not $tag) {
        $tag = @{}
        $Split.Tag = $tag
    }
    if ($PreferredDistance -gt 0) { $tag['PreferredDistance'] = $PreferredDistance }
    if ($PreferredRatio -gt 0) { $tag['PreferredRatio'] = $PreferredRatio }

    $isHorizontal = ($Split.Orientation -eq [System.Windows.Forms.Orientation]::Horizontal)
    $total = if ($isHorizontal) { $Split.Height } else { $Split.Width }
    $minTotal = $Split.Panel1MinSize + $Split.Panel2MinSize + $Split.SplitterWidth
    if ($total -le $minTotal) {
        try { $Split.SplitterDistance = $Split.Panel1MinSize } catch { }
        return
    }

    $max = $total - $Split.SplitterWidth - $Split.Panel2MinSize
    $distance = $Split.Panel1MinSize
    $tagRatio = Get-SplitContainerTagValue -Tag $tag -Name 'PreferredRatio'
    $tagDistance = Get-SplitContainerTagValue -Tag $tag -Name 'PreferredDistance'
    if ($tagRatio -and [double]$tagRatio -gt 0) {
        $distance = [int]($total * [double]$tagRatio)
    }
    elseif ($tagDistance -and [int]$tagDistance -gt 0) {
        $distance = [int]$tagDistance
    }
    elseif ($PreferredDistance -gt 0) {
        $distance = $PreferredDistance
    }
    elseif ($PreferredRatio -gt 0) {
        $distance = [int]($total * $PreferredRatio)
    }

    $distance = [math]::Max($Split.Panel1MinSize, [math]::Min($max, $distance))
    try {
        if ($Split.SplitterDistance -ne $distance) {
            $Split.SplitterDistance = $distance
        }
    }
    catch { }
}

function New-TabPageRootLayout {
    param(
        [System.Windows.Forms.TabPage]$TabPage,
        [System.Windows.Forms.Control]$Content,
        [System.Windows.Forms.Control[]]$TopBars = @(),
        [System.Windows.Forms.Control[]]$BottomBars = @()
    )

    $TabPage.Controls.Clear()
    $TabPage.Padding = New-Object System.Windows.Forms.Padding(0)

    $topCount = @($TopBars).Count
    $bottomCount = @($BottomBars).Count
    $rowCount = $topCount + 1 + $bottomCount

    $layout = New-Object System.Windows.Forms.TableLayoutPanel
    $layout.Dock = [System.Windows.Forms.DockStyle]::Fill
    $layout.ColumnCount = 1
    $layout.RowCount = $rowCount
    $layout.Padding = New-Object System.Windows.Forms.Padding(8)
    $layout.Margin = New-Object System.Windows.Forms.Padding(0)

    $rowIndex = 0
    foreach ($bar in $TopBars) {
        $barHeight = [math]::Max($bar.MinimumSize.Height, $bar.Height)
        if ($barHeight -le 0) { $barHeight = $bar.PreferredSize.Height }
        if ($barHeight -le 0) { $barHeight = 28 }
        $bar.Dock = [System.Windows.Forms.DockStyle]::Fill
        [void]$layout.RowStyles.Add((New-Object System.Windows.Forms.RowStyle(
            [System.Windows.Forms.SizeType]::Absolute, $barHeight)))
        [void]$layout.Controls.Add($bar, 0, $rowIndex)
        $rowIndex++
    }

    [void]$layout.RowStyles.Add((New-Object System.Windows.Forms.RowStyle(
        [System.Windows.Forms.SizeType]::Percent, 100)))
    if ($Content -is [System.Windows.Forms.SplitContainer]) {
        Initialize-SplitContainerDistance -Split $Content
    }
    try {
        $content.Dock = [System.Windows.Forms.DockStyle]::Fill
    }
    catch {
        if ($Content -is [System.Windows.Forms.SplitContainer]) {
            $Content.Panel1MinSize = 25
            $Content.Panel2MinSize = 25
            Initialize-SplitContainerDistance -Split $Content
        }
        $content.Dock = [System.Windows.Forms.DockStyle]::Fill
    }
    [void]$layout.Controls.Add($Content, 0, $rowIndex)
    if ($Content -is [System.Windows.Forms.SplitContainer]) {
        Initialize-SplitContainerDistance -Split $Content
    }
    $rowIndex++

    foreach ($bar in $BottomBars) {
        $barHeight = [math]::Max($bar.MinimumSize.Height, $bar.Height)
        if ($barHeight -le 0) { $barHeight = $bar.PreferredSize.Height }
        if ($barHeight -le 0) { $barHeight = Get-ToolbarButtonHeight }
        $bar.Dock = [System.Windows.Forms.DockStyle]::Fill
        [void]$layout.RowStyles.Add((New-Object System.Windows.Forms.RowStyle(
            [System.Windows.Forms.SizeType]::Absolute, $barHeight)))
        [void]$layout.Controls.Add($bar, 0, $rowIndex)
        $rowIndex++
    }

    $TabPage.Controls.Add($layout)
    return $layout
}

function New-FlowButton {
    param(
        [System.Windows.Forms.Control]$Parent,
        [string]$Text,
        [int]$Width = 0,
        [scriptblock]$OnClick,
        [System.Drawing.Color]$BackColor = $colors.Button,
        [System.Drawing.Color]$ForeColor = $colors.Text,
        [System.Drawing.Color]$HoverColor = $colors.ButtonHover
    )

    $button = New-Object System.Windows.Forms.Button
    $button.Text = $Text
    $button.AutoSize = $false
    $button.Font = New-ToolkitFont
    $button.Height = Get-ToolbarButtonHeight
    if ($Width -gt 0) {
        $button.Width = $Width
    }
    else {
        $textWidth = [System.Windows.Forms.TextRenderer]::MeasureText($Text, $button.Font).Width + 24
        $button.Width = [math]::Max(72, $textWidth)
    }
    $button.Margin = New-Object System.Windows.Forms.Padding(0, 0, 6, 0)
    $button.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $button.FlatAppearance.BorderSize = 1
    $button.FlatAppearance.BorderColor = $colors.PanelBorder
    $button.BackColor = $BackColor
    $button.ForeColor = $ForeColor
    $button.Cursor = [System.Windows.Forms.Cursors]::Hand
    $button.Tag = @{ NormalColor = $BackColor; HoverColor = $HoverColor }
    $button.Add_MouseEnter({
        $c = $this.Tag
        if ($c -and $c.HoverColor) { $this.BackColor = $c.HoverColor }
    })
    $button.Add_MouseLeave({
        $c = $this.Tag
        if ($c -and $c.NormalColor) { $this.BackColor = $c.NormalColor }
    })
    if ($OnClick) { $button.Add_Click($OnClick) }
    $Parent.Controls.Add($button) | Out-Null
    return $button
}

function Add-ToolbarField {
    param(
        [System.Windows.Forms.Control]$Parent,
        [string]$LabelText,
        [System.Windows.Forms.Control]$FieldControl,
        [int]$FieldWidth = 0
    )

    if ($LabelText) {
        $label = New-Object System.Windows.Forms.Label
        $label.Text = $LabelText
        $label.AutoSize = $true
        $label.ForeColor = $colors.Muted
        $label.Margin = New-Object System.Windows.Forms.Padding(0, 8, 4, 0)
        $label.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
        [void]$Parent.Controls.Add($label)
    }

    if ($FieldControl) {
        $FieldControl.Height = Get-ToolbarButtonHeight
        if ($FieldWidth -gt 0) { $FieldControl.Width = $FieldWidth }
        $FieldControl.Margin = New-Object System.Windows.Forms.Padding(0, 0, 8, 0)
        [void]$Parent.Controls.Add($FieldControl)
    }
}

function New-ModeCard {
    param(
        [string]$ModeKey,
        [string]$Title,
        [string]$Subtitle
    )

    $panel = New-Object System.Windows.Forms.Panel
    $panel.Dock = [System.Windows.Forms.DockStyle]::Fill
    $panel.Margin = New-Object System.Windows.Forms.Padding(6)
    $panel.BackColor = $colors.Panel
    $panel.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
    $panel.Tag = $ModeKey
    $panel.Cursor = [System.Windows.Forms.Cursors]::Hand
    $panel.Padding = New-Object System.Windows.Forms.Padding(12, 10, 12, 10)

    $titleLabel = New-Object System.Windows.Forms.Label
    $titleLabel.Text = $Title
    $titleLabel.Dock = [System.Windows.Forms.DockStyle]::Top
    $titleLabel.Height = 28
    $titleLabel.ForeColor = $colors.Text
    $titleLabel.Font = New-ToolkitFont -Size 12 -Style Bold
    $titleLabel.BackColor = [System.Drawing.Color]::Transparent

    $subLabel = New-Object System.Windows.Forms.Label
    $subLabel.Text = $Subtitle
    $subLabel.Dock = [System.Windows.Forms.DockStyle]::Fill
    $subLabel.ForeColor = $colors.Muted
    $subLabel.Font = New-ToolkitFont -Size 8.5
    $subLabel.BackColor = [System.Drawing.Color]::Transparent

    $panel.Controls.Add($subLabel)
    $panel.Controls.Add($titleLabel)
    return $panel
}

# --- Main form ---
$form = New-Object System.Windows.Forms.Form
$form.Text = 'Ollama AMD Vulkan Manager'
$form.Size = New-Object System.Drawing.Size(980, 720)
$form.MinimumSize = New-Object System.Drawing.Size(900, 640)
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
$form.WindowState = [System.Windows.Forms.FormWindowState]::Maximized
$form.BackColor = $colors.Bg
$form.ForeColor = $colors.Text
$form.Font = New-ToolkitFont

$alertPanel = New-Object System.Windows.Forms.Panel
$alertPanel.Dock = [System.Windows.Forms.DockStyle]::Top
$alertPanel.Height = 0
$alertPanel.BackColor = $colors.WarningBg
$alertPanel.Visible = $false

$alertLabel = New-Object System.Windows.Forms.Label
$alertLabel.Dock = [System.Windows.Forms.DockStyle]::Fill
$alertLabel.ForeColor = $colors.Warning
$alertLabel.Font = New-ToolkitFont -Size 9
$alertLabel.Padding = New-Object System.Windows.Forms.Padding(12, 8, 12, 8)
$alertLabel.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
$alertPanel.Controls.Add($alertLabel)

$tabControl = New-Object System.Windows.Forms.TabControl
$tabControl.Dock = [System.Windows.Forms.DockStyle]::Fill
$tabControl.Font = New-ToolkitFont -Size 9.5

$tabModes = New-Object System.Windows.Forms.TabPage
$tabModes.Text = 'Compute Modes'
$tabModes.BackColor = $colors.Bg
$tabModes.Padding = New-Object System.Windows.Forms.Padding(0)

$tabModels = New-Object System.Windows.Forms.TabPage
$tabModels.Text = 'Models & Launch'
$tabModels.BackColor = $colors.Bg
$tabModels.Padding = New-Object System.Windows.Forms.Padding(0)

$tabRegistry = New-Object System.Windows.Forms.TabPage
$tabRegistry.Text = 'Model Library'
$tabRegistry.BackColor = $colors.Bg
$tabRegistry.Padding = New-Object System.Windows.Forms.Padding(0)

$tabTesting = New-Object System.Windows.Forms.TabPage
$tabTesting.Text = 'Testing Suite'
$tabTesting.BackColor = $colors.Bg
$tabTesting.Padding = New-Object System.Windows.Forms.Padding(0)

$tabResults = New-Object System.Windows.Forms.TabPage
$tabResults.Text = 'Test Results'
$tabResults.BackColor = $colors.Bg
$tabResults.Padding = New-Object System.Windows.Forms.Padding(0)

$tabAiActivity = New-Object System.Windows.Forms.TabPage
$tabAiActivity.Text = 'AI Activity'
$tabAiActivity.BackColor = $colors.Bg
$tabAiActivity.Padding = New-Object System.Windows.Forms.Padding(0)

$tabControl.TabPages.AddRange(@($tabModes, $tabModels, $tabRegistry, $tabTesting, $tabResults, $tabAiActivity))

$footerPanel = New-Object System.Windows.Forms.Panel
$footerPanel.Dock = [System.Windows.Forms.DockStyle]::Bottom
$footerPanel.Height = 34
$footerPanel.BackColor = $colors.Panel
$footerPanel.Padding = New-Object System.Windows.Forms.Padding(8, 4, 8, 4)

$aiStatusHintLabel = New-Object System.Windows.Forms.Label
$aiStatusHintLabel.Dock = [System.Windows.Forms.DockStyle]::Fill
$aiStatusHintLabel.Text = 'Model lists load from Ollama API and cached ollama.com catalog. Start Ollama if lists are empty.'
$aiStatusHintLabel.ForeColor = $colors.Muted
$aiStatusHintLabel.Font = New-ToolkitFont -Size 8.5
$aiStatusHintLabel.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft

$aiStatusBtn = New-Object System.Windows.Forms.Button
$aiStatusBtn.Dock = [System.Windows.Forms.DockStyle]::Right
$aiStatusBtn.Width = 132
$aiStatusBtn.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$aiStatusBtn.FlatAppearance.BorderSize = 1
$aiStatusBtn.FlatAppearance.BorderColor = $colors.PanelBorder
$aiStatusBtn.Font = New-ToolkitFont -Size 9
$aiStatusBtn.Cursor = [System.Windows.Forms.Cursors]::Hand
$aiStatusBtn.Text = 'AI: checking...'
$aiStatusBtn.Tag = @{
    NormalColor = $colors.Button
    HoverColor  = $colors.ButtonHover
}
$aiStatusBtn.Add_MouseEnter({
    $c = $this.Tag
    if ($c -and $c.HoverColor) { $this.BackColor = $c.HoverColor }
})
$aiStatusBtn.Add_MouseLeave({
    $c = $this.Tag
    if ($c -and $c.NormalColor) { $this.BackColor = $c.NormalColor }
})
$aiStatusBtn.Add_Click({ Show-AiStatusDetails })

$footerPanel.Controls.Add($aiStatusHintLabel)
$footerPanel.Controls.Add($aiStatusBtn)

$formLayout = New-Object System.Windows.Forms.TableLayoutPanel
$formLayout.Dock = [System.Windows.Forms.DockStyle]::Fill
$formLayout.ColumnCount = 1
$formLayout.RowCount = 3
$formLayout.Padding = New-Object System.Windows.Forms.Padding(0)
$formLayout.Margin = New-Object System.Windows.Forms.Padding(0)
[void]$formLayout.RowStyles.Add((New-Object System.Windows.Forms.RowStyle(
    [System.Windows.Forms.SizeType]::Absolute, 0)))
[void]$formLayout.RowStyles.Add((New-Object System.Windows.Forms.RowStyle(
    [System.Windows.Forms.SizeType]::Percent, 100)))
[void]$formLayout.RowStyles.Add((New-Object System.Windows.Forms.RowStyle(
    [System.Windows.Forms.SizeType]::Absolute, 38)))
$alertPanel.Dock = [System.Windows.Forms.DockStyle]::Fill
$tabControl.Dock = [System.Windows.Forms.DockStyle]::Fill
$footerPanel.Dock = [System.Windows.Forms.DockStyle]::Fill
[void]$formLayout.Controls.Add($alertPanel, 0, 0)
[void]$formLayout.Controls.Add($tabControl, 0, 1)
[void]$formLayout.Controls.Add($footerPanel, 0, 2)
$form.Controls.Add($formLayout)
$Script:FormLayout = $formLayout

# --- Modes tab ---
$map = $Script:DeviceMap

$modesToolbar = New-ToolbarPanel -Rows 1
$modesBtnPanel = $modesToolbar.Panel
$modesToolbarRow = $modesToolbar.Rows[0]

$modesSplit = New-Object System.Windows.Forms.SplitContainer
$modesSplit.Dock = [System.Windows.Forms.DockStyle]::Fill
$modesSplit.Orientation = [System.Windows.Forms.Orientation]::Horizontal
$modesSplit.Panel1MinSize = 25
$modesSplit.Panel2MinSize = 25
$modesSplit.Tag = @{
    PreferredRatio = 0.55
    Panel1MinSize  = 260
    Panel2MinSize  = 140
}

$modesHeaderPanel = New-Object System.Windows.Forms.Panel
$modesHeaderPanel.Dock = [System.Windows.Forms.DockStyle]::Fill
$modesHeaderPanel.BackColor = $colors.Bg

$deviceLabel = New-Object System.Windows.Forms.Label
$deviceLabel.Text = "Vulkan: APU=$($map.ApuVulkanIndex) (680M), GPU=$($map.GpuVulkanIndex) (6700S)  |  Task Manager: GPU 0=6700S, GPU 1=680M"
$deviceLabel.Dock = [System.Windows.Forms.DockStyle]::Top
$deviceLabel.Height = 22
$deviceLabel.ForeColor = $colors.Muted
$deviceLabel.Font = New-ToolkitFont -Size 8.5
$deviceLabel.AutoEllipsis = $true

$statusPanel = New-Object System.Windows.Forms.Panel
$statusPanel.Dock = [System.Windows.Forms.DockStyle]::Top
$statusPanel.Height = 88
$statusPanel.BackColor = $colors.Panel
$statusPanel.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
$statusPanel.Padding = New-Object System.Windows.Forms.Padding(12, 8, 12, 8)

$currentModeLabel = New-Object System.Windows.Forms.Label
$currentModeLabel.Dock = [System.Windows.Forms.DockStyle]::Top
$currentModeLabel.Height = 24
$currentModeLabel.Font = New-ToolkitFont -Size 11 -Style Bold
$currentModeLabel.Text = 'Current mode: -'

$ollamaStatusLabel = New-Object System.Windows.Forms.Label
$ollamaStatusLabel.Dock = [System.Windows.Forms.DockStyle]::Top
$ollamaStatusLabel.Height = 20
$ollamaStatusLabel.ForeColor = $colors.Muted
$ollamaStatusLabel.Text = 'Ollama: checking...'

$workaroundStatusLabel = New-Object System.Windows.Forms.Label
$workaroundStatusLabel.Dock = [System.Windows.Forms.DockStyle]::Top
$workaroundStatusLabel.Height = 20
$workaroundStatusLabel.ForeColor = $colors.Muted
$workaroundStatusLabel.Text = '680M Vulkan workaround: checking...'

$statusPanel.Controls.AddRange(@($workaroundStatusLabel, $ollamaStatusLabel, $currentModeLabel))

$modeHost = New-Object System.Windows.Forms.Panel
$modeHost.Dock = [System.Windows.Forms.DockStyle]::Fill
$modeHost.BackColor = $colors.Bg

$modeTable = New-Object System.Windows.Forms.TableLayoutPanel
$modeTable.Dock = [System.Windows.Forms.DockStyle]::Fill
$modeTable.ColumnCount = 2
$modeTable.RowCount = 2
$modeTable.Padding = New-Object System.Windows.Forms.Padding(0, 4, 0, 0)
[void]$modeTable.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle([System.Windows.Forms.SizeType]::Percent, 50)))
[void]$modeTable.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle([System.Windows.Forms.SizeType]::Percent, 50)))
[void]$modeTable.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::Percent, 50)))
[void]$modeTable.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::Percent, 50)))

$modeCards = @{}
$modeCards['CPU'] = New-ModeCard -ModeKey 'CPU' -Title 'CPU Only' `
    -Subtitle 'Ryzen 6900HX CPU inference.'
$modeCards['APU'] = New-ModeCard -ModeKey 'APU' -Title 'APU (680M)' `
    -Subtitle "680M iGPU Vulkan $($map.ApuVulkanIndex). Needs OLLAMA_IGPU_ENABLE."
$modeCards['GPU'] = New-ModeCard -ModeKey 'GPU' -Title 'GPU (6700S)' `
    -Subtitle "RX 6700S Vulkan $($map.GpuVulkanIndex). Fastest for most models."
$modeCards['Hybrid'] = New-ModeCard -ModeKey 'Hybrid' -Title 'Hybrid' `
    -Subtitle "Both GPUs ($($map.HybridVulkanValue)). Split scheduling."
[void]$modeTable.Controls.Add($modeCards['CPU'], 0, 0)
[void]$modeTable.Controls.Add($modeCards['APU'], 1, 0)
[void]$modeTable.Controls.Add($modeCards['GPU'], 0, 1)
[void]$modeTable.Controls.Add($modeCards['Hybrid'], 1, 1)
$modeHost.Controls.Add($modeTable)

$modesRestartPanel = New-Object System.Windows.Forms.Panel
$modesRestartPanel.Dock = [System.Windows.Forms.DockStyle]::Bottom
$modesRestartPanel.Height = 28
$modesRestartPanel.BackColor = $colors.Bg

$restartCheck = New-Object System.Windows.Forms.CheckBox
$restartCheck.Text = 'Restart Ollama after applying mode'
$restartCheck.Dock = [System.Windows.Forms.DockStyle]::Fill
$restartCheck.Checked = $true
$modesRestartPanel.Controls.Add($restartCheck)

$modesHeaderPanel.Controls.Add($modeHost)
$modesHeaderPanel.Controls.Add($modesRestartPanel)
$modesHeaderPanel.Controls.Add($statusPanel)
$modesHeaderPanel.Controls.Add($deviceLabel)
$modesSplit.Panel1.Controls.Add($modesHeaderPanel)

$modesDetailSplit = New-Object System.Windows.Forms.SplitContainer
$modesDetailSplit.Dock = [System.Windows.Forms.DockStyle]::Fill
$modesDetailSplit.Orientation = [System.Windows.Forms.Orientation]::Horizontal
$modesDetailSplit.Panel1MinSize = 72
$modesDetailSplit.Panel2MinSize = 72
$modesDetailSplit.Tag = @{ PreferredRatio = 0.4 }

$envBox = New-Object System.Windows.Forms.TextBox
$envBox.Dock = [System.Windows.Forms.DockStyle]::Fill
$envBox.Multiline = $true
$envBox.ReadOnly = $true
$envBox.ScrollBars = [System.Windows.Forms.ScrollBars]::Vertical
$envBox.BackColor = $colors.LogBg
$envBox.ForeColor = $colors.Text
$envBox.Font = New-Object System.Drawing.Font('Consolas', 8.5)

$modesLogBox = New-Object System.Windows.Forms.TextBox
$modesLogBox.Dock = [System.Windows.Forms.DockStyle]::Fill
$modesLogBox.Multiline = $true
$modesLogBox.ReadOnly = $true
$modesLogBox.ScrollBars = [System.Windows.Forms.ScrollBars]::Vertical
$modesLogBox.BackColor = $colors.LogBg
$modesLogBox.ForeColor = $colors.Muted
$modesLogBox.Font = New-Object System.Drawing.Font('Consolas', 8.5)

$modesDetailSplit.Panel1.Controls.Add($envBox)
$modesDetailSplit.Panel2.Controls.Add($modesLogBox)
$modesSplit.Panel2.Controls.Add($modesDetailSplit)

New-TabPageRootLayout -TabPage $tabModes -Content $modesSplit -BottomBars @($modesBtnPanel) | Out-Null

# --- Models tab ---
$modelsList = New-Object System.Windows.Forms.ListView
$modelsList.Dock = [System.Windows.Forms.DockStyle]::Fill
$modelsList.View = [System.Windows.Forms.View]::Details
$modelsList.FullRowSelect = $true
$modelsList.GridLines = $true
$modelsList.BackColor = $colors.LogBg
$modelsList.ForeColor = $colors.Text
$modelsList.Font = New-Object System.Drawing.Font('Segoe UI', 9)
$modelsList.Columns.Add('Model', 200) | Out-Null
$modelsList.Columns.Add('Size', 56) | Out-Null
$modelsList.Columns.Add('Param Size', 72) | Out-Null
$modelsList.Columns.Add('Best Mode', 72) | Out-Null
$modelsList.Columns.Add('tok/s', 48) | Out-Null
$modelsList.Columns.Add('Status', 110) | Out-Null
$modelsList.Columns.Add('Last Tested', 100) | Out-Null
$null = $modelsList.Columns.Add('Description', -2)

$modelDescLabel = New-Object System.Windows.Forms.Label
$modelDescLabel.Text = 'Description (from ollama.com)'
$modelDescLabel.Dock = [System.Windows.Forms.DockStyle]::Top
$modelDescLabel.Height = 22
$modelDescLabel.ForeColor = $colors.Muted
$modelDescLabel.Font = New-ToolkitFont -Size 9 -Style Bold

$modelDescBox = New-Object System.Windows.Forms.TextBox
$modelDescBox.Dock = [System.Windows.Forms.DockStyle]::Fill
$modelDescBox.Multiline = $true
$modelDescBox.ReadOnly = $true
$modelDescBox.ScrollBars = [System.Windows.Forms.ScrollBars]::Vertical
$modelDescBox.BackColor = $colors.LogBg
$modelDescBox.ForeColor = $colors.Text
$modelDescBox.Font = New-Object System.Drawing.Font('Segoe UI', 9)
$modelDescBox.Text = 'Select a model to view its ollama.com description.'

$modelsSplit = New-Object System.Windows.Forms.SplitContainer
$modelsSplit.Dock = [System.Windows.Forms.DockStyle]::Fill
$modelsSplit.Orientation = [System.Windows.Forms.Orientation]::Horizontal
$modelsSplit.Tag = @{ PreferredRatio = 0.45 }
$modelsSplit.Panel1.Controls.Add($modelsList)
$modelsSplit.Panel2.Controls.Add($modelDescLabel)
$modelsSplit.Panel2.Controls.Add($modelDescBox)

$modelsToolbar = New-ToolbarPanel -Rows 1
$modelsBottom = $modelsToolbar.Panel
$modelsToolbarRow = $modelsToolbar.Rows[0]

New-TabPageRootLayout -TabPage $tabModels -Content $modelsSplit -BottomBars @($modelsBottom) | Out-Null

# --- Model Library tab ---
$registryInstalledLabel = New-Object System.Windows.Forms.Label
$registryInstalledLabel.Text = 'Installed Models'
$registryInstalledLabel.Dock = [System.Windows.Forms.DockStyle]::Top
$registryInstalledLabel.Height = 22
$registryInstalledLabel.ForeColor = $colors.Muted
$registryInstalledLabel.Font = New-ToolkitFont -Size 9 -Style Bold

$registryInstalledList = New-Object System.Windows.Forms.ListView
$registryInstalledList.Dock = [System.Windows.Forms.DockStyle]::Fill
$registryInstalledList.View = [System.Windows.Forms.View]::Details
$registryInstalledList.HeaderStyle = [System.Windows.Forms.ColumnHeaderStyle]::Clickable
$registryInstalledList.FullRowSelect = $true
$registryInstalledList.GridLines = $true
$registryInstalledList.BackColor = $colors.LogBg
$registryInstalledList.ForeColor = $colors.Text
$registryInstalledList.Font = New-Object System.Drawing.Font('Segoe UI', 9)
$registryInstalledList.Columns.Add('Model', 180) | Out-Null
$registryInstalledList.Columns.Add('Size', 70) | Out-Null
$registryInstalledList.Columns.Add('Param Size', 72) | Out-Null
$registryInstalledList.Columns.Add('Type', 60) | Out-Null
$registryInstalledList.Columns.Add('Modified', 100) | Out-Null
$null = $registryInstalledList.Columns.Add('Description', -2)

$registryInstalledHost = New-Object System.Windows.Forms.Panel
$registryInstalledHost.Dock = [System.Windows.Forms.DockStyle]::Fill
# ListView before Top-docked label so headers are not covered (WinForms dock z-order).
$registryInstalledHost.Controls.Add($registryInstalledList)
$registryInstalledHost.Controls.Add($registryInstalledLabel)

$registryCatalogLabel = New-Object System.Windows.Forms.Label
$registryCatalogLabel.Text = 'Available on ollama.com'
$registryCatalogLabel.Dock = [System.Windows.Forms.DockStyle]::Top
$registryCatalogLabel.Height = 22
$registryCatalogLabel.ForeColor = $colors.Muted
$registryCatalogLabel.Font = New-ToolkitFont -Size 9 -Style Bold

$registryCatalogList = New-Object System.Windows.Forms.ListView
$registryCatalogList.Dock = [System.Windows.Forms.DockStyle]::Fill
$registryCatalogList.View = [System.Windows.Forms.View]::Details
$registryCatalogList.HeaderStyle = [System.Windows.Forms.ColumnHeaderStyle]::Clickable
$registryCatalogList.FullRowSelect = $true
$registryCatalogList.GridLines = $true
$registryCatalogList.BackColor = $colors.LogBg
$registryCatalogList.ForeColor = $colors.Text
$registryCatalogList.Font = New-Object System.Drawing.Font('Segoe UI', 9)
$registryCatalogList.Columns.Add('Model', 150) | Out-Null
$registryCatalogList.Columns.Add('Size', 72) | Out-Null
$registryCatalogList.Columns.Add('Param Size', 80) | Out-Null
$registryCatalogList.Columns.Add('Installed', 64) | Out-Null
$registryCatalogList.Columns.Add('Date Updated', 100) | Out-Null
$null = $registryCatalogList.Columns.Add('Description', -2)

$registryTagCombo = New-Object System.Windows.Forms.ComboBox
$registryTagCombo.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$registryTagCombo.BackColor = $colors.LogBg
$registryTagCombo.ForeColor = $colors.Text
$registryTagCombo.Width = 280

$registrySearchBox = New-Object System.Windows.Forms.TextBox
$registrySearchBox.BackColor = $colors.LogBg
$registrySearchBox.ForeColor = $colors.Text
$registrySearchBox.Width = 260

$registryStatusLabel = New-Object System.Windows.Forms.Label
$registryStatusLabel.Dock = [System.Windows.Forms.DockStyle]::Bottom
$registryStatusLabel.Height = 22
$registryStatusLabel.ForeColor = $colors.Muted
$registryStatusLabel.Text = 'Browse, download, or remove models from ollama.com.'

$registryProgress = New-Object System.Windows.Forms.ProgressBar
$registryProgress.Dock = [System.Windows.Forms.DockStyle]::Bottom
$registryProgress.Height = 6
$registryProgress.Style = [System.Windows.Forms.ProgressBarStyle]::Marquee
$registryProgress.Visible = $false

$registryToolbar = New-ToolbarPanel -Rows 1
$registryBottom = $registryToolbar.Panel
$registryToolbarRow = $registryToolbar.Rows[0]
Add-ToolbarField -Parent $registryToolbarRow -LabelText 'Variant:' -FieldControl $registryTagCombo -FieldWidth 200
$registrySearchBtn = New-FlowButton -Parent $registryToolbarRow -Text 'Search' -Width 72 `
    -OnClick { Invoke-RegistryCatalogSearch }
Add-ToolbarField -Parent $registryToolbarRow -FieldControl $registrySearchBox -FieldWidth 180

$registryFooter = New-Object System.Windows.Forms.Panel
$registryFooter.BackColor = $colors.Bg
$registryFooter.Height = $registryBottom.Height + 28
$registryFooter.MinimumSize = New-Object System.Drawing.Size(0, $registryFooter.Height)

$registryFooterLayout = New-Object System.Windows.Forms.TableLayoutPanel
$registryFooterLayout.Dock = [System.Windows.Forms.DockStyle]::Fill
$registryFooterLayout.ColumnCount = 1
$registryFooterLayout.RowCount = 3
$registryFooterLayout.Margin = New-Object System.Windows.Forms.Padding(0)
$registryFooterLayout.Padding = New-Object System.Windows.Forms.Padding(0)
[void]$registryFooterLayout.RowStyles.Add((New-Object System.Windows.Forms.RowStyle(
    [System.Windows.Forms.SizeType]::Absolute, $registryBottom.Height)))
[void]$registryFooterLayout.RowStyles.Add((New-Object System.Windows.Forms.RowStyle(
    [System.Windows.Forms.SizeType]::Absolute, 6)))
[void]$registryFooterLayout.RowStyles.Add((New-Object System.Windows.Forms.RowStyle(
    [System.Windows.Forms.SizeType]::Absolute, 22)))
$registryBottom.Dock = [System.Windows.Forms.DockStyle]::Fill
$registryProgress.Dock = [System.Windows.Forms.DockStyle]::Fill
$registryStatusLabel.Dock = [System.Windows.Forms.DockStyle]::Fill
[void]$registryFooterLayout.Controls.Add($registryBottom, 0, 0)
[void]$registryFooterLayout.Controls.Add($registryProgress, 0, 1)
[void]$registryFooterLayout.Controls.Add($registryStatusLabel, 0, 2)
$registryFooter.Controls.Add($registryFooterLayout)

$registryCatalogHost = New-Object System.Windows.Forms.Panel
$registryCatalogHost.Dock = [System.Windows.Forms.DockStyle]::Fill
$registryCatalogHost.Controls.Add($registryCatalogList)
$registryCatalogHost.Controls.Add($registryCatalogLabel)

$registrySplit = New-Object System.Windows.Forms.SplitContainer
$registrySplit.Dock = [System.Windows.Forms.DockStyle]::Fill
$registrySplit.Orientation = [System.Windows.Forms.Orientation]::Horizontal
$registrySplit.IsSplitterFixed = $false
$registrySplit.FixedPanel = [System.Windows.Forms.FixedPanel]::None
$registrySplit.SplitterWidth = 8
$registrySplit.SplitterIncrement = 1
$registrySplit.Panel1MinSize = 25
$registrySplit.Panel2MinSize = 25
$registrySplit.Tag = @{ PreferredRatio = 0.35 }
$registrySplit.Panel1.Controls.Add($registryInstalledHost)
$registrySplit.Panel2.Controls.Add($registryCatalogHost)

New-TabPageRootLayout -TabPage $tabRegistry -Content $registrySplit -BottomBars @($registryFooter) | Out-Null

Enable-UiDoubleBuffering -Control $tabRegistry
$registrySplit.BackColor = $colors.PanelBorder
$registryInstalledHost.BackColor = $colors.Bg
$registryCatalogHost.BackColor = $colors.Bg
$registrySplit.Panel1.BackColor = $colors.Bg
$registrySplit.Panel2.BackColor = $colors.Bg

$Script:RegistryCatalog = @()
$Script:RegistryCatalogAll = @()
$Script:ResultsGridRows = @()
$Script:ResultsGridSortColumn = $null
$Script:ResultsGridSortAscending = $true
$Script:RegistryOnlyOperations = @(
    'PullRegistryModel', 'RemoveRegistryModel',
    'RefreshRegistryInstalled', 'LoadRegistryTags', 'ShowRegistryDescription'
)
$Script:RegistryBackgroundJob = $null
$Script:RegistryBackgroundTimer = $null
$Script:RegistryCatalogForceWeb = $false
$Script:RegistryCatalogRefreshDescriptions = $false
$Script:DescriptionBackgroundJob = $null
$Script:DescriptionBackgroundTimer = $null
$Script:MetadataSyncMaxBatch = 6
$Script:MetadataSyncCooldownSec = 120
$Script:MetadataSyncLastFinished = [datetime]::MinValue
$Script:LastAiStatusUiUpdate = [datetime]::MinValue

# --- Testing tab ---
$testOptionsPanel = New-Object System.Windows.Forms.Panel
$testOptionsPanel.Dock = [System.Windows.Forms.DockStyle]::Top
$testOptionsPanel.Height = 72
$testOptionsPanel.BackColor = $colors.Bg

$testModelLabel = New-Object System.Windows.Forms.Label
$testModelLabel.Text = 'Model:'
$testModelLabel.Location = New-Object System.Drawing.Point(0, 8)
$testModelLabel.Size = New-Object System.Drawing.Size(48, 20)
$testOptionsPanel.Controls.Add($testModelLabel)

$testModelCombo = New-Object System.Windows.Forms.ComboBox
$testModelCombo.Location = New-Object System.Drawing.Point(52, 5)
$testModelCombo.Size = New-Object System.Drawing.Size(280, 24)
$testModelCombo.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$testOptionsPanel.Controls.Add($testModelCombo)

$testCtxLabel = New-Object System.Windows.Forms.Label
$testCtxLabel.Text = 'num_ctx:'
$testCtxLabel.Location = New-Object System.Drawing.Point(344, 8)
$testCtxLabel.Size = New-Object System.Drawing.Size(52, 20)
$testOptionsPanel.Controls.Add($testCtxLabel)

$testCtxNumeric = New-Object System.Windows.Forms.NumericUpDown
$testCtxNumeric.Location = New-Object System.Drawing.Point(400, 5)
$testCtxNumeric.Size = New-Object System.Drawing.Size(72, 24)
$testCtxNumeric.Minimum = 2048
$testCtxNumeric.Maximum = 32768
$testCtxNumeric.Increment = 1024
$testCtxNumeric.Value = 8192
$testOptionsPanel.Controls.Add($testCtxNumeric)

$testTokensLabel = New-Object System.Windows.Forms.Label
$testTokensLabel.Text = 'tokens:'
$testTokensLabel.Location = New-Object System.Drawing.Point(484, 8)
$testTokensLabel.Size = New-Object System.Drawing.Size(48, 20)
$testOptionsPanel.Controls.Add($testTokensLabel)

$testTokensNumeric = New-Object System.Windows.Forms.NumericUpDown
$testTokensNumeric.Location = New-Object System.Drawing.Point(536, 5)
$testTokensNumeric.Size = New-Object System.Drawing.Size(56, 24)
$testTokensNumeric.Minimum = 8
$testTokensNumeric.Maximum = 256
$testTokensNumeric.Value = 32
$testOptionsPanel.Controls.Add($testTokensNumeric)

$testStatusLabel = New-Object System.Windows.Forms.Label
$testStatusLabel.Location = New-Object System.Drawing.Point(0, 40)
$testStatusLabel.Size = New-Object System.Drawing.Size(860, 22)
$testStatusLabel.ForeColor = $colors.Muted
$testStatusLabel.Text = 'Ready - select a model and run a 4-mode benchmark suite.'
$testOptionsPanel.Controls.Add($testStatusLabel)

$testProgress = New-Object System.Windows.Forms.ProgressBar
$testProgress.Dock = [System.Windows.Forms.DockStyle]::Top
$testProgress.Height = 6
$testProgress.Style = [System.Windows.Forms.ProgressBarStyle]::Marquee
$testProgress.MarqueeAnimationSpeed = 30
$testProgress.Visible = $false

$testLogBox = New-Object System.Windows.Forms.TextBox
$testLogBox.Dock = [System.Windows.Forms.DockStyle]::Fill
$testLogBox.Multiline = $true
$testLogBox.ReadOnly = $true
$testLogBox.ScrollBars = [System.Windows.Forms.ScrollBars]::Vertical
$testLogBox.BackColor = $colors.LogBg
$testLogBox.ForeColor = [System.Drawing.Color]::FromArgb(200, 210, 220)
$testLogBox.Font = New-Object System.Drawing.Font('Consolas', 9)
$testLogBox.WordWrap = $false

$testToolbar = New-ToolbarPanel -Rows 1
$testBtnPanel = $testToolbar.Panel
$testToolbarRow = $testToolbar.Rows[0]

$testLogHost = New-Object System.Windows.Forms.Panel
$testLogHost.Dock = [System.Windows.Forms.DockStyle]::Fill
$testLogHost.Controls.Add($testLogBox)
New-TabPageRootLayout -TabPage $tabTesting -Content $testLogHost `
    -TopBars @($testOptionsPanel, $testProgress) -BottomBars @($testBtnPanel) | Out-Null

# --- Results tab ---
$resultsGrid = New-Object System.Windows.Forms.DataGridView
$resultsGrid.Dock = [System.Windows.Forms.DockStyle]::Fill
$resultsGrid.BackgroundColor = $colors.LogBg
$resultsGrid.ForeColor = $colors.Text
$resultsGrid.GridColor = $colors.PanelBorder
$resultsGrid.BorderStyle = [System.Windows.Forms.BorderStyle]::None
$resultsGrid.ReadOnly = $true
$resultsGrid.AllowUserToAddRows = $false
$resultsGrid.AllowUserToDeleteRows = $false
$resultsGrid.RowHeadersVisible = $false
$resultsGrid.AutoSizeColumnsMode = [System.Windows.Forms.DataGridViewAutoSizeColumnsMode]::Fill
$resultsGrid.SelectionMode = [System.Windows.Forms.DataGridViewSelectionMode]::FullRowSelect
$resultsGrid.DefaultCellStyle.BackColor = $colors.LogBg
$resultsGrid.DefaultCellStyle.ForeColor = $colors.Text
$resultsGrid.DefaultCellStyle.SelectionBackColor = $colors.Panel
$resultsGrid.ColumnHeadersDefaultCellStyle.BackColor = $colors.Panel
$resultsGrid.ColumnHeadersDefaultCellStyle.ForeColor = $colors.Text
$resultsGrid.EnableHeadersVisualStyles = $false

$resultsToolbar = New-ToolbarPanel -Rows 1
$resultsBtnPanel = $resultsToolbar.Panel
$resultsToolbarRow = $resultsToolbar.Rows[0]

$resultsHost = New-Object System.Windows.Forms.Panel
$resultsHost.Dock = [System.Windows.Forms.DockStyle]::Fill
$resultsHost.Controls.Add($resultsGrid)
New-TabPageRootLayout -TabPage $tabResults -Content $resultsHost -BottomBars @($resultsBtnPanel) | Out-Null

# --- AI Activity tab ---
$aiActivityStatusLabel = New-Object System.Windows.Forms.Label
$aiActivityStatusLabel.Dock = [System.Windows.Forms.DockStyle]::Top
$aiActivityStatusLabel.Height = 24
$aiActivityStatusLabel.ForeColor = $colors.Muted
$aiActivityStatusLabel.Text = 'Monitoring AI background tasks...'

$aiActivityLogBox = New-Object System.Windows.Forms.TextBox
$aiActivityLogBox.Dock = [System.Windows.Forms.DockStyle]::Fill
$aiActivityLogBox.Multiline = $true
$aiActivityLogBox.ReadOnly = $true
$aiActivityLogBox.ScrollBars = [System.Windows.Forms.ScrollBars]::Vertical
$aiActivityLogBox.BackColor = $colors.LogBg
$aiActivityLogBox.ForeColor = [System.Drawing.Color]::FromArgb(190, 220, 190)
$aiActivityLogBox.Font = New-Object System.Drawing.Font('Consolas', 9)
$aiActivityLogBox.WordWrap = $false

$aiActivityToolbar = New-ToolbarPanel -Rows 1
$aiActivityBtnPanel = $aiActivityToolbar.Panel
$aiActivityToolbarRow = $aiActivityToolbar.Rows[0]

$aiActivityHost = New-Object System.Windows.Forms.Panel
$aiActivityHost.Dock = [System.Windows.Forms.DockStyle]::Fill
$aiActivityHost.Controls.Add($aiActivityLogBox)
$aiActivityHost.Controls.Add($aiActivityStatusLabel)
New-TabPageRootLayout -TabPage $tabAiActivity -Content $aiActivityHost -BottomBars @($aiActivityBtnPanel) | Out-Null

$aiActivityClearBtn = New-FlowButton -Parent $aiActivityToolbarRow -Text 'Clear Log' -Width 90 `
    -OnClick {
        Initialize-GuiAiActivityLog
        $Script:AiActivityLogPosition = 0
        $aiActivityLogBox.Clear()
        Add-AiActivityUiLine -Line '=== AI activity log cleared ==='
        Update-AiActivityStatusPanel
    }

# --- Shared state ---
$Script:GuiBusy = $false
$Script:GuiShuttingDown = $false
$Script:GuiPostShowStarted = $false
$Script:BenchmarkImportJob = $null
$Script:PendingOperation = $null
$Script:GuiWorkerJob = $null
$Script:GuiWorkerTimer = $null
$Script:RegistryCatalogLoadTimer = $null
$Script:RegistryCatalogLoadState = $null
$Script:RegistryInstalledCache = $null
$Script:RegistryInstalledCacheTime = $null
$Script:AiActivityLogPosition = 0
$Script:RegistryTabInitPending = $false
$Script:ModeCardControls = $modeCards
$Script:ActiveLogBox = $modesLogBox
$Script:TestLogPosition = 0
$Script:NewModelPromptShown = @{}
$Script:TestingControls = @()

function Update-AlertPanelLayout {
    param([bool]$Visible)

    if ($form.InvokeRequired) {
        $form.Invoke([Action[bool]] { param($v) Update-AlertPanelLayout -Visible $v }, $Visible) | Out-Null
        return
    }

    $height = if ($Visible) { 36 } else { 0 }
    if ($Script:FormLayout -and $Script:FormLayout.RowStyles.Count -gt 0) {
        $Script:FormLayout.RowStyles[0].Height = $height
    }
    $alertPanel.Visible = $Visible
    $alertPanel.Height = $height
}

function Add-GuiLog {
    param(
        [string]$Message,
        [System.Windows.Forms.TextBox]$Target = $null
    )

    if ($form.InvokeRequired) {
        $form.Invoke([Action[String, Object]] {
            param($m, $t)
            Add-GuiLog -Message $m -Target $t
        }, $Message, $Target) | Out-Null
        return
    }

    $box = if ($Target) { $Target } else { $Script:ActiveLogBox }
    if (-not $box) { $box = $modesLogBox }
    $stamp = (Get-Date).ToString('HH:mm:ss')
    $box.AppendText("[$stamp] $Message`r`n")
    $box.SelectionStart = $box.TextLength
    $box.ScrollToCaret()
    $testLogBox.AppendText("[$stamp] $Message`r`n")
    $testLogBox.SelectionStart = $testLogBox.TextLength
    $testLogBox.ScrollToCaret()
}

$Script:UiPumpAction = { [System.Windows.Forms.Application]::DoEvents() }
$Script:LogAction = {
    param([string]$Text, [ConsoleColor]$ForegroundColor)
    if ([string]::IsNullOrWhiteSpace($Text)) { return }
    Write-GuiAiActivity -Message $Text -Category 'AI'
    Add-GuiLog -Message $Text
}

function Invoke-SafeTimerTick {
    param(
        [scriptblock]$Action,
        [string]$Context = 'Timer'
    )

    if ($Script:GuiShuttingDown) { return }

    try {
        $previousEap = $ErrorActionPreference
        $ErrorActionPreference = 'Stop'
        try {
            & $Action
        }
        finally {
            $ErrorActionPreference = $previousEap
        }
    }
    catch {
        if (-not $Script:GuiShuttingDown) {
            $msg = "$Context error: $($_.Exception.Message)"
            Add-GuiLog -Message $msg
            Write-GuiAiActivity -Message $msg -Category 'Error'
        }
    }
}

function Update-AiStatusButtonThrottled {
    param([int]$MinIntervalMs = 2500)

    $elapsed = ((Get-Date) - $Script:LastAiStatusUiUpdate).TotalMilliseconds
    if ($elapsed -lt $MinIntervalMs) { return }

    $Script:LastAiStatusUiUpdate = Get-Date
    Update-AiStatusButton
}

function Stop-AllGuiBackgroundActivity {
    $Script:GuiShuttingDown = $true
    $Script:UiPumpAction = $null

    foreach ($timer in @(
            $statusTimer,
            $testPollTimer,
            $aiActivityPollTimer,
            $aiStatusTimer,
            $descStartupTimer,
            $registryTabInitTimer,
            $Script:GuiPostShowTimer,
            $Script:GuiDeferredRefreshTimer,
            $Script:GuiFullRefreshTimer,
            $Script:RegistryCatalogLoadTimer,
            $Script:DescriptionBackgroundTimer,
            $Script:RegistryBackgroundTimer,
            $Script:GuiWorkerTimer
        )) {
        if ($timer) { $timer.Stop() }
    }

    Stop-RegistryCatalogListLoad
    Stop-GuiBenchmarkProcess

    foreach ($job in @($Script:GuiWorkerJob, $Script:RegistryBackgroundJob, $Script:DescriptionBackgroundJob)) {
        if (-not $job) { continue }
        Stop-Job -Job $job -ErrorAction SilentlyContinue
        Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
    }
    $Script:GuiWorkerJob = $null
    $Script:RegistryBackgroundJob = $null
    $Script:DescriptionBackgroundJob = $null
    if ($Script:BenchmarkImportJob) {
        Stop-Job -Job $Script:BenchmarkImportJob -ErrorAction SilentlyContinue
        Remove-Job -Job $Script:BenchmarkImportJob -Force -ErrorAction SilentlyContinue
        $Script:BenchmarkImportJob = $null
    }
}

function Dispose-AllGuiTimers {
    foreach ($timer in @(
            $statusTimer,
            $testPollTimer,
            $aiActivityPollTimer,
            $aiStatusTimer,
            $descStartupTimer,
            $registryTabInitTimer,
            $Script:GuiPostShowTimer,
            $Script:GuiDeferredRefreshTimer,
            $Script:GuiFullRefreshTimer,
            $Script:RegistryCatalogLoadTimer,
            $Script:DescriptionBackgroundTimer,
            $Script:RegistryBackgroundTimer,
            $Script:GuiWorkerTimer
        )) {
        if ($timer) {
            $timer.Stop()
            $timer.Dispose()
        }
    }
}

function Stop-OllamaOnGuiExit {
    if ((Get-SafeCollectionCount (Get-OllamaProcesses)) -eq 0) { return }

    $previousLogAction = $Script:LogAction
    $Script:LogAction = $null
    try {
        Stop-OllamaProcesses -Quick
    }
    catch {
        Write-Host "Warning: could not stop Ollama on exit: $($_.Exception.Message)" -ForegroundColor Yellow
    }
    finally {
        $Script:LogAction = $previousLogAction
    }
}

function Add-AiActivityUiLine {
    param([string]$Line)

    if ($form.InvokeRequired) {
        $form.BeginInvoke([Action[String]] { param($l) Add-AiActivityUiLine -Line $l }, $Line) | Out-Null
        return
    }
    if ([string]::IsNullOrWhiteSpace($Line)) { return }
    $aiActivityLogBox.AppendText("$Line`r`n")
    $aiActivityLogBox.SelectionStart = $aiActivityLogBox.TextLength
    $aiActivityLogBox.ScrollToCaret()
}

function Update-AiActivityStatusPanel {
    if ($Script:GuiShuttingDown) { return }

    if ($form.InvokeRequired) {
        $form.BeginInvoke([Action] { Update-AiActivityStatusPanel }) | Out-Null
        return
    }

    $status = Get-GuiAiStatus
    $parts = New-Object System.Collections.Generic.List[string]
    [void]$parts.Add($status.Detail)
    if ((Get-SafeCollectionCount $status.BackgroundTasks) -gt 0) {
        [void]$parts.Add("Running: $(@($status.BackgroundTasks) -join ', ')")
    }
    $aiActivityStatusLabel.Text = ($parts -join ' | ')
}

function Set-GuiBusy {
    param(
        [bool]$Busy,
        [switch]$RegistryOperation
    )

    if ($form.InvokeRequired) {
        $form.Invoke([Action[bool, bool]] { param($b, $r) Set-GuiBusy -Busy $b -RegistryOperation:$r }, $Busy, $RegistryOperation.IsPresent) | Out-Null
        return
    }

    $Script:GuiBusy = $Busy
    $testProgress.Visible = $Busy -and -not $RegistryOperation
    $registryProgress.Visible = $Busy -and ($RegistryOperation -or $tabControl.SelectedTab -eq $tabRegistry)

    foreach ($card in $Script:ModeCardControls.Values) { $card.Enabled = -not $Busy }
    $restartCheck.Enabled = -not $Busy

    $registryUi = @(
        $registrySearchBtn, $registryRefreshCatalogBtn, $registryDownloadBtn, $registryRemoveBtn,
        $registryRefreshInstalledBtn, $registryOpenWebBtn, $registrySearchBox, $registryTagCombo
    )
    foreach ($ctrl in $registryUi) {
        if ($null -ne $ctrl) { $ctrl.Enabled = -not $Busy }
    }
    foreach ($ctrl in $Script:TestingControls) {
        if ($null -eq $ctrl) { continue }
        if ($RegistryOperation -and $ctrl -in @($registryInstalledList, $registryCatalogList)) { continue }
        $ctrl.Enabled = -not $Busy
    }
    if ($Busy) {
        $testStopButton.Enabled = -not $RegistryOperation
    }
}

function Update-ModeCards {
    param([string]$ActiveMode)
    if ($form.InvokeRequired) {
        $form.Invoke([Action[String]] { param($m) Update-ModeCards -ActiveMode $m }, $ActiveMode) | Out-Null
        return
    }
    foreach ($entry in $Script:ModeCardControls.GetEnumerator()) {
        $card = $entry.Value
        if ($entry.Key -eq $ActiveMode) { $card.BackColor = $colors.ActiveBg }
        else { $card.BackColor = $colors.Panel }
    }
}

function Get-GuiAiStatus {
    $localModels = @(Get-ToolkitLocalOllamaModels)
    $localNames = @($localModels | ForEach-Object { $_.name })
    $localCount = Get-SafeCollectionCount $localModels
    $apiReady = Test-OllamaApiReadyCached -TtlSec 10
    $summarizer = $null
    if ($apiReady) {
        $summarizer = Get-DescriptionSummarizerModel -ExcludeModelName '' -LocalModelNames $localNames
    }

    $bgTasks = New-Object System.Collections.Generic.List[string]
    if ($Script:DescriptionBackgroundJob) {
        $job = $Script:DescriptionBackgroundJob
        if ($job.State -eq 'Running') {
            [void]$bgTasks.Add('descriptions')
        }
    }
    if ($Script:RegistryBackgroundJob) {
        $job = $Script:RegistryBackgroundJob
        if ($job.State -eq 'Running') {
            [void]$bgTasks.Add('catalog refresh')
        }
    }
    if ($Script:GuiWorkerJob) {
        $job = $Script:GuiWorkerJob
        if ($job.State -eq 'Running') {
            [void]$bgTasks.Add('GUI worker')
        }
    }
    $bgBusy = ((Get-SafeCollectionCount $bgTasks) -gt 0)

    $state = 'Inactive'
    $label = 'AI Inactive'
    $detail = 'Ollama API not reachable. Start Ollama to enable AI features.'

    if ($bgBusy) {
        $state = 'Busy'
        $label = 'AI Busy'
        $detail = "Background AI running: $($bgTasks -join ', ')."
    }
    elseif ($apiReady -and $summarizer) {
        $state = 'Active'
        $label = 'AI Active'
        $detail = "Local summarizer ready: $summarizer"
    }
    elseif ($apiReady) {
        $detail = 'Ollama API is ready but no suitable local summarizer model is installed.'
        if ($localCount -eq 0) {
            $detail += ' Pull a small model (e.g. llama3.2:3b) to enable AI features.'
        }
    }

    return [pscustomobject]@{
        State           = $state
        Label           = $label
        Detail          = $detail
        ApiReady        = $apiReady
        SummarizerModel = $summarizer
        LocalModelCount = $localCount
        BackgroundBusy  = $bgBusy
        BackgroundTasks = @($bgTasks.ToArray())
    }
}

function Update-AiStatusButton {
    if ($form.InvokeRequired) {
        $form.Invoke([Action] { Update-AiStatusButton }) | Out-Null
        return
    }
    if (-not $aiStatusBtn) { return }

    $status = Get-GuiAiStatus
    $aiStatusBtn.Text = $status.Label

    switch ($status.State) {
        'Active' {
            $normal = $colors.ActiveBg
            $hover = [System.Drawing.Color]::FromArgb(30, 72, 48)
            $aiStatusBtn.ForeColor = $colors.Active
        }
        'Busy' {
            $normal = $colors.WarningBg
            $hover = [System.Drawing.Color]::FromArgb(72, 58, 22)
            $aiStatusBtn.ForeColor = $colors.Warning
        }
        default {
            $normal = $colors.Button
            $hover = $colors.ButtonHover
            $aiStatusBtn.ForeColor = $colors.Muted
        }
    }

    $aiStatusBtn.BackColor = $normal
    $aiStatusBtn.Tag = @{
        NormalColor = $normal
        HoverColor  = $hover
        Status      = $status
    }
}

function Show-AiStatusDetails {
    Update-AiStatusButton
    $status = Get-GuiAiStatus
    $lines = New-Object System.Collections.Generic.List[string]
    [void]$lines.Add($status.Label)
    [void]$lines.Add('')
    [void]$lines.Add($status.Detail)
    [void]$lines.Add("Ollama API: $(if ($status.ApiReady) { 'ready' } else { 'not ready' })")
    [void]$lines.Add("Local models installed: $($status.LocalModelCount)")
    if ($status.SummarizerModel) {
        [void]$lines.Add("Summarizer model: $($status.SummarizerModel)")
    }
    if ($status.BackgroundBusy) {
        [void]$lines.Add("Background tasks: $($status.BackgroundTasks -join ', ')")
    }
    [void]$lines.Add('')
    [void]$lines.Add('AI features: model descriptions and catalog classification.')

    [System.Windows.Forms.MessageBox]::Show(
        ($lines -join [Environment]::NewLine),
        'AI Status',
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Information
    ) | Out-Null
}

function Update-StatusPanel {
    param($Status)
    if ($form.InvokeRequired) {
        $form.Invoke([Action[Object]] { param($s) Update-StatusPanel -Status $s }, $Status) | Out-Null
        return
    }

    $currentModeLabel.Text = "Current mode: $($Status.ModeLabel)"
    $currentModeLabel.ForeColor = if ($Status.DetectedMode -eq 'Custom/Unknown') { $colors.Warning } else { $colors.Text }

    if ($Status.ApiReady) {
        $ollamaStatusLabel.Text = 'Ollama: running, API ready'
        $ollamaStatusLabel.ForeColor = $colors.Active
    }
    elseif ($Status.OllamaRunning) {
        $ollamaStatusLabel.Text = 'Ollama: running, API not ready'
        $ollamaStatusLabel.ForeColor = $colors.Warning
    }
    else {
        $ollamaStatusLabel.Text = 'Ollama: not running'
        $ollamaStatusLabel.ForeColor = $colors.Muted
    }

    if ($Status.VulkanWorkaround.Active) {
        $workaroundStatusLabel.Text = '680M Vulkan workaround: ACTIVE'
        $workaroundStatusLabel.ForeColor = $colors.Active
    }
    elseif ($Status.VulkanWorkaround.NeedsReapply) {
        $workaroundStatusLabel.Text = '680M Vulkan workaround: NEEDS RE-APPLY'
        $workaroundStatusLabel.ForeColor = $colors.Warning
    }
    else {
        $workaroundStatusLabel.Text = '680M Vulkan workaround: not applied'
        $workaroundStatusLabel.ForeColor = $colors.Muted
    }

    $envLines = New-Object System.Collections.Generic.List[string]
    foreach ($name in $Script:ManagedVars) {
        $value = $Status.UserVariables[$name]
        if ([string]::IsNullOrWhiteSpace($value)) { $value = '(not set)' }
        $envLines.Add(('{0,-28} {1}' -f $name, $value))
    }
    $envBox.Text = ($envLines -join [Environment]::NewLine)
    Update-ModeCards -ActiveMode $Status.DetectedMode
    Update-AiStatusButton
}

function Get-ModelsListDescriptionCharBudget {
    if ($modelsList.Columns.Count -eq 0) {
        return $Script:ListDescriptionMaxChars
    }

    $descIndex = $modelsList.Columns.Count - 1
    $descWidth = $modelsList.Columns[$descIndex].Width
    if ($descWidth -le 0) {
        $fixedWidth = 0
        for ($i = 0; $i -lt $descIndex; $i++) {
            $colWidth = $modelsList.Columns[$i].Width
            if ($colWidth -gt 0) { $fixedWidth += $colWidth }
        }
        $descWidth = [math]::Max(180, $modelsList.ClientSize.Width - $fixedWidth - 24)
    }

    return [math]::Max(40, [int]($descWidth / 6.5))
}

function Resize-ModelsListColumns {
    if ($form.InvokeRequired) {
        $form.Invoke([Action] { Resize-ModelsListColumns }) | Out-Null
        return
    }

    if ($modelsList.Columns.Count -lt 2) { return }
    $modelsList.Columns[$modelsList.Columns.Count - 1].Width = -2
}

function Refresh-ModelsUi {
    param([switch]$Fast)

    if ($Script:GuiShuttingDown) { return }

    if ($form.InvokeRequired) {
        $form.Invoke([Action[bool]] { param($f) Refresh-ModelsUi -Fast:$f }, $Fast.IsPresent) | Out-Null
        return
    }

    Resize-ModelsListColumns
    $summaries = Get-AllModelProfileSummaries
    $modelsList.Items.Clear()
    $testModelCombo.Items.Clear()

    foreach ($s in $summaries) {
        $item = New-Object System.Windows.Forms.ListViewItem($s.Model)
        $item.SubItems.Add(("{0} GB" -f $s.SizeGB)) | Out-Null
        $item.SubItems.Add($(if ($s.ParameterSize) { $s.ParameterSize } else { '-' })) | Out-Null
        $item.SubItems.Add($(if ($s.BestMode) { $s.BestMode } else { '-' })) | Out-Null
        $item.SubItems.Add($(if ($s.BestTps) { [string][math]::Round($s.BestTps, 2) } else { '-' })) | Out-Null
        $item.SubItems.Add($s.Status) | Out-Null
        $item.SubItems.Add($(if ($s.LastTested) { $s.LastTested } else { '-' })) | Out-Null
        if ($Fast) {
            $item.SubItems.Add('-') | Out-Null
        }
        else {
            $item.SubItems.Add((Get-ModelListDescription -ModelName $s.Model)) | Out-Null
        }
        $item.Tag = $s
        if ($s.NeedsRetest) { $item.ForeColor = $colors.Warning }
        elseif ($s.BestMode) { $item.ForeColor = $colors.Active }
        $modelsList.Items.Add($item) | Out-Null
        [void]$testModelCombo.Items.Add($s.Model)
    }

    if ($testModelCombo.Items.Count -gt 0 -and $testModelCombo.SelectedIndex -lt 0) {
        $testModelCombo.SelectedIndex = 0
    }

    $untested = @($summaries | Where-Object { $_.NeedsRetest })
    $untestedCount = Get-SafeCollectionCount $untested
    if ($untestedCount -gt 0) {
        $names = ($untested | ForEach-Object { $_.Model }) -join ', '
        $alertLabel.Text = "⚠ $untestedCount model(s) need benchmark testing: $names"
        Update-AlertPanelLayout -Visible $true
    }
    else {
        Update-AlertPanelLayout -Visible $false
    }

    Refresh-ResultsGrid
    Reapply-ListViewSort -List $modelsList
}

$Script:RegistrySplitLayoutFile = Join-Path $Script:ConfigDir 'registry-split-layout.json'
$Script:RegistrySplitAutoSizing = $false

function Get-RegistrySplitRatio {
    Ensure-ConfigDirectory
    if (-not (Test-Path -LiteralPath $Script:RegistrySplitLayoutFile)) {
        return 0.35
    }

    try {
        $data = Get-Content -LiteralPath $Script:RegistrySplitLayoutFile -Raw | ConvertFrom-Json
        $ratio = [double]$data.SplitterRatio
        if ($ratio -ge 0.15 -and $ratio -le 0.85) { return $ratio }
    }
    catch { }

    return 0.35
}

function Save-RegistrySplitLayout {
    Ensure-ConfigDirectory
    if ($registrySplit.Height -le ($registrySplit.Panel1MinSize + $registrySplit.Panel2MinSize + $registrySplit.SplitterWidth)) {
        return
    }

    $ratio = [math]::Round($registrySplit.SplitterDistance / $registrySplit.Height, 4)
    $payload = [ordered]@{ SplitterRatio = $ratio }
    $payload | ConvertTo-Json | Set-Content -LiteralPath $Script:RegistrySplitLayoutFile -Encoding UTF8
}

function Update-RegistryInstalledSplitHeight {
    if ($form.InvokeRequired) {
        $form.Invoke([Action] { Update-RegistryInstalledSplitHeight }) | Out-Null
        return
    }

    if ($registrySplit.Height -le ($registrySplit.Panel1MinSize + $registrySplit.Panel2MinSize + $registrySplit.SplitterWidth)) {
        return
    }

    $labelHeight = $registryInstalledLabel.Height
    $rowHeight = [System.Windows.Forms.TextRenderer]::MeasureText('Ag', $registryInstalledList.Font).Height + 4
    $headerHeight = if ($registryInstalledList.HeaderStyle -ne [System.Windows.Forms.ColumnHeaderStyle]::None) {
        $rowHeight
    }
    else { 0 }
    $itemCount = $registryInstalledList.Items.Count
    $rows = if ($itemCount -gt 0) { $itemCount } else { 1 }
    $desired = $labelHeight + $headerHeight + ($rowHeight * $rows) + 6

    $minDistance = $registrySplit.Panel1MinSize
    $maxDistance = $registrySplit.Height - $registrySplit.Panel2MinSize - $registrySplit.SplitterWidth
    if ($maxDistance -lt $minDistance) {
        Initialize-SplitContainerDistance -Split $registrySplit -PreferredRatio (Get-RegistrySplitRatio)
        return
    }

    $maxPanel1ByRatio = [int](($registrySplit.Height - $registrySplit.SplitterWidth) * 0.5)
    $target = [math]::Max($minDistance, [math]::Min($maxDistance, [math]::Min($desired, $maxPanel1ByRatio)))

    $Script:RegistrySplitAutoSizing = $true
    try {
        $registrySplit.SplitterDistance = $target
    }
    catch {
        Initialize-SplitContainerDistance -Split $registrySplit -PreferredRatio (Get-RegistrySplitRatio)
    }
    finally {
        $Script:RegistrySplitAutoSizing = $false
    }
}

function Initialize-ModesSplitLayout {
    if ($form.InvokeRequired) {
        $form.Invoke([Action] { Initialize-ModesSplitLayout }) | Out-Null
        return
    }

    if ($modesSplit -and $modesSplit.Tag) {
        $panel1Min = Get-SplitContainerTagValue -Tag $modesSplit.Tag -Name 'Panel1MinSize'
        $panel2Min = Get-SplitContainerTagValue -Tag $modesSplit.Tag -Name 'Panel2MinSize'
        if ($panel1Min) { $modesSplit.Panel1MinSize = [int]$panel1Min }
        if ($panel2Min) { $modesSplit.Panel2MinSize = [int]$panel2Min }
    }
    Initialize-SplitContainerDistance -Split $modesSplit -PreferredRatio 0.55
    Initialize-SplitContainerDistance -Split $modesDetailSplit -PreferredRatio 0.4
    Initialize-SplitContainerDistance -Split $modelsSplit -PreferredRatio 0.45
}

function Initialize-RegistrySplitLayout {
    Initialize-SplitContainerDistance -Split $registrySplit -PreferredRatio (Get-RegistrySplitRatio)
    Update-RegistryInstalledSplitHeight
}

function Resize-RegistryListColumns {
    if ($form.InvokeRequired) {
        $form.Invoke([Action] { Resize-RegistryListColumns }) | Out-Null
        return
    }

    if ($registryInstalledList.Columns.Count -gt 0) {
        $registryInstalledList.Columns[$registryInstalledList.Columns.Count - 1].Width = -2
    }
    if ($registryCatalogList.Columns.Count -gt 0) {
        $registryCatalogList.Columns[$registryCatalogList.Columns.Count - 1].Width = -2
    }
}

function Invoke-RegistryUiBatch {
    param([scriptblock]$Action)

    if ($form.InvokeRequired) {
        $form.Invoke([Action[scriptblock]] { param($a) Invoke-RegistryUiBatch -Action $a }, $Action) | Out-Null
        return
    }

    $registrySplit.SuspendLayout()
    $registryInstalledList.BeginUpdate()
    $registryCatalogList.BeginUpdate()
    try {
        & $Action
    }
    finally {
        $registryCatalogList.EndUpdate()
        $registryInstalledList.EndUpdate()
        $registrySplit.ResumeLayout($false)
    }
}

function Stop-RegistryCatalogListLoad {
    if ($Script:RegistryCatalogLoadTimer) {
        $Script:RegistryCatalogLoadTimer.Stop()
    }
    if ($Script:RegistryCatalogLoadState) {
        $Script:RegistryCatalogLoadState.Active = $false
    }
}

function New-RegistryCatalogListItem {
    param(
        $Entry,
        $InstalledSet,
        [switch]$Fast
    )

    if ($null -eq $Entry) { return $null }

    $libraryName = [string](Get-LibraryCatalogEntryValue -Entry $Entry -Name 'Name')
    if ([string]::IsNullOrWhiteSpace($libraryName) -and $Entry.Name) {
        $libraryName = [string]$Entry.Name
    }
    if ([string]::IsNullOrWhiteSpace($libraryName)) { $libraryName = '-' }

    $item = New-Object System.Windows.Forms.ListViewItem($libraryName)
    $fileSize = [string](Get-LibraryCatalogEntryValue -Entry $Entry -Name 'FileSize')
    $paramSize = [string](Get-LibraryCatalogEntryValue -Entry $Entry -Name 'ParameterSize')
    $item.SubItems.Add($(if ($fileSize) { $fileSize } else { '-' })) | Out-Null
    $item.SubItems.Add($(if ($paramSize) { $paramSize } else { '-' })) | Out-Null
    $isInstalled = Test-LibraryModelInstalled -LibraryName $libraryName -InstalledNames $InstalledSet
    $item.SubItems.Add($(if ($isInstalled) { 'Yes' } else { '-' })) | Out-Null
    if ($Fast) {
        $dateUpdated = '-'
        $description = [string](Get-LibraryCatalogEntryValue -Entry $Entry -Name 'Description')
        if ([string]::IsNullOrWhiteSpace($description)) { $description = '-' }
    }
    else {
        $dateUpdated = Get-LibraryCatalogDateUpdatedDisplay -LibraryName $libraryName
        $description = Get-LibraryCatalogDescriptionText -LibraryName $libraryName `
            -FallbackDescription ([string](Get-LibraryCatalogEntryValue -Entry $Entry -Name 'Description'))
    }
    $item.SubItems.Add($(if ($dateUpdated) { $dateUpdated } else { '-' })) | Out-Null
    $item.SubItems.Add($(if ($description) { $description } else { '-' })) | Out-Null
    $item.Tag = $Entry
    if ($isInstalled) { $item.ForeColor = $colors.Active }
    return $item
}

function Load-RegistryCatalogList {
    param(
        [object[]]$Entries,
        [switch]$Fast
    )

    Stop-RegistryCatalogListLoad

    if ($form.InvokeRequired) {
        $entriesCopy = @($Entries)
        $fastCopy = $Fast.IsPresent
        $form.Invoke([Action] {
            Load-RegistryCatalogList -Entries $entriesCopy -Fast:$fastCopy
        }) | Out-Null
        return
    }

    $entries = @($Entries | Where-Object { $null -ne $_ })
    $registryCatalogList.Items.Clear()
    $entryCount = Get-SafeCollectionCount $entries
    if ($entryCount -eq 0) { return }

    $installedSet = Get-InstalledModelNameSet
    $registryCatalogList.BeginUpdate()
    try {
        foreach ($entry in $entries) {
            $item = New-RegistryCatalogListItem -Entry $entry -InstalledSet $installedSet -Fast:$Fast
            if ($item) {
                [void]$registryCatalogList.Items.Add($item)
            }
        }
    }
    finally {
        $registryCatalogList.EndUpdate()
    }

    Reapply-ListViewSort -List $registryCatalogList
    Resize-RegistryListColumns
    Update-RegistryHeaderLabels
    Update-RegistryInstalledSplitHeight
    Set-RegistryStatus "Showing $entryCount catalog model(s)."
}

function Start-RegistryCatalogListLoad {
    param(
        [object[]]$Entries,
        [switch]$Fast
    )

    Load-RegistryCatalogList -Entries $Entries -Fast:$Fast
}

function Get-RegistryInstalledModelsCached {
    param([switch]$ForceRefresh)

    if (-not $ForceRefresh -and $Script:RegistryInstalledCache -and $Script:RegistryInstalledCacheTime) {
        if (((Get-Date) - $Script:RegistryInstalledCacheTime).TotalSeconds -lt 20) {
            return @($Script:RegistryInstalledCache)
        }
    }

    $models = @(Get-AllInstalledOllamaModels)
    $Script:RegistryInstalledCache = $models
    $Script:RegistryInstalledCacheTime = Get-Date
    return $models
}

function Set-RegistryTagComboItems {
    param(
        [string]$LibraryName,
        [string[]]$Tags
    )

    if ($form.InvokeRequired) {
        $form.Invoke([Action[String, Object]] { param($n, $t) Set-RegistryTagComboItems -LibraryName $n -Tags $t }, $LibraryName, $Tags) | Out-Null
        return
    }

    if ($LibraryName -and $Script:RegistryTagLibrary -and $LibraryName -ne $Script:RegistryTagLibrary) {
        return
    }

    $registryTagCombo.Items.Clear()
    if ((Get-SafeCollectionCount $Tags) -eq 0) { return }
    foreach ($tag in $Tags) {
        [void]$registryTagCombo.Items.Add($tag)
    }
    $latest = "${LibraryName}:latest"
    $idx = $registryTagCombo.Items.IndexOf($latest)
    $registryTagCombo.SelectedIndex = if ($idx -ge 0) { $idx } else { 0 }
}

function Request-RegistryTagCombo {
    param([string]$LibraryName)

    if (-not $LibraryName) { return }
    $Script:RegistryTagLibrary = $LibraryName
    Set-RegistryTagComboItems -LibraryName $LibraryName -Tags @('Loading...')
    if (-not $Script:GuiBusy) {
        Start-GuiWorker -Operation 'LoadRegistryTags' -RegistryOperation
    }
}

function Refresh-RegistryInstalledList {
    param(
        [switch]$ForceApiRefresh,
        [switch]$Fast
    )

    if ($Script:GuiShuttingDown) { return }

    $installed = @(Get-RegistryInstalledModelsCached -ForceRefresh:$ForceApiRefresh)
    $registryInstalledList.Items.Clear()

    foreach ($model in $installed) {
        $item = New-Object System.Windows.Forms.ListViewItem($model.name)
        $item.SubItems.Add((Format-OllamaModelSize -SizeBytes $model.size)) | Out-Null
        $item.SubItems.Add((Get-OllamaModelParameterSizeDisplay -Model $model)) | Out-Null
        if ($Fast) {
            $item.SubItems.Add((Get-OllamaModelInstallType -Model $model)) | Out-Null
            $modified = if ($model.modified_at) {
                try { ([datetime]$model.modified_at).ToString('yyyy-MM-dd HH:mm') } catch { '-' }
            } else { '-' }
            $item.SubItems.Add($modified) | Out-Null
            $item.SubItems.Add('-') | Out-Null
        }
        else {
            $item.SubItems.Add((Get-OllamaModelInstallType -Model $model)) | Out-Null
            $modified = if ($model.modified_at) {
                try { ([datetime]$model.modified_at).ToString('yyyy-MM-dd HH:mm') } catch { '-' }
            } else { '-' }
            $item.SubItems.Add($modified) | Out-Null
            $item.SubItems.Add((Get-ModelListDescription -ModelName $model.name)) | Out-Null
        }
        $item.Tag = $model
        if ((Get-OllamaModelInstallType -Model $model) -eq 'local') {
            $item.ForeColor = $colors.Active
        }
        $registryInstalledList.Items.Add($item) | Out-Null
    }
    Reapply-ListViewSort -List $registryInstalledList
}

function Update-RegistryCatalogInstalledFlags {
    $installedSet = Get-InstalledModelNameSet
    foreach ($item in $registryCatalogList.Items) {
        $isInstalled = Test-LibraryModelInstalled -LibraryName $item.Text -InstalledNames $installedSet
        if ($item.SubItems.Count -gt 2) {
            $item.SubItems[2].Text = if ($isInstalled) { 'Yes' } else { '-' }
        }
        if ($isInstalled) { $item.ForeColor = $colors.Active }
        else { $item.ForeColor = $colors.Text }
    }
}

function Refresh-RegistryCatalogList {
    Load-RegistryCatalogList -Entries $Script:RegistryCatalog -Fast
}

function Update-RegistryHeaderLabels {
    $registryInstalledLabel.Text = "Installed Models ($($registryInstalledList.Items.Count))"
    $registryCatalogLabel.Text = "Available on ollama.com ($($registryCatalogList.Items.Count))"
}

function Refresh-RegistryUi {
    param([switch]$InstalledOnly)

    if ($form.InvokeRequired) {
        $form.Invoke([Action[bool]] { param($io) Refresh-RegistryUi -InstalledOnly:$io }, $InstalledOnly.IsPresent) | Out-Null
        return
    }

    if ($InstalledOnly) {
        Invoke-RegistryUiBatch {
            Update-RegistryCatalogInstalledFlags
            Update-RegistryHeaderLabels
        }
        return
    }

    Resize-RegistryListColumns
    Refresh-RegistryInstalledList
    Update-RegistryHeaderLabels
    Load-RegistryCatalogList -Entries $Script:RegistryCatalog -Fast
}

function Get-RegistryCatalogSourceItems {
    if ((Get-SafeCollectionCount $Script:RegistryCatalogAll) -gt 0) {
        return @($Script:RegistryCatalogAll)
    }

    $stored = @(Get-LibraryCatalogStoreItems)
    if ((Get-SafeCollectionCount $stored) -gt 0) {
        $Script:RegistryCatalogAll = $stored
    }
    return $stored
}

function Set-RegistryCatalogAll {
    param([object[]]$Items)

    $Script:RegistryCatalogAll = @($Items)
}

function Apply-RegistryCatalogDisplay {
    param(
        [string]$Search = '',
        [switch]$InstalledOnly,
        [switch]$Fast
    )

    if ($form.InvokeRequired) {
        $form.Invoke([Action[String, bool, bool]] {
            param($s, $io, $f)
            Apply-RegistryCatalogDisplay -Search $s -InstalledOnly:$io -Fast:$f
        }, $Search, $InstalledOnly.IsPresent, $Fast.IsPresent) | Out-Null
        return (Get-SafeCollectionCount (Get-RegistryCatalogSourceItems)) -gt 0
    }

    $allItems = @(Get-RegistryCatalogSourceItems)
    if ((Get-SafeCollectionCount $allItems) -eq 0) { return $false }

    $Script:RegistryCatalog = @(Filter-LibraryCatalogItems -Items $allItems -Search $Search)
    Resize-RegistryListColumns
    Refresh-RegistryInstalledList -Fast:$Fast
    Update-RegistryHeaderLabels
    if (-not $InstalledOnly -and -not $Script:GuiShuttingDown) {
        Load-RegistryCatalogList -Entries $Script:RegistryCatalog -Fast:$Fast
    }
    return $true
}

function Invoke-RegistryCatalogSearch {
    $Script:RegistrySearchQuery = $registrySearchBox.Text
    if (Apply-RegistryCatalogDisplay -Search $Script:RegistrySearchQuery) {
        $query = $Script:RegistrySearchQuery.Trim()
        if ($query) {
            Set-RegistryStatus "Showing $(Get-SafeCollectionCount $Script:RegistryCatalog) match(es) for '$query'."
        }
        else {
            Set-RegistryStatus "Showing all $(Get-SafeCollectionCount $Script:RegistryCatalog) catalog model(s)."
        }
        return
    }

    Set-RegistryStatus 'No catalog loaded; fetching in background...'
    Start-RegistryBackgroundRefresh
}

function Get-DescriptionRefreshModelNames {
    param(
        [switch]$IncludeCatalog,
        [switch]$AllInstalled
    )

    $names = New-Object System.Collections.Generic.List[string]
    if ($IncludeCatalog) {
        foreach ($item in @(Get-LibraryCatalogStoreItems)) {
            $name = [string](Get-LibraryCatalogEntryValue -Entry $item -Name 'Name')
            if ($name) { [void]$names.Add($name) }
        }
    }
    elseif ($AllInstalled) {
        foreach ($model in @(Get-ToolkitLocalOllamaModels)) {
            if ($model.name) { [void]$names.Add([string]$model.name) }
        }
    }
    else {
        foreach ($name in @(Get-MissingDescriptionModels)) {
            if ($name) { [void]$names.Add([string]$name) }
        }
    }

    return @($names | Select-Object -Unique)
}

function Get-ModelMetadataRefreshModelNames {
    param(
        [switch]$IncludeCatalog,
        [switch]$AllInstalled
    )

    if ($IncludeCatalog -or $AllInstalled) {
        return @(Get-DescriptionRefreshModelNames -IncludeCatalog:$IncludeCatalog -AllInstalled:$AllInstalled)
    }

    return @(Get-MissingDescriptionModels)
}

function Request-MetadataBackgroundSync {
    param(
        [string[]]$ModelNames = @(),
        [switch]$Force,
        [switch]$Chain,
        [string]$StatusMessage = '',
        [int]$MaxBatch = 0
    )

    if ($Script:GuiShuttingDown) { return $false }

    if ($Script:DescriptionBackgroundJob -or $Script:GuiBusy -or $Script:RegistryBackgroundJob) {
        return $false
    }

    if (-not $Force -and -not $Chain) {
        $elapsed = ((Get-Date) - $Script:MetadataSyncLastFinished).TotalSeconds
        if ($elapsed -lt $Script:MetadataSyncCooldownSec) { return $false }
    }

    $names = @($ModelNames | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ((Get-SafeCollectionCount $names) -eq 0) {
        $names = @(Get-ModelMetadataRefreshModelNames)
    }

    $batchSize = if ($MaxBatch -gt 0) { $MaxBatch } else { $Script:MetadataSyncMaxBatch }
    $names = @(Select-Object -InputObject @($names) -Unique -First $batchSize)
    $nameCount = Get-SafeCollectionCount $names
    if ($nameCount -eq 0) { return $false }

    $message = if ($StatusMessage) {
        $StatusMessage
    }
    else {
        $remaining = (Get-SafeCollectionCount (Get-ModelMetadataRefreshModelNames)) - $nameCount
        if ($remaining -gt 0) {
            "Updating metadata for $nameCount model(s) ($remaining more queued)..."
        }
        else {
            "Updating metadata for $nameCount model(s) in background..."
        }
    }

    return Start-DescriptionBackgroundSync -ModelNames $names -Force:$Force -StatusMessage $message
}

function Start-DescriptionBackgroundSync {
    param(
        [string[]]$ModelNames,
        [switch]$Force,
        [string]$StatusMessage = ''
    )

    if ($Script:DescriptionBackgroundJob) { return $false }

    $names = @(Select-Object -InputObject @($ModelNames) -Unique)
    $names = @($names | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $nameCount = Get-SafeCollectionCount $names
    if ($nameCount -eq 0) { return $false }

    $root = $Script:ToolkitRoot
    $forceSync = $Force.IsPresent
    $listMaxChars = Get-ModelsListDescriptionCharBudget
    $message = if ($StatusMessage) {
        $StatusMessage
    }
    else {
        "Updating descriptions for $nameCount model(s) in background..."
    }

    Add-GuiLog -Message $message
    if ($tabControl.SelectedTab -eq $tabRegistry) {
        Set-RegistryStatus $message
    }

    $Script:DescriptionBackgroundJob = Start-Job -ArgumentList $root, $names, $forceSync, $listMaxChars -ScriptBlock {
        param($ToolkitRoot, [string[]]$Names, [bool]$ForceRefresh, [int]$ListMaxChars)
        Set-Location $ToolkitRoot
        $Script:ToolkitRoot = $ToolkitRoot
        $Script:ListDescriptionMaxChars = $ListMaxChars
        . (Join-Path $ToolkitRoot 'Ollama-Toolkit.Core.ps1')
        . (Join-Path $ToolkitRoot 'Ollama-Toolkit.BenchmarkStore.ps1')
        . (Join-Path $ToolkitRoot 'Ollama-Toolkit.ModelCatalog.ps1')
        . (Join-Path $ToolkitRoot 'Ollama-Toolkit.GuiAiActivity.ps1')
        . (Join-Path $ToolkitRoot 'Ollama-Toolkit.ModelRegistry.ps1')
        $Script:LogAction = {
            param([string]$Text, [ConsoleColor]$ForegroundColor)
            if ($Text) { Write-GuiAiActivity -Message $Text -Category 'AI' }
        }
        $syncCount = Get-SafeCollectionCount $Names
        Write-GuiAiActivity -Message "Background sync started for $syncCount model(s)." -Category 'Task'
        $desc = Sync-LocalModelDescriptions -ModelNames @($Names) -Force:$ForceRefresh
        Write-GuiAiActivity -Message "Background sync finished for $syncCount model(s)." -Category 'Task'
        $desc
    }

    if (-not $Script:DescriptionBackgroundTimer) {
        $Script:DescriptionBackgroundTimer = New-Object System.Windows.Forms.Timer
        $Script:DescriptionBackgroundTimer.Interval = 1000
        $Script:DescriptionBackgroundTimer.Add_Tick({
            Invoke-SafeTimerTick -Context 'Description sync' -Action { Complete-DescriptionBackgroundSync }
        })
    }
    $Script:DescriptionBackgroundTimer.Start()
    Update-AiStatusButton
    return $true
}

function Complete-DescriptionBackgroundSync {
    try {
        Complete-DescriptionBackgroundSyncCore
    }
    catch {
        Add-GuiLog -Message "Description sync completion error: $($_.Exception.Message)"
    }
}

function Complete-DescriptionBackgroundSyncCore {
    $job = $Script:DescriptionBackgroundJob
    if (-not $job) { return }

    if ($job.State -eq 'Running') {
        Update-AiStatusButtonThrottled
        return
    }

    if ($Script:DescriptionBackgroundTimer) {
        $Script:DescriptionBackgroundTimer.Stop()
    }

    try {
        $sync = Receive-Job -Job $job -ErrorAction SilentlyContinue
        if ($job.State -eq 'Failed') {
            $errors = @(Receive-Job -Job $job -ErrorAction SilentlyContinue 2>&1)
            $detail = if ((Get-SafeCollectionCount $errors) -gt 0) { ($errors | ForEach-Object { "$_" }) -join ' ' } else { 'Description sync failed.' }
            Add-GuiLog -Message "ERROR: $detail"
            if ($tabControl.SelectedTab -eq $tabRegistry) {
                Set-RegistryStatus $detail
            }
        }
        elseif ($sync) {
            $Script:ListDescriptionMaxChars = Get-ModelsListDescriptionCharBudget
            $descSync = if ($sync.Descriptions) { $sync.Descriptions } else { $sync }
            $summary = "Descriptions: $($descSync.Updated) refreshed, $($descSync.Skipped) cached (LLM @ $($Script:ListDescriptionMaxChars) chars)."
            Add-GuiLog -Message $summary

            Clear-MetadataStoreCaches

            $selected = Get-SelectedModelName
            if ($selected) { Update-ModelDescriptionPanel -ModelName $selected }

            if ($tabControl.SelectedTab -eq $tabRegistry) {
                if (Apply-RegistryCatalogDisplay -Search $Script:RegistrySearchQuery) {
                    Set-RegistryStatus "$summary Showing $(Get-SafeCollectionCount $Script:RegistryCatalog) catalog model(s)."
                }
                else {
                    Set-RegistryStatus $summary
                }
            }
            elseif ($tabControl.SelectedTab -eq $tabModels) {
                Refresh-ModelsUi -Fast
            }
            else {
                Refresh-RegistryInstalledList -Fast
            }
        }
    }
    catch {
        Add-GuiLog -Message "ERROR: $($_.Exception.Message)"
    }
    finally {
        Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
        $Script:DescriptionBackgroundJob = $null
        $Script:MetadataSyncLastFinished = Get-Date
        Update-AiStatusButton
    }
}

function Complete-RegistryBackgroundRefresh {
    try {
        Complete-RegistryBackgroundRefreshCore
    }
    catch {
        Add-GuiLog -Message "Registry refresh completion error: $($_.Exception.Message)"
    }
}

function Complete-RegistryBackgroundRefreshCore {
    $job = $Script:RegistryBackgroundJob
    if (-not $job) { return }

    if ($job.State -eq 'Running') {
        Update-AiStatusButtonThrottled
        return
    }

    if ($Script:RegistryBackgroundTimer) {
        $Script:RegistryBackgroundTimer.Stop()
    }

    $message = $null
    try {
        $result = Receive-Job -Job $job -ErrorAction SilentlyContinue
        if ($job.State -eq 'Failed') {
            $errors = @(Receive-Job -Job $job -ErrorAction SilentlyContinue 2>&1)
            $message = if ((Get-SafeCollectionCount $errors) -gt 0) { ($errors | ForEach-Object { "$_" }) -join ' ' } `
                else { 'Catalog background refresh failed.' }
            Add-GuiLog -Message "ERROR: $message"
        }
        elseif ($result -and $result.Message) {
            $message = [string]$result.Message
            Add-GuiLog -Message $message
        }

        $stored = @(Get-LibraryCatalogStoreItems)
        if ((Get-SafeCollectionCount $stored) -gt 0) {
            Set-RegistryCatalogAll -Items $stored
        }
        Clear-MetadataStoreCaches
        $search = [string]$Script:RegistrySearchQuery
        if (Apply-RegistryCatalogDisplay -Search $search) {
            if (-not $message) {
                $message = "Showing $(Get-SafeCollectionCount $Script:RegistryCatalog) catalog model(s)."
            }
            Set-RegistryStatus $message
        }
        elseif ($message) {
            Set-RegistryStatus $message
        }
        else {
            Set-RegistryStatus 'Catalog refresh finished but no cached models are available.'
        }

        if ($tabControl.SelectedTab -eq $tabModels) {
            Refresh-ModelsUi -Fast
        }

        if ($Script:RegistryCatalogRefreshDescriptions) {
            Request-MetadataBackgroundSync -Force -Chain `
                -StatusMessage 'Refreshing AI metadata after catalog update...' | Out-Null
        }
    }
    catch {
        Add-GuiLog -Message "ERROR: $($_.Exception.Message)"
        Set-RegistryStatus $_.Exception.Message
        $search = [string]$Script:RegistrySearchQuery
        if (Apply-RegistryCatalogDisplay -Search $search) {
            Set-RegistryStatus "Showing cached catalog ($(Get-SafeCollectionCount $Script:RegistryCatalog) models)."
        }
    }
    finally {
        Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
        $Script:RegistryBackgroundJob = $null
        $Script:RegistryCatalogForceWeb = $false
        $Script:RegistryCatalogRefreshDescriptions = $false
        Set-GuiBusy -Busy $false -RegistryOperation
        Update-AiStatusButton
    }
}

function Start-RegistryBackgroundRefresh {
    param(
        [switch]$ForceWeb,
        [switch]$ForceReclassify,
        [switch]$RefreshDescriptions
    )

    if ($Script:RegistryBackgroundJob) { return }

    $Script:RegistryCatalogForceWeb = $ForceWeb.IsPresent
    $root = $Script:ToolkitRoot
    $forceWeb = $Script:RegistryCatalogForceWeb
    $reclassify = $ForceReclassify.IsPresent
    $refreshDescriptions = $RefreshDescriptions.IsPresent
    $Script:RegistryCatalogRefreshDescriptions = $refreshDescriptions

    Set-GuiBusy -Busy $true -RegistryOperation
    $status = if ($reclassify) {
        'Refreshing catalog and reclassifying models in background...'
    }
    elseif ($refreshDescriptions) {
        'Refreshing catalog in background (descriptions will follow)...'
    }
    elseif ($forceWeb) {
        'Refreshing catalog from ollama.com...'
    }
    else {
        'Updating catalog cache...'
    }
    Set-RegistryStatus $status

    $Script:RegistryBackgroundJob = Start-Job -ArgumentList $root, $forceWeb, $reclassify -ScriptBlock {
        param(
            $ToolkitRoot,
            [bool]$ForceWebRefresh,
            [bool]$ForceReclassifyRefresh
        )
        Set-Location $ToolkitRoot
        $Script:ToolkitRoot = $ToolkitRoot
        . (Join-Path $ToolkitRoot 'Ollama-Toolkit.Core.ps1')
        . (Join-Path $ToolkitRoot 'Ollama-Toolkit.GuiAiActivity.ps1')
        . (Join-Path $ToolkitRoot 'Ollama-Toolkit.BenchmarkStore.ps1')
        . (Join-Path $ToolkitRoot 'Ollama-Toolkit.ModelCatalog.ps1')
        . (Join-Path $ToolkitRoot 'Ollama-Toolkit.ModelRegistry.ps1')
        $Script:LogAction = {
            param([string]$Text, [ConsoleColor]$ForegroundColor)
            if ($Text) { Write-GuiAiActivity -Message $Text -Category 'Catalog' }
        }
        Write-GuiAiActivity -Message 'Catalog background refresh started.' -Category 'Task'
        $result = Update-EnrichedLibraryCatalog -ForceWeb:$ForceWebRefresh `
            -ForceReclassify:$ForceReclassifyRefresh
        Write-GuiAiActivity -Message "Catalog background refresh finished: $($result.Message)" -Category 'Task'
        $result
    }

    if (-not $Script:RegistryBackgroundTimer) {
        $Script:RegistryBackgroundTimer = New-Object System.Windows.Forms.Timer
        $Script:RegistryBackgroundTimer.Interval = 250
        $Script:RegistryBackgroundTimer.Add_Tick({
            Invoke-SafeTimerTick -Context 'Registry refresh' -Action { Complete-RegistryBackgroundRefresh }
        })
    }
    $Script:RegistryBackgroundTimer.Start()
    Update-AiStatusButton
}

function Get-RegistrySelectedLibraryName {
    if ($registryCatalogList.SelectedItems.Count -gt 0) {
        return [string]$registryCatalogList.SelectedItems[0].Text
    }
    return $null
}

function Get-RegistrySelectedInstalledName {
    if ($registryInstalledList.SelectedItems.Count -gt 0) {
        return [string]$registryInstalledList.SelectedItems[0].Text
    }
    return $null
}

function Set-RegistryStatus {
    param([string]$Text)

    if ($form.InvokeRequired) {
        $form.Invoke([Action[String]] { param($t) Set-RegistryStatus -Text $t }, $Text) | Out-Null
        return
    }
    $registryStatusLabel.Text = $Text
}

function Refresh-ResultsGrid {
    if ($form.InvokeRequired) {
        $form.Invoke([Action] { Refresh-ResultsGrid }) | Out-Null
        return
    }

    $summaries = Get-AllModelProfileSummaries
    $rows = @($summaries | ForEach-Object { Format-ModelResultsGridRow -Summary $_ })
    $Script:ResultsGridRows = $rows
    if ($Script:ResultsGridSortColumn) {
        $rows = if ($Script:ResultsGridSortAscending) {
            $rows | Sort-Object -Property $Script:ResultsGridSortColumn
        }
        else {
            $rows | Sort-Object -Property $Script:ResultsGridSortColumn -Descending
        }
    }
    $resultsGrid.DataSource = $null
    $resultsGrid.DataSource = @($rows)
    if ($resultsGrid.Columns.Count -gt 0) {
        $resultsGrid.Columns['Model'].FillWeight = 180
        $resultsGrid.Columns['BestMode'].FillWeight = 70
        if ($resultsGrid.Columns['SizeGB']) { $resultsGrid.Columns['SizeGB'].HeaderText = 'Size' }
        if ($resultsGrid.Columns['ParameterSize']) { $resultsGrid.Columns['ParameterSize'].HeaderText = 'Param Size' }
    }
}

function Get-SelectedModelName {
    if ($modelsList.SelectedItems.Count -gt 0) {
        return [string]$modelsList.SelectedItems[0].Text
    }
    if ($testModelCombo.SelectedItem) {
        return [string]$testModelCombo.SelectedItem
    }
    return $null
}

function Update-ModelDescriptionPanel {
    param([string]$ModelName)

    if ($form.InvokeRequired) {
        $form.Invoke([Action[String]] { param($m) Update-ModelDescriptionPanel -ModelName $m }, $ModelName) | Out-Null
        return
    }

    if (-not $ModelName) {
        $modelDescBox.Text = 'Select a model to view its ollama.com description.'
        return
    }

    $text = Get-ModelDescriptionDisplayText -ModelName $ModelName -Format 'Short'
    $entry = Get-ModelDescriptionEntry -ModelName $ModelName
    if ($entry -and $entry.Readme) {
        $preview = $entry.Readme
        if ($preview.Length -gt 600) {
            $preview = $preview.Substring(0, 600) + '...'
        }
        if ($preview) {
            $text += "`r`n`r`n--- Preview ---`r`n$preview"
        }
    }
    $modelDescBox.Text = $text
}

function Start-RegistryModelDescription {
    param([string]$ModelName)

    if (-not $ModelName) { return }
    if ($Script:GuiBusy) { return }

    $Script:PendingDescriptionModel = $ModelName
    Start-GuiWorker -Operation 'ShowRegistryDescription' -RegistryOperation
}

function Register-RegistryListDescriptionHandler {
    param([System.Windows.Forms.ListView]$List)

    $List.Add_MouseUp({
        param($sender, $e)
        if ($e.Button -ne [System.Windows.Forms.MouseButtons]::Right) { return }

        $hit = $sender.HitTest($e.Location)
        if (-not $hit.Item) { return }

        $hit.Item.Selected = $true
        $sender.Select()
        Start-RegistryModelDescription -ModelName $hit.Item.Text
    })
}

function Show-ModelDescriptionDialog {
    param([string]$ModelName)

    if (-not $ModelName) { return }

    $entry = Get-ModelDescriptionEntry -ModelName $ModelName
    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = "Model Description - $ModelName"
    $dlg.Size = New-Object System.Drawing.Size(760, 560)
    $dlg.StartPosition = 'CenterParent'
    $dlg.BackColor = $colors.Bg
    $dlg.ForeColor = $colors.Text
    $dlg.Font = New-ToolkitFont

    $box = New-Object System.Windows.Forms.TextBox
    $box.Dock = 'Fill'
    $box.Multiline = $true
    $box.ReadOnly = $true
    $box.ScrollBars = 'Vertical'
    $box.BackColor = $colors.LogBg
    $box.ForeColor = $colors.Text
    $box.Font = New-Object System.Drawing.Font('Segoe UI', 9.5)
    $box.Text = Get-ModelDescriptionDisplayText -ModelName $ModelName -Format 'Full'

    $btnPanel = New-Object System.Windows.Forms.Panel
    $btnPanel.Dock = 'Bottom'
    $btnPanel.Height = 40
    $btnPanel.BackColor = $colors.Bg

    $libraryUrl = if ($entry -and $entry.SourceUrl) { $entry.SourceUrl } else { "https://ollama.com/library/$ModelName" }
    $openBtn = New-FlatButton -Parent $btnPanel -Text 'Open on ollama.com' -X 12 -Y 6 -Width 130 -Height 28
    $openBtn.Add_Click({ param($sender, $e) Start-Process $libraryUrl })
    $closeBtn = New-FlatButton -Parent $btnPanel -Text 'Close' -X 620 -Y 6 -Width 80 -Height 28
    $closeBtn.Add_Click({ param($sender, $e) $dlg.Close() })

    $dlg.Controls.Add($box)
    $dlg.Controls.Add($btnPanel)
    [void]$dlg.ShowDialog($form)
    $openBtn.Dispose(); $closeBtn.Dispose(); $dlg.Dispose()
}

function Refresh-GuiStatus {
    param(
        [switch]$Fast,
        [switch]$ForceApiRefresh
    )

    if ($Script:GuiShuttingDown) { return }

    try {
        if ($ForceApiRefresh) {
            Clear-OllamaToolkitRuntimeCaches
        }
        Update-StatusPanel -Status (Get-OllamaToolkitStatus -UseCachedApi:$Fast)
        Refresh-ModelsUi -Fast:$Fast
    }
    catch {
        Add-GuiLog -Message "Status refresh failed: $($_.Exception.Message)"
    }
}

function Start-DeferredBenchmarkImport {
    if ($Script:BenchmarkImportJob) { return }

    $root = $Script:ToolkitRoot
    $Script:BenchmarkImportJob = Start-Job -ArgumentList $root -ScriptBlock {
        param($ToolkitRoot)
        Set-Location $ToolkitRoot
        $Script:ToolkitRoot = $ToolkitRoot
        . (Join-Path $ToolkitRoot 'Ollama-Toolkit.Core.ps1')
        . (Join-Path $ToolkitRoot 'Ollama-Toolkit.BenchmarkStore.ps1')
        Import-ToolkitBenchmarkReports
    }
}

function Complete-DeferredBenchmarkImport {
    $job = $Script:BenchmarkImportJob
    if (-not $job) { return $false }

    if ($job.State -eq 'Running') { return $false }

    try {
        $imported = Receive-Job -Job $job -ErrorAction SilentlyContinue
        if ($imported -gt 0) {
            Add-GuiLog -Message "Imported $imported benchmark profile(s) from reports folder."
        }
    }
    catch {
        Add-GuiLog -Message "Benchmark import failed: $($_.Exception.Message)"
    }
    finally {
        Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
        $Script:BenchmarkImportJob = $null
    }
    return $true
}

function Start-PostShowGuiInitialization {
    if ($Script:GuiPostShowStarted) { return }
    $Script:GuiPostShowStarted = $true

    Initialize-GuiAiActivityLog
    Add-AiActivityUiLine -Line '=== AI Activity monitor ready ==='
    Add-GuiLog -Message 'Ollama AMD Vulkan Manager ready.'

    $statusTimer.Start()
    $aiActivityPollTimer.Start()
    $aiStatusTimer.Start()
    $descStartupTimer.Start()

    Update-AiStatusButton
    Refresh-GuiStatus -ForceApiRefresh
    Start-DeferredBenchmarkImport
    $Script:GuiDeferredRefreshTimer.Start()
    $Script:GuiFullRefreshTimer.Start()
}

function Invoke-GuiOperation {
    param(
        [string]$Operation,
        [string]$Mode = '',
        [bool]$RestartAfter = $true,
        [switch]$RegistryOperation
    )

    $previousCursor = $form.Cursor
    $isRegistryOp = $RegistryOperation -or ($Operation -in $Script:RegistryOnlyOperations)
    if (-not $isRegistryOp) {
        $form.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
    }
    try {
        switch ($Operation) {
            'ApplyMode' {
                Apply-OllamaToolkitMode -TargetMode $Mode -TargetScope User `
                    -RestartOllama:$RestartAfter -SuppressRestartGuidance:$RestartAfter | Out-Null
            }
            'Restart' { Restart-Ollama | Out-Null }
            'Restore' { Restore-EnvBackup; Show-OllamaRestartGuidance }
            'Enable680MWorkaround' { Ensure-680MVulkanWorkaround -SkipConfirmation | Out-Null }
            'ImportReports' {
                $n = Import-ToolkitBenchmarkReports
                Add-GuiLog -Message "Imported $n profile(s) from reports."
            }
            'SyncDescriptions' {
                $names = @(Get-ModelMetadataRefreshModelNames)
                if ((Get-SafeCollectionCount $names) -eq 0) {
                    Add-GuiLog -Message 'All installed model descriptions are already cached.'
                    return
                }
                Request-MetadataBackgroundSync -ModelNames $names -Chain | Out-Null
            }
            'SyncAllDescriptions' {
                $names = @(Get-ModelMetadataRefreshModelNames -AllInstalled)
                if ((Get-SafeCollectionCount $names) -eq 0) {
                    Add-GuiLog -Message 'No installed models to refresh.'
                    return
                }
                Request-MetadataBackgroundSync -ModelNames $names -Force -Chain `
                    -StatusMessage 'Refreshing descriptions and categories for installed models...' | Out-Null
            }
            'SyncDescriptionForce' {
                $name = $Script:PendingDescriptionModel
                if (-not $name) {
                    $name = Get-SelectedModelName
                }
                if ($name) {
                    Update-ModelDescriptionCache -ModelName $name -Force | Out-Null
                    Update-ModelDescriptionPanel -ModelName $name
                    Refresh-RegistryInstalledList
                    Add-GuiLog -Message "Refreshed description for '$name'."
                }
            }

            'LoadRegistryTags' {
                $lib = [string]$Script:RegistryTagLibrary
                if (-not $lib) { return }
                $tags = @(Get-OllamaModelTagsFromWeb -LibraryName $lib)
                Set-RegistryTagComboItems -LibraryName $lib -Tags $tags
            }
            'PullRegistryModel' {
                $modelName = [string]$Script:RegistryPullName
                if (-not $modelName) { throw 'No model selected for download.' }
                Wait-OllamaApiReady -AutoStart
                Set-RegistryStatus "Downloading '$modelName'..."
                Add-GuiLog -Message "Downloading '$modelName' from ollama.com..."
                Invoke-OllamaPullModel -ModelName $modelName -OnProgress {
                    param($chunk)
                    $status = if ($chunk.status) { [string]$chunk.status } else { 'downloading' }
                    if ($chunk.completed -and $chunk.total) {
                        $pct = [math]::Round(100.0 * [double]$chunk.completed / [double]$chunk.total, 1)
                        Set-RegistryStatus "$status ($pct%) - $modelName"
                    }
                    else {
                        Set-RegistryStatus "$status - $modelName"
                    }
                }
                Add-GuiLog -Message "Download complete: $modelName"
                Set-RegistryStatus "Download complete: $modelName"
                Refresh-RegistryUi -InstalledOnly
            }
            'RemoveRegistryModel' {
                $modelName = [string]$Script:RegistryRemoveName
                if (-not $modelName) { throw 'No model selected for removal.' }
                Wait-OllamaApiReady -AutoStart
                Set-RegistryStatus "Removing '$modelName'..."
                Add-GuiLog -Message "Removing '$modelName'..."
                Remove-OllamaLocalModel -ModelName $modelName
                Add-GuiLog -Message "Removed: $modelName"
                Set-RegistryStatus "Removed: $modelName"
                Refresh-RegistryUi -InstalledOnly
            }
            'RefreshRegistryInstalled' {
                Refresh-RegistryUi -InstalledOnly
                Set-RegistryStatus 'Installed model list refreshed.'
            }
            'ShowRegistryDescription' {
                $name = [string]$Script:PendingDescriptionModel
                if (-not $name) { return }
                Set-RegistryStatus "Loading description for '$name'..."
                Update-ModelDescriptionCache -ModelName $name -SkipListSummary | Out-Null
                Show-ModelDescriptionDialog -ModelName $name
                Set-RegistryStatus ''
            }
            'Refresh' { }
            default { throw "Unknown operation: $Operation" }
        }
        if (-not $isRegistryOp) {
            Refresh-GuiStatus
        }
    }
    catch {
        Add-GuiLog -Message "ERROR: $($_.Exception.Message)"
        [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Error', 'OK', 'Error') | Out-Null
    }
    finally {
        $form.Cursor = $previousCursor
        Set-GuiBusy -Busy $false -RegistryOperation:$isRegistryOp
    }
}

function Complete-GuiWorkerJob {
    try {
        Complete-GuiWorkerJobCore
    }
    catch {
        Add-GuiLog -Message "GUI worker completion error: $($_.Exception.Message)"
    }
}

function Complete-GuiWorkerJobCore {
    $job = $Script:GuiWorkerJob
    if (-not $job) { return }

    if ($job.State -eq 'Running') {
        Update-AiStatusButtonThrottled
        Update-AiActivityStatusPanel
        return
    }

    if ($Script:GuiWorkerTimer) {
        $Script:GuiWorkerTimer.Stop()
    }

    $isRegistryOp = $false
    try {
        $result = Receive-Job -Job $job -ErrorAction SilentlyContinue
        if ($job.State -eq 'Failed') {
            $errors = @(Receive-Job -Job $job -ErrorAction SilentlyContinue 2>&1)
            $detail = if ((Get-SafeCollectionCount $errors) -gt 0) { ($errors | ForEach-Object { "$_" }) -join ' ' } else { 'Background task failed.' }
            Add-GuiLog -Message "ERROR: $detail"
            Write-GuiAiActivity -Message $detail -Category 'Error'
            [System.Windows.Forms.MessageBox]::Show($detail, 'Error', 'OK', 'Error') | Out-Null
        }
        elseif ($result) {
            $isRegistryOp = [bool]$result.RegistryOperation
            if ($result.Message) {
                Add-GuiLog -Message $result.Message
                Write-GuiAiActivity -Message $result.Message -Category 'Task'
            }
            switch ($result.Operation) {
                'PullRegistryModel' {
                    Set-RegistryStatus "Download complete: $($result.ModelName)"
                    $Script:RegistryInstalledCache = $null
                    Refresh-RegistryUi -InstalledOnly
                }
                'RemoveRegistryModel' {
                    Set-RegistryStatus "Removed: $($result.ModelName)"
                    $Script:RegistryInstalledCache = $null
                    Refresh-RegistryUi -InstalledOnly
                }
                'LoadRegistryTags' {
                    Set-RegistryTagComboItems -LibraryName $result.LibraryName -Tags $result.Tags
                }
                'RefreshRegistryInstalled' {
                    $Script:RegistryInstalledCache = $result.InstalledModels
                    $Script:RegistryInstalledCacheTime = Get-Date
                    Refresh-RegistryInstalledList
                    Set-RegistryStatus 'Installed model list refreshed.'
                }
                'ShowRegistryDescription' {
                    Set-RegistryStatus ''
                    Show-ModelDescriptionDialog -ModelName $result.ModelName
                }
                'SyncDescriptionForce' {
                    Update-ModelDescriptionPanel -ModelName $result.ModelName
                    Refresh-RegistryInstalledList
                }
                'Restore' {
                    Show-OllamaRestartGuidance
                }
                default {
                    if (-not $isRegistryOp) {
                        Refresh-GuiStatus
                    }
                }
            }
        }
    }
    catch {
        Add-GuiLog -Message "ERROR: $($_.Exception.Message)"
        Write-GuiAiActivity -Message $_.Exception.Message -Category 'Error'
    }
    finally {
        Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
        $Script:GuiWorkerJob = $null
        Set-GuiBusy -Busy $false -RegistryOperation:$isRegistryOp
        $form.Cursor = [System.Windows.Forms.Cursors]::Default
        Update-AiStatusButton
        Update-AiActivityStatusPanel
    }
}

function Start-GuiWorker {
    param(
        [string]$Operation,
        [string]$Mode = '',
        [bool]$RestartAfter = $true,
        [switch]$RegistryOperation
    )

    if ($Script:GuiBusy -or $Script:GuiWorkerJob) { return }

    $isRegistryOp = $RegistryOperation -or ($Operation -in $Script:RegistryOnlyOperations)

    if ($Operation -in @('SyncDescriptions', 'SyncAllDescriptions')) {
        Invoke-GuiOperation -Operation $Operation -Mode $Mode -RestartAfter $RestartAfter `
            -RegistryOperation:$RegistryOperation
        return
    }

    Set-GuiBusy -Busy $true -RegistryOperation:$isRegistryOp
    Write-GuiAiActivity -Message "Starting background task: $Operation" -Category 'Task'

    $payload = [ordered]@{
        Operation               = $Operation
        Mode                    = $Mode
        RestartAfter            = $RestartAfter
        RegistryOperation       = $isRegistryOp
        RegistryPullName          = [string]$Script:RegistryPullName
        RegistryRemoveName        = [string]$Script:RegistryRemoveName
        RegistryTagLibrary        = [string]$Script:RegistryTagLibrary
        PendingDescriptionModel = [string]$Script:PendingDescriptionModel
    }
    $root = $Script:ToolkitRoot

    $Script:GuiWorkerJob = Start-Job -ArgumentList $root, $payload -ScriptBlock {
        param($ToolkitRoot, $Payload)
        Set-Location $ToolkitRoot
        $Script:ToolkitRoot = $ToolkitRoot
        . (Join-Path $ToolkitRoot 'Ollama-Toolkit.Core.ps1')
        . (Join-Path $ToolkitRoot 'Ollama-Toolkit.GuiAiActivity.ps1')
        . (Join-Path $ToolkitRoot 'Ollama-Toolkit.BenchmarkStore.ps1')
        . (Join-Path $ToolkitRoot 'Ollama-Toolkit.ModelCatalog.ps1')
        . (Join-Path $ToolkitRoot 'Ollama-Toolkit.ModelRegistry.ps1')

        $Script:LogAction = {
            param([string]$Text, [ConsoleColor]$ForegroundColor)
            if ($Text) { Write-GuiAiActivity -Message $Text -Category 'Worker' }
        }

        $op = [string]$Payload.Operation
        $result = [ordered]@{
            Operation          = $op
            RegistryOperation  = [bool]$Payload.RegistryOperation
            Success            = $true
            Message            = $null
        }

        try {
            switch ($op) {
                'ApplyMode' {
                    Write-GuiAiActivity -Message "Applying mode $($Payload.Mode)..." -Category 'Task'
                    Apply-OllamaToolkitMode -TargetMode $Payload.Mode -TargetScope User `
                        -RestartOllama:[bool]$Payload.RestartAfter `
                        -SuppressRestartGuidance:[bool]$Payload.RestartAfter | Out-Null
                    $result.Message = "Applied mode $($Payload.Mode)."
                }
                'Restart' {
                    Restart-Ollama | Out-Null
                    $result.Message = 'Ollama restarted.'
                }
                'Restore' {
                    Restore-EnvBackup
                    $result.Message = 'Environment backup restored.'
                }
                'Enable680MWorkaround' {
                    Ensure-680MVulkanWorkaround -SkipConfirmation | Out-Null
                    $result.Message = '680M Vulkan workaround applied.'
                }
                'ImportReports' {
                    $n = Import-ToolkitBenchmarkReports
                    $result.Message = "Imported $n profile(s) from reports."
                }
                'SyncDescriptionForce' {
                    $name = [string]$Payload.PendingDescriptionModel
                    if ($name) {
                        Write-GuiAiActivity -Message "Refreshing description for '$name'..." -Category 'AI'
                        Update-ModelDescriptionCache -ModelName $name -Force | Out-Null
                        $result.ModelName = $name
                        $result.Message = "Refreshed description for '$name'."
                    }
                }
                'LoadRegistryTags' {
                    $lib = [string]$Payload.RegistryTagLibrary
                    Write-GuiAiActivity -Message "Loading tags for '$lib' from ollama.com..." -Category 'Task'
                    $tags = @(Get-OllamaModelTagsFromWeb -LibraryName $lib)
                    $result.LibraryName = $lib
                    $result.Tags = $tags
                    $result.Message = "Loaded $(Get-SafeCollectionCount $tags) tag(s) for '$lib'."
                }
                'PullRegistryModel' {
                    $modelName = [string]$Payload.RegistryPullName
                    if (-not $modelName) { throw 'No model selected for download.' }
                    Write-GuiAiActivity -Message "Downloading '$modelName' from ollama.com..." -Category 'Task'
                    Wait-OllamaApiReady -AutoStart
                    Invoke-OllamaPullModel -ModelName $modelName -OnProgress {
                        param($chunk)
                        $status = if ($chunk.status) { [string]$chunk.status } else { 'downloading' }
                        if ($chunk.completed -and $chunk.total) {
                            $pct = [math]::Round(100.0 * [double]$chunk.completed / [double]$chunk.total, 1)
                            Write-GuiAiActivity -Message "$status ($pct%) - $modelName" -Category 'Download'
                        }
                        else {
                            Write-GuiAiActivity -Message "$status - $modelName" -Category 'Download'
                        }
                    }
                    $result.ModelName = $modelName
                    $result.Message = "Download complete: $modelName"
                }
                'RemoveRegistryModel' {
                    $modelName = [string]$Payload.RegistryRemoveName
                    if (-not $modelName) { throw 'No model selected for removal.' }
                    Write-GuiAiActivity -Message "Removing '$modelName'..." -Category 'Task'
                    Wait-OllamaApiReady -AutoStart
                    Remove-OllamaLocalModel -ModelName $modelName
                    $result.ModelName = $modelName
                    $result.Message = "Removed: $modelName"
                }
                'RefreshRegistryInstalled' {
                    Write-GuiAiActivity -Message 'Refreshing installed model list from Ollama API...' -Category 'Task'
                    $models = @(Get-AllInstalledOllamaModels)
                    $result.InstalledModels = $models
                    $result.Message = "Refreshed $(Get-SafeCollectionCount $models) installed model(s)."
                }
                'ShowRegistryDescription' {
                    $name = [string]$Payload.PendingDescriptionModel
                    if (-not $name) { return $result }
                    Write-GuiAiActivity -Message "Fetching description for '$name'..." -Category 'AI'
                    Update-ModelDescriptionCache -ModelName $name -SkipListSummary | Out-Null
                    $result.ModelName = $name
                    $result.Message = "Loaded description for '$name'."
                }
                'Refresh' {
                    $result.Message = 'Status refresh requested.'
                }
                default { throw "Unknown operation: $op" }
            }
        }
        catch {
            $result.Success = $false
            $result.Message = $_.Exception.Message
            Write-GuiAiActivity -Message $result.Message -Category 'Error'
            throw
        }

        [pscustomobject]$result
    }

    if (-not $Script:GuiWorkerTimer) {
        $Script:GuiWorkerTimer = New-Object System.Windows.Forms.Timer
        $Script:GuiWorkerTimer.Interval = 250
        $Script:GuiWorkerTimer.Add_Tick({
            Invoke-SafeTimerTick -Context 'GUI worker' -Action { Complete-GuiWorkerJob }
        })
    }
    $Script:GuiWorkerTimer.Start()
    Update-AiStatusButton
    Update-AiActivityStatusPanel
}

function Start-ModelBenchmark {
    param([string[]]$ModelNames)

    if ($Script:GuiBusy) { return }
    $ModelNames = @($ModelNames | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $modelCount = Get-SafeCollectionCount $ModelNames
    if ($modelCount -eq 0) {
        [System.Windows.Forms.MessageBox]::Show('Select at least one model.', 'Testing', 'OK', 'Information') | Out-Null
        return
    }

    Set-GuiBusy -Busy $true
    $testStatusLabel.Text = "Running benchmark on $modelCount model(s)..."
    Add-GuiLog -Message "=== Benchmark queue: $($ModelNames -join ', ') ==="

    $queueItems = @()
    foreach ($name in $ModelNames) {
        $summary = Get-AllModelProfileSummaries | Where-Object { $_.Model -eq $name } | Select-Object -First 1
        $ctx = if ($testModelCombo.SelectedItem -eq $name) { [int]$testCtxNumeric.Value } `
               elseif ($summary) { $summary.RecommendedCtx } else { 8192 }
        $queueItems += [pscustomobject]@{
            Model      = $name
            NumCtx     = $ctx
            NumPredict = [int]$testTokensNumeric.Value
            Runs       = 1
        }
    }

    try {
        Start-GuiBenchmarkQueue -Models $queueItems -NumPredict ([int]$testTokensNumeric.Value) -Runs 1 -OnComplete {
            Refresh-GuiStatus
            $testStatusLabel.Text = 'Benchmark queue complete.'
            Set-GuiBusy -Busy $false
        }
        $Script:TestLogPosition = 0
        $testPollTimer.Start()
    }
    catch {
        Add-GuiLog -Message "ERROR: $($_.Exception.Message)"
        Set-GuiBusy -Busy $false
        $testStatusLabel.Text = 'Benchmark failed to start.'
    }
}

function Invoke-ModeSelection {
    param([string]$ModeKey)
    if ($Script:GuiBusy) { return }

    $definition = $Script:ModeDefinitions[$ModeKey]
    $current = Get-DetectedMode
    if ($current -eq $ModeKey) {
        if ($restartCheck.Checked) {
            $answer = [System.Windows.Forms.MessageBox]::Show(
                "$($definition.ShortLabel) is already active. Restart Ollama?",
                'Mode', 'YesNo', 'Question'
            )
            if ($answer -eq 'Yes') { Add-GuiLog -Message 'Restarting Ollama...'; Start-GuiWorker -Operation 'Restart' }
        }
        return
    }

    Add-GuiLog -Message "Applying $($definition.ShortLabel)..."
    Start-GuiWorker -Operation 'ApplyMode' -Mode $ModeKey -RestartAfter $restartCheck.Checked
}

$modeClickHandler = {
    param($sender, $eventArgs)
    Invoke-ModeSelection -ModeKey ([string]$sender.Tag)
}
foreach ($entry in $modeCards.GetEnumerator()) {
    $card = $entry.Value
    $card.Add_Click($modeClickHandler)
    foreach ($child in $card.Controls) {
        $child.Tag = $entry.Key
        $child.Add_Click($modeClickHandler)
    }
}

# --- Buttons ---
$refreshModesBtn = New-FlowButton -Parent $modesToolbarRow -Text 'Refresh' -Width 80 `
    -OnClick { Add-GuiLog -Message 'Refreshing...'; Start-GuiWorker -Operation 'Refresh' }
$restartModesBtn = New-FlowButton -Parent $modesToolbarRow -Text 'Restart Ollama' -Width 108 `
    -OnClick { Add-GuiLog -Message 'Restarting Ollama...'; Start-GuiWorker -Operation 'Restart' }
$restoreBtn = New-FlowButton -Parent $modesToolbarRow -Text 'Restore Backup' -Width 124 `
    -OnClick {
        if ([System.Windows.Forms.MessageBox]::Show('Restore env backup?', 'Restore', 'YesNo', 'Warning') -eq 'Yes') {
            Start-GuiWorker -Operation 'Restore'
        }
    }
$workaroundBtn = New-FlowButton -Parent $modesToolbarRow -Text '680M Fix' -Width 84 `
    -OnClick { Start-GuiWorker -Operation 'Enable680MWorkaround' }
$vulkanBtn = New-FlowButton -Parent $modesToolbarRow -Text 'Vulkan Info' -Width 96 `
    -OnClick {
        Start-Process powershell.exe -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'Get-VulkanDevices.ps1'))
    }

$launchBtn = New-FlowButton -Parent $modelsToolbarRow -Text 'Launch (Best Mode)' -Width 130 `
    -BackColor $colors.ActiveBg -ForeColor $colors.Active -OnClick {
        $name = Get-SelectedModelName
        if (-not $name) { return }
        try {
            Set-GuiBusy -Busy $true
            Launch-OllamaModelInBestMode -ModelName $name | Out-Null
            Refresh-GuiStatus
        }
        catch {
            [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Launch', 'OK', 'Warning') | Out-Null
        }
        finally { Set-GuiBusy -Busy $false }
    }
$testSelectedModelsBtn = New-FlowButton -Parent $modelsToolbarRow -Text 'Test Selected' -Width 100 `
    -OnClick {
        $names = @($modelsList.SelectedItems | ForEach-Object { $_.Text })
        if ((Get-SafeCollectionCount $names) -eq 0) { $name = Get-SelectedModelName; if ($name) { $names = @($name) } }
        Start-ModelBenchmark -ModelNames $names
    }
$testUntestedModelsBtn = New-FlowButton -Parent $modelsToolbarRow -Text 'Test Untested' -Width 110 `
    -OnClick {
        $names = @(Get-UntestedLocalModels | ForEach-Object { $_.Model })
        if ((Get-SafeCollectionCount $names) -eq 0) {
            [System.Windows.Forms.MessageBox]::Show('All models have benchmark profiles.', 'Testing', 'OK', 'Information') | Out-Null
            return
        }
        Start-ModelBenchmark -ModelNames $names
    }
$refreshModelsBtn = New-FlowButton -Parent $modelsToolbarRow -Text 'Refresh' -Width 80 `
    -OnClick { Refresh-GuiStatus -ForceApiRefresh }
$importModelsBtn = New-FlowButton -Parent $modelsToolbarRow -Text 'Import Reports' -Width 110 `
    -OnClick { Start-GuiWorker -Operation 'ImportReports' }
$refreshDescBtn = New-FlowButton -Parent $modelsToolbarRow -Text 'Refresh Descriptions' -Width 160 `
    -OnClick {
        $names = @(Get-ModelMetadataRefreshModelNames -AllInstalled)
        if ((Get-SafeCollectionCount $names) -eq 0) {
            Add-GuiLog -Message 'No installed models to refresh.'
            return
        }
        Request-MetadataBackgroundSync -ModelNames $names -Force -Chain `
            -StatusMessage 'Refreshing descriptions and categories for installed models...' | Out-Null
    }
$viewDescBtn = New-FlowButton -Parent $modelsToolbarRow -Text 'Full Description' -Width 120 `
    -OnClick {
        $name = Get-SelectedModelName
        if ($name) { Show-ModelDescriptionDialog -ModelName $name }
    }
$openOllamaBtn = New-FlowButton -Parent $modelsToolbarRow -Text 'ollama.com' -Width 90 `
    -OnClick {
        $name = Get-SelectedModelName
        if (-not $name) { return }
        $entry = Get-ModelDescriptionEntry -ModelName $name
        $url = if ($entry -and $entry.SourceUrl) { $entry.SourceUrl } else { "https://ollama.com/library/$name" }
        Start-Process $url
    }

$registryDownloadBtn = New-FlowButton -Parent $registryToolbarRow -Text 'Download' -Width 90 `
    -BackColor $colors.ActiveBg -ForeColor $colors.Active -OnClick {
        $pullName = [string]$registryTagCombo.SelectedItem
        if (-not $pullName) {
            $lib = Get-RegistrySelectedLibraryName
            if ($lib) { $pullName = "$lib`:latest" }
        }
        if (-not $pullName) {
            [System.Windows.Forms.MessageBox]::Show('Select a model from the catalog first.', 'Download', 'OK', 'Information') | Out-Null
            return
        }
        $Script:RegistryPullName = $pullName
        Start-GuiWorker -Operation 'PullRegistryModel'
    }
$registryRemoveBtn = New-FlowButton -Parent $registryToolbarRow -Text 'Remove' -Width 80 `
    -BackColor ([System.Drawing.Color]::FromArgb(90, 30, 30)) -OnClick {
        $name = Get-RegistrySelectedInstalledName
        if (-not $name) {
            [System.Windows.Forms.MessageBox]::Show('Select an installed model to remove.', 'Remove', 'OK', 'Information') | Out-Null
            return
        }
        $answer = [System.Windows.Forms.MessageBox]::Show(
            "Remove '$name' from this computer?`n`nThis deletes the local model files.",
            'Confirm Remove', 'YesNo', 'Warning'
        )
        if ($answer -ne 'Yes') { return }
        $Script:RegistryRemoveName = $name
        Start-GuiWorker -Operation 'RemoveRegistryModel'
    }
$registryRefreshInstalledBtn = New-FlowButton -Parent $registryToolbarRow -Text 'Refresh Installed' -Width 120 `
    -OnClick { Start-GuiWorker -Operation 'RefreshRegistryInstalled' }
$registryOpenWebBtn = New-FlowButton -Parent $registryToolbarRow -Text 'ollama.com' -Width 90 `
    -OnClick {
        $lib = Get-RegistrySelectedLibraryName
        if (-not $lib) { $lib = Get-RegistrySelectedInstalledName }
        if (-not $lib) { return }
        $base = ($lib -split ':', 2)[0]
        Start-Process "https://ollama.com/library/$base"
    }
$registryRefreshCatalogBtn = New-FlowButton -Parent $registryToolbarRow -Text 'Refresh Catalog' -Width 110 `
    -OnClick {
        $Script:RegistrySearchQuery = $registrySearchBox.Text
        if (Apply-RegistryCatalogDisplay -Search $Script:RegistrySearchQuery) {
            Set-RegistryStatus 'Showing cached catalog while refreshing...'
        }
        Start-RegistryBackgroundRefresh -ForceWeb -ForceReclassify -RefreshDescriptions
    }

$testOneBtn = New-FlowButton -Parent $testToolbarRow -Text 'Test Selected Model' -Width 140 -OnClick {
    $name = Get-SelectedModelName
    if ($name) { Start-ModelBenchmark -ModelNames @($name) }
}
$testAllBtn = New-FlowButton -Parent $testToolbarRow -Text 'Test All Models' -Width 110 -OnClick {
    $names = @(Get-AllModelProfileSummaries | ForEach-Object { $_.Model })
    Start-ModelBenchmark -ModelNames $names
}
$testUntestedBtn = New-FlowButton -Parent $testToolbarRow -Text 'Test Untested' -Width 110 -OnClick {
    $names = @(Get-UntestedLocalModels | ForEach-Object { $_.Model })
    Start-ModelBenchmark -ModelNames $names
}
$testStopButton = New-FlowButton -Parent $testToolbarRow -Text 'Stop' -Width 70 `
    -BackColor ([System.Drawing.Color]::FromArgb(90, 30, 30)) -OnClick {
    Stop-GuiBenchmarkProcess
    $testStatusLabel.Text = 'Stopping benchmark...'
}
$testClearLogBtn = New-FlowButton -Parent $testToolbarRow -Text 'Clear Log' -Width 80 -OnClick {
    $testLogBox.Clear()
    $Script:TestLogPosition = 0
}

$openReportsBtn = New-FlowButton -Parent $resultsToolbarRow -Text 'Open Reports Folder' -Width 140 -OnClick {
    $path = Join-Path $Script:ToolkitRoot 'reports'
    if (-not (Test-Path $path)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
    Start-Process explorer.exe $path
}
$importResultsBtn = New-FlowButton -Parent $resultsToolbarRow -Text 'Import Reports' -Width 110 `
    -OnClick { Start-GuiWorker -Operation 'ImportReports' }
$refreshResultsBtn = New-FlowButton -Parent $resultsToolbarRow -Text 'Refresh' -Width 80 `
    -OnClick { Refresh-GuiStatus -ForceApiRefresh }
$launchFromResultsBtn = New-FlowButton -Parent $resultsToolbarRow -Text 'Launch Selected' -Width 120 `
    -BackColor $colors.ActiveBg -ForeColor $colors.Active -OnClick {
        if ($resultsGrid.SelectedRows.Count -eq 0) { return }
        $name = [string]$resultsGrid.SelectedRows[0].Cells['Model'].Value
        try {
            Set-GuiBusy -Busy $true
            Launch-OllamaModelInBestMode -ModelName $name | Out-Null
            Refresh-GuiStatus
        }
        catch {
            [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Launch', 'OK', 'Warning') | Out-Null
        }
        finally { Set-GuiBusy -Busy $false }
    }

$Script:TestingControls = @(
    $refreshModesBtn, $restartModesBtn, $restoreBtn, $workaroundBtn, $vulkanBtn,
    $launchBtn, $testSelectedModelsBtn, $testUntestedModelsBtn, $refreshModelsBtn, $importModelsBtn,
    $refreshDescBtn, $viewDescBtn, $openOllamaBtn,
    $registrySearchBtn, $registryRefreshCatalogBtn, $registryDownloadBtn, $registryRemoveBtn,
    $registryRefreshInstalledBtn, $registryOpenWebBtn, $registrySearchBox, $registryTagCombo,
    $testOneBtn, $testAllBtn, $testUntestedBtn, $testClearLogBtn,
    $openReportsBtn, $importResultsBtn, $refreshResultsBtn, $launchFromResultsBtn,
    $aiActivityClearBtn,
    $testModelCombo, $testCtxNumeric, $testTokensNumeric, $modelsList, $resultsGrid
)

$testModelCombo.Add_SelectedIndexChanged({
    $name = [string]$testModelCombo.SelectedItem
    if (-not $name) { return }
    $summary = Get-AllModelProfileSummaries | Where-Object { $_.Model -eq $name } | Select-Object -First 1
    if ($summary) {
        $testCtxNumeric.Value = [decimal][math]::Min([math]::Max($summary.RecommendedCtx, 2048), 32768)
        $testStatusLabel.Text = "Selected: $name - recommended num_ctx=$($summary.RecommendedCtx), status=$($summary.Status)"
    }
})

$modelsList.Add_SelectedIndexChanged({
    if ($modelsList.SelectedItems.Count -eq 0) { return }
    $name = $modelsList.SelectedItems[0].Text
    $idx = $testModelCombo.Items.IndexOf($name)
    if ($idx -ge 0) { $testModelCombo.SelectedIndex = $idx }
    Update-ModelDescriptionPanel -ModelName $name
})
$modelsList.Add_DoubleClick({
    $name = Get-SelectedModelName
    if ($name) { Show-ModelDescriptionDialog -ModelName $name }
})

$registrySearchBox.Add_KeyDown({
    param($sender, $e)
    if ($e.KeyCode -eq 'Enter') {
        Invoke-RegistryCatalogSearch
        $e.SuppressKeyPress = $true
    }
})
$registryCatalogList.Add_SelectedIndexChanged({
    if ($registryCatalogList.SelectedItems.Count -eq 0) { return }
    $name = $registryCatalogList.SelectedItems[0].Text
    Request-RegistryTagCombo -LibraryName $name
})
$registryCatalogList.Add_DoubleClick({
    $pullName = [string]$registryTagCombo.SelectedItem
    if (-not $pullName) { return }
    $Script:RegistryPullName = $pullName
    Start-GuiWorker -Operation 'PullRegistryModel'
})
$registryInstalledList.Add_SelectedIndexChanged({
    if ($registryInstalledList.SelectedItems.Count -eq 0) { return }
    $name = $registryInstalledList.SelectedItems[0].Text
    $base = ($name -split ':', 2)[0]
    $idx = -1
    for ($i = 0; $i -lt $registryCatalogList.Items.Count; $i++) {
        if ($registryCatalogList.Items[$i].Text -eq $base) { $idx = $i; break }
    }
    if ($idx -ge 0) {
        $registryCatalogList.Items[$idx].Selected = $true
        $registryCatalogList.Select()
    }
})
Register-RegistryListDescriptionHandler -List $registryInstalledList
Register-RegistryListDescriptionHandler -List $registryCatalogList
Register-ListViewColumnSorting -List $modelsList
Register-ListViewColumnSorting -List $registryInstalledList
Register-ListViewColumnSorting -List $registryCatalogList
Register-ResultsGridColumnSorting -Grid $resultsGrid

$Script:RegistryCatalogLoaded = $false
$registrySplit.Add_SplitterMoved({
    if (-not $Script:RegistrySplitAutoSizing) {
        Save-RegistrySplitLayout
    }
    Resize-RegistryListColumns
})
function Initialize-RegistryTabDeferred {
    if ($Script:RegistryCatalogLoaded) { return }
    $Script:RegistryCatalogLoaded = $true
    $Script:RegistrySearchQuery = ''
    Write-GuiAiActivity -Message 'Model Library tab opened; loading catalog from cache...' -Category 'UI'

    if (Apply-RegistryCatalogDisplay -Search '' -Fast) {
        Set-RegistryStatus "Loaded $(Get-SafeCollectionCount $Script:RegistryCatalog) model(s) from cache."
        if (Test-LibraryCatalogStoreStale) {
            Start-RegistryBackgroundRefresh
        }
    }
    else {
        Set-RegistryStatus 'Loading catalog in background...'
        Start-RegistryBackgroundRefresh
    }
    $Script:RegistryTabInitPending = $false
}

$registryTabInitTimer = New-Object System.Windows.Forms.Timer
$registryTabInitTimer.Interval = 60
$registryTabInitTimer.Add_Tick({
    Invoke-SafeTimerTick -Context 'Registry tab init' -Action {
        $registryTabInitTimer.Stop()
        Initialize-RegistryTabDeferred
    }
})

$tabControl.Add_SelectedIndexChanged({
    if ($tabControl.SelectedTab -eq $tabModes) {
        Initialize-ModesSplitLayout
    }
    if ($tabControl.SelectedTab -eq $tabAiActivity) {
        Update-AiActivityStatusPanel
    }
    if ($tabControl.SelectedTab -ne $tabRegistry) { return }

    Initialize-RegistrySplitLayout
    if (-not $Script:RegistryCatalogLoaded) {
        if (-not $Script:RegistryTabInitPending) {
            $Script:RegistryTabInitPending = $true
            Set-RegistryStatus 'Preparing Model Library view...'
            $registryTabInitTimer.Start()
        }
        return
    }

    if ((Get-SafeCollectionCount $Script:RegistryCatalog) -eq 0) {
        if (Apply-RegistryCatalogDisplay -Search $Script:RegistrySearchQuery -Fast) {
            Set-RegistryStatus "Loaded $(Get-SafeCollectionCount $Script:RegistryCatalog) model(s) from cache."
        }
        elseif (-not $Script:RegistryBackgroundJob) {
            Start-RegistryBackgroundRefresh
        }
    }
    else {
        Refresh-RegistryUi -InstalledOnly
    }
})

$alertLabel.Add_Click({
    $tabControl.SelectedTab = $tabModels
    $untested = @(Get-UntestedLocalModels)
    $untestedCount = Get-SafeCollectionCount $untested
    if ($untestedCount -eq 0) { return }
    $answer = [System.Windows.Forms.MessageBox]::Show(
        "Run benchmark tests on $untestedCount untested model(s) now?",
        'New Models Detected', 'YesNo', 'Question'
    )
    if ($answer -eq 'Yes') {
        Start-ModelBenchmark -ModelNames @($untested | ForEach-Object { $_.Model })
    }
})

$testPollTimer = New-Object System.Windows.Forms.Timer
$testPollTimer.Interval = 500
$testPollTimer.Add_Tick({
    Invoke-SafeTimerTick -Context 'Benchmark poll' -Action {
    $lines = @(Read-GuiBenchmarkLogTail -LastPosition ([ref]$Script:TestLogPosition))
    foreach ($line in $lines) {
        $testLogBox.AppendText("$line`r`n")
    }
    if ((Get-SafeCollectionCount $lines) -gt 0) {
        $testLogBox.SelectionStart = $testLogBox.TextLength
        $testLogBox.ScrollToCaret()
    }

    if ($Script:GuiTestRunner.Process) {
        $model = $Script:GuiTestRunner.ModelName
        $remaining = Get-SafeCollectionCount $Script:GuiTestRunner.Queue
        if ($remaining -gt 0) {
            $testStatusLabel.Text = "Testing '$model'... ($remaining more in queue)"
        }
        else {
            $testStatusLabel.Text = "Testing '$model' (all 4 modes)..."
        }
    }

    if (Test-GuiBenchmarkProcessComplete) {
        if ($Script:GuiTestRunner.Process -or $Script:GuiTestRunner.RunningAll -or (Get-SafeCollectionCount $Script:GuiTestRunner.Queue) -gt 0) {
            $done = Complete-GuiBenchmarkProcess
            if ($done -and -not $Script:GuiTestRunner.Process) {
                $testPollTimer.Stop()
                Refresh-GuiStatus
                if (-not $Script:GuiBusy) { return }
                Set-GuiBusy -Busy $false
            }
        }
        elseif ($Script:GuiBusy) {
            $testPollTimer.Stop()
            Set-GuiBusy -Busy $false
        }
    }
    }
})

$statusTimer = New-Object System.Windows.Forms.Timer
$statusTimer.Interval = 60000
$statusTimer.Add_Tick({
    Invoke-SafeTimerTick -Context 'Status' -Action {
        if ($Script:GuiShuttingDown) { return }
        Refresh-GuiStatus -ForceApiRefresh
        if ($Script:GuiShuttingDown) { return }
        $untested = @(Get-UntestedLocalModels)
        foreach ($u in $untested) {
            if (-not $Script:NewModelPromptShown.ContainsKey($u.Model)) {
                $Script:NewModelPromptShown[$u.Model] = $true
                $answer = [System.Windows.Forms.MessageBox]::Show(
                    "New or untested model detected: $($u.Model)`n`nRun a benchmark now to determine the best compute mode?",
                    'New Model', 'YesNo', 'Question'
                )
                if ($answer -eq 'Yes') {
                    Start-ModelBenchmark -ModelNames @($u.Model)
                }
                break
            }
        }
    }
})

$aiActivityPollTimer = New-Object System.Windows.Forms.Timer
$aiActivityPollTimer.Interval = 4000
$aiActivityPollTimer.Add_Tick({
    Invoke-SafeTimerTick -Context 'AI activity poll' -Action {
        $lines = @(Read-GuiAiActivityTail -LastPosition ([ref]$Script:AiActivityLogPosition))
        foreach ($line in $lines) {
            Add-AiActivityUiLine -Line $line
        }
        if ($tabControl.SelectedTab -eq $tabAiActivity -or (Get-SafeCollectionCount $lines) -gt 0) {
            Update-AiActivityStatusPanel
        }
    }
})

$aiStatusTimer = New-Object System.Windows.Forms.Timer
$aiStatusTimer.Interval = 10000
$aiStatusTimer.Add_Tick({
    Invoke-SafeTimerTick -Context 'AI status' -Action { Update-AiStatusButton }
})

$descStartupTimer = New-Object System.Windows.Forms.Timer
$descStartupTimer.Interval = 8000
$descStartupTimer.Add_Tick({
    Invoke-SafeTimerTick -Context 'Startup refresh' -Action {
        $descStartupTimer.Stop()
        Refresh-GuiStatus -ForceApiRefresh
    }
})

$Script:GuiPostShowTimer = New-Object System.Windows.Forms.Timer
$Script:GuiPostShowTimer.Interval = 1
$Script:GuiPostShowTimer.Add_Tick({
    Invoke-SafeTimerTick -Context 'Post-show init' -Action {
        $Script:GuiPostShowTimer.Stop()
        Start-PostShowGuiInitialization
    }
})

$Script:GuiDeferredRefreshTimer = New-Object System.Windows.Forms.Timer
$Script:GuiDeferredRefreshTimer.Interval = 1500
$Script:GuiDeferredRefreshTimer.Add_Tick({
    Invoke-SafeTimerTick -Context 'Deferred refresh' -Action {
        if (-not (Complete-DeferredBenchmarkImport)) { return }
        $Script:GuiDeferredRefreshTimer.Stop()
        Refresh-GuiStatus -ForceApiRefresh
    }
})

$Script:GuiFullRefreshTimer = New-Object System.Windows.Forms.Timer
$Script:GuiFullRefreshTimer.Interval = 5000
$Script:GuiFullRefreshTimer.Add_Tick({
    Invoke-SafeTimerTick -Context 'Full refresh' -Action {
        $Script:GuiFullRefreshTimer.Stop()
        if (-not $Script:BenchmarkImportJob) {
            Complete-DeferredBenchmarkImport | Out-Null
        }
        Refresh-GuiStatus -ForceApiRefresh
    }
})

$form.Add_Shown({
    Initialize-ModesSplitLayout
    Initialize-RegistrySplitLayout
    Set-RegistryStatus 'Open Model Library tab to load the catalog.'
    $Script:GuiPostShowTimer.Start()
})

$form.Add_Resize({
    Resize-ModelsListColumns
    Resize-RegistryListColumns
    if ($tabControl.SelectedTab -eq $tabRegistry) {
        Update-RegistryInstalledSplitHeight
    }
})
$modelsSplit.Add_SizeChanged({ Resize-ModelsListColumns })
$modelsList.Add_SizeChanged({ Resize-ModelsListColumns })
$registrySplit.Add_SizeChanged({
    Resize-RegistryListColumns
    Update-RegistryInstalledSplitHeight
})
$registryInstalledList.Add_SizeChanged({ Resize-RegistryListColumns })
$registryCatalogList.Add_SizeChanged({ Resize-RegistryListColumns })

$form.Add_FormClosing({
    param($sender, $e)
    Stop-AllGuiBackgroundActivity
})

[void]$form.ShowDialog()

Stop-AllGuiBackgroundActivity
Dispose-AllGuiTimers
Stop-OllamaOnGuiExit