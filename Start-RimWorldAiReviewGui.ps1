param(
    [string]$ReviewRoot = "",
    [string]$LayoutSnapshotPath = "",
    [int]$LayoutSnapshotWidth = 0,
    [int]$LayoutSnapshotHeight = 0,
    [string]$InitialDashboardTab = "",
    [string]$PreviewTheme = "",
    [int]$PreviewTextSize = 0,
    [switch]$PreviewHighContrast
)

$systemPowerShell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
if (-not (Test-Path -LiteralPath $systemPowerShell -PathType Leaf)) {
    throw "Windows PowerShell was not found at the expected system path: $systemPowerShell"
}

if ([System.Threading.Thread]::CurrentThread.ApartmentState -ne "STA") {
    $self = $PSCommandPath
    if (-not $self) { $self = $MyInvocation.MyCommand.Path }
    $args = @("-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-File", "`"$self`"")
    if ($ReviewRoot) { $args += @("-ReviewRoot", "`"$ReviewRoot`"") }
    if ($LayoutSnapshotPath) { $args += @("-LayoutSnapshotPath", "`"$LayoutSnapshotPath`"") }
    if ($LayoutSnapshotWidth -gt 0) { $args += @("-LayoutSnapshotWidth", [string]$LayoutSnapshotWidth) }
    if ($LayoutSnapshotHeight -gt 0) { $args += @("-LayoutSnapshotHeight", [string]$LayoutSnapshotHeight) }
    if ($InitialDashboardTab) { $args += @("-InitialDashboardTab", "`"$InitialDashboardTab`"") }
    if ($PreviewTheme) { $args += @("-PreviewTheme", "`"$PreviewTheme`"") }
    if ($PreviewTextSize -gt 0) { $args += @("-PreviewTextSize", [string]$PreviewTextSize) }
    if ($PreviewHighContrast) { $args += "-PreviewHighContrast" }
    Start-Process -FilePath $systemPowerShell -ArgumentList $args -WindowStyle Hidden
    return
}

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
if (-not ("RimWorldTranslatorNativeMethods" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class RimWorldTranslatorNativeMethods
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
}
"@
}
[System.Windows.Forms.Application]::EnableVisualStyles()

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:powershellExe = $systemPowerShell
$script:cmdExe = Join-Path $env:SystemRoot "System32\cmd.exe"
$script:explorerExe = Join-Path $env:SystemRoot "explorer.exe"
foreach ($systemExecutable in @($script:powershellExe, $script:cmdExe, $script:explorerExe)) {
    if (-not (Test-Path -LiteralPath $systemExecutable -PathType Leaf)) {
        throw "Required Windows executable was not found: $systemExecutable"
    }
}
$script:initialReviewRoot = $ReviewRoot
$script:layoutSnapshotPath = $LayoutSnapshotPath
$script:layoutSnapshotTimer = $null
$script:reviewRoot = ""
$script:comparisonFile = ""
$script:rows = @()
$script:decisions = @{}
$script:fileGroups = @()
$script:visibleRowIndexes = @()
$script:maxRenderedCards = 80
$script:currentRowIndex = -1
$script:currentFile = "__ALL__"
$script:loading = $false
$script:layouting = $false
$script:loadingProjectList = $false
$script:loadingDashboard = $false
$script:syncingSettings = $false
$script:dirty = $false
$script:glossary = @()
$script:glossaryLoaded = $false
$script:appDataRoot = Join-Path $env:LOCALAPPDATA "RimWorldAiTranslator"
$script:projectStorePath = Join-Path $script:appDataRoot "projects.json"
$script:settingsPath = Join-Path $script:appDataRoot "settings.json"
$script:modCatalogCachePath = Join-Path $script:appDataRoot "mod-catalog.json"
$script:appReviewRoot = Join-Path $script:appDataRoot "reviews"
$script:themeMode = "System"
$script:textSize = 10
$script:highContrast = $false
$script:autoSave = $true
$script:settingsLoading = $false
$script:accentColor = [System.Drawing.Color]::FromArgb(166, 124, 70)
$script:surfaceColor = [System.Drawing.Color]::White
$script:textColor = [System.Drawing.Color]::FromArgb(47, 44, 38)
$script:mutedColor = [System.Drawing.Color]::FromArgb(103, 97, 86)
$script:translatorScript = Join-Path $scriptRoot "Invoke-RimWorldAiTranslation.ps1"
$script:reviewApplyScript = Join-Path $scriptRoot "Apply-RimWorldAiReviewResults.ps1"
$script:modCatalog = @()
$script:projects = @()
$script:selectedModRoot = ""
$script:selectedProjectId = ""
$script:lastReviewOutputPath = ""
$script:lastProvider = ""
$script:process = $null
$script:processExitHandled = $false
$script:translationLogFile = ""
$script:translationLogOffset = 0L
$script:translationLogPartial = ""
$script:tempFiles = New-Object "System.Collections.Generic.List[string]"
$script:startedAt = $null
$script:stopRequested = $false
$script:itemCardBack = [System.Drawing.Color]::FromArgb(255, 255, 255)
$script:itemCardSelected = [System.Drawing.Color]::FromArgb(232, 237, 244)
$script:itemText = [System.Drawing.Color]::FromArgb(25, 35, 45)
$script:itemMuted = [System.Drawing.Color]::FromArgb(93, 107, 122)
$script:itemSubtle = [System.Drawing.Color]::FromArgb(126, 139, 153)
$script:tabBack = [System.Drawing.Color]::FromArgb(245, 243, 236)
$script:tabActive = [System.Drawing.Color]::FromArgb(92, 79, 58)
$script:tabText = [System.Drawing.Color]::FromArgb(78, 74, 66)
$script:tabActiveText = [System.Drawing.Color]::White

function New-Font([float]$Size, [System.Drawing.FontStyle]$Style = [System.Drawing.FontStyle]::Regular) {
    return New-Object System.Drawing.Font("Malgun Gothic", $Size, $Style)
}

function New-Label([string]$Text, [int]$X, [int]$Y, [int]$W, [int]$H, [System.Drawing.Color]$Color, [float]$Size = 9, [System.Drawing.FontStyle]$Style = [System.Drawing.FontStyle]::Regular) {
    $label = New-Object System.Windows.Forms.Label
    $label.Text = $Text
    $label.Location = New-Object System.Drawing.Point($X, $Y)
    $label.Size = New-Object System.Drawing.Size($W, $H)
    $label.Font = New-Font $Size $Style
    $label.ForeColor = $Color
    $label.BackColor = [System.Drawing.Color]::Transparent
    return $label
}

function New-TextBox([switch]$Multiline) {
    $box = New-Object System.Windows.Forms.TextBox
    $box.Font = New-Font 9.5
    $box.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
    $box.BackColor = [System.Drawing.Color]::White
    if ($Multiline) {
        $box.Multiline = $true
        $box.AcceptsReturn = $true
        $box.AcceptsTab = $true
        $box.ScrollBars = [System.Windows.Forms.ScrollBars]::Vertical
    }
    return $box
}

function Set-CueBanner([System.Windows.Forms.TextBox]$TextBox, [string]$Text) {
    if (-not $TextBox -or $TextBox.Multiline) { return }
    [void][RimWorldTranslatorNativeMethods]::SendMessage($TextBox.Handle, 0x1501, [IntPtr]1, $Text)
}

function New-Button([string]$Text, [System.Drawing.Color]$BackColor) {
    $button = New-Object System.Windows.Forms.Button
    $button.Text = $Text
    $button.Font = New-Font 9 ([System.Drawing.FontStyle]::Bold)
    $button.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $button.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(76, 86, 96)
    $button.FlatAppearance.BorderSize = 1
    $button.BackColor = $BackColor
    $button.ForeColor = [System.Drawing.Color]::FromArgb(28, 35, 42)
    $button.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
    $button.AutoEllipsis = $true
    return $button
}

function Set-AccessibleControl([object]$Control, [string]$Name, [string]$Description = "", [int]$TabIndex = -1) {
    if (-not $Control) { return }
    $Control.AccessibleName = $Name
    if ($Description) { $Control.AccessibleDescription = $Description }
    if ($TabIndex -ge 0 -and $Control.PSObject.Properties["TabIndex"]) {
        $Control.TabIndex = $TabIndex
    }
}

function Read-SafeXmlDocument([string]$Path) {
    $settings = New-Object System.Xml.XmlReaderSettings
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersFromEntities = 1024
    $settings.MaxCharactersInDocument = 134217728
    $reader = [System.Xml.XmlReader]::Create($Path, $settings)
    try {
        $doc = New-Object System.Xml.XmlDocument
        $doc.PreserveWhitespace = $false
        $doc.XmlResolver = $null
        $doc.Load($reader)
        return ,$doc
    } finally {
        $reader.Dispose()
    }
}

function Get-AccessibilityAuditRows([System.Windows.Forms.Control]$Parent, [string]$ParentPath = "Form") {
    $rows = New-Object "System.Collections.Generic.List[object]"
    $index = 0
    foreach ($control in $Parent.Controls) {
        $typeName = $control.GetType().Name
        $controlPath = "$ParentPath/$typeName`[$index`]"
        $interactive = $control.TabStop -or $control -is [System.Windows.Forms.Button] -or $control -is [System.Windows.Forms.TextBoxBase] -or $control -is [System.Windows.Forms.ComboBox] -or $control -is [System.Windows.Forms.CheckBox] -or $control -is [System.Windows.Forms.TabControl] -or $control -is [System.Windows.Forms.ListView]
        $layoutDetail = ""
        if ($control -is [System.Windows.Forms.TabControl]) {
            $layoutDetail = "client=$($control.ClientSize.Width)x$($control.ClientSize.Height);item=$($control.ItemSize.Width)x$($control.ItemSize.Height);pages=$($control.TabPages.Count)"
        }
        [void]$rows.Add([pscustomobject]@{
            path = $controlPath
            type = $typeName
            visible = [bool]$control.Visible
            enabled = [bool]$control.Enabled
            interactive = [bool]$interactive
            tabStop = [bool]$control.TabStop
            tabIndex = [int]$control.TabIndex
            accessibleName = [string]$control.AccessibleName
            accessibleDescription = [string]$control.AccessibleDescription
            bounds = "$($control.Left),$($control.Top),$($control.Width),$($control.Height)"
            layoutDetail = $layoutDetail
        })
        foreach ($child in @(Get-AccessibilityAuditRows -Parent $control -ParentPath $controlPath)) {
            [void]$rows.Add($child)
        }
        $index++
    }
    return $rows.ToArray()
}

function Get-IsWindowsDarkMode {
    if ($script:themeMode -eq "Dark") { return $true }
    if ($script:themeMode -eq "Light") { return $false }
    try {
        $value = (Get-ItemProperty -LiteralPath "HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize" -Name AppsUseLightTheme -ErrorAction Stop).AppsUseLightTheme
        return [int]$value -eq 0
    } catch {
        return $false
    }
}

function Ensure-AppDataStore {
    foreach ($dir in @($script:appDataRoot, $script:appReviewRoot)) {
        if (-not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
        }
    }
}

function Load-AppSettings {
    $script:themeMode = "System"
    $script:textSize = 10
    $script:highContrast = $false
    $script:autoSave = $true
    if (-not (Test-Path -LiteralPath $script:settingsPath -PathType Leaf)) { return }
    try {
        $settings = [System.IO.File]::ReadAllText($script:settingsPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        if ([string]$settings.themeMode -in @("System", "Light", "Dark")) {
            $script:themeMode = [string]$settings.themeMode
        }
        if ($null -ne $settings.textSize) {
            $script:textSize = [Math]::Max(9, [Math]::Min(12, [int]$settings.textSize))
        }
        if ($null -ne $settings.highContrast) { $script:highContrast = [bool]$settings.highContrast }
        if ($null -ne $settings.autoSave) { $script:autoSave = [bool]$settings.autoSave }
    } catch {
        $script:themeMode = "System"
        $script:textSize = 10
        $script:highContrast = $false
        $script:autoSave = $true
    }
}

function Save-AppSettings {
    Ensure-AppDataStore
    $settings = [ordered]@{
        version = 1
        themeMode = $script:themeMode
        textSize = $script:textSize
        highContrast = $script:highContrast
        autoSave = $script:autoSave
    }
    [System.IO.File]::WriteAllText(
        $script:settingsPath,
        ($settings | ConvertTo-Json -Depth 4),
        (New-Object System.Text.UTF8Encoding($false))
    )
}

function Quote-CmdArgument([string]$Value) {
    if ($null -eq $Value -or $Value.Length -eq 0) { return '""' }

    $quoted = New-Object System.Text.StringBuilder
    [void]$quoted.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq [char]92) {
            $backslashes++
            continue
        }
        if ($character -eq '"') {
            [void]$quoted.Append([char]92, (($backslashes * 2) + 1))
            [void]$quoted.Append('"')
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0) {
            [void]$quoted.Append([char]92, $backslashes)
            $backslashes = 0
        }
        [void]$quoted.Append($character)
    }
    if ($backslashes -gt 0) {
        [void]$quoted.Append([char]92, ($backslashes * 2))
    }
    [void]$quoted.Append('"')
    return $quoted.ToString()
}

function Get-NormalizedDirectoryPath([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootPath = [System.IO.Path]::GetPathRoot($fullPath)
    while ($fullPath.Length -gt $rootPath.Length -and ($fullPath.EndsWith("\") -or $fullPath.EndsWith("/"))) {
        $fullPath = $fullPath.Substring(0, $fullPath.Length - 1)
    }
    return $fullPath
}

function New-TempFilePath([string]$Prefix, [string]$Extension) {
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) "RimWorldAiTranslatorGui"
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    return Join-Path $dir ("{0}-{1}{2}" -f $Prefix, [System.Guid]::NewGuid().ToString("N"), $Extension)
}

function Remove-TempFiles {
    foreach ($path in @($script:tempFiles)) {
        try {
            if ($path -and (Test-Path -LiteralPath $path)) {
                Remove-Item -LiteralPath $path -Force
            }
        } catch {
        }
    }
    $script:tempFiles.Clear()
}

function Get-ApiKeyLines([string]$Text) {
    $keys = New-Object "System.Collections.Generic.List[string]"
    foreach ($line in ([System.Text.RegularExpressions.Regex]::Split($Text, "\r\n|\n|\r"))) {
        $trim = $line.Trim()
        if (-not $trim -or $trim.StartsWith("#")) { continue }
        if ($trim -match "^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.+)\s*$") {
            $trim = $matches[2].Trim()
        }
        $trim = $trim.Trim('"').Trim("'")
        if ($trim.StartsWith("Bearer ", [System.StringComparison]::OrdinalIgnoreCase)) {
            $trim = $trim.Substring(7).Trim()
        }
        foreach ($part in ($trim -split "[,;]")) {
            $key = $part.Trim().Trim('"').Trim("'")
            if ($key) { [void]$keys.Add($key) }
        }
    }
    return $keys.ToArray()
}

function Stop-ProcessTree([int]$ProcessId) {
    if ($ProcessId -le 0) { return }
    $children = @()
    try {
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $ProcessId" -ErrorAction SilentlyContinue)
    } catch {
        $children = @()
    }
    foreach ($child in $children) {
        Stop-ProcessTree ([int]$child.ProcessId)
    }
    try {
        $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($proc -and -not $proc.HasExited) {
            Stop-Process -Id $ProcessId -Force -ErrorAction Stop
        }
    } catch {
    }
}

function ConvertTo-GuiLogLine([string]$Text) {
    if ($null -eq $Text) { return "" }
    $line = $Text.TrimEnd()
    if ([string]::IsNullOrWhiteSpace($line)) { return "" }
    if ($line -match '^\s*\{"translations"\s*:\s*\[') {
        return "[로그 축약] 모델 응답 원문은 숨겼습니다."
    }
    if ($line -match '(\\u000a\s*){6,}') {
        return "[로그 축약] 반복 개행 escape를 숨겼습니다."
    }
    if ($line.Length -gt 1000) {
        return $line.Substring(0, 1000) + "... [로그 축약]"
    }
    return $line
}

function Add-Log([string]$Text) {
    if (-not $txtLog) { return }
    $line = ConvertTo-GuiLogLine $Text
    if ([string]::IsNullOrWhiteSpace($line)) { return }
    $stamp = Get-Date -Format "HH:mm:ss"
    $txtLog.AppendText("[$stamp] $line`r`n")
    $txtLog.SelectionStart = $txtLog.TextLength
    $txtLog.ScrollToCaret()
}

function Read-NewProcessLogLines {
    $lines = New-Object "System.Collections.Generic.List[string]"
    if (-not $script:translationLogFile -or -not (Test-Path -LiteralPath $script:translationLogFile)) { return $lines.ToArray() }
    try {
        $fs = [System.IO.File]::Open($script:translationLogFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            if ($fs.Length -le $script:translationLogOffset) { return $lines.ToArray() }
            [void]$fs.Seek($script:translationLogOffset, [System.IO.SeekOrigin]::Begin)
            $count = [int]($fs.Length - $script:translationLogOffset)
            $bytes = New-Object byte[] $count
            $read = $fs.Read($bytes, 0, $count)
            $script:translationLogOffset += $read
            if ($read -le 0) { return $lines.ToArray() }
            $text = [System.Text.Encoding]::UTF8.GetString($bytes, 0, $read)
            $combined = $script:translationLogPartial + $text
            $parts = [System.Text.RegularExpressions.Regex]::Split($combined, "\r?\n")
            if ($combined -match "\r?\n$") {
                $script:translationLogPartial = ""
                $complete = $parts
            } else {
                $script:translationLogPartial = $parts[$parts.Count - 1]
                $complete = if ($parts.Count -gt 1) { $parts[0..($parts.Count - 2)] } else { @() }
            }
            foreach ($line in $complete) {
                if ($line.Length -gt 0) { [void]$lines.Add($line) }
            }
        } finally {
            $fs.Close()
        }
    } catch {
    }
    return $lines.ToArray()
}

function Update-ProgressFromLine([string]$Line) {
    if ($Line -match "Translating batch\s+(\d+)/(\d+)\s+\((\d+)\s+entries\)") {
        $current = [int]$matches[1]
        $total = [int]$matches[2]
        if ($total -gt 0) {
            $progressRun.Maximum = $total
            $progressRun.Value = [Math]::Min($current, $total)
            $lblRunStatus.Text = "번역 배치 $current / $total"
        }
    } elseif ($Line -match "^Review output:\s+(.+)$") {
        $script:lastReviewOutputPath = $matches[1].Trim()
        $lblRunStatus.Text = "검수 결과 생성됨"
    } elseif ($Line -match "^Translation provider:\s+(.+)$") {
        $script:lastProvider = $matches[1].Trim()
        $lblRunStatus.Text = "번역 엔진: $($matches[1])"
    } elseif ($Line -match "^Detected source language:\s+(.+)$") {
        $lblRunStatus.Text = "원문 언어: $($matches[1])"
    } elseif ($Line -match "^Pending entries:\s+(.+)$") {
        $lblRunStatus.Text = "번역 대상: $($matches[1])개"
    } elseif ($Line -match "^Done\.$") {
        if ($progressRun.Maximum -gt 0) { $progressRun.Value = $progressRun.Maximum }
        $lblRunStatus.Text = "완료"
    }
}

function ConvertTo-FlatString([object]$Value) {
    if ($null -eq $Value) { return "" }
    return ([string]$Value).Replace("`r`n", "`n").Replace("`r", "`n")
}

function ConvertTo-BoolValue([object]$Value) {
    if ($Value -is [bool]) { return [bool]$Value }
    if ($null -eq $Value) { return $false }
    return ([string]$Value).Trim() -match "^(true|1|yes)$"
}

function Get-StableId([string]$Text) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text.ToLowerInvariant())
        $hash = $sha.ComputeHash($bytes)
        return ([BitConverter]::ToString($hash).Replace("-", "").Substring(0, 16)).ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}

function Get-TextFingerprint([string]$Text) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-FlatString $Text))
        return ([BitConverter]::ToString($sha.ComputeHash($bytes)).Replace("-", "")).ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}

function Load-ProjectStore {
    Ensure-AppDataStore
    if (-not (Test-Path -LiteralPath $script:projectStorePath)) {
        $script:projects = @()
        return
    }
    try {
        $json = [System.IO.File]::ReadAllText($script:projectStorePath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        $script:projects = @($json.projects)
    } catch {
        $script:projects = @()
    }
}

function Save-ProjectStore {
    Ensure-AppDataStore
    $payload = [ordered]@{
        version = 1
        updatedAt = (Get-Date).ToString("o")
        projects = @($script:projects)
    }
    $payload | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $script:projectStorePath -Encoding UTF8
}

function Get-ProjectIdForMod([string]$ModRoot, [string]$PackageId = "", [string]$WorkshopId = "") {
    $basis = if ($PackageId) { "pkg:$PackageId" } elseif ($WorkshopId) { "ws:$WorkshopId" } else { [System.IO.Path]::GetFullPath($ModRoot).TrimEnd("\", "/") }
    return Get-StableId $basis
}

function Get-WorkshopIdFromPath([string]$Path) {
    $match = [System.Text.RegularExpressions.Regex]::Match($Path, '\\workshop\\content\\294100\\(\d+)')
    if ($match.Success) { return $match.Groups[1].Value }
    return ""
}

function Get-OrCreateProject([object]$ModInfo) {
    $modRoot = Get-NormalizedDirectoryPath ([string]$ModInfo.Path)
    $projectId = Get-ProjectIdForMod -ModRoot $modRoot -PackageId ([string]$ModInfo.PackageId) -WorkshopId ([string]$ModInfo.WorkshopId)
    $existing = @($script:projects | Where-Object { $_.id -eq $projectId } | Select-Object -First 1)
    if ($existing.Count -gt 0) {
        $project = $existing[0]
        $project.name = [string]$ModInfo.Name
        $project.modRoot = $modRoot
        $project.packageId = [string]$ModInfo.PackageId
        $project.workshopId = [string]$ModInfo.WorkshopId
        $project.updatedAt = (Get-Date).ToString("o")
        return $project
    }
    $project = [pscustomobject]@{
        id = $projectId
        name = [string]$ModInfo.Name
        modRoot = $modRoot
        packageId = [string]$ModInfo.PackageId
        workshopId = [string]$ModInfo.WorkshopId
        latestReviewRoot = ""
        latestReviewAt = ""
        lastAppliedAt = ""
        createdAt = (Get-Date).ToString("o")
        updatedAt = (Get-Date).ToString("o")
        runs = @()
    }
    $script:projects = @($script:projects) + $project
    return $project
}

function Get-SelectedProject {
    if (-not $script:selectedProjectId) { return $null }
    foreach ($project in @($script:projects)) {
        if ([string]$project.id -eq [string]$script:selectedProjectId) { return $project }
    }
    return $null
}

function Get-ActiveProjectModRoot([switch]$Require) {
    $path = ""
    $project = Get-SelectedProject
    if ($project -and $project.modRoot) {
        $path = [string]$project.modRoot
    } elseif ($script:selectedModRoot) {
        $path = [string]$script:selectedModRoot
    }

    if (-not [string]::IsNullOrWhiteSpace($path)) {
        try { $path = Get-NormalizedDirectoryPath $path } catch { $path = "" }
    }

    if ($path -and (Test-Path -LiteralPath $path -PathType Container)) {
        $script:selectedModRoot = $path
        return $path
    }

    if ($Require) {
        throw "프로젝트에 연결된 모드 폴더를 찾을 수 없습니다."
    }
    return ""
}

function Set-ActiveProject([object]$Project) {
    if (-not $Project) { return }
    $script:selectedProjectId = [string]$Project.id
    if ($Project.modRoot) {
        try { $script:selectedModRoot = Get-NormalizedDirectoryPath ([string]$Project.modRoot) } catch { $script:selectedModRoot = [string]$Project.modRoot }
    } else {
        $script:selectedModRoot = ""
    }

    if ($lblProject) { $lblProject.Text = if ($Project.name) { [string]$Project.name } else { "RimWorld AI Translator" } }
    if ($lblPath) { $lblPath.Text = $script:selectedModRoot }
    Update-SearchCrumb

    $hasProjectMod = [bool](Get-ActiveProjectModRoot)
    if ($btnTranslate) { $btnTranslate.Enabled = $hasProjectMod }
    if ($btnApply) { $btnApply.Enabled = [bool]($script:reviewRoot -and (Test-Path -LiteralPath $script:reviewRoot) -and $hasProjectMod) }
    if ($btnApplyTranslated) { $btnApplyTranslated.Enabled = [bool]($script:reviewRoot -and (Test-Path -LiteralPath $script:reviewRoot) -and $hasProjectMod) }
    Refresh-ProjectList
    Refresh-DashboardProjects
    Refresh-DashboardActivity
}

function Register-ProjectRun([string]$ReviewRoot, [string]$Provider = "") {
    $project = Get-SelectedProject
    if (-not $project) {
        if (-not $script:selectedModRoot) { return }
        $info = Get-RimWorldModInfo -ModPath $script:selectedModRoot -Source "Selected"
        if (-not $info) {
            $info = [pscustomobject]@{
                Display = Split-Path -Leaf $script:selectedModRoot
                Name = Split-Path -Leaf $script:selectedModRoot
                Path = $script:selectedModRoot
                Source = "Selected"
                Folder = Split-Path -Leaf $script:selectedModRoot
                PackageId = ""
                WorkshopId = Get-WorkshopIdFromPath $script:selectedModRoot
                Search = $script:selectedModRoot.ToLowerInvariant()
            }
        }
        $project = Get-OrCreateProject $info
    }
    $script:selectedProjectId = $project.id
    if ($ReviewRoot) {
        $project.latestReviewRoot = [System.IO.Path]::GetFullPath($ReviewRoot)
        $project.latestReviewAt = (Get-Date).ToString("o")
        $existingRun = @($project.runs | Where-Object { $_.reviewRoot -eq $project.latestReviewRoot } | Select-Object -First 1)
        if ($existingRun.Count -eq 0) {
            $run = [pscustomobject]@{
                reviewRoot = $project.latestReviewRoot
                createdAt = $project.latestReviewAt
                provider = $Provider
            }
            $project.runs = @($project.runs) + $run
        } else {
            $existingRun[0].createdAt = $project.latestReviewAt
            $existingRun[0].provider = $Provider
        }
        if (@($project.runs).Count -gt 40) {
            $project.runs = @($project.runs | Select-Object -Last 40)
        }
    }
    $project.updatedAt = (Get-Date).ToString("o")
    Save-ProjectStore
    Refresh-ProjectList
    Refresh-DashboardProjects
    Refresh-DashboardActivity
}

function Mark-ProjectApplied {
    if (-not $script:selectedProjectId) { return }
    foreach ($project in $script:projects) {
        if ($project.id -eq $script:selectedProjectId) {
            $project.lastAppliedAt = (Get-Date).ToString("o")
            $project.updatedAt = (Get-Date).ToString("o")
            break
        }
    }
    Save-ProjectStore
    Refresh-ProjectList
    Refresh-DashboardProjects
    Refresh-DashboardActivity
}

function Add-UniqueExistingDirectory($List, $Seen, [string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    try { $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\') } catch { return }
    if (-not (Test-Path -LiteralPath $full -PathType Container)) { return }
    if ($Seen.Add($full)) { [void]$List.Add($full) }
}

function Get-SteamRootCandidates {
    $roots = New-Object "System.Collections.Generic.List[string]"
    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($regPath in @("HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam", "HKLM:\SOFTWARE\Valve\Steam")) {
        try {
            $props = Get-ItemProperty -LiteralPath $regPath -ErrorAction Stop
            Add-UniqueExistingDirectory $roots $seen $props.SteamPath
            Add-UniqueExistingDirectory $roots $seen $props.InstallPath
        } catch {
        }
    }
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, $env:LOCALAPPDATA)) {
        if ([string]::IsNullOrWhiteSpace($base)) { continue }
        Add-UniqueExistingDirectory $roots $seen (Join-Path $base "Steam")
    }
    foreach ($drive in [System.IO.DriveInfo]::GetDrives()) {
        if (-not $drive.IsReady -or $drive.DriveType -ne [System.IO.DriveType]::Fixed) { continue }
        Add-UniqueExistingDirectory $roots $seen (Join-Path $drive.RootDirectory.FullName "SteamLibrary")
        Add-UniqueExistingDirectory $roots $seen (Join-Path $drive.RootDirectory.FullName "Steam")
    }
    return $roots.ToArray()
}

function Get-SteamLibrariesFromVdf([string]$SteamRoot) {
    $libs = New-Object "System.Collections.Generic.List[string]"
    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($vdfPath in @((Join-Path $SteamRoot "steamapps\libraryfolders.vdf"), (Join-Path $SteamRoot "config\libraryfolders.vdf"))) {
        if (-not (Test-Path -LiteralPath $vdfPath -PathType Leaf)) { continue }
        try {
            $text = [System.IO.File]::ReadAllText($vdfPath)
            foreach ($match in [System.Text.RegularExpressions.Regex]::Matches($text, '"path"\s+"([^"]+)"')) {
                Add-UniqueExistingDirectory $libs $seen ($match.Groups[1].Value -replace '\\\\', '\')
            }
            foreach ($match in [System.Text.RegularExpressions.Regex]::Matches($text, '(?m)^\s*"\d+"\s+"([^"]+)"')) {
                Add-UniqueExistingDirectory $libs $seen ($match.Groups[1].Value -replace '\\\\', '\')
            }
        } catch {
        }
    }
    return $libs.ToArray()
}

function Get-SteamLibraryRoots {
    $roots = New-Object "System.Collections.Generic.List[string]"
    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($root in (Get-SteamRootCandidates)) { Add-UniqueExistingDirectory $roots $seen $root }
    foreach ($root in @($roots.ToArray())) {
        foreach ($lib in (Get-SteamLibrariesFromVdf $root)) { Add-UniqueExistingDirectory $roots $seen $lib }
    }
    return $roots.ToArray()
}

function Get-RimWorldModContainers {
    $containers = New-Object "System.Collections.Generic.List[object]"
    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($root in (Get-SteamLibraryRoots)) {
        $workshop = Join-Path $root "steamapps\workshop\content\294100"
        if ((Test-Path -LiteralPath $workshop -PathType Container) -and $seen.Add($workshop)) {
            [void]$containers.Add([pscustomobject]@{ Path = $workshop; Source = "Workshop" })
        }
        $localMods = Join-Path $root "steamapps\common\RimWorld\Mods"
        if ((Test-Path -LiteralPath $localMods -PathType Container) -and $seen.Add($localMods)) {
            [void]$containers.Add([pscustomobject]@{ Path = $localMods; Source = "Local" })
        }
    }
    return $containers.ToArray()
}

function Get-ModAboutPath([string]$ModPath) {
    $direct = Join-Path $ModPath "About\About.xml"
    if (Test-Path -LiteralPath $direct -PathType Leaf) { return $direct }
    try {
        foreach ($child in Get-ChildItem -LiteralPath $ModPath -Directory -ErrorAction SilentlyContinue) {
            $nested = Join-Path $child.FullName "About\About.xml"
            if (Test-Path -LiteralPath $nested -PathType Leaf) { return $nested }
        }
    } catch {
    }
    return $null
}

function Get-PreferredLoadFolderRoot([string]$ModPath) {
    $loadFoldersPath = Join-Path $ModPath "LoadFolders.xml"
    if (-not (Test-Path -LiteralPath $loadFoldersPath -PathType Leaf)) { return $null }
    try {
        $doc = Read-SafeXmlDocument $loadFoldersPath
        $versionNodes = @($doc.DocumentElement.ChildNodes |
            Where-Object { $_.NodeType -eq [System.Xml.XmlNodeType]::Element -and $_.LocalName -match '^v\d' } |
            Sort-Object @{ Expression = {
                $numbers = [System.Text.RegularExpressions.Regex]::Matches($_.LocalName, '\d+')
                $score = 0
                foreach ($number in $numbers) { $score = ($score * 100) + [int]$number.Value }
                return $score
            }; Descending = $true })
        foreach ($node in $versionNodes) {
            foreach ($li in @($node.li)) {
                if ($li -is [System.Xml.XmlElement] -and ($li.HasAttribute("IfModActive") -or $li.HasAttribute("IfModNotActive"))) { continue }
                $relative = if ($li -is [System.Xml.XmlElement]) { ([string]$li.InnerText).Trim() } else { ([string]$li).Trim() }
                if ([string]::IsNullOrWhiteSpace($relative)) { continue }
                $candidate = Join-Path $ModPath $relative
                if (-not (Test-Path -LiteralPath $candidate -PathType Container)) { continue }
                if ((Test-Path -LiteralPath (Join-Path $candidate "Defs") -PathType Container) -or
                    (Test-Path -LiteralPath (Join-Path $candidate "Languages") -PathType Container) -or
                    (Test-Path -LiteralPath (Join-Path $candidate "About\About.xml") -PathType Leaf)) {
                    return [System.IO.Path]::GetFullPath($candidate)
                }
            }
        }
    } catch {
    }
    return $null
}

function Get-RimWorldModInfo([string]$ModPath, [string]$Source) {
    if (-not (Test-Path -LiteralPath $ModPath -PathType Container)) { return $null }
    $aboutPath = Get-ModAboutPath $ModPath
    $preferredLoadRoot = Get-PreferredLoadFolderRoot $ModPath
    $effectiveModPath = if ($preferredLoadRoot) { $preferredLoadRoot } else { $ModPath }
    if ($aboutPath -and -not $preferredLoadRoot) {
        try {
            $aboutDir = Split-Path -Parent $aboutPath
            $candidateRoot = Split-Path -Parent $aboutDir
            if ($candidateRoot -and ([System.IO.Path]::GetFullPath($candidateRoot).TrimEnd('\') -ne [System.IO.Path]::GetFullPath($ModPath).TrimEnd('\'))) {
                $effectiveModPath = $candidateRoot
            }
        } catch {
        }
    }
    $hasContent = $aboutPath -or
        (Test-Path -LiteralPath (Join-Path $effectiveModPath "Defs") -PathType Container) -or
        (Test-Path -LiteralPath (Join-Path $effectiveModPath "Languages") -PathType Container)
    if (-not $hasContent) { return $null }
    $name = Split-Path -Leaf $effectiveModPath
    $packageId = ""
    if ($aboutPath) {
        try {
            $about = Read-SafeXmlDocument $aboutPath
            $aboutName = [string]$about.ModMetaData.name
            $aboutPackageId = [string]$about.ModMetaData.packageId
            if (-not [string]::IsNullOrWhiteSpace($aboutName)) { $name = $aboutName.Trim() }
            if (-not [string]::IsNullOrWhiteSpace($aboutPackageId)) { $packageId = $aboutPackageId.Trim() }
        } catch {
        }
    }
    $folderName = Split-Path -Leaf $ModPath
    $effectiveLeaf = Split-Path -Leaf $effectiveModPath
    $workshopId = Get-WorkshopIdFromPath $ModPath
    $tag = if ($workshopId) {
        if ($effectiveLeaf -and $effectiveLeaf -ne $folderName) { "W:$workshopId\$effectiveLeaf" } else { "W:$workshopId" }
    } elseif ($Source -eq "Local") {
        "Local"
    } else {
        $Source
    }
    $displayName = $name
    if ($displayName.Length -gt 44) { $displayName = $displayName.Substring(0, 41) + "..." }
    return [pscustomobject]@{
        Display = "$displayName [$tag]"
        Name = $name
        Path = [System.IO.Path]::GetFullPath($effectiveModPath)
        Source = $Source
        Folder = $folderName
        PackageId = $packageId
        WorkshopId = $workshopId
        Search = ("$name $folderName $effectiveLeaf $packageId $workshopId $ModPath $effectiveModPath").ToLowerInvariant()
    }
}

function Find-RimWorldMods {
    $mods = New-Object "System.Collections.Generic.List[object]"
    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($container in (Get-RimWorldModContainers)) {
        try {
            foreach ($dir in Get-ChildItem -LiteralPath $container.Path -Directory -ErrorAction SilentlyContinue) {
                $info = Get-RimWorldModInfo $dir.FullName $container.Source
                if ($info -and $seen.Add($info.Path)) { [void]$mods.Add($info) }
            }
        } catch {
        }
    }
    return @($mods | Sort-Object @{ Expression = "Name"; Ascending = $true }, @{ Expression = "Folder"; Ascending = $true })
}

function Get-ModContainerState {
    $states = New-Object "System.Collections.Generic.List[object]"
    foreach ($container in @(Get-RimWorldModContainers)) {
        try {
            $item = Get-Item -LiteralPath $container.Path -ErrorAction Stop
            $directoryCount = @(Get-ChildItem -LiteralPath $container.Path -Directory -Name -ErrorAction Stop).Count
            [void]$states.Add([pscustomobject]@{
                path = [System.IO.Path]::GetFullPath($item.FullName)
                source = [string]$container.Source
                lastWriteUtc = $item.LastWriteTimeUtc.ToString("o")
                directoryCount = $directoryCount
            })
        } catch {
        }
    }
    return @($states.ToArray() | Sort-Object path)
}

function Test-ModContainerState([object[]]$Cached, [object[]]$Current) {
    $cachedRows = @($Cached)
    $currentRows = @($Current)
    if ($cachedRows.Count -ne $currentRows.Count) { return $false }
    foreach ($row in $currentRows) {
        $match = @($cachedRows | Where-Object { [string]$_.path -eq [string]$row.path } | Select-Object -First 1)
        if ($match.Count -eq 0) { return $false }
        if ([string]$match[0].lastWriteUtc -ne [string]$row.lastWriteUtc) { return $false }
        if ([int]$match[0].directoryCount -ne [int]$row.directoryCount) { return $false }
    }
    return $true
}

function Save-ModCatalogCache {
    try {
        Ensure-AppDataStore
        $payload = [ordered]@{
            version = 1
            updatedAt = (Get-Date).ToString("o")
            containers = @(Get-ModContainerState)
            mods = @($script:modCatalog | Select-Object Display, Name, Path, Source, Folder, PackageId, WorkshopId, Search)
        }
        [System.IO.File]::WriteAllText(
            $script:modCatalogCachePath,
            ($payload | ConvertTo-Json -Depth 7),
            (New-Object System.Text.UTF8Encoding($false))
        )
    } catch {
        Add-Log "모드 목록 캐시 저장 실패: $($_.Exception.Message)"
    }
}

function Try-LoadModCatalogCache {
    if (-not (Test-Path -LiteralPath $script:modCatalogCachePath -PathType Leaf)) { return $false }
    try {
        $cache = [System.IO.File]::ReadAllText($script:modCatalogCachePath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        if ([int]$cache.version -ne 1) { return $false }
        $currentState = @(Get-ModContainerState)
        if (-not (Test-ModContainerState -Cached @($cache.containers) -Current $currentState)) { return $false }
        $script:modCatalog = @($cache.mods | Sort-Object @{ Expression = "Name"; Ascending = $true }, @{ Expression = "Folder"; Ascending = $true })
        return $true
    } catch {
        return $false
    }
}

function Get-RowIdentity([object]$Row) {
    if ($Row.id) { return "id:$($Row.id)" }
    return "key:$($Row.key)"
}

function Get-DecisionPath {
    if (-not $script:reviewRoot) { return "" }
    return Join-Path $script:reviewRoot "review-decisions.json"
}

function Get-StatusText([string]$Status) {
    switch ($Status) {
        "translated" { return "번역됨" }
        "approved" { return "검토됨" }
        "rejected" { return "반려" }
        "hold" { return "보류" }
        default { return "미번역" }
    }
}

function Get-StatusColor([string]$Status) {
    $isDark = Get-IsWindowsDarkMode
    if ($script:highContrast) {
        switch ($Status) {
            "translated" { return $(if ($isDark) { [System.Drawing.Color]::FromArgb(116, 190, 255) } else { [System.Drawing.Color]::FromArgb(18, 86, 153) }) }
            "approved" { return $(if ($isDark) { [System.Drawing.Color]::FromArgb(105, 211, 139) } else { [System.Drawing.Color]::FromArgb(24, 105, 55) }) }
            "rejected" { return $(if ($isDark) { [System.Drawing.Color]::FromArgb(255, 124, 124) } else { [System.Drawing.Color]::FromArgb(151, 32, 32) }) }
            "hold" { return $(if ($isDark) { [System.Drawing.Color]::FromArgb(255, 198, 92) } else { [System.Drawing.Color]::FromArgb(135, 82, 0) }) }
            default { return $(if ($isDark) { [System.Drawing.Color]::FromArgb(224, 224, 224) } else { [System.Drawing.Color]::FromArgb(70, 70, 70) }) }
        }
    }
    switch ($Status) {
        "translated" { return $(if ($isDark) { [System.Drawing.Color]::FromArgb(107, 177, 236) } else { [System.Drawing.Color]::FromArgb(36, 105, 170) }) }
        "approved" { return $(if ($isDark) { [System.Drawing.Color]::FromArgb(99, 191, 124) } else { [System.Drawing.Color]::FromArgb(42, 116, 67) }) }
        "rejected" { return $(if ($isDark) { [System.Drawing.Color]::FromArgb(232, 116, 116) } else { [System.Drawing.Color]::FromArgb(164, 57, 57) }) }
        "hold" { return $(if ($isDark) { [System.Drawing.Color]::FromArgb(229, 176, 77) } else { [System.Drawing.Color]::FromArgb(145, 94, 20) }) }
        default { return $(if ($isDark) { [System.Drawing.Color]::FromArgb(190, 190, 184) } else { [System.Drawing.Color]::FromArgb(91, 91, 86) }) }
    }
}

function Get-UpdateColor {
    $isDark = Get-IsWindowsDarkMode
    if ($script:highContrast) {
        return $(if ($isDark) { [System.Drawing.Color]::FromArgb(255, 202, 96) } else { [System.Drawing.Color]::FromArgb(128, 72, 0) })
    }
    return $(if ($isDark) { [System.Drawing.Color]::FromArgb(232, 177, 82) } else { [System.Drawing.Color]::FromArgb(174, 105, 24) })
}

function Update-StopButtonAppearance {
    if (-not $btnStop) { return }
    $isDark = Get-IsWindowsDarkMode
    if ($btnStop.Enabled) {
        $btnStop.BackColor = $(if ($isDark) { [System.Drawing.Color]::FromArgb(174, 76, 73) } else { [System.Drawing.Color]::FromArgb(153, 67, 64) })
        $btnStop.ForeColor = [System.Drawing.Color]::White
    } else {
        $btnStop.BackColor = $(if ($isDark) { [System.Drawing.Color]::FromArgb(48, 53, 49) } else { [System.Drawing.Color]::FromArgb(53, 59, 54) })
        $btnStop.ForeColor = [System.Drawing.Color]::FromArgb(185, 192, 184)
    }
    $btnStop.FlatAppearance.BorderColor = $btnStop.BackColor
    $btnStop.FlatAppearance.BorderSize = 0
}

function Get-CurrentDisplayName {
    if ($script:selectedProjectId) {
        $project = @($script:projects | Where-Object { $_.id -eq $script:selectedProjectId } | Select-Object -First 1)
        if ($project.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace([string]$project[0].name)) {
            return [string]$project[0].name
        }
    }
    if ($script:selectedModRoot) {
        $info = Get-RimWorldModInfo -ModPath $script:selectedModRoot -Source "Selected"
        if ($info -and -not [string]::IsNullOrWhiteSpace([string]$info.Name)) { return [string]$info.Name }
        return Split-Path -Leaf $script:selectedModRoot
    }
    if ($script:reviewRoot) { return Split-Path -Leaf $script:reviewRoot }
    return "모드"
}

function Update-SearchCrumb {
    if ($lblSearchCrumb) {
        $name = Get-CurrentDisplayName
        if ($name.Length -gt 44) { $name = $name.Substring(0, 41) + "..." }
        $fileLabel = if ($script:currentFile -and $script:currentFile -ne "__ALL__") {
            Split-Path -Leaf $script:currentFile
        } else {
            "전체 문자열"
        }
        if ($fileLabel.Length -gt 40) { $fileLabel = $fileLabel.Substring(0, 37) + "..." }
        $statusLabel = if ($cmbStatus -and $cmbStatus.SelectedItem -and [string]$cmbStatus.SelectedItem -ne "전체") {
            [string]$cmbStatus.SelectedItem
        } else {
            "모든 상태"
        }
        $lblSearchCrumb.Text = "$name`r`n$fileLabel  ·  $statusLabel"
    }
}

function Refresh-StatusFilterButtons {
    if (-not $cmbStatus -or -not $statusFilterButtons) { return }
    $selected = if ($cmbStatus.SelectedItem) { [string]$cmbStatus.SelectedItem } else { "전체" }
    $line = if (Get-IsWindowsDarkMode) { [System.Drawing.Color]::FromArgb(68, 74, 69) } else { [System.Drawing.Color]::FromArgb(204, 209, 203) }
    foreach ($button in $statusFilterButtons) {
        $active = [string]$button.Tag -eq $selected
        if ($active) {
            $button.BackColor = $script:accentColor
            $button.ForeColor = [System.Drawing.Color]::White
            $button.FlatAppearance.BorderColor = $script:accentColor
            $button.FlatAppearance.BorderSize = 0
        } else {
            $button.BackColor = $script:surfaceColor
            $button.ForeColor = $script:mutedColor
            $button.FlatAppearance.BorderColor = $line
            $button.FlatAppearance.BorderSize = 1
        }
    }
}

function Get-RowWarnings([object]$Row) {
    $warnings = New-Object "System.Collections.Generic.List[string]"
    if (-not (ConvertTo-BoolValue $Row.safeToApply)) { [void]$warnings.Add("안전 적용 아님") }
    if (ConvertTo-BoolValue $Row.candidateBlank) { [void]$warnings.Add("빈 후보") }
    if (ConvertTo-BoolValue $Row.pathologicalCandidate) { [void]$warnings.Add("비정상 개행") }
    if (-not [string]::IsNullOrWhiteSpace([string]$Row.missingTokens)) { [void]$warnings.Add("토큰 누락: $($Row.missingTokens)") }
    if (ConvertTo-BoolValue $Row.candidateSameAsSource) { [void]$warnings.Add("원문과 동일") }
    if (-not (ConvertTo-BoolValue $Row.candidateHasKorean)) { [void]$warnings.Add("한글 없음") }
    return $warnings.ToArray()
}

function Get-RelativeTarget([object]$Row) {
    $target = [string]$Row.target
    if (-not $target) { return "(unknown)" }
    if (-not $script:reviewRoot) { return $target }
    try {
        $reviewFull = [System.IO.Path]::GetFullPath($script:reviewRoot).TrimEnd("\", "/")
        $reviewPrefix = $reviewFull + [System.IO.Path]::DirectorySeparatorChar
        $targetFull = [System.IO.Path]::GetFullPath($target)
        if ($targetFull.StartsWith($reviewPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            $relative = $targetFull.Substring($reviewPrefix.Length)
            $prefix = "Languages\Korean\"
            if ($relative.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                $relative = $relative.Substring($prefix.Length)
            }
            return $relative
        }
    } catch {
    }
    return $target
}

function Get-DefaultTranslationForRow([object]$Row) {
    $candidate = ConvertTo-FlatString $Row.candidate
    if (-not [string]::IsNullOrWhiteSpace($candidate)) { return $candidate }
    $existing = ConvertTo-FlatString $Row.existing
    if (-not [string]::IsNullOrWhiteSpace($existing)) { return $existing }
    return ""
}

function New-Decision([object]$Row) {
    $defaultTranslation = Get-DefaultTranslationForRow $Row
    $source = ConvertTo-FlatString $Row.source
    return [pscustomobject]@{
        id = [string]$Row.id
        key = [string]$Row.key
        target = Get-RelativeTarget $Row
        status = if ([string]::IsNullOrWhiteSpace($defaultTranslation)) { "pending" } else { "translated" }
        text = $defaultTranslation
        note = ""
        sourceHash = Get-TextFingerprint $source
        sourceText = $source
        sourceChanged = $false
        previousSourceText = ""
        updatedAt = ""
    }
}

function Get-Decision([object]$Row) {
    $identity = Get-RowIdentity $Row
    if (-not $script:decisions.ContainsKey($identity)) {
        $script:decisions[$identity] = New-Decision $Row
    }
    Normalize-DecisionForRow -Row $Row -Decision $script:decisions[$identity]
    return $script:decisions[$identity]
}

function Normalize-DecisionForRow([object]$Row, [object]$Decision) {
    $source = ConvertTo-FlatString $Row.source
    $sourceHash = Get-TextFingerprint $source
    $defaultTranslation = Get-DefaultTranslationForRow $Row
    $status = [string]$Decision.status
    if ([string]::IsNullOrWhiteSpace($status)) { $status = if ($defaultTranslation) { "translated" } else { "pending" } }
    if ($status -eq "reviewed") { $status = "approved" }

    $sourceChangedNow = $false
    $storedSourceText = if ($Decision.PSObject.Properties["sourceText"]) { ConvertTo-FlatString $Decision.sourceText } else { "" }
    if ($Decision.PSObject.Properties["sourceHash"] -and -not [string]::IsNullOrWhiteSpace([string]$Decision.sourceHash)) {
        $sourceChangedNow = ([string]$Decision.sourceHash) -ne $sourceHash
    } elseif ($Decision.PSObject.Properties["sourceText"] -and -not [string]::IsNullOrWhiteSpace([string]$Decision.sourceText)) {
        $sourceChangedNow = $storedSourceText -ne $source
    }
    $sourceChanged = $sourceChangedNow -or ($Decision.PSObject.Properties["sourceChanged"] -and (ConvertTo-BoolValue $Decision.sourceChanged))

    if ($sourceChanged) {
        $Decision.status = "pending"
        if ([string]::IsNullOrWhiteSpace([string]$Decision.text)) {
            $Decision.text = $defaultTranslation
        }
        if ($sourceChangedNow) {
            $previousSource = if (-not [string]::IsNullOrWhiteSpace($storedSourceText)) { $storedSourceText } else { ConvertTo-FlatString $Decision.previousSourceText }
            if ($Decision.PSObject.Properties["previousSourceText"]) {
                $Decision.previousSourceText = $previousSource
            } else {
                $Decision | Add-Member -NotePropertyName previousSourceText -NotePropertyValue $previousSource
            }
            $Decision.updatedAt = (Get-Date).ToString("o")
            $script:dirty = $true
        }
        if ($Decision.PSObject.Properties["sourceChanged"]) {
            $Decision.sourceChanged = $true
        } else {
            $Decision | Add-Member -NotePropertyName sourceChanged -NotePropertyValue $true
        }
    } else {
        if ($status -eq "pending" -and [string]::IsNullOrWhiteSpace([string]$Decision.text) -and -not [string]::IsNullOrWhiteSpace($defaultTranslation) -and [string]::IsNullOrWhiteSpace([string]$Decision.updatedAt)) {
            $status = "translated"
            $Decision.text = $defaultTranslation
        }
        $Decision.status = $status
        if (-not $Decision.PSObject.Properties["sourceChanged"]) {
            $Decision | Add-Member -NotePropertyName sourceChanged -NotePropertyValue $false
        }
        if (-not $Decision.PSObject.Properties["previousSourceText"]) {
            $Decision | Add-Member -NotePropertyName previousSourceText -NotePropertyValue ""
        }
    }

    $Decision.sourceHash = $sourceHash
    $Decision.sourceText = $source
    $Decision.target = Get-RelativeTarget $Row
}

function Set-DecisionStatus([object]$Row, [string]$Status) {
    $decision = Get-Decision $Row
    $decision.status = $Status
    if ($Status -ne "pending") {
        $decision.sourceChanged = $false
        $decision.previousSourceText = ""
    }
    $decision.updatedAt = (Get-Date).ToString("o")
    $script:dirty = $true
}

function Find-ComparisonFile([string]$Root) {
    $auditRoot = Join-Path $Root "_TranslationAudit"
    if (-not (Test-Path -LiteralPath $auditRoot)) {
        throw "검토 감사 폴더를 찾을 수 없습니다: $auditRoot"
    }
    $file = Get-ChildItem -LiteralPath $auditRoot -File -Filter "*-comparison.json" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $file) { throw "comparison.json 파일을 찾을 수 없습니다: $auditRoot" }
    return $file.FullName
}

function Find-LatestReviewRoot {
    $roots = @($script:appReviewRoot, (Join-Path $scriptRoot "reviews"))
    $candidates = New-Object "System.Collections.Generic.List[object]"
    foreach ($reviewsRoot in $roots) {
        if (-not (Test-Path -LiteralPath $reviewsRoot)) { continue }
        Get-ChildItem -LiteralPath $reviewsRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object {
                $audit = Join-Path $_.FullName "_TranslationAudit"
                (Test-Path -LiteralPath $audit) -and $null -ne (Get-ChildItem -LiteralPath $audit -File -Filter "*-comparison.json" -ErrorAction SilentlyContinue | Select-Object -First 1)
            } |
            ForEach-Object { [void]$candidates.Add($_) }
    }
    $dir = $candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($dir) { return $dir.FullName }
    return ""
}

function Load-Glossary {
    if ($script:glossaryLoaded) { return }
    $script:glossaryLoaded = $true
    $path = Join-Path $scriptRoot "glossary.generated.ko.json"
    if (-not (Test-Path -LiteralPath $path)) { return }
    try {
        $json = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        $script:glossary = @($json.terms | Where-Object { $_.source -and $_.ko })
    } catch {
        $script:glossary = @()
    }
}

function Load-Decisions {
    $script:decisions = @{}
    $path = Get-DecisionPath
    if (-not $path -or -not (Test-Path -LiteralPath $path)) { return }
    try {
        $json = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        foreach ($item in @($json.items)) {
            $key = if ($item.id) { "id:$($item.id)" } else { "key:$($item.key)" }
            $script:decisions[$key] = [pscustomobject]@{
                id = [string]$item.id
                key = [string]$item.key
                target = [string]$item.target
                status = if ($item.status) { [string]$item.status } else { "pending" }
                text = ConvertTo-FlatString $item.text
                note = [string]$item.note
                sourceHash = [string]$item.sourceHash
                sourceText = ConvertTo-FlatString $item.sourceText
                sourceChanged = if ($item.PSObject.Properties["sourceChanged"]) { ConvertTo-BoolValue $item.sourceChanged } else { $false }
                previousSourceText = if ($item.PSObject.Properties["previousSourceText"]) { ConvertTo-FlatString $item.previousSourceText } else { "" }
                updatedAt = [string]$item.updatedAt
            }
        }
    } catch {
        [System.Windows.Forms.MessageBox]::Show("검수 상태 파일을 읽지 못했습니다.`r`n$($_.Exception.Message)", "RimWorld AI Translator") | Out-Null
    }
}

function Import-PreviousProjectDecisions {
    $path = Get-DecisionPath
    if ($path -and (Test-Path -LiteralPath $path)) { return }
    if (-not $script:selectedProjectId -or -not $script:reviewRoot) { return }
    $project = @($script:projects | Where-Object { $_.id -eq $script:selectedProjectId } | Select-Object -First 1)
    if ($project.Count -eq 0 -or -not $project[0].latestReviewRoot) { return }

    $previousRoot = [System.IO.Path]::GetFullPath([string]$project[0].latestReviewRoot)
    $currentRoot = [System.IO.Path]::GetFullPath($script:reviewRoot)
    if ($previousRoot.TrimEnd("\", "/") -eq $currentRoot.TrimEnd("\", "/")) { return }

    $previousDecisionFile = Join-Path $previousRoot "review-decisions.json"
    if (-not (Test-Path -LiteralPath $previousDecisionFile)) { return }

    try {
        $json = [System.IO.File]::ReadAllText($previousDecisionFile, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        $lookup = @{}
        foreach ($item in @($json.items)) {
            if (-not $item) { continue }
            if ($item.target -and $item.key) { $lookup["target:$($item.target)|key:$($item.key)"] = $item }
            if ($item.key) { $lookup["key:$($item.key)"] = $item }
            if ($item.id) { $lookup["id:$($item.id)"] = $item }
        }

        $imported = 0
        foreach ($row in $script:rows) {
            $target = Get-RelativeTarget $row
            $item = $null
            foreach ($lookupKey in @("target:$target|key:$($row.key)", "key:$($row.key)", "id:$($row.id)")) {
                if ($lookup.ContainsKey($lookupKey)) {
                    $item = $lookup[$lookupKey]
                    break
                }
            }
            if (-not $item) { continue }
            $decision = [pscustomobject]@{
                id = [string]$row.id
                key = [string]$row.key
                target = $target
                status = if ($item.status) { [string]$item.status } else { "pending" }
                text = ConvertTo-FlatString $item.text
                note = [string]$item.note
                sourceHash = [string]$item.sourceHash
                sourceText = ConvertTo-FlatString $item.sourceText
                sourceChanged = if ($item.PSObject.Properties["sourceChanged"]) { ConvertTo-BoolValue $item.sourceChanged } else { $false }
                previousSourceText = if ($item.PSObject.Properties["previousSourceText"]) { ConvertTo-FlatString $item.previousSourceText } else { "" }
                updatedAt = [string]$item.updatedAt
            }
            Normalize-DecisionForRow -Row $row -Decision $decision
            $script:decisions[(Get-RowIdentity $row)] = $decision
            $imported++
        }
        if ($imported -gt 0) {
            Add-Log "이전 검수 상태 $imported개를 이어받았습니다."
            $script:dirty = $true
            Save-Decisions
        }
    } catch {
        Add-Log "이전 검수 상태를 이어받지 못했습니다: $($_.Exception.Message)"
    }
}

function Save-Decisions {
    if (-not $script:reviewRoot) { return }
    Save-CurrentEdit
    $items = New-Object "System.Collections.Generic.List[object]"
    foreach ($row in $script:rows) {
        $decision = Get-Decision $row
        [void]$items.Add([pscustomobject]@{
            id = [string]$row.id
            key = [string]$row.key
            target = Get-RelativeTarget $row
            status = [string]$decision.status
            text = ConvertTo-FlatString $decision.text
            note = [string]$decision.note
            sourceHash = [string]$decision.sourceHash
            sourceText = ConvertTo-FlatString $decision.sourceText
            sourceChanged = ConvertTo-BoolValue $decision.sourceChanged
            previousSourceText = ConvertTo-FlatString $decision.previousSourceText
            updatedAt = [string]$decision.updatedAt
        })
    }
    $payload = [ordered]@{
        version = 3
        reviewRoot = $script:reviewRoot
        comparison = $script:comparisonFile
        updatedAt = (Get-Date).ToString("o")
        items = $items.ToArray()
    }
    $path = Get-DecisionPath
    $payload | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8
    $script:dirty = $false
    $lblSave.Text = "저장됨 " + (Get-Date -Format "HH:mm:ss")
}

function Save-CurrentEdit {
    if ($script:loading -or $script:currentRowIndex -lt 0 -or $script:currentRowIndex -ge $script:rows.Count) { return }
    $row = $script:rows[$script:currentRowIndex]
    $decision = Get-Decision $row
    if ($decision.text -ne $txtTranslation.Text -or $decision.note -ne $txtMemo.Text) {
        $decision.text = ConvertTo-FlatString $txtTranslation.Text
        $decision.note = [string]$txtMemo.Text
        if ([string]::IsNullOrWhiteSpace($decision.text)) {
            $decision.status = "pending"
        } elseif ($decision.status -eq "pending" -or $decision.status -eq "approved") {
            $decision.status = "translated"
            $decision.sourceChanged = $false
            $decision.previousSourceText = ""
        }
        $decision.updatedAt = (Get-Date).ToString("o")
        $script:dirty = $true
        $lblSave.Text = "저장 필요"
    }
}

function Get-RowPassesFilter([object]$Row) {
    $decision = Get-Decision $Row
    $status = [string]$cmbStatus.SelectedItem
    $warnings = @(Get-RowWarnings $Row)
    if ($script:currentFile -ne "__ALL__" -and (Get-RelativeTarget $Row) -ne $script:currentFile) { return $false }
    switch ($status) {
        "미번역" { if ($decision.status -ne "pending") { return $false } }
        "번역됨" { if ($decision.status -ne "translated") { return $false } }
        "검토됨" { if ($decision.status -ne "approved") { return $false } }
        "업데이트로 변경됨" { if (-not (ConvertTo-BoolValue $decision.sourceChanged)) { return $false } }
        "반려" { if ($decision.status -ne "rejected") { return $false } }
        "보류" { if ($decision.status -ne "hold") { return $false } }
        "주의" { if ($warnings.Count -eq 0) { return $false } }
        "후보 있음" { if ([string]::IsNullOrWhiteSpace([string]$Row.candidate)) { return $false } }
        "기존 있음" { if ([string]::IsNullOrWhiteSpace([string]$Row.existing)) { return $false } }
    }

    $query = $txtSearch.Text.Trim().ToLowerInvariant()
    if ($query) {
        $mode = if ($cmbSearchField -and $cmbSearchField.SelectedItem) { [string]$cmbSearchField.SelectedItem } else { "텍스트/키" }
        $keyBlob = @([string]$Row.id, [string]$Row.key, (Get-RelativeTarget $Row)) -join "`n"
        $textBlob = @(
            (ConvertTo-FlatString $Row.source),
            (ConvertTo-FlatString $Row.existing),
            (ConvertTo-FlatString $Row.candidate),
            (ConvertTo-FlatString $decision.text),
            [string]$decision.note
        ) -join "`n"
        $blob = switch ($mode) {
            "키" { $keyBlob }
            "텍스트" { $textBlob }
            default { "$keyBlob`n$textBlob" }
        }
        if (-not $blob.ToLowerInvariant().Contains($query)) { return $false }
    }
    return $true
}

function Get-ItemPreview([object]$Row) {
    $source = ((ConvertTo-FlatString $Row.source) -replace "\s+", " ").Trim()
    $candidate = ((ConvertTo-FlatString (Get-Decision $Row).text) -replace "\s+", " ").Trim()
    if ($source.Length -gt 64) { $source = $source.Substring(0, 61) + "..." }
    if ($candidate.Length -gt 64) { $candidate = $candidate.Substring(0, 61) + "..." }
    return @($source, $candidate)
}

function Refresh-Summary {
    $total = $script:rows.Count
    $approved = 0
    $translated = 0
    $rejected = 0
    $hold = 0
    $pending = 0
    $updated = 0
    $warn = 0
    foreach ($row in $script:rows) {
        $decision = Get-Decision $row
        switch ($decision.status) {
            "approved" { $approved++ }
            "translated" { $translated++ }
            "rejected" { $rejected++ }
            "hold" { $hold++ }
            default { $pending++ }
        }
        if (ConvertTo-BoolValue $decision.sourceChanged) { $updated++ }
        if (@(Get-RowWarnings $row).Count -gt 0) { $warn++ }
    }
    $done = if ($total -gt 0) { [int](($approved / $total) * 100) } else { 0 }
    $lblProjectStats.Text = "전체 $total  ·  미번역 $pending  ·  번역 $translated`r`n검토 $approved  ·  업데이트 변경 $updated"
    if ($toolTip) { $toolTip.SetToolTip($lblProjectStats, "주의가 필요한 문자열: $warn개") }
    if ($statusFilterButtons -and $statusFilterButtons.Count -ge 5) {
        $filterCounts = @($total, $pending, $translated, $approved, $updated)
        for ($i = 0; $i -lt 5; $i++) {
            $statusFilterButtons[$i].AccessibleDescription = "$($statusFilterButtons[$i].Text) 상태 $($filterCounts[$i])개만 목록에 표시합니다."
            if ($toolTip) { $toolTip.SetToolTip($statusFilterButtons[$i], "$($statusFilterButtons[$i].Text) $($filterCounts[$i])개") }
        }
    }
    $progressReview.Maximum = [Math]::Max(1, $total)
    $progressReview.Value = [Math]::Min($approved, $progressReview.Maximum)
    $lblProgress.Text = "검토 진행률 $done%"
}

function Build-FileGroups {
    $groups = @{}
    foreach ($row in $script:rows) {
        $rel = Get-RelativeTarget $row
        if (-not $groups.ContainsKey($rel)) {
        $groups[$rel] = [pscustomobject]@{ File = $rel; Total = 0; Approved = 0; Translated = 0; Pending = 0; Warnings = 0 }
        }
        $group = $groups[$rel]
        $group.Total++
        switch ((Get-Decision $row).status) {
            "approved" { $group.Approved++ }
            "translated" { $group.Translated++ }
            default { $group.Pending++ }
        }
        if (@(Get-RowWarnings $row).Count -gt 0) { $group.Warnings++ }
    }
    $script:fileGroups = @($groups.Values | Sort-Object File)
}

function Refresh-FileList {
    Build-FileGroups
    $lvFiles.BeginUpdate()
    try {
        $lvFiles.Items.Clear()
        $all = New-Object System.Windows.Forms.ListViewItem("전체")
        $all.Tag = "__ALL__"
        [void]$all.SubItems.Add([string]$script:rows.Count)
        [void]$all.SubItems.Add("")
        [void]$lvFiles.Items.Add($all)
        foreach ($group in $script:fileGroups) {
            $item = New-Object System.Windows.Forms.ListViewItem($group.File)
            $item.Tag = $group.File
            [void]$item.SubItems.Add("$($group.Approved)/$($group.Total)")
            [void]$item.SubItems.Add([string]$group.Warnings)
            [void]$lvFiles.Items.Add($item)
        }
    } finally {
        $lvFiles.EndUpdate()
    }
}

function Refresh-ItemList([int]$SelectRowIndex = -1) {
    Save-CurrentEdit
    $script:visibleRowIndexes = @()
    $flowItems.SuspendLayout()
    try {
        $lvItems.Items.Clear()
        $flowItems.Controls.Clear()
        $rendered = 0
        $matched = 0
        for ($i = 0; $i -lt $script:rows.Count; $i++) {
            $row = $script:rows[$i]
            if (-not (Get-RowPassesFilter $row)) { continue }
            $matched++
            $script:visibleRowIndexes += $i
            if ($rendered -ge $script:maxRenderedCards) { continue }
            $decision = Get-Decision $row
            $warnings = @(Get-RowWarnings $row)
            $preview = Get-ItemPreview $row
            $isUpdated = ConvertTo-BoolValue $decision.sourceChanged
            $statusText = if ($isUpdated) { "변경됨" } else { Get-StatusText $decision.status }
            $statusColor = if ($isUpdated) { Get-UpdateColor } else { Get-StatusColor $decision.status }
            $textDelta = [Math]::Max(-1, [Math]::Min(2, $script:textSize - 10))

            $card = New-Object System.Windows.Forms.Panel
            $card.Width = [Math]::Max(240, $flowItems.ClientSize.Width - 18)
            $card.Height = 88 + ($textDelta * 4)
            $card.Margin = New-Object System.Windows.Forms.Padding(0, 0, 0, 6)
            $card.Tag = $i
            $card.BorderStyle = [System.Windows.Forms.BorderStyle]::None
            $card.BackColor = $script:itemCardBack
            $card.Cursor = [System.Windows.Forms.Cursors]::Hand
            $card.AccessibleRole = [System.Windows.Forms.AccessibleRole]::ListItem

            $stripe = New-Object System.Windows.Forms.Panel
            $stripe.Name = "StatusStripe"
            $stripe.SetBounds(0, 0, 3, $card.Height)
            $stripe.BackColor = $statusColor

            $keyText = [string]$row.key
            if ($keyText.Length -gt 46) { $keyText = $keyText.Substring(0, 43) + "..." }
            $statusWidth = if ($isUpdated) { 70 } else { 60 }
            $lblStatus = New-Label $statusText ($card.Width - $statusWidth - 14) 9 $statusWidth 20 $statusColor 8 ([System.Drawing.FontStyle]::Bold)
            $lblStatus.TextAlign = [System.Drawing.ContentAlignment]::MiddleRight
            $lblSourcePreview = New-Label $preview[0] 18 9 ($card.Width - $statusWidth - 42) (23 + $textDelta) $script:itemText ([Math]::Max(8.5, $script:textSize - 0.5))
            $translationY = 34 + $textDelta
            $keyY = 62 + ($textDelta * 3)
            $lblTranslationPreview = New-Label $preview[1] 18 $translationY ($card.Width - 34) (22 + $textDelta) $script:itemMuted ([Math]::Max(8, $script:textSize - 1.5))
            $lblKey = New-Label $keyText 18 $keyY ($card.Width - 34) 18 $script:itemSubtle 7.8
            if ([string]::IsNullOrWhiteSpace($preview[1])) { $lblTranslationPreview.Text = "번역 대기" }
            if ($warnings.Count -gt 0) {
                $lblTranslationPreview.Text = "주의 · " + $lblTranslationPreview.Text
                $lblTranslationPreview.ForeColor = if (Get-IsWindowsDarkMode) { [System.Drawing.Color]::FromArgb(238, 183, 92) } else { [System.Drawing.Color]::FromArgb(145, 91, 16) }
            }
            $card.AccessibleName = "$statusText, $keyText"
            $card.AccessibleDescription = "$($preview[0]). 번역: $($lblTranslationPreview.Text)"

            $card.Controls.AddRange(@($stripe, $lblSourcePreview, $lblStatus, $lblTranslationPreview, $lblKey))
            foreach ($control in @($card, $stripe, $lblKey, $lblSourcePreview, $lblStatus, $lblTranslationPreview)) {
                $control.Add_Click({
                    $target = $this
                    while ($target -and -not ($target.Tag -is [int])) { $target = $target.Parent }
                    if ($target) { Select-RowIndex ([int]$target.Tag) }
                })
            }
            $flowItems.Controls.Add($card)
            $rendered++
        }
        if ($matched -gt $rendered) {
            $more = New-Object System.Windows.Forms.Label
            $more.Text = "총 $matched개 중 $rendered개 표시 · 검색어로 좁혀보세요"
            $more.Width = [Math]::Max(240, $flowItems.ClientSize.Width - 18)
            $more.Height = 38
            $more.Margin = New-Object System.Windows.Forms.Padding(0, 8, 0, 8)
            $more.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
            $more.Font = New-Font 8.5
            $more.ForeColor = $script:itemMuted
            $more.BackColor = $flowItems.BackColor
            $flowItems.Controls.Add($more)
        }
    } finally {
        $flowItems.ResumeLayout()
    }

    if ($script:visibleRowIndexes.Count -eq 0) {
        Clear-CurrentView
        return
    }

    $targetRow = $script:visibleRowIndexes[0]
    if ($SelectRowIndex -ge 0) {
        foreach ($visible in $script:visibleRowIndexes) { if ($visible -eq $SelectRowIndex) { $targetRow = $visible; break } }
    }
    Select-RowIndex ([int]$targetRow)
    Refresh-Summary
}

function Refresh-ResultSelection {
    if (-not $flowItems) { return }
    foreach ($card in @($flowItems.Controls)) {
        if (-not ($card.Tag -is [int])) { continue }
        if ([int]$card.Tag -eq $script:currentRowIndex) {
            $card.BackColor = $script:itemCardSelected
        } else {
            $card.BackColor = $script:itemCardBack
        }
    }
}

function Clear-CurrentView {
    $script:currentRowIndex = -1
    $txtSource.Text = ""
    $txtTranslation.Text = ""
    $txtExisting.Text = ""
    $txtCandidate.Text = ""
    $txtMeta.Text = ""
    $txtWarnings.Text = ""
    $txtHistory.Text = ""
    $txtTerms.Text = ""
    $txtMemo.Text = ""
    $lblCurrent.Text = "항목 없음"
    if ($lblUpdateBadge) { $lblUpdateBadge.Visible = $false }
}

function Set-HistoryView([string]$Source, [string]$Existing, [string]$Candidate, [string]$Translation, [string]$PreviousSource = "", [bool]$SourceChanged = $false) {
    $isDark = Get-IsWindowsDarkMode
    $titleColor = if ($isDark) { [System.Drawing.Color]::FromArgb(183, 174, 159) } else { [System.Drawing.Color]::FromArgb(112, 102, 86) }
    $bodyColor = if ($isDark) { [System.Drawing.Color]::FromArgb(239, 233, 221) } else { [System.Drawing.Color]::FromArgb(47, 44, 38) }
    $reviewColor = if ($isDark) { [System.Drawing.Color]::FromArgb(107, 188, 129) } else { [System.Drawing.Color]::FromArgb(47, 126, 75) }
    $sections = New-Object "System.Collections.Generic.List[object]"
    if ($SourceChanged) {
        [void]$sections.Add([pscustomobject]@{ Title = "업데이트 전 원문"; Value = $(if ($PreviousSource) { $PreviousSource } else { "(기록 없음)" }); Color = Get-UpdateColor })
        [void]$sections.Add([pscustomobject]@{ Title = "현재 원문"; Value = $Source; Color = $bodyColor })
    } else {
        [void]$sections.Add([pscustomobject]@{ Title = "원문"; Value = $Source; Color = $bodyColor })
    }
    [void]$sections.Add([pscustomobject]@{ Title = "기존 번역"; Value = $(if ($Existing) { $Existing } else { "(없음)" }); Color = $bodyColor })
    [void]$sections.Add([pscustomobject]@{ Title = "AI 후보"; Value = $(if ($Candidate) { $Candidate } else { "(없음)" }); Color = $bodyColor })
    [void]$sections.Add([pscustomobject]@{ Title = "현재 검수"; Value = $(if ($Translation) { $Translation } else { "(비어 있음)" }); Color = $reviewColor })

    $txtHistory.Clear()
    foreach ($section in $sections) {
        $txtHistory.SelectionStart = $txtHistory.TextLength
        $txtHistory.SelectionFont = $script:historyTitleFont
        $txtHistory.SelectionColor = $titleColor
        $txtHistory.AppendText("$($section.Title)`r`n")
        $txtHistory.SelectionStart = $txtHistory.TextLength
        $txtHistory.SelectionFont = $script:historyBodyFont
        $txtHistory.SelectionColor = $section.Color
        $txtHistory.AppendText("$($section.Value)`r`n`r`n")
    }
    $txtHistory.SelectionStart = 0
    $txtHistory.ScrollToCaret()
}

function Select-RowIndex([int]$Index) {
    if ($Index -lt 0 -or $Index -ge $script:rows.Count) { return }
    Save-CurrentEdit
    $script:loading = $true
    try {
        $script:currentRowIndex = $Index
        $row = $script:rows[$Index]
        $decision = Get-Decision $row
        $warnings = @(Get-RowWarnings $row)
        $relative = Get-RelativeTarget $row
        $source = ConvertTo-FlatString $row.source
        $candidate = ConvertTo-FlatString $row.candidate
        $existing = ConvertTo-FlatString $row.existing
        $translation = ConvertTo-FlatString $decision.text
        $sourceChanged = ConvertTo-BoolValue $decision.sourceChanged
        $previousSource = ConvertTo-FlatString $decision.previousSourceText

        $statusLabel = Get-StatusText $decision.status
        $updateLabel = if ($sourceChanged) { "  ·  업데이트로 변경됨" } else { "" }
        $warningLabel = if ($warnings.Count -gt 0) { "  ·  주의 $($warnings.Count)" } else { "" }
        $lblCurrent.Text = "{0} / {1}   {2}{3}" -f ($Index + 1), $script:rows.Count, $statusLabel, $warningLabel
        $lblCurrent.ForeColor = if ($sourceChanged) { Get-UpdateColor } else { Get-StatusColor $decision.status }
        $lblUpdateBadge.Visible = $sourceChanged
        $lblUpdateBadge.ForeColor = Get-UpdateColor
        $lblCurrent.AccessibleName = "현재 문자열 상태"
        $lblCurrent.AccessibleDescription = "$statusLabel$updateLabel, 전체 $($script:rows.Count)개 중 $($Index + 1)번째$warningLabel"
        $txtSource.Text = $source
        $txtTranslation.Text = $translation
        $txtExisting.Text = $existing
        $txtCandidate.Text = $candidate
        $txtMemo.Text = [string]$decision.note
        $wordCount = @($source -split '\s+' | Where-Object { $_ }).Count
        $safeText = if (ConvertTo-BoolValue $row.safeToApply) { "예" } else { "아니요" }
        $txtMeta.Text = "키  $($row.key)`r`n파일  $relative`r`nID  $($row.id)   ·   단어 $wordCount   ·   안전 적용 $safeText"
        $issueLines = New-Object "System.Collections.Generic.List[string]"
        if ($sourceChanged) { [void]$issueLines.Add("업데이트로 원문이 변경되었습니다. 다시 번역하거나 검토해야 적용할 수 있습니다.") }
        foreach ($warning in $warnings) { [void]$issueLines.Add([string]$warning) }
        $txtWarnings.Text = if ($issueLines.Count -gt 0) { [string]::Join("`r`n", $issueLines.ToArray()) } else { "문제 없음" }
        Set-HistoryView -Source $source -Existing $existing -Candidate $candidate -Translation $translation -PreviousSource $previousSource -SourceChanged $sourceChanged
        Update-TermsForRow $row
        Refresh-ResultSelection
    } finally {
        $script:loading = $false
    }
}

function Update-TermsForRow([object]$Row) {
    if (-not $script:glossaryLoaded) {
        $txtTerms.Clear()
        return
    }
    $text = ((ConvertTo-FlatString $Row.source) + "`n" + (ConvertTo-FlatString $Row.candidate)).ToLowerInvariant()
    $hits = New-Object "System.Collections.Generic.List[string]"
    foreach ($term in $script:glossary) {
        $source = [string]$term.source
        if ([string]::IsNullOrWhiteSpace($source)) { continue }
        if ($source.Length -lt 3) { continue }
        if ($text.Contains($source.ToLowerInvariant())) {
            $line = "$source => $($term.ko)"
            if ($term.note) { $line += " ($($term.note))" }
            [void]$hits.Add($line)
        }
        if ($hits.Count -ge 60) { break }
    }
    if ($hits.Count -eq 0) {
        $txtTerms.Text = "관련 용어 없음"
    } else {
        $txtTerms.Text = [string]::Join("`r`n", $hits.ToArray())
    }
}

function Move-Selection([int]$Delta) {
    if (-not $script:visibleRowIndexes -or $script:visibleRowIndexes.Count -eq 0) { return }
    $currentVisible = 0
    for ($i = 0; $i -lt $script:visibleRowIndexes.Count; $i++) {
        if ([int]$script:visibleRowIndexes[$i] -eq $script:currentRowIndex) { $currentVisible = $i; break }
    }
    $next = [Math]::Max(0, [Math]::Min($script:visibleRowIndexes.Count - 1, $currentVisible + $Delta))
    Select-RowIndex ([int]$script:visibleRowIndexes[$next])
}

function Mark-Current([string]$Status, [bool]$Advance) {
    if ($script:currentRowIndex -lt 0) { return }
    Save-CurrentEdit
    $row = $script:rows[$script:currentRowIndex]
    Set-DecisionStatus -Row $row -Status $Status
    Save-Decisions
    $old = $script:currentRowIndex
    Refresh-FileList
    Refresh-ItemList -SelectRowIndex $old
    if ($Advance) { Move-Selection 1 }
}

function Load-ReviewRoot([string]$Root, [switch]$NoImport) {
    if (-not $Root -or -not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "검토 폴더를 찾을 수 없습니다: $Root"
    }
    if ($script:reviewRoot -and $script:dirty) {
        Save-Decisions
    }
    $script:reviewRoot = [System.IO.Path]::GetFullPath($Root)
    if (-not $script:selectedProjectId) {
        foreach ($project in $script:projects) {
            if (-not $project.latestReviewRoot) { continue }
            try {
                if ([System.IO.Path]::GetFullPath([string]$project.latestReviewRoot) -ieq $script:reviewRoot) {
                    $script:selectedProjectId = [string]$project.id
                    $script:selectedModRoot = [string]$project.modRoot
                    break
                }
            } catch {}
        }
    }
    $script:comparisonFile = Find-ComparisonFile $script:reviewRoot
    $parsed = [System.IO.File]::ReadAllText($script:comparisonFile, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $script:rows = @($parsed)
    $script:currentRowIndex = -1
    Load-Decisions
    if (-not $NoImport) { Import-PreviousProjectDecisions }
    foreach ($row in $script:rows) { [void](Get-Decision $row) }
    if ($script:dirty) { Save-Decisions }
    $script:currentFile = "__ALL__"
    $lblProject.Text = Get-CurrentDisplayName
    $lblPath.Text = if ($script:selectedModRoot) { $script:selectedModRoot } else { $script:reviewRoot }
    Update-SearchCrumb
    Refresh-FileList
    Refresh-ItemList
    $lblSave.Text = "불러옴 " + (Get-Date -Format "HH:mm:ss")
    $script:lastReviewOutputPath = $script:reviewRoot
    $hasProjectMod = [bool](Get-ActiveProjectModRoot)
    if ($btnApply) { $btnApply.Enabled = $hasProjectMod }
    if ($btnApplyTranslated) { $btnApplyTranslated.Enabled = $hasProjectMod }
}

function Choose-ReviewRoot {
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = "검수할 리뷰 결과 폴더를 선택하세요."
    if ($script:reviewRoot -and (Test-Path -LiteralPath $script:reviewRoot)) {
        $dlg.SelectedPath = $script:reviewRoot
    } else {
        $reviewsRoot = Join-Path $scriptRoot "reviews"
        if (Test-Path -LiteralPath $reviewsRoot) { $dlg.SelectedPath = $reviewsRoot }
    }
    if ($dlg.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
        Load-ReviewRoot $dlg.SelectedPath
        if ($script:selectedModRoot) {
            Register-ProjectRun -ReviewRoot $dlg.SelectedPath -Provider "manual"
        }
    }
}

function Open-ReviewFolder {
    if ($script:reviewRoot -and (Test-Path -LiteralPath $script:reviewRoot)) {
        Start-Process -FilePath $script:explorerExe -ArgumentList "`"$script:reviewRoot`""
    }
}

function Open-ModFolder {
    $modRoot = Get-ActiveProjectModRoot
    if ($modRoot -and (Test-Path -LiteralPath $modRoot -PathType Container)) {
        Start-Process -FilePath $script:explorerExe -ArgumentList "`"$modRoot`""
    }
}

function Copy-ToClipboard([string]$Text) {
    if ($null -ne $Text) { [System.Windows.Forms.Clipboard]::SetText($Text) }
}

function Refresh-ProjectList {
    if (-not $cmbProject) { return }
    $current = $script:selectedProjectId
    $cmbProject.BeginUpdate()
    $script:loadingProjectList = $true
    try {
        $cmbProject.Items.Clear()
        foreach ($project in @($script:projects | Sort-Object @{ Expression = "updatedAt"; Descending = $true })) {
            $label = $project.name
            if ($project.workshopId) { $label += " [$($project.workshopId)]" }
            $item = [pscustomobject]@{ Display = $label; Project = $project }
            [void]$cmbProject.Items.Add($item)
            if ($current -and $project.id -eq $current) { $cmbProject.SelectedItem = $item }
        }
    } finally {
        $script:loadingProjectList = $false
        $cmbProject.EndUpdate()
    }
}

function Update-ModCatalogControls {
    if (-not $cmbModCatalog) { return }
    $cmbModCatalog.BeginUpdate()
    try {
        $cmbModCatalog.Items.Clear()
        foreach ($mod in $script:modCatalog) { [void]$cmbModCatalog.Items.Add($mod) }
    } finally {
        $cmbModCatalog.EndUpdate()
    }
    if ($cmbDashboardMods) {
        $cmbDashboardMods.BeginUpdate()
        try {
            $cmbDashboardMods.Items.Clear()
            foreach ($mod in $script:modCatalog) { [void]$cmbDashboardMods.Items.Add($mod) }
            if ($cmbDashboardMods.Items.Count -gt 0 -and $cmbDashboardMods.SelectedIndex -lt 0) { $cmbDashboardMods.SelectedIndex = 0 }
        } finally {
            $cmbDashboardMods.EndUpdate()
        }
    }
}

function Refresh-ModCatalog([switch]$PreferCache) {
    if (-not $cmbModCatalog) { return }
    if ($PreferCache -and (Try-LoadModCatalogCache)) {
        Update-ModCatalogControls
        $lblRunStatus.Text = "모드 $($script:modCatalog.Count)개 준비됨"
        return
    }

    $lblRunStatus.Text = "모드 검색 중..."
    [System.Windows.Forms.Application]::DoEvents()
    $script:modCatalog = @(Find-RimWorldMods)
    Update-ModCatalogControls
    Save-ModCatalogCache
    $lblRunStatus.Text = "모드 $($script:modCatalog.Count)개 검색됨"
}

function Set-SelectedMod([object]$ModInfo) {
    if (-not $ModInfo) { return }
    $project = Get-OrCreateProject $ModInfo
    Save-ProjectStore
    Set-ActiveProject $project
    Add-Log "프로젝트 열림: $($project.name)"
}

function Choose-ModFolder {
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = "프로젝트로 만들 RimWorld 모드 폴더를 선택하세요."
    if ($script:selectedModRoot -and (Test-Path -LiteralPath $script:selectedModRoot)) {
        $dlg.SelectedPath = $script:selectedModRoot
    }
    if ($dlg.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
        $info = Get-RimWorldModInfo -ModPath $dlg.SelectedPath -Source "Manual"
        if (-not $info) {
            $info = [pscustomobject]@{
                Display = Split-Path -Leaf $dlg.SelectedPath
                Name = Split-Path -Leaf $dlg.SelectedPath
                Path = [System.IO.Path]::GetFullPath($dlg.SelectedPath)
                Source = "Manual"
                Folder = Split-Path -Leaf $dlg.SelectedPath
                PackageId = ""
                WorkshopId = Get-WorkshopIdFromPath $dlg.SelectedPath
                Search = $dlg.SelectedPath.ToLowerInvariant()
            }
        }
        Set-SelectedMod $info
        if ($dashboardPanel -and $dashboardPanel.Visible) {
            Show-Workspace
            Load-SourceOnlyForSelectedMod
        }
    }
}

function Set-TranslationRunning([bool]$Running) {
    $hasProjectMod = [bool](Get-ActiveProjectModRoot)
    $btnTranslate.Enabled = (-not $Running) -and $hasProjectMod
    $btnStop.Enabled = $Running
    $btnApply.Enabled = (-not $Running) -and [bool]$script:reviewRoot -and $hasProjectMod
    $btnApplyTranslated.Enabled = (-not $Running) -and [bool]$script:reviewRoot -and $hasProjectMod
    $btnLoad.Enabled = -not $Running
    $btnChooseMod.Enabled = -not $Running
    $btnRefreshMods.Enabled = -not $Running
    $cmbModCatalog.Enabled = -not $Running
    $cmbProject.Enabled = -not $Running
    $txtApiKeys.Enabled = -not $Running
    $chkIncludePatches.Enabled = -not $Running
    $chkDryRun.Enabled = -not $Running
    Update-StopButtonAppearance
}

function Start-Translation {
    if ($script:process -and -not $script:process.HasExited) {
        [System.Windows.Forms.MessageBox]::Show("이미 번역이 실행 중입니다.", "RimWorld AI Translator") | Out-Null
        return
    }
    try {
        $modRoot = Get-ActiveProjectModRoot -Require
    } catch {
        [System.Windows.Forms.MessageBox]::Show("먼저 프로젝트를 만들거나 여세요.", "RimWorld AI Translator") | Out-Null
        return
    }
    if (-not (Test-Path -LiteralPath $script:translatorScript)) {
        [System.Windows.Forms.MessageBox]::Show("번역 스크립트를 찾을 수 없습니다.`r`n$script:translatorScript", "RimWorld AI Translator") | Out-Null
        return
    }

    Save-Decisions
    Ensure-AppDataStore
    Remove-TempFiles
    $script:lastReviewOutputPath = ""
    $script:lastProvider = ""
    $script:translationLogFile = New-TempFilePath "translation-output" ".log"
    [System.IO.File]::WriteAllText($script:translationLogFile, "", [System.Text.UTF8Encoding]::new($false))
    [void]$script:tempFiles.Add($script:translationLogFile)
    $script:translationLogOffset = 0L
    $script:translationLogPartial = ""

    $keys = @(Get-ApiKeyLines $txtApiKeys.Text)
    $args = New-Object "System.Collections.Generic.List[string]"
    foreach ($item in @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $script:translatorScript, "-ModRoot", $modRoot, "-LanguageFolderName", "Korean", "-ReviewOnly", "-ReviewRoot", $script:appReviewRoot, "-BatchSize", "40", "-MaxGeneratedGlossaryTermsPerBatch", "40")) {
        [void]$args.Add($item)
    }
    if ($chkIncludePatches.Checked) { [void]$args.Add("-IncludePatches") }
    if ($chkDryRun.Checked) { [void]$args.Add("-DryRun") }

    $txtLog.Clear()
    Add-Log "번역 시작: $modRoot"
    if ($keys.Count -gt 0) {
        Add-Log "Cerebras API 키 $($keys.Count)개 사용"
    } else {
        Add-Log "API 키 없음: Google 번역 후보를 생성합니다."
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $command = (Quote-CmdArgument $script:powershellExe) + " " + ([string]::Join(" ", @($args | ForEach-Object { Quote-CmdArgument $_ }))) + " > " + (Quote-CmdArgument $script:translationLogFile) + " 2>&1"
    $psi.FileName = $script:cmdExe
    $psi.Arguments = "/d /s /c `"$command`""
    $psi.WorkingDirectory = $scriptRoot
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $false
    $psi.RedirectStandardError = $false
    $psi.CreateNoWindow = $true
    if ($keys.Count -gt 0) {
        $psi.EnvironmentVariables["RIMWORLD_TRANSLATOR_API_KEYS"] = [string]::Join("`n", $keys)
    } else {
        $psi.EnvironmentVariables["RIMWORLD_TRANSLATOR_API_KEYS"] = ""
        $psi.EnvironmentVariables["CEREBRAS_API_KEY"] = ""
    }

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    $proc.EnableRaisingEvents = $true
    $script:process = $proc
    $script:startedAt = Get-Date
    $script:processExitHandled = $false
    $script:stopRequested = $false
    $progressRun.Value = 0
    $progressRun.Maximum = 100
    $lblRunStatus.Text = "실행 준비 중"
    Set-TranslationRunning $true
    try {
        [void]$proc.Start()
        Add-Log "번역 프로세스 PID=$($proc.Id)"
    } catch {
        Add-Log "실행 실패: $($_.Exception.Message)"
        Set-TranslationRunning $false
    }
}

function Stop-Translation {
    if ($script:process -and -not $script:process.HasExited) {
        $script:stopRequested = $true
        $btnStop.Enabled = $false
        $lblRunStatus.Text = "중지 요청 중"
        Add-Log "사용자 요청으로 중지합니다."
        Stop-ProcessTree $script:process.Id
    }
}

function Load-SourceOnlyForSelectedMod {
    try {
        $modRoot = Get-ActiveProjectModRoot -Require
    } catch {
        [System.Windows.Forms.MessageBox]::Show("먼저 프로젝트를 만들거나 여세요.", "RimWorld AI Translator") | Out-Null
        return
    }
    if (-not (Test-Path -LiteralPath $script:translatorScript)) {
        [System.Windows.Forms.MessageBox]::Show("번역 스크립트를 찾을 수 없습니다.`r`n$script:translatorScript", "RimWorld AI Translator") | Out-Null
        return
    }

    Save-Decisions
    Ensure-AppDataStore
    Remove-TempFiles
    $script:lastReviewOutputPath = ""
    $script:lastProvider = "sourceonly"
    $args = @(
        "-NoProfile", "-ExecutionPolicy", "Bypass",
        "-File", $script:translatorScript,
        "-ModRoot", $modRoot,
        "-LanguageFolderName", "Korean",
        "-ReviewOnly",
        "-ReviewRoot", $script:appReviewRoot,
        "-SourceOnly"
    )
    if ($chkIncludePatches.Checked) { $args += "-IncludePatches" }

    $txtLog.Clear()
    Add-Log "원문 로드 시작: $modRoot"
    $lblRunStatus.Text = "원문 로드 중"
    Set-TranslationRunning $true
    try {
        $output = & $script:powershellExe @args 2>&1
        $exitCode = $LASTEXITCODE
        foreach ($line in @($output)) {
            Add-Log ([string]$line)
            Update-ProgressFromLine ([string]$line)
        }
        if ($exitCode -eq 0 -and $script:lastReviewOutputPath -and (Test-Path -LiteralPath $script:lastReviewOutputPath -PathType Container)) {
            Load-ReviewRoot $script:lastReviewOutputPath -NoImport
            Register-ProjectRun -ReviewRoot $script:lastReviewOutputPath -Provider "sourceonly"
            $lblRunStatus.Text = "원문 로드 완료"
            Add-Log "원문 목록을 불러왔습니다. 기존 한국어 번역이 있으면 번역칸의 기본값으로 사용합니다."
        } elseif ($exitCode -eq 0) {
            $lblRunStatus.Text = "원문 로드 완료"
        } else {
            $lblRunStatus.Text = "원문 로드 실패"
        }
    } catch {
        Add-Log "원문 로드 실패: $($_.Exception.Message)"
        $lblRunStatus.Text = "원문 로드 실패"
    } finally {
        Set-TranslationRunning $false
    }
}

function Apply-ReviewedTranslations([string]$ApplyStatus = "ApprovedOnly") {
    try {
        $modRoot = Get-ActiveProjectModRoot -Require
    } catch {
        [System.Windows.Forms.MessageBox]::Show("적용할 프로젝트를 먼저 열어주세요.", "RimWorld AI Translator") | Out-Null
        return
    }
    if (-not $script:reviewRoot -or -not (Test-Path -LiteralPath $script:reviewRoot)) {
        [System.Windows.Forms.MessageBox]::Show("적용할 검수 결과가 없습니다.", "RimWorld AI Translator") | Out-Null
        return
    }
    if (-not (Test-Path -LiteralPath $script:reviewApplyScript)) {
        [System.Windows.Forms.MessageBox]::Show("검토 적용 스크립트를 찾을 수 없습니다.`r`n$script:reviewApplyScript", "RimWorld AI Translator") | Out-Null
        return
    }
    Save-Decisions
    $modeLabel = if ($ApplyStatus -eq "TranslatedAndApproved") { "번역됨 적용" } else { "검토됨 적용" }
    $eligibleCount = 0
    foreach ($row in $script:rows) {
        $status = [string](Get-Decision $row).status
        if ($status -eq "approved" -or ($ApplyStatus -eq "TranslatedAndApproved" -and $status -eq "translated")) {
            $eligibleCount++
        }
    }
    if ($eligibleCount -eq 0) {
        [System.Windows.Forms.MessageBox]::Show("현재 적용할 문자열이 없습니다.", "RimWorld AI Translator", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
        return
    }
    $applyTarget = Join-Path $modRoot "Languages\Korean"
    $confirmText = "$modeLabel 대상 $eligibleCount개를 Korean 폴더에 반영합니다.`r`n`r`n대상: $applyTarget`r`n`r`n계속할까요?"
    $confirm = [System.Windows.Forms.MessageBox]::Show(
        $confirmText,
        "번역 적용 확인",
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Warning,
        [System.Windows.Forms.MessageBoxDefaultButton]::Button2
    )
    if ($confirm -ne [System.Windows.Forms.DialogResult]::Yes) { return }
    $applyArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $script:reviewApplyScript, "-ModRoot", $modRoot, "-ReviewRoot", $script:reviewRoot, "-LanguageFolderName", "Korean", "-Overwrite", "-ApplyStatus", $ApplyStatus)
    Add-Log "$modeLabel 시작: $script:reviewRoot"
    $lblRunStatus.Text = "$modeLabel 중"
    Set-TranslationRunning $true
    try {
        $output = & $script:powershellExe @applyArgs 2>&1
        $exitCode = $LASTEXITCODE
        foreach ($line in @($output)) { Add-Log ([string]$line) }
        if ($exitCode -eq 0) {
            $lblRunStatus.Text = "$modeLabel 완료"
            Mark-ProjectApplied
        } else {
            $lblRunStatus.Text = "$modeLabel 실패"
        }
    } catch {
        Add-Log "$modeLabel 실패: $($_.Exception.Message)"
        $lblRunStatus.Text = "$modeLabel 실패"
    } finally {
        Set-TranslationRunning $false
    }
}

function Format-LocalTimeText([string]$IsoText) {
    if ([string]::IsNullOrWhiteSpace($IsoText)) { return "-" }
    try {
        $dt = [datetime]::Parse($IsoText).ToLocalTime()
        return $dt.ToString("yyyy-MM-dd HH:mm")
    } catch {
        return $IsoText
    }
}

function Get-ProjectReviewStats([object]$Project) {
    $stats = [pscustomobject]@{
        Total = 0
        Pending = 0
        Translated = 0
        Approved = 0
        Updated = 0
        Warnings = 0
        Label = "검수 없음"
    }
    if (-not $Project -or -not $Project.latestReviewRoot -or -not (Test-Path -LiteralPath $Project.latestReviewRoot -PathType Container)) {
        return $stats
    }

    try {
        $comparison = Find-ComparisonFile ([string]$Project.latestReviewRoot)
        $parsedRows = [System.IO.File]::ReadAllText($comparison, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        $rows = @($parsedRows)
        $decisionPath = Join-Path ([string]$Project.latestReviewRoot) "review-decisions.json"
        $decisions = @{}
        if (Test-Path -LiteralPath $decisionPath -PathType Leaf) {
            $json = [System.IO.File]::ReadAllText($decisionPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
            foreach ($item in @($json.items)) {
                if (-not $item) { continue }
                if ($item.id) { $decisions["id:$($item.id)"] = $item }
                if ($item.key) { $decisions["key:$($item.key)"] = $item }
                if ($item.target -and $item.key) { $decisions["target:$($item.target)|key:$($item.key)"] = $item }
            }
        }

        foreach ($row in $rows) {
            $stats.Total++
            if (@(Get-RowWarnings $row).Count -gt 0) { $stats.Warnings++ }

            $decision = $null
            foreach ($identity in @("id:$($row.id)", "key:$($row.key)")) {
                if ($identity -and $decisions.ContainsKey($identity)) {
                    $decision = $decisions[$identity]
                    break
                }
            }

            $status = if ($decision -and $decision.status) { [string]$decision.status } else {
                if ([string]::IsNullOrWhiteSpace((Get-DefaultTranslationForRow $row))) { "pending" } else { "translated" }
            }
            if ($status -eq "reviewed") { $status = "approved" }

            $sourceChanged = $false
            if ($decision) {
                $source = ConvertTo-FlatString $row.source
                $sourceHash = Get-TextFingerprint $source
                $sourceChanged = $decision.PSObject.Properties["sourceChanged"] -and (ConvertTo-BoolValue $decision.sourceChanged)
                if ($decision.PSObject.Properties["sourceHash"] -and -not [string]::IsNullOrWhiteSpace([string]$decision.sourceHash)) {
                    $sourceChanged = $sourceChanged -or (([string]$decision.sourceHash) -ne $sourceHash)
                } elseif ($decision.PSObject.Properties["sourceText"] -and -not [string]::IsNullOrWhiteSpace([string]$decision.sourceText)) {
                    $sourceChanged = $sourceChanged -or ((ConvertTo-FlatString $decision.sourceText) -ne $source)
                }
                if ($sourceChanged) { $status = "pending" }
            }
            if ($sourceChanged) { $stats.Updated++ }

            switch ($status) {
                "approved" { $stats.Approved++ }
                "translated" { $stats.Translated++ }
                default { $stats.Pending++ }
            }
        }

        $stats.Label = "전체 $($stats.Total) / 미번역 $($stats.Pending) / 번역됨 $($stats.Translated) / 검토됨 $($stats.Approved) / 변경 $($stats.Updated)"
    } catch {
        $stats.Label = "검수 통계 읽기 실패"
    }
    return $stats
}

function Get-ProjectActivityRows {
    $rows = New-Object "System.Collections.Generic.List[object]"
    foreach ($project in @($script:projects)) {
        foreach ($run in @($project.runs)) {
            if (-not $run) { continue }
            [void]$rows.Add([pscustomobject]@{
                Time = [string]$run.createdAt
                Project = [string]$project.name
                Kind = "번역"
                Text = "검수 작업 생성"
            })
        }
        if ($project.lastAppliedAt) {
            [void]$rows.Add([pscustomobject]@{
                Time = [string]$project.lastAppliedAt
                Project = [string]$project.name
                Kind = "적용"
                Text = "Korean 폴더 반영"
            })
        }
        if ($project.latestReviewRoot -and (Test-Path -LiteralPath $project.latestReviewRoot -PathType Container)) {
            $decisionPath = Join-Path ([string]$project.latestReviewRoot) "review-decisions.json"
            if (Test-Path -LiteralPath $decisionPath -PathType Leaf) {
                try {
                    $json = [System.IO.File]::ReadAllText($decisionPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
                    foreach ($item in @($json.items | Where-Object { $_.updatedAt } | Sort-Object updatedAt -Descending | Select-Object -First 12)) {
                        $keyText = if ($item.key) { [string]$item.key } else { [string]$item.id }
                        $activityStatus = Get-StatusText ([string]$item.status)
                        if ($item.PSObject.Properties["sourceChanged"] -and (ConvertTo-BoolValue $item.sourceChanged)) {
                            $activityStatus += " (업데이트 변경)"
                        }
                        [void]$rows.Add([pscustomObject]@{
                            Time = [string]$item.updatedAt
                            Project = [string]$project.name
                            Kind = "검수"
                            Text = "$keyText -> $activityStatus"
                        })
                    }
                } catch {
                }
            }
        }
    }
    return @($rows | Sort-Object @{ Expression = "Time"; Descending = $true } | Select-Object -First 120)
}

function Open-ProjectWorkspace([object]$Project) {
    if (-not $Project) { return }
    Set-ActiveProject $Project
    if ($Project.modRoot -and (Test-Path -LiteralPath $Project.modRoot -PathType Container)) {
        Show-Workspace
        if ($Project.latestReviewRoot -and (Test-Path -LiteralPath $Project.latestReviewRoot -PathType Container)) {
            Load-ReviewRoot ([string]$Project.latestReviewRoot)
        } else {
            Load-SourceOnlyForSelectedMod
        }
        return
    }
    if ($Project.latestReviewRoot -and (Test-Path -LiteralPath $Project.latestReviewRoot -PathType Container)) {
        Load-ReviewRoot ([string]$Project.latestReviewRoot)
        Show-Workspace
        return
    }
    [System.Windows.Forms.MessageBox]::Show("저장된 모드 폴더를 찾을 수 없습니다.", "RimWorld AI Translator") | Out-Null
}

function Refresh-DashboardProjects {
    if (-not $flowDashboardProjects) { return }
    $filter = if ($txtDashboardSearch) { $txtDashboardSearch.Text.Trim().ToLowerInvariant() } else { "" }
    $projectAccent = if ($script:highContrast) {
        if (Get-IsWindowsDarkMode) { [System.Drawing.Color]::FromArgb(224, 177, 92) } else { [System.Drawing.Color]::FromArgb(119, 77, 22) }
    } else {
        if (Get-IsWindowsDarkMode) { [System.Drawing.Color]::FromArgb(190, 150, 92) } else { [System.Drawing.Color]::FromArgb(166, 124, 70) }
    }
    $flowDashboardProjects.SuspendLayout()
    try {
        $flowDashboardProjects.Controls.Clear()
        $matches = @($script:projects | Sort-Object @{ Expression = "updatedAt"; Descending = $true } | Where-Object {
            if (-not $filter) { return $true }
            $blob = @([string]$_.name, [string]$_.modRoot, [string]$_.packageId, [string]$_.workshopId) -join "`n"
            return $blob.ToLowerInvariant().Contains($filter)
        })

        if ($matches.Count -eq 0) {
            $empty = New-Label "아직 프로젝트가 없습니다. 감지된 모드를 선택해 프로젝트를 만들거나 폴더를 직접 추가하세요." 12 12 820 34 $script:itemMuted 10
            $flowDashboardProjects.Controls.Add($empty)
            return
        }

        foreach ($project in $matches) {
            $stats = Get-ProjectReviewStats $project
            $card = New-Object System.Windows.Forms.Panel
            $card.Size = New-Object System.Drawing.Size(410, 204)
            $card.Margin = New-Object System.Windows.Forms.Padding(10)
            $card.BackColor = $script:itemCardBack
            $card.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
            $card.Tag = $project
            $card.Cursor = [System.Windows.Forms.Cursors]::Hand

            $name = [string]$project.name
            if ($name.Length -gt 42) { $name = $name.Substring(0, 39) + "..." }
            $accentLine = New-Object System.Windows.Forms.Panel
            $accentLine.SetBounds(0, 0, 4, 204)
            $accentLine.BackColor = $projectAccent
            $lblName = New-Label $name 22 18 366 26 $script:itemText 11.5 ([System.Drawing.FontStyle]::Bold)
            $idText = if ($project.workshopId) { "Workshop $($project.workshopId)" } elseif ($project.packageId) { [string]$project.packageId } else { Split-Path -Leaf ([string]$project.modRoot) }
            $lblId = New-Label $idText 22 48 366 20 $script:itemMuted 8.3
            $lblTotal = New-Label ("전체 " + $stats.Total) 22 78 104 30 $script:itemText 13 ([System.Drawing.FontStyle]::Bold)
            $lblCoverage = New-Label ("번역 $($stats.Translated)  ·  검토 $($stats.Approved)") 128 83 250 24 $script:itemMuted 8.8
            $lblPending = New-Label ("미번역 " + $stats.Pending) 22 116 96 22 (Get-StatusColor "pending") 8.7 ([System.Drawing.FontStyle]::Bold)
            $lblUpdated = New-Label ("업데이트 변경 " + $stats.Updated) 128 116 180 22 $(if ($stats.Updated -gt 0) { Get-UpdateColor } else { $script:itemSubtle }) 8.7 ([System.Drawing.FontStyle]::Bold)

            $progressTrack = New-Object System.Windows.Forms.Panel
            $progressTrack.SetBounds(22, 150, 252, 5)
            $progressTrack.BackColor = if (Get-IsWindowsDarkMode) { [System.Drawing.Color]::FromArgb(69, 70, 66) } else { [System.Drawing.Color]::FromArgb(220, 222, 216) }
            $progressFill = New-Object System.Windows.Forms.Panel
            $completed = $stats.Translated + $stats.Approved
            $fillWidth = if ($stats.Total -gt 0) { [int][Math]::Round(252 * ($completed / [double]$stats.Total)) } else { 0 }
            $progressFill.SetBounds(0, 0, [Math]::Max(0, [Math]::Min(252, $fillWidth)), 5)
            $progressFill.BackColor = $projectAccent
            $progressTrack.Controls.Add($progressFill)

            $lblTime = New-Label ("최근 작업 " + (Format-LocalTimeText ([string]$project.latestReviewAt))) 22 170 262 20 $script:itemSubtle 8.1
            $btnOpen = New-Button "열기" $projectAccent
            $btnOpen.ForeColor = [System.Drawing.Color]::White
            $btnOpen.SetBounds(304, 154, 86, 34)
            $btnOpen.Tag = $project
            Set-AccessibleControl $btnOpen "$name 프로젝트 열기" "$name 모드의 번역 및 검수 작업 화면을 엽니다." 0
            $btnOpen.Add_Click({ Open-ProjectWorkspace $this.Tag })

            $card.AccessibleName = "$name 프로젝트"
            $card.AccessibleDescription = "$idText, $($stats.Label), 최근 검수 $(Format-LocalTimeText ([string]$project.latestReviewAt))"

            foreach ($clickTarget in @($card, $accentLine, $lblName, $lblId, $lblTotal, $lblCoverage, $lblPending, $lblUpdated, $progressTrack, $lblTime)) {
                $clickTarget.Tag = $project
                $clickTarget.Add_Click({ Open-ProjectWorkspace $this.Tag })
            }
            $card.Controls.AddRange(@($accentLine, $lblName, $lblId, $lblTotal, $lblCoverage, $lblPending, $lblUpdated, $progressTrack, $lblTime, $btnOpen))
            $flowDashboardProjects.Controls.Add($card)
        }
    } finally {
        $flowDashboardProjects.ResumeLayout()
    }
}

function Refresh-DashboardActivity {
    if (-not $lvDashboardActivity) { return }
    $lvDashboardActivity.BeginUpdate()
    try {
        $lvDashboardActivity.Items.Clear()
        foreach ($row in Get-ProjectActivityRows) {
            $item = New-Object System.Windows.Forms.ListViewItem((Format-LocalTimeText ([string]$row.Time)))
            [void]$item.SubItems.Add([string]$row.Project)
            [void]$item.SubItems.Add([string]$row.Kind)
            [void]$item.SubItems.Add([string]$row.Text)
            [void]$lvDashboardActivity.Items.Add($item)
        }
    } finally {
        $lvDashboardActivity.EndUpdate()
    }
}

function Sync-DashboardSettingsFromMain {
    if (-not $txtDashboardApiKeys) { return }
    $script:syncingSettings = $true
    try {
        $txtDashboardApiKeys.Text = $txtApiKeys.Text
        $chkDashboardIncludePatches.Checked = $chkIncludePatches.Checked
        $chkDashboardDryRun.Checked = $chkDryRun.Checked
        $cmbDashboardTheme.SelectedIndex = switch ($script:themeMode) { "Light" { 1 } "Dark" { 2 } default { 0 } }
        $sizeIndex = $cmbDashboardTextSize.Items.IndexOf([string]$script:textSize)
        $cmbDashboardTextSize.SelectedIndex = if ($sizeIndex -ge 0) { $sizeIndex } else { 1 }
        $chkDashboardHighContrast.Checked = $script:highContrast
        $chkDashboardAutoSave.Checked = $script:autoSave
    } finally {
        $script:syncingSettings = $false
    }
}

function Sync-MainSettingsFromDashboard {
    if (-not $txtApiKeys -or $script:syncingSettings) { return }
    $script:syncingSettings = $true
    try {
        $txtApiKeys.Text = $txtDashboardApiKeys.Text
        $chkIncludePatches.Checked = $chkDashboardIncludePatches.Checked
        $chkDryRun.Checked = $chkDashboardDryRun.Checked
    } finally {
        $script:syncingSettings = $false
    }
}

function Apply-TextSize {
    $bodySize = [float]$script:textSize
    $txtSource.Font = New-Font $bodySize
    $txtTranslation.Font = New-Font ($bodySize + 0.5)
    $txtExisting.Font = New-Font ([Math]::Max(9, $bodySize - 0.5))
    $txtCandidate.Font = New-Font ([Math]::Max(9, $bodySize - 0.5))
    $txtTerms.Font = New-Font $bodySize
    $txtMemo.Font = New-Font $bodySize
    $txtWarnings.Font = New-Font $bodySize
    $txtMeta.Font = New-Font ([Math]::Max(8.5, $bodySize - 1.5))
    $script:historyTitleFont = New-Font ([Math]::Max(8.5, $bodySize - 1.5)) ([System.Drawing.FontStyle]::Bold)
    $script:historyBodyFont = New-Font ([Math]::Max(9, $bodySize - 0.5))
    $txtHistory.Font = $script:historyBodyFont
}

function Apply-DashboardPreferences {
    if ($script:syncingSettings) { return }
    $script:themeMode = switch ($cmbDashboardTheme.SelectedIndex) { 1 { "Light" } 2 { "Dark" } default { "System" } }
    if ($cmbDashboardTextSize.SelectedItem) {
        $script:textSize = [Math]::Max(9, [Math]::Min(12, [int][string]$cmbDashboardTextSize.SelectedItem))
    }
    $script:highContrast = $chkDashboardHighContrast.Checked
    $script:autoSave = $chkDashboardAutoSave.Checked
    Save-AppSettings
    Apply-TextSize
    Apply-AppTheme
    if ($script:rows.Count -gt 0) {
        Refresh-ItemList -SelectRowIndex $script:currentRowIndex
    }
    Refresh-DashboardProjects
    if ($dashboardPanel.Visible) { Show-Dashboard "settings" }
}

function Queue-AutoSave {
    if (-not $script:autoSave -or -not $autoSaveTimer) { return }
    $autoSaveTimer.Stop()
    $autoSaveTimer.Start()
}

function Focus-NextWorkRegion([int]$Direction = 1) {
    if ($dashboardPanel.Visible) {
        if ($dashSettingsPage.Visible) {
            $targets = @($txtDashboardApiKeys, $cmbDashboardTheme, $cmbDashboardTextSize, $chkDashboardAutoSave)
        } elseif ($dashActivityPage.Visible) {
            $targets = @($btnDashProjects, $lvDashboardActivity)
        } else {
            $targets = @($txtDashboardSearch, $cmbDashboardMods, $btnDashboardAddMod)
        }
    } else {
        $targets = @($txtSearch, $txtTranslation, $tabs)
    }
    $targets = @($targets | Where-Object { $_ -and $_.Visible -and $_.Enabled })
    if ($targets.Count -eq 0) { return }
    $current = -1
    for ($i = 0; $i -lt $targets.Count; $i++) {
        if ($targets[$i].ContainsFocus -or $targets[$i].Focused) { $current = $i; break }
    }
    $next = ($current + $Direction) % $targets.Count
    if ($next -lt 0) { $next += $targets.Count }
    [void]$targets[$next].Focus()
}

function Show-Dashboard([string]$Tab = "projects") {
    if (-not $dashboardPanel) { return }
    Save-CurrentEdit
    $top.Visible = $false
    $main.Visible = $false
    $dashboardPanel.Visible = $true
    $dashboardPanel.BringToFront()
    if (Get-Command Apply-AppTheme -ErrorAction SilentlyContinue) { Apply-AppTheme }

    $isDark = Get-IsWindowsDarkMode
    $inactiveBack = [System.Drawing.Color]::FromArgb(53, 59, 54)
    $inactiveFore = [System.Drawing.Color]::FromArgb(224, 229, 222)
    $activeBack = if ($script:highContrast) {
        if ($isDark) { [System.Drawing.Color]::FromArgb(224, 177, 92) } else { [System.Drawing.Color]::FromArgb(119, 77, 22) }
    } else {
        if ($isDark) { [System.Drawing.Color]::FromArgb(190, 150, 92) } else { [System.Drawing.Color]::FromArgb(166, 124, 70) }
    }
    foreach ($button in @($btnDashProjects, $btnDashActivity, $btnDashSettings)) {
        if ($button) {
            $button.BackColor = $inactiveBack
            $button.ForeColor = $inactiveFore
        }
    }
    $dashProjectsPage.Visible = $false
    $dashActivityPage.Visible = $false
    $dashSettingsPage.Visible = $false

    switch ($Tab) {
        "activity" {
            $dashActivityPage.Visible = $true
            $btnDashActivity.BackColor = $activeBack
            $btnDashActivity.ForeColor = [System.Drawing.Color]::White
            Refresh-DashboardActivity
        }
        "settings" {
            $dashSettingsPage.Visible = $true
            $btnDashSettings.BackColor = $activeBack
            $btnDashSettings.ForeColor = [System.Drawing.Color]::White
            Sync-DashboardSettingsFromMain
        }
        default {
            $dashProjectsPage.Visible = $true
            $btnDashProjects.BackColor = $activeBack
            $btnDashProjects.ForeColor = [System.Drawing.Color]::White
            Refresh-DashboardProjects
        }
    }
}

function Show-Workspace {
    if ($dashboardPanel) { $dashboardPanel.Visible = $false }
    $top.Visible = $true
    $main.Visible = $true
    $main.BringToFront()
    $top.BringToFront()
    Sync-MainSettingsFromDashboard
    if (Get-Command Apply-AppTheme -ErrorAction SilentlyContinue) { Apply-AppTheme }
}

Ensure-AppDataStore
Load-AppSettings
if ($PreviewTheme -in @("System", "Light", "Dark")) { $script:themeMode = $PreviewTheme }
if ($PreviewTextSize -ge 9 -and $PreviewTextSize -le 12) { $script:textSize = $PreviewTextSize }
if ($PreviewHighContrast) { $script:highContrast = $true }

$form = New-Object System.Windows.Forms.Form
$form.Text = "RimWorld AI Translator"
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
$form.AutoScaleMode = [System.Windows.Forms.AutoScaleMode]::None
$form.ClientSize = New-Object System.Drawing.Size(1180, 780)
$form.MinimumSize = New-Object System.Drawing.Size(1120, 720)
$form.BackColor = [System.Drawing.Color]::FromArgb(18, 24, 30)
$form.Font = New-Font 9
$form.KeyPreview = $true
$form.WindowState = [System.Windows.Forms.FormWindowState]::Maximized

$toolTip = New-Object System.Windows.Forms.ToolTip
$toolTip.AutoPopDelay = 6000
$toolTip.InitialDelay = 450
$toolTip.ReshowDelay = 100

$top = New-Object System.Windows.Forms.Panel
$top.Dock = [System.Windows.Forms.DockStyle]::Top
$top.Height = 78
$top.BackColor = [System.Drawing.Color]::FromArgb(34, 42, 50)
$form.Controls.Add($top)

$topAccent = New-Object System.Windows.Forms.Panel
$topAccent.SetBounds(0, 75, 1180, 3)
$topAccent.Anchor = [System.Windows.Forms.AnchorStyles]::Left -bor [System.Windows.Forms.AnchorStyles]::Right -bor [System.Windows.Forms.AnchorStyles]::Bottom

$lblProject = New-Label "RimWorld AI Translator" 18 8 420 24 ([System.Drawing.Color]::FromArgb(230, 238, 246)) 12 ([System.Drawing.FontStyle]::Bold)
$lblPath = New-Label "모드를 선택하면 이 화면에서 번역과 검수를 바로 시작합니다." 18 34 650 20 ([System.Drawing.Color]::FromArgb(148, 161, 174)) 8.5
$lblSave = New-Label "" 940 134 96 20 ([System.Drawing.Color]::FromArgb(160, 174, 188)) 8.5

$lblProjectPick = New-Label "" 18 60 90 18 ([System.Drawing.Color]::FromArgb(188, 199, 210)) 8.5 ([System.Drawing.FontStyle]::Bold)
$lblProjectPick.Visible = $false
$cmbProject = New-Object System.Windows.Forms.ComboBox
$cmbProject.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbProject.DisplayMember = "Display"
$cmbProject.Font = New-Font 9
$cmbProject.SetBounds(18, 80, 250, 28)
$cmbProject.Visible = $false

$lblModPick = New-Label "모드" 18 60 80 18 ([System.Drawing.Color]::FromArgb(188, 199, 210)) 8.5 ([System.Drawing.FontStyle]::Bold)
$cmbModCatalog = New-Object System.Windows.Forms.ComboBox
$cmbModCatalog.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbModCatalog.DisplayMember = "Display"
$cmbModCatalog.Font = New-Font 9
$cmbModCatalog.SetBounds(18, 80, 620, 28)

$btnRefreshMods = New-Button "새로고침" ([System.Drawing.Color]::FromArgb(72, 86, 100))
$btnRefreshMods.ForeColor = [System.Drawing.Color]::White
$btnRefreshMods.SetBounds(646, 78, 86, 30)
$btnChooseMod = New-Button "찾기" ([System.Drawing.Color]::FromArgb(72, 86, 100))
$btnChooseMod.ForeColor = [System.Drawing.Color]::White
$btnChooseMod.SetBounds(740, 78, 62, 30)

$lblApi = New-Label "API 키 (비우면 Google)" 820 12 180 18 ([System.Drawing.Color]::FromArgb(188, 199, 210)) 8.5 ([System.Drawing.FontStyle]::Bold)
$txtApiKeys = New-TextBox -Multiline
$txtApiKeys.SetBounds(820, 32, 260, 76)
$txtApiKeys.BackColor = [System.Drawing.Color]::FromArgb(26, 34, 42)
$txtApiKeys.ForeColor = [System.Drawing.Color]::FromArgb(230, 238, 246)

$chkIncludePatches = New-Object System.Windows.Forms.CheckBox
$chkIncludePatches.Text = "Patches 포함"
$chkIncludePatches.SetBounds(1096, 52, 120, 24)
$chkIncludePatches.ForeColor = [System.Drawing.Color]::FromArgb(218, 226, 234)
$chkIncludePatches.BackColor = [System.Drawing.Color]::Transparent
$chkDryRun = New-Object System.Windows.Forms.CheckBox
$chkDryRun.Text = "Dry run"
$chkDryRun.SetBounds(1096, 78, 90, 24)
$chkDryRun.ForeColor = [System.Drawing.Color]::FromArgb(218, 226, 234)
$chkDryRun.BackColor = [System.Drawing.Color]::Transparent

$btnTranslate = New-Button "번역 시작" ([System.Drawing.Color]::FromArgb(27, 126, 220))
$btnTranslate.ForeColor = [System.Drawing.Color]::White
$btnTranslate.SetBounds(1040, 108, 100, 34)
$btnTranslate.Enabled = $false
$btnStop = New-Button "중지" ([System.Drawing.Color]::FromArgb(124, 58, 68))
$btnStop.ForeColor = [System.Drawing.Color]::White
$btnStop.SetBounds(1148, 108, 64, 34)
$btnStop.Enabled = $false
$btnApply = New-Button "검토됨 적용" ([System.Drawing.Color]::FromArgb(35, 154, 94))
$btnApply.ForeColor = [System.Drawing.Color]::White
$btnApply.SetBounds(1220, 108, 118, 34)
$btnApply.Enabled = $false
$btnApplyTranslated = New-Button "번역됨 적용" ([System.Drawing.Color]::FromArgb(66, 149, 228))
$btnApplyTranslated.ForeColor = [System.Drawing.Color]::White
$btnApplyTranslated.SetBounds(1346, 108, 126, 34)
$btnApplyTranslated.Enabled = $false

$btnHome = New-Button "홈" ([System.Drawing.Color]::FromArgb(72, 86, 100))
$btnHome.ForeColor = [System.Drawing.Color]::White
$btnHome.SetBounds(1096, 14, 60, 30)
$btnLoad = New-Button "원문 로드" ([System.Drawing.Color]::FromArgb(72, 86, 100))
$btnLoad.ForeColor = [System.Drawing.Color]::White
$btnLoad.SetBounds(1164, 14, 96, 30)
$btnOpenFolder = New-Button "모드 폴더" ([System.Drawing.Color]::FromArgb(72, 86, 100))
$btnOpenFolder.ForeColor = [System.Drawing.Color]::White
$btnOpenFolder.SetBounds(1268, 14, 88, 30)
$btnSave = New-Button "저장" ([System.Drawing.Color]::FromArgb(72, 86, 100))
$btnSave.ForeColor = [System.Drawing.Color]::White
$btnSave.SetBounds(1364, 14, 70, 30)

$progressRun = New-Object System.Windows.Forms.ProgressBar
$progressRun.SetBounds(18, 140, 520, 10)
$progressRun.Minimum = 0
$progressRun.Maximum = 100
$progressRun.Value = 0
$progressRun.TabStop = $false
$progressRun.AccessibleName = "AI 번역 진행률"
$lblRunStatus = New-Label "대기 중" 552 134 380 20 ([System.Drawing.Color]::FromArgb(160, 174, 188)) 8.5

$top.Controls.AddRange(@($lblProject, $lblPath, $lblSave, $lblProjectPick, $cmbProject, $lblModPick, $cmbModCatalog, $btnRefreshMods, $btnChooseMod, $lblApi, $txtApiKeys, $chkIncludePatches, $chkDryRun, $btnTranslate, $btnStop, $btnApply, $btnApplyTranslated, $btnHome, $btnLoad, $btnOpenFolder, $btnSave, $progressRun, $lblRunStatus, $topAccent))

$main = New-Object System.Windows.Forms.SplitContainer
$main.Dock = [System.Windows.Forms.DockStyle]::Fill
$main.SplitterWidth = 2
$main.SplitterDistance = 390
$main.BackColor = [System.Drawing.Color]::FromArgb(232, 236, 240)
$main.TabStop = $false
$form.Controls.Add($main)

$rightSplit = New-Object System.Windows.Forms.SplitContainer
$rightSplit.Dock = [System.Windows.Forms.DockStyle]::Fill
$rightSplit.SplitterWidth = 2
$rightSplit.SplitterDistance = 690
$rightSplit.BackColor = [System.Drawing.Color]::FromArgb(232, 236, 240)
$rightSplit.TabStop = $false
$main.Panel2.Controls.Add($rightSplit)

$left = $main.Panel1
$left.BackColor = [System.Drawing.Color]::FromArgb(248, 249, 250)

$lblSearchCrumb = New-Label "모드`r`n전체 문자열  ·  모든 상태" 16 12 352 62 ([System.Drawing.Color]::FromArgb(36, 45, 54)) 10.5 ([System.Drawing.FontStyle]::Bold)

$cmbSearchField = New-Object System.Windows.Forms.ComboBox
$cmbSearchField.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbSearchField.Font = New-Font 9
$cmbSearchField.SetBounds(16, 66, 92, 30)
[void]$cmbSearchField.Items.AddRange(@("텍스트/키", "텍스트", "키"))
$cmbSearchField.SelectedIndex = 0

$txtSearch = New-TextBox
$txtSearch.SetBounds(108, 66, 178, 30)
$txtSearch.Text = ""
$cmbStatus = New-Object System.Windows.Forms.ComboBox
$cmbStatus.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbStatus.Font = New-Font 9
$cmbStatus.SetBounds(292, 66, 76, 30)
$cmbStatus.DropDownWidth = 168
[void]$cmbStatus.Items.AddRange(@("전체", "미번역", "번역됨", "검토됨", "업데이트로 변경됨", "주의", "후보 있음", "기존 있음"))
$cmbStatus.SelectedIndex = 0

$statusFilterBar = New-Object System.Windows.Forms.FlowLayoutPanel
$statusFilterBar.FlowDirection = [System.Windows.Forms.FlowDirection]::LeftToRight
$statusFilterBar.WrapContents = $false
$statusFilterBar.AutoScroll = $false
$statusFilterBar.Margin = New-Object System.Windows.Forms.Padding(0)
$statusFilterButtons = New-Object "System.Collections.Generic.List[System.Windows.Forms.Button]"
foreach ($spec in @(
    [pscustomobject]@{ Text = "전체"; Status = "전체"; Width = 48 },
    [pscustomobject]@{ Text = "미번역"; Status = "미번역"; Width = 60 },
    [pscustomobject]@{ Text = "번역됨"; Status = "번역됨"; Width = 60 },
    [pscustomobject]@{ Text = "검토됨"; Status = "검토됨"; Width = 60 },
    [pscustomobject]@{ Text = "변경됨"; Status = "업데이트로 변경됨"; Width = 60 }
)) {
    $filterButton = New-Button $spec.Text ([System.Drawing.Color]::White)
    $filterButton.Tag = $spec.Status
    $filterButton.Width = $spec.Width
    $filterButton.Height = 30
    $filterButton.Margin = New-Object System.Windows.Forms.Padding(0, 0, 4, 0)
    $filterButton.Font = New-Font 8 ([System.Drawing.FontStyle]::Bold)
    [void]$statusFilterButtons.Add($filterButton)
    [void]$statusFilterBar.Controls.Add($filterButton)
}

$lblProjectStats = New-Label "전체 0" 16 102 350 42 ([System.Drawing.Color]::FromArgb(53, 63, 72)) 8.5 ([System.Drawing.FontStyle]::Bold)
$progressReview = New-Object System.Windows.Forms.ProgressBar
$progressReview.SetBounds(16, 128, 260, 14)
$progressReview.Minimum = 0
$progressReview.Maximum = 1
$progressReview.Value = 0
$progressReview.TabStop = $false
$progressReview.AccessibleName = "검토 진행률"
$lblProgress = New-Label "검토 진행률 0%" 282 124 100 20 ([System.Drawing.Color]::FromArgb(80, 88, 96)) 8

$lvFiles = New-Object System.Windows.Forms.ListView
$lvFiles.SetBounds(16, 152, 352, 120)
$lvFiles.View = [System.Windows.Forms.View]::Details
$lvFiles.FullRowSelect = $true
$lvFiles.HideSelection = $false
$lvFiles.MultiSelect = $false
$lvFiles.Font = New-Font 8.5
[void]$lvFiles.Columns.Add("파일", 230)
[void]$lvFiles.Columns.Add("검토됨", 70)
[void]$lvFiles.Columns.Add("주의", 46)

$lvItems = New-Object System.Windows.Forms.ListView
$lvItems.SetBounds(16, 284, 352, 436)
$lvItems.Anchor = [System.Windows.Forms.AnchorStyles]::Top -bor [System.Windows.Forms.AnchorStyles]::Bottom -bor [System.Windows.Forms.AnchorStyles]::Left -bor [System.Windows.Forms.AnchorStyles]::Right
$lvItems.View = [System.Windows.Forms.View]::Details
$lvItems.FullRowSelect = $true
$lvItems.HideSelection = $false
$lvItems.MultiSelect = $false
$lvItems.Font = New-Font 8.5
[void]$lvItems.Columns.Add("상태", 58)
[void]$lvItems.Columns.Add("키", 128)
[void]$lvItems.Columns.Add("원문", 126)
[void]$lvItems.Columns.Add("번역", 126)
$lvItems.Visible = $false

$flowItems = New-Object System.Windows.Forms.FlowLayoutPanel
$flowItems.SetBounds(16, 284, 352, 436)
$flowItems.Anchor = [System.Windows.Forms.AnchorStyles]::Top -bor [System.Windows.Forms.AnchorStyles]::Bottom -bor [System.Windows.Forms.AnchorStyles]::Left -bor [System.Windows.Forms.AnchorStyles]::Right
$flowItems.FlowDirection = [System.Windows.Forms.FlowDirection]::TopDown
$flowItems.WrapContents = $false
$flowItems.AutoScroll = $true

$left.Controls.AddRange(@($lblSearchCrumb, $cmbSearchField, $txtSearch, $cmbStatus, $statusFilterBar, $lblProjectStats, $progressReview, $lblProgress, $lvFiles, $lvItems, $flowItems))

$center = $rightSplit.Panel1
$center.BackColor = [System.Drawing.Color]::White

$lblCurrent = New-Label "항목 없음" 18 14 520 24 ([System.Drawing.Color]::FromArgb(36, 45, 54)) 11 ([System.Drawing.FontStyle]::Bold)
$lblUpdateBadge = New-Label "업데이트로 변경됨" 520 14 150 24 ([System.Drawing.Color]::FromArgb(174, 105, 24)) 8.5 ([System.Drawing.FontStyle]::Bold)
$lblUpdateBadge.TextAlign = [System.Drawing.ContentAlignment]::MiddleRight
$lblUpdateBadge.Visible = $false
$lblSourceTitle = New-Label "원문" 18 42 120 20 ([System.Drawing.Color]::FromArgb(52, 61, 70)) 9 ([System.Drawing.FontStyle]::Bold)
$pnlSourceFrame = New-Object System.Windows.Forms.Panel
$pnlSourceFrame.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
$txtSource = New-TextBox -Multiline
$txtSource.ReadOnly = $true
$txtSource.Font = New-Font 10
$txtSource.BackColor = [System.Drawing.Color]::FromArgb(245, 247, 249)
$txtSource.BorderStyle = [System.Windows.Forms.BorderStyle]::None
$pnlSourceFrame.Controls.Add($txtSource)

$lblTranslationTitle = New-Label "번역" 18 150 120 20 ([System.Drawing.Color]::FromArgb(52, 61, 70)) 9 ([System.Drawing.FontStyle]::Bold)
$pnlTranslationFrame = New-Object System.Windows.Forms.Panel
$pnlTranslationFrame.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
$txtTranslation = New-TextBox -Multiline
$txtTranslation.Font = New-Font 10.5
$txtTranslation.BorderStyle = [System.Windows.Forms.BorderStyle]::None
$translationAccent = New-Object System.Windows.Forms.Panel
$translationAccent.Height = 3
$pnlTranslationFrame.Controls.AddRange(@($txtTranslation, $translationAccent))

$txtMeta = New-TextBox -Multiline
$txtMeta.SetBounds(18, 296, 640, 76)
$txtMeta.ReadOnly = $true
$txtMeta.BackColor = [System.Drawing.Color]::FromArgb(248, 249, 250)

$btnPrev = New-Button "‹" ([System.Drawing.Color]::FromArgb(238, 240, 242))
$btnNext = New-Button "›" ([System.Drawing.Color]::FromArgb(238, 240, 242))
$btnUseCandidate = New-Button "AI 후보" ([System.Drawing.Color]::FromArgb(230, 238, 248))
$btnUseExisting = New-Button "기존" ([System.Drawing.Color]::FromArgb(230, 238, 248))
$btnUseSource = New-Button "복사" ([System.Drawing.Color]::FromArgb(238, 238, 238))
$btnResetEdit = New-Button "되돌리기" ([System.Drawing.Color]::FromArgb(238, 238, 238))
$btnPending = New-Button "미번역" ([System.Drawing.Color]::FromArgb(236, 240, 245))
$btnTranslated = New-Button "번역됨" ([System.Drawing.Color]::FromArgb(220, 235, 252))
$btnApprove = New-Button "검토 완료" ([System.Drawing.Color]::FromArgb(218, 242, 226))
$btnApproveNext = New-Button "완료 후 다음" ([System.Drawing.Color]::FromArgb(51, 174, 111))
$btnApproveNext.ForeColor = [System.Drawing.Color]::White

$editorDivider = New-Object System.Windows.Forms.Panel
$lblReferenceTitle = New-Label "참고 번역" 18 430 120 20 ([System.Drawing.Color]::FromArgb(52, 61, 70)) 8.5 ([System.Drawing.FontStyle]::Bold)

$buttons = @($btnPrev, $btnNext, $btnUseCandidate, $btnUseExisting, $btnUseSource, $btnResetEdit, $btnPending, $btnTranslated, $btnApprove, $btnApproveNext)
$x = 18
foreach ($button in $buttons) {
    $w = switch ($button.Text) {
        "‹" { 40 }
        "›" { 40 }
        "되돌리기" { 82 }
        "완료 후 다음" { 112 }
        "AI 후보" { 70 }
        "기존" { 58 }
        "복사" { 58 }
        default { 74 }
    }
    $button.SetBounds($x, 388, $w, 34)
    $x += $w + 8
}

$lblExisting = New-Label "기존 번역" 18 440 120 20 ([System.Drawing.Color]::FromArgb(52, 61, 70)) 9 ([System.Drawing.FontStyle]::Bold)
$txtExisting = New-TextBox -Multiline
$txtExisting.SetBounds(18, 464, 310, 94)
$txtExisting.ReadOnly = $true
$txtExisting.Font = New-Font 9.5
$txtExisting.BackColor = [System.Drawing.Color]::FromArgb(248, 249, 250)
$lblCandidate = New-Label "번역 후보" 348 440 120 20 ([System.Drawing.Color]::FromArgb(52, 61, 70)) 9 ([System.Drawing.FontStyle]::Bold)
$txtCandidate = New-TextBox -Multiline
$txtCandidate.SetBounds(348, 464, 310, 94)
$txtCandidate.ReadOnly = $true
$txtCandidate.Font = New-Font 9.5
$txtCandidate.BackColor = [System.Drawing.Color]::FromArgb(248, 249, 250)

$center.Controls.AddRange(@($lblCurrent, $lblUpdateBadge, $lblSourceTitle, $pnlSourceFrame, $lblTranslationTitle, $pnlTranslationFrame, $txtMeta, $editorDivider, $btnPrev, $btnNext, $btnUseCandidate, $btnUseExisting, $btnUseSource, $btnResetEdit, $btnPending, $btnTranslated, $btnApprove, $btnApproveNext, $lblReferenceTitle, $lblExisting, $txtExisting, $lblCandidate, $txtCandidate))

function Resize-ReviewEditorLayout {
    if (-not $center -or $center.ClientSize.Width -le 0) { return }
    $pad = if ($center.ClientSize.Width -lt 520) { 18 } else { 24 }
    $contentWidth = [Math]::Max(400, $center.ClientSize.Width - ($pad * 2))
    $contentHeight = [Math]::Max(540, $center.ClientSize.Height)
    $veryCompact = $contentHeight -lt 660
    $compact = $contentHeight -lt 760
    $sourceHeight = if ($veryCompact) { 72 } elseif ($compact) { 92 } else { 118 }
    $translationHeight = if ($veryCompact) { 112 } elseif ($compact) { 148 } else { 190 }
    $metaHeight = if ($veryCompact) { 52 } else { 58 }

    $lblCurrent.SetBounds($pad, 18, [Math]::Max(180, $contentWidth - 178), 28)
    $lblUpdateBadge.SetBounds(($pad + $contentWidth - 168), 18, 168, 26)
    $lblSourceTitle.SetBounds($pad, 56, $contentWidth, 20)
    $pnlSourceFrame.SetBounds($pad, 80, $contentWidth, $sourceHeight)
    $txtSource.SetBounds(11, 9, [Math]::Max(120, $contentWidth - 24), [Math]::Max(38, $sourceHeight - 18))

    $translationLabelY = 80 + $sourceHeight + 18
    $translationBoxY = $translationLabelY + 24
    $lblTranslationTitle.SetBounds($pad, $translationLabelY, $contentWidth, 20)
    $pnlTranslationFrame.SetBounds($pad, $translationBoxY, $contentWidth, $translationHeight)
    $txtTranslation.SetBounds(11, 9, [Math]::Max(120, $contentWidth - 24), [Math]::Max(42, $translationHeight - 20))
    $translationAccent.SetBounds(0, [Math]::Max(0, $translationHeight - 3), $contentWidth, 3)

    $metaY = $translationBoxY + $translationHeight + 14
    $txtMeta.SetBounds($pad, $metaY, $contentWidth, $metaHeight)
    $dividerY = $metaY + $metaHeight + 8
    $editorDivider.SetBounds($pad, $dividerY, $contentWidth, 1)
    $toolbarY = $dividerY + 12

    $utilityButtons = @($btnPrev, $btnNext, $btnUseCandidate, $btnUseExisting, $btnUseSource, $btnResetEdit)
    $utilityWidths = @(38, 38, 72, 62, 58, 72)
    $statusButtons = @($btnPending, $btnTranslated, $btnApprove, $btnApproveNext)
    $statusWidths = @(72, 76, 88, 108)
    $gap = 7
    $utilityTotal = ($utilityWidths | Measure-Object -Sum).Sum + ($gap * ($utilityWidths.Count - 1))
    $statusTotal = ($statusWidths | Measure-Object -Sum).Sum + ($gap * ($statusWidths.Count - 1))
    $singleRow = $contentWidth -ge ($utilityTotal + $statusTotal + 22)

    $x = $pad
    for ($i = 0; $i -lt $utilityButtons.Count; $i++) {
        $utilityButtons[$i].SetBounds($x, $toolbarY, $utilityWidths[$i], 36)
        $x += $utilityWidths[$i] + $gap
    }

    if ($singleRow) {
        $x = $pad + $contentWidth - $statusTotal
        $statusY = $toolbarY
        $toolbarBottom = $toolbarY + 36
    } else {
        $x = $pad
        $statusY = $toolbarY + 44
        $toolbarBottom = $statusY + 36
    }
    for ($i = 0; $i -lt $statusButtons.Count; $i++) {
        $statusButtons[$i].SetBounds($x, $statusY, $statusWidths[$i], 36)
        $x += $statusWidths[$i] + $gap
    }

    $referenceTitleY = $toolbarBottom + 17
    $suggestionLabelY = $referenceTitleY + 25
    $lblReferenceTitle.SetBounds($pad, $referenceTitleY, $contentWidth, 20)
    $halfWidth = [Math]::Max(190, [int](($contentWidth - 14) / 2))
    $bottomBoxY = $suggestionLabelY + 22
    $bottomHeight = [Math]::Max(76, $center.ClientSize.Height - $bottomBoxY - 18)
    $lblExisting.SetBounds($pad, $suggestionLabelY, $halfWidth, 18)
    $txtExisting.SetBounds($pad, $bottomBoxY, $halfWidth, $bottomHeight)
    $candidateX = $pad + $halfWidth + 14
    $lblCandidate.SetBounds($candidateX, $suggestionLabelY, $halfWidth, 18)
    $txtCandidate.SetBounds($candidateX, $bottomBoxY, $halfWidth, $bottomHeight)
}

$center.Add_Resize({ Resize-ReviewEditorLayout })
Resize-ReviewEditorLayout

$side = $rightSplit.Panel2
$side.BackColor = [System.Drawing.Color]::White

$tabs = New-Object System.Windows.Forms.TabControl
$tabs.Dock = [System.Windows.Forms.DockStyle]::Fill
$tabs.Font = New-Font 8.5 ([System.Drawing.FontStyle]::Bold)
$tabs.DrawMode = [System.Windows.Forms.TabDrawMode]::OwnerDrawFixed
$tabs.SizeMode = [System.Windows.Forms.TabSizeMode]::Fixed
$tabs.ItemSize = New-Object System.Drawing.Size(44, 38)
$tabs.Padding = New-Object System.Drawing.Point(2, 3)
$tabs.Multiline = $false
$side.Controls.Add($tabs)

$tabHistory = New-Object System.Windows.Forms.TabPage
$tabHistory.Text = "역사"
$tabTerms = New-Object System.Windows.Forms.TabPage
$tabTerms.Text = "용어"
$tabMemo = New-Object System.Windows.Forms.TabPage
$tabMemo.Text = "메모"
$tabIssues = New-Object System.Windows.Forms.TabPage
$tabIssues.Text = "문제"
$tabLog = New-Object System.Windows.Forms.TabPage
$tabLog.Text = "로그"
[void]$tabs.TabPages.AddRange(@($tabHistory, $tabTerms, $tabMemo, $tabIssues, $tabLog))

function Resize-SideTabs {
    if (-not $tabs -or $tabs.TabPages.Count -le 0 -or $tabs.ClientSize.Width -le 0) { return }
    $availableWidth = [Math]::Max(220, $tabs.ClientSize.Width - 100)
    $itemWidth = [Math]::Max(44, [int][Math]::Floor($availableWidth / $tabs.TabPages.Count))
    $tabs.ItemSize = New-Object System.Drawing.Size($itemWidth, 38)
}

$script:sideTabResizePending = $false
function Queue-SideTabResize {
    if (-not $side -or -not $side.IsHandleCreated) {
        Resize-SideTabs
        return
    }
    if ($script:sideTabResizePending) { return }
    $script:sideTabResizePending = $true
    try {
        [void]$side.BeginInvoke([System.Windows.Forms.MethodInvoker]{
            $script:sideTabResizePending = $false
            Resize-SideTabs
        })
    } catch {
        $script:sideTabResizePending = $false
    }
}

$side.Add_Resize({ Queue-SideTabResize })
Resize-SideTabs
$tabs.Add_DrawItem({
    $bounds = $_.Bounds
    $selected = $_.Index -eq $this.SelectedIndex
    $fore = if ($selected) { $script:tabActive } else { $script:tabText }
    $brush = New-Object System.Drawing.SolidBrush($script:tabBack)
    try {
        $_.Graphics.FillRectangle($brush, $bounds)
        if ($selected) {
            $accentBrush = New-Object System.Drawing.SolidBrush($script:tabActive)
            try {
                $_.Graphics.FillRectangle($accentBrush, $bounds.X + 8, $bounds.Bottom - 3, [Math]::Max(8, $bounds.Width - 16), 3)
            } finally {
                $accentBrush.Dispose()
            }
        }
        [System.Windows.Forms.TextRenderer]::DrawText(
            $_.Graphics,
            $this.TabPages[$_.Index].Text,
            $this.Font,
            $bounds,
            $fore,
            ([System.Windows.Forms.TextFormatFlags]::HorizontalCenter -bor [System.Windows.Forms.TextFormatFlags]::VerticalCenter -bor [System.Windows.Forms.TextFormatFlags]::SingleLine)
        )
    } finally {
        $brush.Dispose()
    }
})

$txtHistory = New-Object System.Windows.Forms.RichTextBox
$txtHistory.Dock = [System.Windows.Forms.DockStyle]::Fill
$txtHistory.ReadOnly = $true
$txtHistory.DetectUrls = $false
$txtHistory.Font = New-Font 9.5
$txtHistory.BackColor = [System.Drawing.Color]::FromArgb(248, 249, 250)
$script:historyTitleFont = New-Font 8.5 ([System.Drawing.FontStyle]::Bold)
$script:historyBodyFont = New-Font 9.5
$tabHistory.Controls.Add($txtHistory)

$txtTerms = New-TextBox -Multiline
$txtTerms.Dock = [System.Windows.Forms.DockStyle]::Fill
$txtTerms.ReadOnly = $true
$txtTerms.BackColor = [System.Drawing.Color]::FromArgb(248, 249, 250)
$tabTerms.Controls.Add($txtTerms)

$txtMemo = New-TextBox -Multiline
$txtMemo.Dock = [System.Windows.Forms.DockStyle]::Fill
$txtMemo.BackColor = [System.Drawing.Color]::White
$tabMemo.Controls.Add($txtMemo)

$txtWarnings = New-TextBox -Multiline
$txtWarnings.Dock = [System.Windows.Forms.DockStyle]::Fill
$txtWarnings.ReadOnly = $true
$txtWarnings.BackColor = [System.Drawing.Color]::FromArgb(248, 249, 250)
$tabIssues.Controls.Add($txtWarnings)

$txtLog = New-TextBox -Multiline
$txtLog.Dock = [System.Windows.Forms.DockStyle]::Fill
$txtLog.ReadOnly = $true
$txtLog.Font = New-Object System.Drawing.Font("Consolas", 8.5)
$txtLog.BackColor = [System.Drawing.Color]::FromArgb(17, 23, 29)
$txtLog.ForeColor = [System.Drawing.Color]::FromArgb(214, 224, 234)
$tabLog.Controls.Add($txtLog)

foreach ($panel in @($left, $center, $side, $main.Panel1, $main.Panel2, $rightSplit.Panel1, $rightSplit.Panel2)) {
    $panel.BackColor = [System.Drawing.Color]::FromArgb(24, 31, 38)
}
$main.BackColor = [System.Drawing.Color]::FromArgb(42, 51, 60)
$rightSplit.BackColor = [System.Drawing.Color]::FromArgb(42, 51, 60)

foreach ($list in @($lvFiles, $lvItems)) {
    $list.BackColor = [System.Drawing.Color]::FromArgb(30, 38, 46)
    $list.ForeColor = [System.Drawing.Color]::FromArgb(222, 231, 240)
    $list.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
}

foreach ($box in @($txtSearch, $txtSource, $txtTranslation, $txtMeta, $txtExisting, $txtCandidate, $txtHistory, $txtTerms, $txtMemo, $txtWarnings)) {
    $box.BackColor = [System.Drawing.Color]::FromArgb(30, 38, 46)
    $box.ForeColor = [System.Drawing.Color]::FromArgb(226, 235, 244)
}
$txtSource.BackColor = [System.Drawing.Color]::FromArgb(37, 46, 55)
$txtTranslation.BackColor = [System.Drawing.Color]::FromArgb(246, 248, 250)
$txtTranslation.ForeColor = [System.Drawing.Color]::FromArgb(18, 24, 30)

foreach ($label in @($lblProjectStats, $lblProgress, $lblCurrent, $lblExisting, $lblCandidate)) {
    $label.ForeColor = [System.Drawing.Color]::FromArgb(214, 224, 234)
}

foreach ($page in @($tabHistory, $tabTerms, $tabMemo, $tabIssues, $tabLog)) {
    $page.BackColor = [System.Drawing.Color]::FromArgb(24, 31, 38)
}

$dashboardPanel = New-Object System.Windows.Forms.Panel
$dashboardPanel.Dock = [System.Windows.Forms.DockStyle]::Fill
$dashboardPanel.BackColor = [System.Drawing.Color]::FromArgb(18, 24, 30)
$dashboardPanel.Visible = $false
$dashboardPanel.AutoScroll = $false
$form.Controls.Add($dashboardPanel)

$dashHeader = New-Object System.Windows.Forms.Panel
$dashHeader.SetBounds(0, 0, $form.ClientSize.Width, 76)
$dashHeader.Anchor = [System.Windows.Forms.AnchorStyles]::Top -bor [System.Windows.Forms.AnchorStyles]::Left -bor [System.Windows.Forms.AnchorStyles]::Right
$dashHeader.BackColor = [System.Drawing.Color]::FromArgb(31, 39, 48)
$dashboardPanel.Controls.Add($dashHeader)

$dashAccent = New-Object System.Windows.Forms.Panel
$dashAccent.SetBounds(0, 67, $form.ClientSize.Width, 3)
$dashAccent.Anchor = [System.Windows.Forms.AnchorStyles]::Left -bor [System.Windows.Forms.AnchorStyles]::Right -bor [System.Windows.Forms.AnchorStyles]::Bottom

$lblDashTitle = New-Label "RimWorld AI Translator" 22 14 340 28 ([System.Drawing.Color]::White) 13 ([System.Drawing.FontStyle]::Bold)
$lblDashSub = New-Label "모드별로 원문 로드, 번역, 검수, 적용 상태를 관리합니다." 22 44 560 20 ([System.Drawing.Color]::FromArgb(150, 164, 178)) 8.5
$btnDashProjects = New-Button "프로젝트" ([System.Drawing.Color]::FromArgb(28, 126, 220))
$btnDashProjects.ForeColor = [System.Drawing.Color]::White
$btnDashProjects.SetBounds(620, 22, 96, 34)
$btnDashActivity = New-Button "활동" ([System.Drawing.Color]::FromArgb(37, 46, 55))
$btnDashActivity.ForeColor = [System.Drawing.Color]::FromArgb(215, 226, 237)
$btnDashActivity.SetBounds(724, 22, 74, 34)
$btnDashSettings = New-Button "설정" ([System.Drawing.Color]::FromArgb(37, 46, 55))
$btnDashSettings.ForeColor = [System.Drawing.Color]::FromArgb(215, 226, 237)
$btnDashSettings.SetBounds(806, 22, 74, 34)
$dashHeader.Controls.AddRange(@($lblDashTitle, $lblDashSub, $btnDashProjects, $btnDashActivity, $btnDashSettings, $dashAccent))

$dashContent = New-Object System.Windows.Forms.Panel
$dashContent.SetBounds(0, 76, $form.ClientSize.Width, [Math]::Max(1, $form.ClientSize.Height - 76))
$dashContent.Anchor = [System.Windows.Forms.AnchorStyles]::Top -bor [System.Windows.Forms.AnchorStyles]::Bottom -bor [System.Windows.Forms.AnchorStyles]::Left -bor [System.Windows.Forms.AnchorStyles]::Right
$dashContent.BackColor = [System.Drawing.Color]::FromArgb(18, 24, 30)
$dashboardPanel.Controls.Add($dashContent)
$dashHeader.BringToFront()

$dashProjectsPage = New-Object System.Windows.Forms.Panel
$dashProjectsPage.Dock = [System.Windows.Forms.DockStyle]::Fill
$dashProjectsPage.BackColor = [System.Drawing.Color]::FromArgb(18, 24, 30)
$dashContent.Controls.Add($dashProjectsPage)

$lblDashProjects = New-Label "프로젝트" 24 20 180 30 ([System.Drawing.Color]::White) 14 ([System.Drawing.FontStyle]::Bold)
$lblDashboardSearch = New-Label "프로젝트 검색" 24 36 170 20 ([System.Drawing.Color]::FromArgb(160, 174, 188)) 8.5 ([System.Drawing.FontStyle]::Bold)
$txtDashboardSearch = New-TextBox
$txtDashboardSearch.SetBounds(24, 60, 330, 30)
$txtDashboardSearch.BackColor = [System.Drawing.Color]::FromArgb(30, 38, 46)
$txtDashboardSearch.ForeColor = [System.Drawing.Color]::FromArgb(226, 235, 244)
$lblDashboardMod = New-Label "프로젝트 대상 모드" 378 36 170 20 ([System.Drawing.Color]::FromArgb(160, 174, 188)) 8.5 ([System.Drawing.FontStyle]::Bold)
$cmbDashboardMods = New-Object System.Windows.Forms.ComboBox
$cmbDashboardMods.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbDashboardMods.DisplayMember = "Display"
$cmbDashboardMods.Font = New-Font 9
$cmbDashboardMods.SetBounds(378, 60, 440, 30)
$btnDashboardAddMod = New-Button "프로젝트 만들기" ([System.Drawing.Color]::FromArgb(28, 126, 220))
$btnDashboardAddMod.ForeColor = [System.Drawing.Color]::White
$btnDashboardAddMod.SetBounds(826, 58, 112, 32)
$btnDashboardChooseMod = New-Button "폴더 선택" ([System.Drawing.Color]::FromArgb(72, 86, 100))
$btnDashboardChooseMod.ForeColor = [System.Drawing.Color]::White
$btnDashboardChooseMod.SetBounds(946, 58, 96, 32)
$btnDashboardRefreshMods = New-Button "새로고침" ([System.Drawing.Color]::FromArgb(72, 86, 100))
$btnDashboardRefreshMods.ForeColor = [System.Drawing.Color]::White
$btnDashboardRefreshMods.SetBounds(1050, 58, 92, 32)

$flowDashboardProjects = New-Object System.Windows.Forms.FlowLayoutPanel
$flowDashboardProjects.SetBounds(16, 112, 1418, 560)
$flowDashboardProjects.Anchor = [System.Windows.Forms.AnchorStyles]::Top -bor [System.Windows.Forms.AnchorStyles]::Bottom -bor [System.Windows.Forms.AnchorStyles]::Left -bor [System.Windows.Forms.AnchorStyles]::Right
$flowDashboardProjects.AutoScroll = $true
$flowDashboardProjects.BackColor = [System.Drawing.Color]::FromArgb(18, 24, 30)
$dashProjectsPage.Controls.AddRange(@($lblDashProjects, $lblDashboardSearch, $txtDashboardSearch, $lblDashboardMod, $cmbDashboardMods, $btnDashboardAddMod, $btnDashboardChooseMod, $btnDashboardRefreshMods, $flowDashboardProjects))

$dashActivityPage = New-Object System.Windows.Forms.Panel
$dashActivityPage.Dock = [System.Windows.Forms.DockStyle]::Fill
$dashActivityPage.BackColor = [System.Drawing.Color]::FromArgb(18, 24, 30)
$dashActivityPage.Visible = $false
$dashContent.Controls.Add($dashActivityPage)

$lblDashActivity = New-Label "활동" 24 20 180 30 ([System.Drawing.Color]::White) 14 ([System.Drawing.FontStyle]::Bold)
$lvDashboardActivity = New-Object System.Windows.Forms.ListView
$lvDashboardActivity.SetBounds(24, 66, 1378, 606)
$lvDashboardActivity.Anchor = [System.Windows.Forms.AnchorStyles]::Top -bor [System.Windows.Forms.AnchorStyles]::Bottom -bor [System.Windows.Forms.AnchorStyles]::Left -bor [System.Windows.Forms.AnchorStyles]::Right
$lvDashboardActivity.View = [System.Windows.Forms.View]::Details
$lvDashboardActivity.FullRowSelect = $true
$lvDashboardActivity.HideSelection = $false
$lvDashboardActivity.Font = New-Font 9
$lvDashboardActivity.BackColor = [System.Drawing.Color]::FromArgb(30, 38, 46)
$lvDashboardActivity.ForeColor = [System.Drawing.Color]::FromArgb(226, 235, 244)
[void]$lvDashboardActivity.Columns.Add("시간", 150)
[void]$lvDashboardActivity.Columns.Add("모드", 320)
[void]$lvDashboardActivity.Columns.Add("종류", 90)
[void]$lvDashboardActivity.Columns.Add("내용", 720)
$dashActivityPage.Controls.AddRange(@($lblDashActivity, $lvDashboardActivity))

$dashSettingsPage = New-Object System.Windows.Forms.Panel
$dashSettingsPage.Dock = [System.Windows.Forms.DockStyle]::Fill
$dashSettingsPage.BackColor = [System.Drawing.Color]::FromArgb(18, 24, 30)
$dashSettingsPage.Visible = $false
$dashContent.Controls.Add($dashSettingsPage)

$lblDashSettings = New-Label "설정" 24 20 180 30 ([System.Drawing.Color]::White) 14 ([System.Drawing.FontStyle]::Bold)
$lblDashApi = New-Label "API 키 (Enter로 여러 개, 비우면 Google)" 28 70 340 22 ([System.Drawing.Color]::FromArgb(218, 228, 238)) 9 ([System.Drawing.FontStyle]::Bold)
$txtDashboardApiKeys = New-TextBox -Multiline
$txtDashboardApiKeys.SetBounds(28, 100, 560, 170)
$txtDashboardApiKeys.BackColor = [System.Drawing.Color]::FromArgb(30, 38, 46)
$txtDashboardApiKeys.ForeColor = [System.Drawing.Color]::FromArgb(226, 235, 244)
$chkDashboardIncludePatches = New-Object System.Windows.Forms.CheckBox
$chkDashboardIncludePatches.Text = "Patches 포함"
$chkDashboardIncludePatches.SetBounds(28, 288, 150, 26)
$chkDashboardIncludePatches.ForeColor = [System.Drawing.Color]::FromArgb(218, 226, 234)
$chkDashboardIncludePatches.BackColor = [System.Drawing.Color]::Transparent
$chkDashboardDryRun = New-Object System.Windows.Forms.CheckBox
$chkDashboardDryRun.Text = "Dry run"
$chkDashboardDryRun.SetBounds(188, 288, 120, 26)
$chkDashboardDryRun.ForeColor = [System.Drawing.Color]::FromArgb(218, 226, 234)
$chkDashboardDryRun.BackColor = [System.Drawing.Color]::Transparent
$lblDashSettingsNote = New-Label "배치 크기는 무료 API 안정성을 우선해서 40으로 고정되어 있습니다." 28 330 680 24 ([System.Drawing.Color]::FromArgb(150, 164, 178)) 8.5

$settingsDivider = New-Object System.Windows.Forms.Panel
$settingsDivider.SetBounds(616, 70, 1, 284)
$lblDashAppearance = New-Label "화면 및 편집" 646 70 240 24 ([System.Drawing.Color]::FromArgb(218, 228, 238)) 10 ([System.Drawing.FontStyle]::Bold)
$lblDashTheme = New-Label "테마" 646 108 120 20 ([System.Drawing.Color]::FromArgb(180, 190, 200)) 8.5 ([System.Drawing.FontStyle]::Bold)
$cmbDashboardTheme = New-Object System.Windows.Forms.ComboBox
$cmbDashboardTheme.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbDashboardTheme.Font = New-Font 9.5
$cmbDashboardTheme.SetBounds(646, 132, 220, 30)
[void]$cmbDashboardTheme.Items.AddRange(@("시스템 설정 따름", "밝게", "어둡게"))

$lblDashTextSize = New-Label "본문 글자 크기" 646 180 160 20 ([System.Drawing.Color]::FromArgb(180, 190, 200)) 8.5 ([System.Drawing.FontStyle]::Bold)
$cmbDashboardTextSize = New-Object System.Windows.Forms.ComboBox
$cmbDashboardTextSize.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbDashboardTextSize.Font = New-Font 9.5
$cmbDashboardTextSize.SetBounds(646, 204, 220, 30)
[void]$cmbDashboardTextSize.Items.AddRange(@("9", "10", "11", "12"))

$chkDashboardHighContrast = New-Object System.Windows.Forms.CheckBox
$chkDashboardHighContrast.Text = "고대비"
$chkDashboardHighContrast.SetBounds(646, 252, 150, 26)
$chkDashboardHighContrast.BackColor = [System.Drawing.Color]::Transparent
$chkDashboardAutoSave = New-Object System.Windows.Forms.CheckBox
$chkDashboardAutoSave.Text = "편집 내용 자동 저장"
$chkDashboardAutoSave.SetBounds(646, 286, 210, 26)
$chkDashboardAutoSave.BackColor = [System.Drawing.Color]::Transparent

$dashSettingsPage.Controls.AddRange(@($lblDashSettings, $lblDashApi, $txtDashboardApiKeys, $chkDashboardIncludePatches, $chkDashboardDryRun, $lblDashSettingsNote, $settingsDivider, $lblDashAppearance, $lblDashTheme, $cmbDashboardTheme, $lblDashTextSize, $cmbDashboardTextSize, $chkDashboardHighContrast, $chkDashboardAutoSave))

$dashboardPanel.Add_Resize({
    $dashHeader.SetBounds(0, 0, $dashboardPanel.ClientSize.Width, 70)
    $dashContent.SetBounds(0, 70, $dashboardPanel.ClientSize.Width, [Math]::Max(1, $dashboardPanel.ClientSize.Height - 70))
})

function Update-SplitMinimumSizes {
    if (-not $main -or -not $rightSplit) { return }

    try {
        $main.Panel1MinSize = 0
        $main.Panel2MinSize = 0
        $mainWidth = $main.ClientSize.Width
        $mainRequired = 340 + 740 + $main.SplitterWidth
        if ($mainWidth -ge $mainRequired) {
            $maxMainDistance = $mainWidth - 740 - $main.SplitterWidth
            $main.SplitterDistance = [Math]::Max(340, [Math]::Min($main.SplitterDistance, $maxMainDistance))
            $main.Panel1MinSize = 340
            $main.Panel2MinSize = 740
        }
    } catch {}

    try {
        $rightSplit.Panel1MinSize = 0
        $rightSplit.Panel2MinSize = 0
        $rightWidth = $rightSplit.ClientSize.Width
        $rightRequired = 420 + 320 + $rightSplit.SplitterWidth
        if ($rightWidth -ge $rightRequired) {
            $maxRightDistance = $rightWidth - 320 - $rightSplit.SplitterWidth
            $rightSplit.SplitterDistance = [Math]::Max(420, [Math]::Min($rightSplit.SplitterDistance, $maxRightDistance))
            $rightSplit.Panel1MinSize = 420
            $rightSplit.Panel2MinSize = 320
        }
    } catch {}
}

function Apply-AppTheme {
    $isDark = Get-IsWindowsDarkMode
    if ($isDark) {
        $bg = [System.Drawing.Color]::FromArgb(25, 28, 27)
        $surface = [System.Drawing.Color]::FromArgb(34, 38, 36)
        $subtle = [System.Drawing.Color]::FromArgb(43, 48, 45)
        $line = [System.Drawing.Color]::FromArgb(68, 74, 69)
        $text = [System.Drawing.Color]::FromArgb(239, 236, 227)
        $muted = [System.Drawing.Color]::FromArgb(181, 180, 169)
        $faint = [System.Drawing.Color]::FromArgb(137, 143, 136)
        $header = [System.Drawing.Color]::FromArgb(30, 33, 31)
        $headerButton = [System.Drawing.Color]::FromArgb(48, 53, 49)
        $headerLine = [System.Drawing.Color]::FromArgb(77, 83, 77)
        $searchCard = [System.Drawing.Color]::FromArgb(43, 49, 44)
    } else {
        $bg = [System.Drawing.Color]::FromArgb(241, 242, 239)
        $surface = [System.Drawing.Color]::FromArgb(253, 253, 251)
        $subtle = [System.Drawing.Color]::FromArgb(235, 238, 235)
        $line = [System.Drawing.Color]::FromArgb(204, 209, 203)
        $text = [System.Drawing.Color]::FromArgb(42, 46, 43)
        $muted = [System.Drawing.Color]::FromArgb(91, 99, 92)
        $faint = [System.Drawing.Color]::FromArgb(132, 140, 133)
        $header = [System.Drawing.Color]::FromArgb(39, 43, 40)
        $headerButton = [System.Drawing.Color]::FromArgb(53, 59, 54)
        $headerLine = [System.Drawing.Color]::FromArgb(80, 87, 81)
        $searchCard = [System.Drawing.Color]::FromArgb(229, 234, 228)
    }
    if ($script:highContrast) {
        if ($isDark) {
            $bg = [System.Drawing.Color]::FromArgb(12, 12, 12)
            $surface = [System.Drawing.Color]::FromArgb(22, 22, 22)
            $subtle = [System.Drawing.Color]::FromArgb(34, 34, 34)
            $line = [System.Drawing.Color]::FromArgb(174, 165, 147)
            $text = [System.Drawing.Color]::White
            $muted = [System.Drawing.Color]::FromArgb(225, 218, 204)
            $faint = [System.Drawing.Color]::FromArgb(194, 188, 176)
            $searchCard = [System.Drawing.Color]::FromArgb(32, 32, 30)
        } else {
            $bg = [System.Drawing.Color]::White
            $surface = [System.Drawing.Color]::White
            $subtle = [System.Drawing.Color]::FromArgb(242, 242, 238)
            $line = [System.Drawing.Color]::FromArgb(74, 70, 63)
            $text = [System.Drawing.Color]::FromArgb(15, 15, 15)
            $muted = [System.Drawing.Color]::FromArgb(55, 52, 47)
            $faint = [System.Drawing.Color]::FromArgb(78, 74, 67)
            $searchCard = [System.Drawing.Color]::FromArgb(238, 235, 226)
        }
    }
    $headerText = [System.Drawing.Color]::FromArgb(245, 242, 233)
    $headerMuted = [System.Drawing.Color]::FromArgb(180, 187, 178)
    $primary = if ($isDark) { [System.Drawing.Color]::FromArgb(196, 154, 91) } else { [System.Drawing.Color]::FromArgb(174, 126, 66) }
    $primarySoft = if ($isDark) { [System.Drawing.Color]::FromArgb(68, 59, 44) } else { [System.Drawing.Color]::FromArgb(240, 229, 210) }
    $steel = if ($isDark) { [System.Drawing.Color]::FromArgb(82, 124, 139) } else { [System.Drawing.Color]::FromArgb(74, 111, 124) }
    $green = if ($isDark) { [System.Drawing.Color]::FromArgb(71, 139, 92) } else { [System.Drawing.Color]::FromArgb(62, 124, 82) }
    $danger = if ($isDark) { [System.Drawing.Color]::FromArgb(174, 76, 73) } else { [System.Drawing.Color]::FromArgb(153, 67, 64) }
    if ($script:highContrast) {
        $primary = if ($isDark) { [System.Drawing.Color]::FromArgb(224, 177, 92) } else { [System.Drawing.Color]::FromArgb(119, 77, 22) }
        $primarySoft = if ($isDark) { [System.Drawing.Color]::FromArgb(75, 61, 38) } else { [System.Drawing.Color]::FromArgb(244, 232, 210) }
        $steel = if ($isDark) { [System.Drawing.Color]::FromArgb(94, 163, 190) } else { [System.Drawing.Color]::FromArgb(38, 88, 108) }
        $green = if ($isDark) { [System.Drawing.Color]::FromArgb(68, 158, 94) } else { [System.Drawing.Color]::FromArgb(31, 103, 56) }
        $danger = if ($isDark) { [System.Drawing.Color]::FromArgb(193, 74, 70) } else { [System.Drawing.Color]::FromArgb(137, 43, 40) }
    }

    $script:itemCardBack = $surface
    $script:itemCardSelected = if ($isDark) { [System.Drawing.Color]::FromArgb(53, 58, 53) } else { [System.Drawing.Color]::FromArgb(225, 232, 225) }
    $script:itemText = $text
    $script:itemMuted = $muted
    $script:itemSubtle = $faint
    $script:tabBack = $surface
    $script:tabActive = $primary
    $script:tabText = $muted
    $script:tabActiveText = [System.Drawing.Color]::White
    $script:accentColor = $primary
    $script:surfaceColor = $surface
    $script:textColor = $text
    $script:mutedColor = $muted

    $form.BackColor = $bg
    $formWidth = [Math]::Max(1, $form.ClientSize.Width)
    $formHeight = [Math]::Max(1, $form.ClientSize.Height)
    try {
        $main.Panel1MinSize = 0
        $main.Panel2MinSize = 0
        $rightSplit.Panel1MinSize = 0
        $rightSplit.Panel2MinSize = 0
    } catch {}
    $top.Dock = [System.Windows.Forms.DockStyle]::None
    $top.SetBounds(0, 0, $formWidth, 78)
    $top.Anchor = [System.Windows.Forms.AnchorStyles]::Top -bor [System.Windows.Forms.AnchorStyles]::Left -bor [System.Windows.Forms.AnchorStyles]::Right
    $top.BackColor = $header
    $topAccent.SetBounds(0, 75, $formWidth, 3)
    $topAccent.BackColor = $primary
    $main.Dock = [System.Windows.Forms.DockStyle]::None
    $main.SetBounds(0, 78, $formWidth, [Math]::Max(1, $formHeight - 78))
    $main.Anchor = [System.Windows.Forms.AnchorStyles]::Top -bor [System.Windows.Forms.AnchorStyles]::Bottom -bor [System.Windows.Forms.AnchorStyles]::Left -bor [System.Windows.Forms.AnchorStyles]::Right
    $dashHeader.BackColor = $header
    $dashHeader.Height = 70
    $dashAccent.SetBounds(0, 67, $formWidth, 3)
    $dashAccent.BackColor = $primary
    $dashContent.Top = 70
    foreach ($control in @($main, $main.Panel1, $main.Panel2, $left, $center, $side, $rightSplit, $rightSplit.Panel1, $rightSplit.Panel2, $dashboardPanel, $dashContent, $dashProjectsPage, $dashActivityPage, $dashSettingsPage, $flowDashboardProjects, $flowItems, $statusFilterBar)) {
        if ($control) { $control.BackColor = $bg }
    }
    foreach ($panel in @($center, $side)) {
        if ($panel) { $panel.BackColor = $surface }
    }

    $topWidth = $formWidth
    $lblProject.SetBounds(24, 12, 286, 25)
    $lblProject.Font = New-Font 11.5 ([System.Drawing.FontStyle]::Bold)
    $lblProject.ForeColor = $headerText
    $lblProject.AutoEllipsis = $true
    $lblPath.SetBounds(24, 42, 286, 18)
    $lblPath.Font = New-Font 8
    $lblPath.ForeColor = $headerMuted
    $lblPath.AutoEllipsis = $true
    $lblPath.Visible = $true
    $lblProjectPick.Visible = $false
    $cmbProject.Visible = $false
    $lblModPick.Visible = $false
    $cmbModCatalog.Visible = $false
    $btnRefreshMods.Visible = $false
    $btnChooseMod.Visible = $false
    $lblApi.Visible = $false
    $txtApiKeys.Visible = $false
    $chkIncludePatches.Visible = $false
    $chkDryRun.Visible = $false
    $progressRun.Visible = $false
    $actionWidths = @(96, 88, 54, 92, 92)
    $actionGap = 8
    $actionTotal = ($actionWidths | Measure-Object -Sum).Sum + ($actionGap * ($actionWidths.Count - 1))
    $actionX = [Math]::Max(646, $topWidth - 24 - $actionTotal)
    $utilityEnd = 570
    $showSaveStatus = ($actionX - $utilityEnd) -ge 116
    $showRunStatus = $showSaveStatus
    $lblSave.Visible = $showSaveStatus
    $lblRunStatus.Visible = $showRunStatus
    $statusWidth = [Math]::Max(96, $actionX - $utilityEnd - 16)
    $lblRunStatus.SetBounds($utilityEnd, 17, $statusWidth, 18)
    $lblRunStatus.ForeColor = $headerMuted
    $lblSave.SetBounds($utilityEnd, 40, $statusWidth, 18)
    $lblSave.ForeColor = $headerMuted

    $btnHome.Text = "프로젝트"
    $btnHome.SetBounds(330, 21, 82, 36)
    $btnOpenFolder.Text = "폴더"
    $btnOpenFolder.SetBounds(420, 21, 66, 36)
    $btnSave.SetBounds(494, 21, 68, 36)
    $btnLoad.Text = "원문 갱신"
    $btnLoad.SetBounds($actionX, 21, $actionWidths[0], 36)
    $btnTranslate.Text = "AI 번역"
    $btnTranslate.SetBounds(($actionX + $actionWidths[0] + $actionGap), 21, $actionWidths[1], 36)
    $btnStop.SetBounds(($actionX + $actionWidths[0] + $actionGap + $actionWidths[1] + $actionGap), 21, $actionWidths[2], 36)
    $btnApply.Text = "검토 적용"
    $btnApply.SetBounds(($actionX + $actionWidths[0] + $actionGap + $actionWidths[1] + $actionGap + $actionWidths[2] + $actionGap), 21, $actionWidths[3], 36)
    $btnApplyTranslated.Text = "전체 적용"
    $btnApplyTranslated.SetBounds(($actionX + $actionWidths[0] + $actionGap + $actionWidths[1] + $actionGap + $actionWidths[2] + $actionGap + $actionWidths[3] + $actionGap), 21, $actionWidths[4], 36)

    foreach ($button in @($btnHome, $btnSave, $btnOpenFolder, $btnLoad, $btnDashboardChooseMod, $btnDashboardRefreshMods, $btnDashActivity, $btnDashSettings)) {
        if ($button) {
            $button.BackColor = $headerButton
            $button.ForeColor = $headerText
            $button.FlatAppearance.BorderColor = $headerLine
            $button.FlatAppearance.BorderSize = 1
        }
    }
    foreach ($button in @($btnTranslate, $btnDashProjects, $btnDashboardAddMod)) {
        if ($button) {
            $button.BackColor = $primary
            $button.ForeColor = [System.Drawing.Color]::White
            $button.FlatAppearance.BorderColor = $primary
            $button.FlatAppearance.BorderSize = 0
        }
    }
    Update-StopButtonAppearance
    $btnApply.BackColor = $green
    $btnApply.ForeColor = [System.Drawing.Color]::White
    $btnApply.FlatAppearance.BorderColor = $green
    $btnApply.FlatAppearance.BorderSize = 0
    $btnApplyTranslated.BackColor = $steel
    $btnApplyTranslated.ForeColor = [System.Drawing.Color]::White
    $btnApplyTranslated.FlatAppearance.BorderColor = $steel
    $btnApplyTranslated.FlatAppearance.BorderSize = 0

    $main.BackColor = $line
    $rightSplit.BackColor = $line
    $mainWidth = [Math]::Max(1, $main.ClientSize.Width)
    $leftWidth = [Math]::Min(410, [Math]::Max(360, [int]($mainWidth * 0.24)))
    $leftWidth = [Math]::Min($leftWidth, [Math]::Max(340, $mainWidth - 740 - $main.SplitterWidth))
    try { $main.SplitterDistance = $leftWidth } catch {}

    $rightWidth = [Math]::Max(1, $rightSplit.ClientSize.Width)
    $sideWidth = [Math]::Min(370, [Math]::Max(330, [int]($rightWidth * 0.25)))
    $centerWidth = [Math]::Max(420, $rightWidth - $sideWidth - $rightSplit.SplitterWidth)
    $centerWidth = [Math]::Min($centerWidth, [Math]::Max(420, $rightWidth - 330 - $rightSplit.SplitterWidth))
    try { $rightSplit.SplitterDistance = $centerWidth } catch {}
    Queue-SideTabResize

    $leftInner = [Math]::Max(268, $leftWidth - 32)
    $lblSearchCrumb.SetBounds(16, 16, $leftInner, 64)
    $lblSearchCrumb.Padding = New-Object System.Windows.Forms.Padding(14, 9, 12, 7)
    $lblSearchCrumb.BackColor = $searchCard
    $lblSearchCrumb.ForeColor = $primary
    $lblSearchCrumb.BorderStyle = [System.Windows.Forms.BorderStyle]::None
    $cmbSearchField.SetBounds(16, 92, 88, 32)
    $statusWidth = 122
    $statusX = $leftWidth - $statusWidth - 16
    $txtSearch.SetBounds(104, 92, [Math]::Max(72, $statusX - 112), 32)
    $cmbStatus.SetBounds($statusX, 92, $statusWidth, 32)
    $statusFilterBar.SetBounds(16, 136, $leftInner, 30)
    $lblProjectStats.SetBounds(16, 176, $leftInner, 42)
    $progressReview.Visible = $false
    $lblProgress.Visible = $false
    $lvFiles.Visible = $false
    $flowItems.SetBounds(16, 228, $leftInner, [Math]::Max(260, $main.ClientSize.Height - 244))
    $flowItems.Padding = New-Object System.Windows.Forms.Padding(0)
    $flowItems.BorderStyle = [System.Windows.Forms.BorderStyle]::None

    foreach ($box in @($txtSearch, $txtSource, $txtTranslation, $txtMeta, $txtExisting, $txtCandidate, $txtHistory, $txtTerms, $txtMemo, $txtWarnings, $txtDashboardSearch, $txtDashboardApiKeys, $txtApiKeys)) {
        if ($box) {
            $box.BackColor = $surface
            $box.ForeColor = $text
        }
    }
    $txtSource.BackColor = $subtle
    $pnlSourceFrame.BackColor = $subtle
    $pnlTranslationFrame.BackColor = $surface
    $pnlSourceFrame.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
    $pnlTranslationFrame.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
    $translationAccent.BackColor = $primary
    $editorDivider.BackColor = $line
    $txtMeta.BackColor = $surface
    $txtMeta.ForeColor = $muted
    $txtMeta.BorderStyle = [System.Windows.Forms.BorderStyle]::None
    $txtExisting.BackColor = $subtle
    $txtCandidate.BackColor = $subtle
    $txtSource.BorderStyle = [System.Windows.Forms.BorderStyle]::None
    $txtTranslation.BorderStyle = [System.Windows.Forms.BorderStyle]::None
    $txtExisting.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
    $txtCandidate.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
    $txtSearch.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
    $txtLog.BackColor = if ($isDark) { [System.Drawing.Color]::FromArgb(16, 22, 28) } else { [System.Drawing.Color]::FromArgb(248, 247, 242) }
    $txtLog.ForeColor = $text

    foreach ($combo in @($cmbSearchField, $cmbStatus, $cmbModCatalog, $cmbProject, $cmbDashboardMods, $cmbDashboardTheme, $cmbDashboardTextSize)) {
        if ($combo) {
            $combo.BackColor = $surface
            $combo.ForeColor = $text
            $combo.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
        }
    }
    foreach ($list in @($lvFiles, $lvDashboardActivity)) {
        if ($list) {
            $list.BackColor = $surface
            $list.ForeColor = $text
            $list.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
        }
    }
    foreach ($page in @($tabHistory, $tabTerms, $tabMemo, $tabIssues, $tabLog)) {
        if ($page) {
            $page.BackColor = $surface
            $page.Padding = New-Object System.Windows.Forms.Padding(12)
        }
    }
    foreach ($sideBox in @($txtHistory, $txtTerms, $txtMemo, $txtWarnings)) {
        if ($sideBox) {
            $sideBox.BorderStyle = [System.Windows.Forms.BorderStyle]::None
            $sideBox.BackColor = $surface
        }
    }
    foreach ($label in @($lblSearchCrumb, $lblProjectStats, $lblProgress, $lblExisting, $lblCandidate, $lblReferenceTitle, $lblDashProjects, $lblDashActivity, $lblDashSettings, $lblDashApi, $lblDashboardSearch, $lblDashboardMod, $lblDashSettingsNote, $lblDashAppearance, $lblDashTheme, $lblDashTextSize)) {
        if ($label -and $label -ne $lblSearchCrumb) { $label.ForeColor = $text }
    }
    $lblSourceTitle.ForeColor = $muted
    $lblTranslationTitle.ForeColor = $muted
    $lblReferenceTitle.ForeColor = $muted
    $lblUpdateBadge.ForeColor = Get-UpdateColor
    if ($script:currentRowIndex -ge 0) {
        $currentDecision = Get-Decision $script:rows[$script:currentRowIndex]
        $lblCurrent.ForeColor = Get-StatusColor ([string]$currentDecision.status)
    } else {
        $lblCurrent.ForeColor = $text
    }
    foreach ($label in @($lblDashSub)) {
        if ($label) { $label.ForeColor = $headerMuted }
    }
    foreach ($check in @($chkDashboardIncludePatches, $chkDashboardDryRun, $chkDashboardHighContrast, $chkDashboardAutoSave)) {
        if ($check) {
            $check.ForeColor = $text
            $check.BackColor = $bg
        }
    }
    $settingsDivider.BackColor = $line

    $lblSourceTitle.Text = "원문"
    $lblTranslationTitle.Text = "번역문"
    $lblSourceTitle.Visible = $true
    $lblTranslationTitle.Visible = $true
    foreach ($button in @($btnPrev, $btnNext, $btnUseCandidate, $btnUseExisting, $btnUseSource, $btnResetEdit, $btnPending, $btnTranslated)) {
        if ($button) {
            $button.BackColor = $surface
            $button.ForeColor = $text
            $button.FlatAppearance.BorderColor = $line
            $button.FlatAppearance.BorderSize = 1
        }
    }
    $btnUseCandidate.BackColor = $primarySoft
    $btnUseCandidate.FlatAppearance.BorderColor = $primary
    $btnTranslated.BackColor = $steel
    $btnTranslated.ForeColor = [System.Drawing.Color]::White
    $btnTranslated.FlatAppearance.BorderColor = $steel
    $btnTranslated.FlatAppearance.BorderSize = 0
    $btnApprove.BackColor = $green
    $btnApprove.ForeColor = [System.Drawing.Color]::White
    $btnApprove.FlatAppearance.BorderColor = $green
    $btnApprove.FlatAppearance.BorderSize = 0
    $btnApproveNext.BackColor = $green
    $btnApproveNext.ForeColor = [System.Drawing.Color]::White
    $btnApproveNext.FlatAppearance.BorderColor = $green
    $btnApproveNext.FlatAppearance.BorderSize = 0
    Refresh-StatusFilterButtons
    Resize-ReviewEditorLayout

    $tabs.Appearance = [System.Windows.Forms.TabAppearance]::Normal
    $tabs.SizeMode = [System.Windows.Forms.TabSizeMode]::Fixed
    $tabWidth = [Math]::Max(54, [Math]::Min(68, [int](($side.ClientSize.Width - 8) / 5)))
    $tabs.ItemSize = New-Object System.Drawing.Size($tabWidth, 38)
    $tabs.BackColor = $surface
    $tabs.Invalidate()

    $dashWidth = [Math]::Max(860, $dashboardPanel.ClientSize.Width)
    $dashHeight = [Math]::Max(560, $dashboardPanel.ClientSize.Height)
    $lblDashTitle.SetBounds(28, 18, 300, 26)
    $lblDashTitle.ForeColor = $headerText
    $lblDashSub.Visible = $false
    $btnDashProjects.SetBounds(350, 16, 92, 36)
    $btnDashActivity.SetBounds(450, 16, 82, 36)
    $btnDashSettings.SetBounds(540, 16, 82, 36)
    $dashHeader.SetBounds(0, 0, $dashWidth, 70)
    $dashContent.SetBounds(0, 70, $dashWidth, [Math]::Max(1, $dashHeight - 70))
    $lblDashProjects.SetBounds(32, 24, 220, 34)
    $searchWidth = [Math]::Min(360, [Math]::Max(250, [int]($dashWidth * 0.25)))
    $buttonTotal = 126 + 90 + 90 + 16
    $modX = 32 + $searchWidth + 28
    $buttonX = $dashWidth - 32 - $buttonTotal
    $comboWidth = [Math]::Max(180, $buttonX - $modX - 14)
    $lblDashboardSearch.SetBounds(32, 72, 170, 20)
    $txtDashboardSearch.SetBounds(32, 96, $searchWidth, 34)
    $lblDashboardMod.SetBounds($modX, 72, 170, 20)
    $cmbDashboardMods.SetBounds($modX, 96, $comboWidth, 34)
    $btnDashboardAddMod.SetBounds($buttonX, 96, 126, 34)
    $btnDashboardChooseMod.SetBounds(($buttonX + 134), 96, 90, 34)
    $btnDashboardRefreshMods.SetBounds(($buttonX + 232), 96, 90, 34)
    foreach ($button in @($btnDashboardChooseMod, $btnDashboardRefreshMods)) {
        $button.BackColor = $surface
        $button.ForeColor = $text
        $button.FlatAppearance.BorderColor = $line
    }
    $flowDashboardProjects.SetBounds(22, 152, [Math]::Max(320, $dashWidth - 44), [Math]::Max(260, $dashHeight - 176))
    $lvDashboardActivity.SetBounds(24, 66, [Math]::Max(320, $dashWidth - 48), [Math]::Max(260, $dashHeight - 154))
    Update-SplitMinimumSizes
}

$form.AccessibleName = "RimWorld AI Translator"
$form.AccessibleDescription = "RimWorld 모드의 원문, 번역문, 검토 상태를 관리하는 로컬 번역 도구"
$lblCurrent.AccessibleRole = [System.Windows.Forms.AccessibleRole]::StaticText
$lblUpdateBadge.AccessibleRole = [System.Windows.Forms.AccessibleRole]::StaticText
$lblUpdateBadge.AccessibleName = "업데이트로 변경된 문자열"
$lblUpdateBadge.AccessibleDescription = "모드 업데이트로 이 키의 원문이 바뀌어 다시 번역하거나 검토해야 합니다."
$flowItems.AccessibleRole = [System.Windows.Forms.AccessibleRole]::List
$flowItems.AccessibleName = "검색된 문자열 목록"
$tabs.AccessibleRole = [System.Windows.Forms.AccessibleRole]::PageTabList

Set-AccessibleControl $btnHome "프로젝트 목록" "프로젝트 화면으로 돌아갑니다." 0
Set-AccessibleControl $btnSave "검수 내용 저장" "현재 편집 내용을 로컬 프로젝트에 저장합니다. 단축키 Ctrl+S." 1
Set-AccessibleControl $btnOpenFolder "모드 폴더 열기" "현재 프로젝트의 RimWorld 모드 폴더를 엽니다." 2
Set-AccessibleControl $btnLoad "원문 불러오기" "현재 모드에서 번역 가능한 문자열을 다시 불러옵니다." 3
Set-AccessibleControl $btnTranslate "AI 초벌 번역" "API 키가 있으면 AI, 없으면 Google 번역으로 초벌 번역을 만듭니다." 4
Set-AccessibleControl $btnStop "번역 중지" "실행 중인 번역 작업을 중지합니다." 5
Set-AccessibleControl $btnApply "검토 완료 번역 적용" "검토 완료 상태만 Korean 폴더에 반영합니다." 6
Set-AccessibleControl $btnApplyTranslated "번역된 항목 모두 적용" "번역됨과 검토 완료 상태를 Korean 폴더에 반영합니다." 7

Set-AccessibleControl $cmbSearchField "검색 범위" "텍스트와 키 중 검색할 범위를 선택합니다." 0
Set-AccessibleControl $txtSearch "문자열 검색" "원문, 번역문 또는 키를 검색합니다. 단축키 Ctrl+F." 1
Set-AccessibleControl $cmbStatus "번역 상태 필터" "미번역, 번역됨, 검토됨, 업데이트로 변경됨 또는 주의 항목을 고릅니다." 2
for ($i = 0; $i -lt $statusFilterButtons.Count; $i++) {
    $filterButton = $statusFilterButtons[$i]
    Set-AccessibleControl $filterButton "$($filterButton.Text) 상태 필터" "$($filterButton.Text) 상태의 문자열만 목록에 표시합니다." $i
}
Set-AccessibleControl $txtSource "원문" "선택된 문자열의 읽기 전용 원문입니다." 0
Set-AccessibleControl $txtTranslation "번역문 편집" "선택된 문자열의 한국어 번역문을 편집합니다." 1
Set-AccessibleControl $txtMeta "문자열 정보" "키, 파일, ID, 단어 수와 안전 적용 여부입니다." 2
Set-AccessibleControl $btnPrev "이전 문자열" "이전 검색 결과로 이동합니다. 단축키 Alt+위쪽 화살표." 3
Set-AccessibleControl $btnNext "다음 문자열" "다음 검색 결과로 이동합니다. 단축키 Alt+아래쪽 화살표." 4
Set-AccessibleControl $btnUseCandidate "AI 후보 사용" "AI 초벌 번역을 편집기에 넣습니다." 5
Set-AccessibleControl $btnUseExisting "기존 번역 사용" "기존 Korean 번역을 편집기에 넣습니다." 6
Set-AccessibleControl $btnUseSource "번역문 복사" "현재 번역문을 클립보드에 복사합니다." 7
Set-AccessibleControl $btnResetEdit "편집 되돌리기" "저장된 번역문으로 되돌립니다." 8
Set-AccessibleControl $btnPending "미번역으로 표시" "현재 항목을 미번역 상태로 바꿉니다." 9
Set-AccessibleControl $btnTranslated "번역됨으로 표시" "현재 항목을 번역됨 상태로 바꿉니다." 10
Set-AccessibleControl $btnApprove "검토 완료로 표시" "현재 항목을 검토 완료 상태로 바꿉니다." 11
Set-AccessibleControl $btnApproveNext "검토 완료 후 다음" "현재 항목을 검토 완료로 저장하고 다음 항목으로 이동합니다. 단축키 Ctrl+Enter." 12
Set-AccessibleControl $txtExisting "기존 번역" "모드에 이미 있던 Korean 번역입니다." 13
Set-AccessibleControl $txtCandidate "AI 번역 후보" "AI 또는 Google이 만든 초벌 번역입니다." 14
Set-AccessibleControl $tabs "참고 정보 탭" "역사, 용어, 메모, 문제와 로그를 전환합니다." 0
Set-AccessibleControl $txtHistory "번역 역사" "원문, 기존 번역, AI 후보와 현재 검수 번역을 보여줍니다." 0
Set-AccessibleControl $txtTerms "관련 용어" "현재 문자열과 관련된 RimWorld 용어를 보여줍니다." 0
Set-AccessibleControl $txtMemo "검수 메모" "현재 문자열에 대한 로컬 메모를 편집합니다." 0
Set-AccessibleControl $txtWarnings "주의 사항" "토큰 누락과 안전 적용 여부 등 현재 문자열의 문제를 보여줍니다." 0
Set-AccessibleControl $txtLog "작업 로그" "원문 로드, 번역과 적용 과정의 로그입니다." 0

Set-AccessibleControl $btnDashProjects "프로젝트 탭" "로컬 번역 프로젝트를 표시합니다." 0
Set-AccessibleControl $btnDashActivity "활동 탭" "최근 번역과 검토 활동을 표시합니다." 1
Set-AccessibleControl $btnDashSettings "설정 탭" "API와 화면 설정을 표시합니다." 2
Set-AccessibleControl $txtDashboardSearch "프로젝트 검색" "모드 이름, Workshop ID 또는 패키지 ID를 검색합니다." 0
Set-AccessibleControl $cmbDashboardMods "프로젝트 대상 모드" "자동으로 찾은 RimWorld 모드 중 프로젝트로 만들 모드를 선택합니다." 1
Set-AccessibleControl $btnDashboardAddMod "프로젝트 만들기" "선택한 모드로 로컬 번역 프로젝트를 만듭니다." 2
Set-AccessibleControl $btnDashboardChooseMod "모드 폴더 선택" "자동 검색되지 않은 모드 폴더를 직접 선택합니다." 3
Set-AccessibleControl $btnDashboardRefreshMods "모드 목록 새로고침" "Steam과 RimWorld 모드 폴더를 다시 검색합니다." 4
Set-AccessibleControl $lvDashboardActivity "최근 활동 목록" "번역, 검토와 적용 내역입니다." 0
Set-AccessibleControl $txtDashboardApiKeys "Cerebras API 키" "한 줄에 하나씩 여러 API 키를 입력합니다. 이 값은 설정 파일에 저장되지 않습니다." 0
Set-AccessibleControl $chkDashboardIncludePatches "Patches 폴더 포함" "모드의 Patches 폴더도 번역 대상으로 포함합니다." 1
Set-AccessibleControl $chkDashboardDryRun "시험 실행" "파일을 쓰지 않고 번역 대상을 점검합니다." 2
Set-AccessibleControl $cmbDashboardTheme "테마" "시스템 설정, 밝은 테마 또는 어두운 테마를 선택합니다." 3
Set-AccessibleControl $cmbDashboardTextSize "본문 글자 크기" "번역문과 참고 정보의 글자 크기를 9에서 12 사이로 선택합니다." 4
Set-AccessibleControl $chkDashboardHighContrast "고대비" "텍스트와 경계선 대비를 높입니다." 5
Set-AccessibleControl $chkDashboardAutoSave "자동 저장" "입력을 멈춘 뒤 편집 내용을 자동으로 저장합니다." 6

foreach ($hiddenControl in @($cmbProject, $cmbModCatalog, $btnRefreshMods, $btnChooseMod, $txtApiKeys, $chkIncludePatches, $chkDryRun, $lvFiles, $lvItems)) {
    if ($hiddenControl) { $hiddenControl.TabStop = $false }
}

Set-CueBanner $txtSearch "검색어 입력"
Set-CueBanner $txtDashboardSearch "프로젝트 검색"

$toolTip.SetToolTip($txtSearch, "검색으로 이동: Ctrl+F · 검색 지우기: Esc")
$toolTip.SetToolTip($lblUpdateBadge, "모드 업데이트로 원문이 바뀐 항목입니다. 다시 번역하거나 검토해야 적용됩니다.")
foreach ($filterButton in $statusFilterButtons) {
    $toolTip.SetToolTip($filterButton, "$($filterButton.Text) 상태만 표시")
}
$toolTip.SetToolTip($btnSave, "저장 (Ctrl+S)")
$toolTip.SetToolTip($btnPrev, "이전 문자열 (Alt+↑)")
$toolTip.SetToolTip($btnNext, "다음 문자열 (Alt+↓)")
$toolTip.SetToolTip($btnUseCandidate, "AI가 만든 초벌 번역을 편집기에 넣습니다.")
$toolTip.SetToolTip($btnUseExisting, "기존 Korean 번역을 편집기에 넣습니다.")
$toolTip.SetToolTip($btnUseSource, "현재 번역문을 클립보드에 복사합니다.")
$toolTip.SetToolTip($btnResetEdit, "저장된 검수 번역으로 되돌립니다.")
$toolTip.SetToolTip($btnApproveNext, "검토 완료 후 다음 (Ctrl+Enter)")
$toolTip.SetToolTip($btnApply, "검토 완료 상태만 Korean 폴더에 반영합니다.")
$toolTip.SetToolTip($btnApplyTranslated, "번역됨과 검토 완료 상태를 함께 반영합니다.")
$toolTip.SetToolTip($cmbDashboardTheme, "기본값은 Windows 앱 테마를 따릅니다.")
$toolTip.SetToolTip($cmbDashboardTextSize, "번역문, 기존 번역, AI 후보와 참고 탭의 글자 크기")

Sync-DashboardSettingsFromMain
Apply-TextSize
Apply-AppTheme

$form.Add_Resize({
    if ($script:layouting -or $form.WindowState -eq [System.Windows.Forms.FormWindowState]::Minimized) { return }
    $script:layouting = $true
    try {
        Apply-AppTheme
    } finally {
        $script:layouting = $false
    }
})

$autoSaveTimer = New-Object System.Windows.Forms.Timer
$autoSaveTimer.Interval = 1200
$autoSaveTimer.Add_Tick({
    $autoSaveTimer.Stop()
    if (-not $script:autoSave -or -not $script:dirty -or -not $script:reviewRoot -or $script:currentRowIndex -lt 0) { return }
    try {
        Save-CurrentEdit
        Save-Decisions
    } catch {
        $lblSave.Text = "자동 저장 실패"
        Add-Log "자동 저장 실패: $($_.Exception.Message)"
    }
})

$cmbProject.Add_SelectedIndexChanged({
    if ($script:loadingProjectList -or -not $cmbProject.SelectedItem) { return }
    $project = $cmbProject.SelectedItem.Project
    if (-not $project) { return }
    Set-ActiveProject $project
    if ($project.modRoot -and (Test-Path -LiteralPath $project.modRoot -PathType Container)) {
        return
    } elseif ($project.latestReviewRoot -and (Test-Path -LiteralPath $project.latestReviewRoot -PathType Container)) {
        Load-ReviewRoot $project.latestReviewRoot
    }
})
$cmbModCatalog.Add_SelectedIndexChanged({
    if ($script:loadingProjectList -or -not $cmbModCatalog.SelectedItem -or -not $cmbModCatalog.Visible) { return }
    Set-SelectedMod $cmbModCatalog.SelectedItem
})
$btnRefreshMods.Add_Click({ Refresh-ModCatalog })
$btnChooseMod.Add_Click({ Choose-ModFolder })
$btnTranslate.Add_Click({ Start-Translation })
$btnStop.Add_Click({ Stop-Translation })
$btnApply.Add_Click({ Apply-ReviewedTranslations "ApprovedOnly" })
$btnApplyTranslated.Add_Click({ Apply-ReviewedTranslations "TranslatedAndApproved" })
$btnHome.Add_Click({ Show-Dashboard "projects" })
$btnLoad.Add_Click({ Load-SourceOnlyForSelectedMod })
$btnOpenFolder.Add_Click({ Open-ModFolder })
$btnSave.Add_Click({ Save-Decisions })
$btnDashProjects.Add_Click({ Show-Dashboard "projects" })
$btnDashActivity.Add_Click({ Show-Dashboard "activity" })
$btnDashSettings.Add_Click({ Show-Dashboard "settings" })
$txtDashboardSearch.Add_TextChanged({ Refresh-DashboardProjects })
$btnDashboardRefreshMods.Add_Click({ Refresh-ModCatalog; Refresh-DashboardProjects })
$btnDashboardChooseMod.Add_Click({ Choose-ModFolder })
$btnDashboardAddMod.Add_Click({
    if (-not $cmbDashboardMods.SelectedItem) {
        [System.Windows.Forms.MessageBox]::Show("프로젝트로 만들 모드를 선택하세요.", "RimWorld AI Translator") | Out-Null
        return
    }
    Set-SelectedMod $cmbDashboardMods.SelectedItem
    Show-Workspace
    Load-SourceOnlyForSelectedMod
})
$txtDashboardApiKeys.Add_TextChanged({ Sync-MainSettingsFromDashboard })
$chkDashboardIncludePatches.Add_CheckedChanged({ Sync-MainSettingsFromDashboard })
$chkDashboardDryRun.Add_CheckedChanged({ Sync-MainSettingsFromDashboard })
$cmbDashboardTheme.Add_SelectedIndexChanged({ Apply-DashboardPreferences })
$cmbDashboardTextSize.Add_SelectedIndexChanged({ Apply-DashboardPreferences })
$chkDashboardHighContrast.Add_CheckedChanged({ Apply-DashboardPreferences })
$chkDashboardAutoSave.Add_CheckedChanged({ Apply-DashboardPreferences })
$cmbSearchField.Add_SelectedIndexChanged({ if (-not $script:loading) { Refresh-ItemList -SelectRowIndex $script:currentRowIndex } })
$txtSearch.Add_TextChanged({ if (-not $script:loading) { Refresh-ItemList -SelectRowIndex $script:currentRowIndex } })
$txtSearch.Add_KeyDown({
    if ($_.KeyCode -eq [System.Windows.Forms.Keys]::Down) {
        Move-Selection 1
        $_.SuppressKeyPress = $true
    } elseif ($_.KeyCode -eq [System.Windows.Forms.Keys]::Up) {
        Move-Selection -1
        $_.SuppressKeyPress = $true
    } elseif ($_.KeyCode -eq [System.Windows.Forms.Keys]::Enter) {
        [void]$txtTranslation.Focus()
        $_.SuppressKeyPress = $true
    }
})
foreach ($filterButton in $statusFilterButtons) {
    $filterButton.Add_Click({
        $index = $cmbStatus.Items.IndexOf([string]$this.Tag)
        if ($index -ge 0) { $cmbStatus.SelectedIndex = $index }
    })
}
$cmbStatus.Add_SelectedIndexChanged({
    Refresh-StatusFilterButtons
    if (-not $script:loading) { Update-SearchCrumb; Refresh-ItemList -SelectRowIndex $script:currentRowIndex }
})
$tabs.Add_SelectedIndexChanged({
    if ($tabs.SelectedTab -ne $tabTerms -or $script:glossaryLoaded) { return }
    $form.UseWaitCursor = $true
    try {
        Load-Glossary
        if ($script:currentRowIndex -ge 0 -and $script:currentRowIndex -lt $script:rows.Count) {
            Update-TermsForRow $script:rows[$script:currentRowIndex]
        }
    } finally {
        $form.UseWaitCursor = $false
    }
})
$txtTranslation.Add_Enter({
    $lblTranslationTitle.Text = "번역문  ·  편집 중"
    $lblTranslationTitle.ForeColor = $script:accentColor
})
$txtTranslation.Add_Leave({
    $lblTranslationTitle.Text = "번역문"
    $lblTranslationTitle.ForeColor = $script:mutedColor
})
$txtTranslation.Add_TextChanged({
    if (-not $script:loading) {
        $script:dirty = $true
        $lblSave.Text = if ($script:autoSave) { "자동 저장 대기" } else { "저장 필요" }
        Queue-AutoSave
    }
})
$txtMemo.Add_TextChanged({
    if (-not $script:loading) {
        $script:dirty = $true
        $lblSave.Text = if ($script:autoSave) { "자동 저장 대기" } else { "저장 필요" }
        Queue-AutoSave
    }
})
$lvFiles.Add_SelectedIndexChanged({
    if ($lvFiles.SelectedItems.Count -eq 0) { return }
    $script:currentFile = [string]$lvFiles.SelectedItems[0].Tag
    Update-SearchCrumb
    Refresh-ItemList
})
$lvItems.Add_SelectedIndexChanged({
    if ($lvItems.SelectedItems.Count -eq 0) { return }
    Select-RowIndex ([int]$lvItems.SelectedItems[0].Tag)
})

$btnPrev.Add_Click({ Move-Selection -1 })
$btnNext.Add_Click({ Move-Selection 1 })
$btnUseCandidate.Add_Click({ if ($script:currentRowIndex -ge 0) { $txtTranslation.Text = ConvertTo-FlatString $script:rows[$script:currentRowIndex].candidate } })
$btnUseExisting.Add_Click({ if ($script:currentRowIndex -ge 0) { $txtTranslation.Text = ConvertTo-FlatString $script:rows[$script:currentRowIndex].existing } })
$btnUseSource.Add_Click({ Copy-ToClipboard $txtTranslation.Text })
$btnResetEdit.Add_Click({
    if ($script:currentRowIndex -ge 0) {
        $decision = Get-Decision $script:rows[$script:currentRowIndex]
        $script:loading = $true
        try { $txtTranslation.Text = ConvertTo-FlatString $decision.text } finally { $script:loading = $false }
        $lblSave.Text = ""
    }
})
$btnPending.Add_Click({ Mark-Current "pending" $false })
$btnTranslated.Add_Click({ Mark-Current "translated" $false })
$btnApprove.Add_Click({ Mark-Current "approved" $false })
$btnApproveNext.Add_Click({ Mark-Current "approved" $true })

$txtSource.Add_DoubleClick({ Copy-ToClipboard $txtSource.Text })
$txtCandidate.Add_DoubleClick({ Copy-ToClipboard $txtCandidate.Text })
$txtExisting.Add_DoubleClick({ Copy-ToClipboard $txtExisting.Text })
$txtMeta.Add_DoubleClick({ Copy-ToClipboard $txtMeta.Text })

$form.Add_KeyDown({
    if ($_.Control -and $_.KeyCode -eq [System.Windows.Forms.Keys]::F) {
        if ($dashboardPanel.Visible) {
            [void]$txtDashboardSearch.Focus()
            $txtDashboardSearch.SelectAll()
        } else {
            [void]$txtSearch.Focus()
            $txtSearch.SelectAll()
        }
        $_.SuppressKeyPress = $true
    } elseif ($_.Control -and $_.KeyCode -eq [System.Windows.Forms.Keys]::S) {
        Save-Decisions
        $_.SuppressKeyPress = $true
    } elseif ($_.Control -and $_.KeyCode -eq [System.Windows.Forms.Keys]::Enter) {
        Mark-Current "approved" $true
        $_.SuppressKeyPress = $true
    } elseif ($_.KeyCode -eq [System.Windows.Forms.Keys]::F6) {
        Focus-NextWorkRegion $(if ($_.Shift) { -1 } else { 1 })
        $_.SuppressKeyPress = $true
    } elseif ($_.KeyCode -eq [System.Windows.Forms.Keys]::Escape) {
        if ($dashboardPanel.Visible -and $txtDashboardSearch.Text) {
            $txtDashboardSearch.Clear()
            [void]$txtDashboardSearch.Focus()
        } elseif (-not $dashboardPanel.Visible -and ($txtSearch.Text -or $cmbStatus.SelectedIndex -gt 0)) {
            $txtSearch.Clear()
            $cmbStatus.SelectedIndex = 0
            [void]$txtSearch.Focus()
        }
        $_.SuppressKeyPress = $true
    } elseif ($_.Alt -and $_.KeyCode -in @([System.Windows.Forms.Keys]::Down, [System.Windows.Forms.Keys]::Right)) {
        Move-Selection 1
        $_.SuppressKeyPress = $true
    } elseif ($_.Alt -and $_.KeyCode -in @([System.Windows.Forms.Keys]::Up, [System.Windows.Forms.Keys]::Left)) {
        Move-Selection -1
        $_.SuppressKeyPress = $true
    }
})

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 250
$timer.Add_Tick({
    foreach ($line in (Read-NewProcessLogLines)) {
        Add-Log $line
        Update-ProgressFromLine $line
    }

    if ($script:process -and $script:process.HasExited -and -not $script:processExitHandled) {
        foreach ($line in (Read-NewProcessLogLines)) {
            Add-Log $line
            Update-ProgressFromLine $line
        }
        if (-not [string]::IsNullOrEmpty($script:translationLogPartial)) {
            Add-Log $script:translationLogPartial
            Update-ProgressFromLine $script:translationLogPartial
            $script:translationLogPartial = ""
        }

        $script:processExitHandled = $true
        $exitCode = $script:process.ExitCode
        $elapsed = if ($script:startedAt) { [Math]::Round(((Get-Date) - $script:startedAt).TotalSeconds, 1) } else { 0 }
        Add-Log "프로세스 종료. ExitCode=$exitCode, 경과 ${elapsed}s"

        if ($script:stopRequested) {
            $lblRunStatus.Text = "중지됨"
            Add-Log "사용자 요청으로 중지 완료."
        } elseif ($exitCode -eq 0) {
            if ($progressRun.Maximum -gt 0) { $progressRun.Value = $progressRun.Maximum }
            if ($script:lastReviewOutputPath -and (Test-Path -LiteralPath $script:lastReviewOutputPath -PathType Container)) {
                try {
                    Load-ReviewRoot $script:lastReviewOutputPath
                    Register-ProjectRun -ReviewRoot $script:lastReviewOutputPath -Provider $script:lastProvider
                    $lblRunStatus.Text = "검수 결과 불러옴"
                    Add-Log "검수 결과를 현재 화면에 불러왔습니다."
                } catch {
                    $lblRunStatus.Text = "검수 결과 열기 실패"
                    Add-Log "검수 결과를 열지 못했습니다: $($_.Exception.Message)"
                }
            } else {
                $lblRunStatus.Text = "완료"
            }
        } else {
            $lblRunStatus.Text = "종료 코드 $exitCode"
        }

        try { $script:process.Dispose() } catch {}
        $script:process = $null
        $script:stopRequested = $false
        Set-TranslationRunning $false
        Remove-TempFiles
    }
})
$timer.Start()

$form.Add_FormClosing({
    if ($autoSaveTimer) { $autoSaveTimer.Stop() }
    if ($script:process -and -not $script:process.HasExited) {
        try { Stop-ProcessTree $script:process.Id } catch {}
    }
    Remove-TempFiles
    if ($script:dirty) {
        $result = [System.Windows.Forms.MessageBox]::Show("저장하지 않은 검수 내용이 있습니다. 저장할까요?", "RimWorld AI Translator", [System.Windows.Forms.MessageBoxButtons]::YesNoCancel, [System.Windows.Forms.MessageBoxIcon]::Question)
        if ($result -eq [System.Windows.Forms.DialogResult]::Cancel) {
            $_.Cancel = $true
            return
        }
        if ($result -eq [System.Windows.Forms.DialogResult]::Yes) { Save-Decisions }
    }
})

Ensure-AppDataStore
Load-ProjectStore
Refresh-ProjectList

Add-Log "프로그램 시작 안내"
Add-Log "1. 프로젝트 화면에서 모드를 골라 프로젝트를 만드세요. 프로젝트 하나가 모드 하나입니다."
Add-Log "2. 프로젝트가 열리면 그 프로젝트의 모드만 원문 로드, AI 번역, 적용 대상으로 사용됩니다."
Add-Log "3. API 키를 비우면 Google 번역 후보를 생성합니다."
Add-Log "4. AI 후보는 번역됨 상태이며, 직접 확인하면 검토됨으로 바꿀 수 있습니다."
Add-Log "5. 원문이 바뀐 키는 업데이트 변경으로 표시되고 미번역으로 내려가며 적용 대상에서 제외됩니다."

$form.Add_Shown({
    try {
        if ($LayoutSnapshotWidth -gt 0 -and $LayoutSnapshotHeight -gt 0) {
            $form.WindowState = [System.Windows.Forms.FormWindowState]::Normal
            $form.ClientSize = New-Object System.Drawing.Size($LayoutSnapshotWidth, $LayoutSnapshotHeight)
        }
        Apply-AppTheme
        if ($script:initialReviewRoot) {
            Load-ReviewRoot $script:initialReviewRoot
            Show-Workspace
        } else {
            $initialTab = if ($InitialDashboardTab -in @("projects", "activity", "settings")) { $InitialDashboardTab } else { "projects" }
            Show-Dashboard $initialTab
        }
    } catch {
        [System.Windows.Forms.MessageBox]::Show("검토 결과를 열지 못했습니다.`r`n$($_.Exception.Message)", "RimWorld AI Translator") | Out-Null
    }

    try {
        Refresh-ModCatalog -PreferCache
    } catch {
        Add-Log "모드 자동 검색 실패: $($_.Exception.Message)"
    }

    if (-not [string]::IsNullOrWhiteSpace($script:layoutSnapshotPath)) {
        $script:layoutSnapshotTimer = New-Object System.Windows.Forms.Timer
        $script:layoutSnapshotTimer.Interval = 1200
        $script:layoutSnapshotTimer.Add_Tick({
            $script:layoutSnapshotTimer.Stop()
            try {
                $snapshotDir = Split-Path -Parent $script:layoutSnapshotPath
                if ($snapshotDir -and -not (Test-Path -LiteralPath $snapshotDir)) {
                    New-Item -ItemType Directory -Force -Path $snapshotDir | Out-Null
                }
                $bitmap = New-Object System.Drawing.Bitmap($form.ClientSize.Width, $form.ClientSize.Height)
                try {
                    $rect = New-Object System.Drawing.Rectangle(0, 0, $form.ClientSize.Width, $form.ClientSize.Height)
                    $form.DrawToBitmap($bitmap, $rect)
                    $bitmap.Save($script:layoutSnapshotPath, [System.Drawing.Imaging.ImageFormat]::Png)
                } finally {
                    $bitmap.Dispose()
                }
                $auditPath = [System.IO.Path]::ChangeExtension($script:layoutSnapshotPath, ".accessibility.json")
                $auditRows = @(Get-AccessibilityAuditRows -Parent $form)
                [System.IO.File]::WriteAllText(
                    $auditPath,
                    ($auditRows | ConvertTo-Json -Depth 5),
                    (New-Object System.Text.UTF8Encoding($false))
                )
            } finally {
                $form.Close()
            }
        })
        $script:layoutSnapshotTimer.Start()
    }
})

[void][System.Windows.Forms.Application]::Run($form)
