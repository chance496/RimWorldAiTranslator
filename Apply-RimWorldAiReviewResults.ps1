param(
    [Parameter(Mandatory = $true)]
    [string]$ModRoot,

    [Parameter(Mandatory = $true)]
    [string]$ReviewRoot,

    [string]$LanguageFolderName = "Korean",
    [switch]$Overwrite,
    [switch]$DryRun,
    [ValidateSet("ApprovedOnly", "TranslatedAndApproved")]
    [string]$ApplyStatus = "ApprovedOnly"
)

$ErrorActionPreference = "Stop"
try {
    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $OutputEncoding = [System.Text.UTF8Encoding]::new($false)
} catch {
}

function Resolve-FullPath([string]$Path) {
    return [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path)
}

function Assert-SafePathSegment([string]$Value, [string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -in @(".", "..") -or $Value.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0 -or $Value.Contains("\") -or $Value.Contains("/")) {
        throw "$Name must be a single safe folder-name segment."
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

function Test-ValidXmlElementName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) { return $false }
    try {
        [void][System.Xml.XmlConvert]::VerifyName($Name)
        return $true
    } catch {
        return $false
    }
}

function ConvertTo-FlatString([object]$Value) {
    if ($null -eq $Value) { return "" }
    return ([string]$Value).Replace("`r`n", "`n").Replace("`r", "`n")
}

function ConvertTo-BoolValue([object]$Value) {
    if ($Value -is [bool]) { return [bool]$Value }
    if ($null -eq $Value) { return $false }
    $text = ([string]$Value).Trim()
    return $text -match "^(true|1|yes)$"
}

function Get-RowIdentity([object]$Row) {
    if ($Row.id) { return "id:$($Row.id)" }
    return "key:$($Row.key)"
}

function Get-DecisionIdentity([object]$Item) {
    if ($Item.target -and $Item.key) { return "target:$($Item.target)|key:$($Item.key)" }
    if ($Item.id) { return "id:$($Item.id)" }
    return "key:$($Item.key)"
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

function Test-DecisionSourceChanged([object]$Item, [object]$Row) {
    $currentSource = ConvertTo-FlatString $Row.source
    if ($Item.PSObject.Properties["sourceHash"] -and -not [string]::IsNullOrWhiteSpace([string]$Item.sourceHash)) {
        return ([string]$Item.sourceHash) -ne (Get-TextFingerprint $currentSource)
    }
    if ($Item.PSObject.Properties["sourceText"] -and -not [string]::IsNullOrWhiteSpace([string]$Item.sourceText)) {
        return (ConvertTo-FlatString $Item.sourceText) -ne $currentSource
    }
    return $false
}

function Test-StatusIncluded([string]$Status, [string]$Mode) {
    if ($Status -eq "approved") { return $true }
    return $Mode -eq "TranslatedAndApproved" -and $Status -eq "translated"
}

function Read-LanguageFile([string]$Path) {
    $map = [ordered]@{}
    if (-not (Test-Path -LiteralPath $Path)) { return $map }

    $doc = Read-SafeXmlDocument $Path
    if (-not $doc.DocumentElement -or $doc.DocumentElement.LocalName -ne "LanguageData") {
        throw "Target XML is not LanguageData: $Path"
    }
    foreach ($child in @($doc.DocumentElement.ChildNodes | Where-Object { $_ -is [System.Xml.XmlElement] })) {
        $map[$child.LocalName] = ConvertTo-FlatString $child.InnerText
    }
    return $map
}

function Remove-InvalidXmlChars([string]$Text) {
    if ($null -eq $Text) { return "" }
    return [System.Text.RegularExpressions.Regex]::Replace($Text, "[^\u0009\u000A\u000D\u0020-\uD7FF\uE000-\uFFFD]", "")
}

function Escape-XmlText([string]$Text) {
    return [System.Security.SecurityElement]::Escape((Remove-InvalidXmlChars $Text))
}

function Write-LanguageFile([string]$Path, [hashtable]$Entries, [switch]$Overwrite) {
    $existing = Read-LanguageFile $Path
    $applied = 0
    $skippedExisting = 0

    foreach ($key in ($Entries.Keys | Sort-Object)) {
        if (-not (Test-ValidXmlElementName ([string]$key))) {
            throw "Refusing to write an invalid XML localization key: $key"
        }
        if ($Overwrite -or -not $existing.Contains($key)) {
            $existing[$key] = $Entries[$key]
            $applied++
        } else {
            $skippedExisting++
        }
    }

    if ($applied -gt 0) {
        $lines = New-Object "System.Collections.Generic.List[string]"
        [void]$lines.Add('<?xml version="1.0" encoding="utf-8"?>')
        [void]$lines.Add('<LanguageData>')
        foreach ($key in ($existing.Keys | Sort-Object)) {
            [void]$lines.Add("  <$key>$(Escape-XmlText ([string]$existing[$key]))</$key>")
        }
        [void]$lines.Add('</LanguageData>')

        $dir = Split-Path -Parent $Path
        if (-not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
        }
        if (-not $DryRun) {
            [System.IO.File]::WriteAllLines($Path, $lines, [System.Text.UTF8Encoding]::new($false))
        }
    }

    return [pscustomobject]@{
        Applied = $applied
        SkippedExisting = $skippedExisting
    }
}

function Get-RelativePathFromReviewTarget([string]$TargetPath, [string]$ReviewLanguageRoot) {
    if ([string]::IsNullOrWhiteSpace($TargetPath)) { return $null }
    $fullTarget = [System.IO.Path]::GetFullPath($TargetPath)
    $fullReviewRoot = [System.IO.Path]::GetFullPath($ReviewLanguageRoot).TrimEnd("\", "/")
    $reviewPrefix = $fullReviewRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullTarget.StartsWith($reviewPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $null
    }
    return $fullTarget.Substring($reviewPrefix.Length)
}

$modFull = Resolve-FullPath $ModRoot
$reviewFull = Resolve-FullPath $ReviewRoot
Assert-SafePathSegment -Value $LanguageFolderName -Name "LanguageFolderName"
$reviewLanguageRoot = Join-Path (Join-Path $reviewFull "Languages") $LanguageFolderName
$outputLanguageRoot = Join-Path (Join-Path $modFull "Languages") $LanguageFolderName
$auditRoot = Join-Path $reviewFull "_TranslationAudit"

if (-not (Test-Path -LiteralPath $auditRoot)) {
    throw "Review audit folder not found: $auditRoot"
}
if (-not (Test-Path -LiteralPath $reviewLanguageRoot)) {
    throw "Review language folder not found: $reviewLanguageRoot"
}

$comparisonFile = Get-ChildItem -LiteralPath $auditRoot -File -Filter "*-comparison.json" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $comparisonFile) {
    throw "Comparison JSON not found in: $auditRoot"
}

Write-Host "Review root: $reviewFull"
Write-Host "Mod root: $modFull"
Write-Host "Comparison: $($comparisonFile.FullName)"
Write-Host "Output language: $outputLanguageRoot"
Write-Host "Overwrite existing: $([bool]$Overwrite)"
Write-Host "Apply status: $ApplyStatus"
if ($DryRun) { Write-Host "Dry run: no files will be written." }

$parsedRows = [System.IO.File]::ReadAllText($comparisonFile.FullName, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$rows = New-Object "System.Collections.Generic.List[object]"
foreach ($row in $parsedRows) {
    [void]$rows.Add($row)
}
$rowByIdentity = @{}
foreach ($row in $rows) {
    $relativeTarget = Get-RelativePathFromReviewTarget -TargetPath ([string]$row.target) -ReviewLanguageRoot $reviewLanguageRoot
    if ($relativeTarget -and $row.key) { $rowByIdentity["target:$relativeTarget|key:$($row.key)"] = $row }
    $rowByIdentity[(Get-RowIdentity $row)] = $row
    if ($row.key) { $rowByIdentity["key:$($row.key)"] = $row }
}

$outputGroups = @{}
$safeRows = 0
$approvedRows = 0
$translatedRows = 0
$skippedNotApproved = 0
$skippedUnsafe = 0
$skippedUnmapped = 0
$skippedBlank = 0

function Add-ReviewedCandidate([object]$Row, [string]$Candidate) {
    $candidateText = ConvertTo-FlatString $Candidate
    if ([string]::IsNullOrWhiteSpace($candidateText)) {
        return "blank"
    }
    if (-not (Test-ValidXmlElementName ([string]$Row.key))) {
        return "unmapped"
    }

    $relative = Get-RelativePathFromReviewTarget -TargetPath ([string]$Row.target) -ReviewLanguageRoot $reviewLanguageRoot
    if (-not $relative) {
        return "unmapped"
    }

    $targetPath = [System.IO.Path]::GetFullPath((Join-Path $outputLanguageRoot $relative))
    $outputRootFull = [System.IO.Path]::GetFullPath($outputLanguageRoot).TrimEnd("\", "/")
    $outputPrefix = $outputRootFull + [System.IO.Path]::DirectorySeparatorChar
    if (-not $targetPath.StartsWith($outputPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        return "unmapped"
    }

    if (-not $outputGroups.ContainsKey($targetPath)) { $outputGroups[$targetPath] = @{} }
    $outputGroups[$targetPath][[string]$Row.key] = $candidateText
    return "ok"
}

$decisionFile = Join-Path $reviewFull "review-decisions.json"
if (Test-Path -LiteralPath $decisionFile) {
    Write-Host "Review decisions: $decisionFile"
    $decisionData = [System.IO.File]::ReadAllText($decisionFile, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    foreach ($item in @($decisionData.items)) {
        if (-not $item) { continue }

        $identity = Get-DecisionIdentity $item
        if (-not $rowByIdentity.ContainsKey($identity)) {
            $skippedUnmapped++
            continue
        }

        $row = $rowByIdentity[$identity]
        $status = [string]$item.status
        $candidateText = [string]$item.text
        if (($item.PSObject.Properties["sourceChanged"] -and (ConvertTo-BoolValue $item.sourceChanged)) -or (Test-DecisionSourceChanged -Item $item -Row $row)) {
            $status = "pending"
            $candidateText = ""
        }

        if (-not (Test-StatusIncluded -Status $status -Mode $ApplyStatus)) {
            $skippedNotApproved++
            continue
        }
        if ($status -eq "translated" -and -not (ConvertTo-BoolValue $row.safeToApply)) {
            $skippedUnsafe++
            continue
        }

        $result = Add-ReviewedCandidate -Row $row -Candidate $candidateText
        switch ($result) {
            "ok" {
                if ($status -eq "approved") { $approvedRows++ } else { $translatedRows++ }
            }
            "blank" { $skippedBlank++ }
            default { $skippedUnmapped++ }
        }
    }
} else {
    Write-Host "Review decisions: none."
    if ($ApplyStatus -eq "TranslatedAndApproved") {
        Write-Host "Applying safe translated candidates from comparison JSON."
        foreach ($row in $rows) {
            if (-not (ConvertTo-BoolValue $row.safeToApply)) {
                $skippedUnsafe++
                continue
            }

            $result = Add-ReviewedCandidate -Row $row -Candidate ([string]$row.candidate)
            switch ($result) {
                "ok" {
                    $safeRows++
                    $translatedRows++
                }
                "blank" { $skippedBlank++ }
                default { $skippedUnmapped++ }
            }
        }
    } else {
        $skippedNotApproved += $rows.Count
    }
}

$appliedEntries = 0
$skippedExisting = 0
foreach ($targetPath in ($outputGroups.Keys | Sort-Object)) {
    $result = Write-LanguageFile -Path $targetPath -Entries $outputGroups[$targetPath] -Overwrite:$Overwrite
    $appliedEntries += $result.Applied
    $skippedExisting += $result.SkippedExisting
}

Write-Host "Done."
Write-Host "Safe candidate rows: $safeRows"
Write-Host "Approved reviewed rows: $approvedRows"
Write-Host "Translated rows: $translatedRows"
Write-Host "Written/updated files: $($outputGroups.Keys.Count)"
Write-Host "Applied entries: $appliedEntries"
Write-Host "Skipped existing entries: $skippedExisting"
Write-Host "Skipped not-approved rows: $skippedNotApproved"
Write-Host "Skipped unsafe rows: $skippedUnsafe"
Write-Host "Skipped blank rows: $skippedBlank"
Write-Host "Skipped unmapped rows: $skippedUnmapped"
