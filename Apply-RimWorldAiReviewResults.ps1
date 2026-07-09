param(
    [Parameter(Mandatory = $true)]
    [string]$ModRoot,

    [Parameter(Mandatory = $true)]
    [string]$ReviewRoot,

    [string]$LanguageFolderName = "Korean",
    [switch]$Overwrite,
    [switch]$DryRun
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

function Read-LanguageFile([string]$Path) {
    $map = [ordered]@{}
    if (-not (Test-Path -LiteralPath $Path)) { return $map }

    $doc = New-Object System.Xml.XmlDocument
    $doc.PreserveWhitespace = $false
    $doc.LoadXml([System.IO.File]::ReadAllText($Path))
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
    if (-not $fullTarget.StartsWith($fullReviewRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $null
    }
    return $fullTarget.Substring($fullReviewRoot.Length).TrimStart("\", "/")
}

$modFull = Resolve-FullPath $ModRoot
$reviewFull = Resolve-FullPath $ReviewRoot
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
if ($DryRun) { Write-Host "Dry run: no files will be written." }

$parsedRows = [System.IO.File]::ReadAllText($comparisonFile.FullName, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$rows = New-Object "System.Collections.Generic.List[object]"
foreach ($row in $parsedRows) {
    [void]$rows.Add($row)
}
$outputGroups = @{}
$safeRows = 0
$skippedUnsafe = 0
$skippedUnmapped = 0
$skippedBlank = 0

foreach ($row in $rows) {
    if (-not (ConvertTo-BoolValue $row.safeToApply)) {
        $skippedUnsafe++
        continue
    }

    $candidate = ConvertTo-FlatString $row.candidate
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $skippedBlank++
        continue
    }

    $relative = Get-RelativePathFromReviewTarget -TargetPath ([string]$row.target) -ReviewLanguageRoot $reviewLanguageRoot
    if (-not $relative) {
        $skippedUnmapped++
        continue
    }

    $targetPath = [System.IO.Path]::GetFullPath((Join-Path $outputLanguageRoot $relative))
    $outputRootFull = [System.IO.Path]::GetFullPath($outputLanguageRoot).TrimEnd("\", "/")
    if (-not $targetPath.StartsWith($outputRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        $skippedUnmapped++
        continue
    }

    if (-not $outputGroups.ContainsKey($targetPath)) { $outputGroups[$targetPath] = @{} }
    $outputGroups[$targetPath][[string]$row.key] = $candidate
    $safeRows++
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
Write-Host "Written/updated files: $($outputGroups.Keys.Count)"
Write-Host "Applied entries: $appliedEntries"
Write-Host "Skipped existing entries: $skippedExisting"
Write-Host "Skipped unsafe rows: $skippedUnsafe"
Write-Host "Skipped blank rows: $skippedBlank"
Write-Host "Skipped unmapped rows: $skippedUnmapped"
