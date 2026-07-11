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

$script:DisplayLocalizationFieldPattern = '^(label|labelshort|description|jobstring|reportstring|deathmessage|deathmessagefemale|deathmessagemale|letterlabel|lettertext|header|headertip|summary|formatstring|formatstringunfinalized|fixedname|reason|text|slateref)$'
$script:TechnicalLocalizationFieldPattern = '^(defname|parentname|classname|class|thingclass|workerclass|compclass|hediffclass|thoughtclass|abilityclass|worldobjectclass|nodeclass|debuglabel|tagdef|texpath|texname|graphicpath|shader|sound|sounddef|iconpath|packageid|xpath|operation|colorchannel|rendernode|rendertree|rendertreedef|bodypart|bodypartdef|bodytype|headtype|racedef|thingdef|pawnkinddef|jobdef|statdef|skilldef|hediffdef|genedef)$'
$script:DeniedLocalizationFields = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
$defFieldRulePath = Join-Path $PSScriptRoot "rimworld-def-field-rules.txt"
if (Test-Path -LiteralPath $defFieldRulePath -PathType Leaf) {
    foreach ($line in [System.IO.File]::ReadAllLines($defFieldRulePath, [System.Text.Encoding]::UTF8)) {
        if ($line -match '^\s*deny\t([A-Za-z_][A-Za-z0-9_]*)\s*$') { [void]$script:DeniedLocalizationFields.Add($matches[1]) }
    }
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

function Get-ProtectedTokenCounts([string]$Text) {
    $counts = New-Object "System.Collections.Generic.Dictionary[string,int]" ([System.StringComparer]::Ordinal)
    $pattern = '(\\r\\n|\\[nrt]|\{[^}\r\n]+\}|\[[A-Za-z0-9_.:;''" -]+\]|</?[A-Za-z][^>\r\n]*>|\$[A-Za-z_][A-Za-z0-9_]*\$?|%[0-9.]*[sdif]|\b[A-Z]{2,}_[A-Z0-9_]+\b|\b[A-Za-z][A-Za-z0-9_]*->)'
    foreach ($match in [System.Text.RegularExpressions.Regex]::Matches([string]$Text, $pattern)) {
        $token = [string]$match.Value
        if ($counts.ContainsKey($token)) { $counts[$token]++ } else { $counts[$token] = 1 }
    }
    return $counts
}

function Test-JsonWithBackupExists([string]$Path) {
    return (Test-Path -LiteralPath $Path -PathType Leaf) -or (Test-Path -LiteralPath "$Path.bak" -PathType Leaf)
}

function Read-JsonWithBackup([string]$Path) {
    $errors = New-Object "System.Collections.Generic.List[string]"
    foreach ($candidate in @($Path, "$Path.bak")) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        try {
            $raw = [System.IO.File]::ReadAllText($candidate, [System.Text.Encoding]::UTF8)
            if ([string]::IsNullOrWhiteSpace($raw)) { throw "JSON file is empty." }
            return $raw | ConvertFrom-Json -ErrorAction Stop
        } catch {
            [void]$errors.Add("$candidate : $($_.Exception.Message)")
        }
    }
    throw "Review decisions and backup could not be read. $([string]::Join(' | ', $errors))"
}

function Test-ProtectedTokenStructure([string]$Source, [string]$Translation) {
    $sourceCounts = Get-ProtectedTokenCounts $Source
    $targetCounts = Get-ProtectedTokenCounts $Translation
    if ($sourceCounts.Count -ne $targetCounts.Count) { return $false }
    foreach ($token in $sourceCounts.Keys) {
        if (-not $targetCounts.ContainsKey($token) -or [int]$targetCounts[$token] -ne [int]$sourceCounts[$token]) { return $false }
    }
    return $true
}

function Test-InternalLocalizationIdentifierRow([object]$Row) {
    if (-not $Row -or [string]::IsNullOrWhiteSpace([string]$Row.key) -or [string]$Row.kind -ne "DefInjected") { return $false }
    $keyLower = ([string]$Row.key).Trim().ToLowerInvariant()
    $typeLower = if ($Row.PSObject.Properties["defClass"] -and $Row.defClass) { ([string]$Row.defClass).Trim().ToLowerInvariant() } else { "" }
    $fieldLower = if ($Row.PSObject.Properties["field"] -and $Row.field) { ([string]$Row.field).Trim().ToLowerInvariant() } else { ($keyLower -replace "^.*\.", "") }
    $isDisplayField = $fieldLower -match $script:DisplayLocalizationFieldPattern
    if ($script:DeniedLocalizationFields.Contains($fieldLower)) { return $true }
    if ($fieldLower -match $script:TechnicalLocalizationFieldPattern) { return $true }
    if ($keyLower -match "\.alienrace\.generalsettings\.alienpartgenerator\.colorchannels\.") { return $true }
    if ($fieldLower -eq "name" -and $keyLower -match "\.alienrace\.") { return $true }
    if ($fieldLower -eq "name" -and $keyLower -match "\.(colorchannels|bodyaddons|powermodes)\.") { return $true }
    if ($keyLower -match "\.(graphicpaths?|rendernodes?|rendertree)\." -and -not $isDisplayField) { return $true }
    return $typeLower -match "pawnrendertreedef" -and -not $isDisplayField
}

function Test-InvalidKoreanParticleNotation([string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }
    return $Text -match '(은\(는\)|는\(은\)|이\(가\)|가\(이\)|을\(를\)|를\(을\)|과\(와\)|와\(과\)|으로\(로\)|로\(으로\)|(?:\[[^\]\r\n]+\]|\{[^}\r\n]+\}|\$[A-Za-z_][A-Za-z0-9_]*)(?:으로|은|는|이|가|을|를|과|와|로)(?=$|[\s.,!?…:;，。！？、]))'
}

function Test-TranslationStructureSafe([object]$Row, [string]$Translation) {
    $source = ConvertTo-FlatString $Row.source
    $translationText = ConvertTo-FlatString $Translation
    if (Test-InternalLocalizationIdentifierRow $Row) { return $false }
    if ([string]::IsNullOrWhiteSpace($translationText)) { return $false }
    if (Test-InvalidKoreanParticleNotation $translationText) { return $false }
    if ($translationText -match "(\r?\n\s*){8,}" -or $translationText -match "(\\u000a\s*){8,}") { return $false }
    $newlineCount = [System.Text.RegularExpressions.Regex]::Matches($translationText, "\r?\n").Count
    if ($newlineCount -ge 20 -and $translationText.Length -lt 4000) { return $false }
    if (-not (Test-ProtectedTokenStructure -Source $source -Translation $translationText)) { return $false }
    $grammarPrefix = [System.Text.RegularExpressions.Regex]::Match($source, '^\s*([A-Za-z][A-Za-z0-9_]*->)')
    if ($grammarPrefix.Success -and -not [System.Text.RegularExpressions.Regex]::IsMatch($translationText, ('^\s*' + [regex]::Escape($grammarPrefix.Groups[1].Value)))) { return $false }
    return $true
}

function Test-TranslationSafe([object]$Row, [string]$Translation) {
    $source = ConvertTo-FlatString $Row.source
    $translationText = ConvertTo-FlatString $Translation
    if (-not (Test-TranslationStructureSafe -Row $Row -Translation $translationText)) { return $false }
    if ([string]::Equals($source, $translationText, [System.StringComparison]::Ordinal)) { return $false }
    return $translationText -match "[\uAC00-\uD7AF]"
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

function Write-Utf8LinesAtomic([string]$Path, [System.Collections.IEnumerable]$Lines) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $directory = [System.IO.Path]::GetDirectoryName($fullPath)
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    $temporaryPath = Join-Path $directory (".{0}.{1}.tmp" -f [System.IO.Path]::GetFileName($fullPath), [System.Guid]::NewGuid().ToString("N"))
    try {
        $text = [string]::Join([Environment]::NewLine, @($Lines)) + [Environment]::NewLine
        $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($text)
        $stream = [System.IO.FileStream]::new($temporaryPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        } finally {
            $stream.Dispose()
        }
        if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            $backupPath = "$fullPath.bak"
            [System.IO.File]::Replace($temporaryPath, $fullPath, $backupPath, $true)
        } else {
            [System.IO.File]::Move($temporaryPath, $fullPath)
        }
    } finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Copy-FileFlushed([string]$SourcePath, [string]$DestinationPath) {
    $bytes = [System.IO.File]::ReadAllBytes($SourcePath)
    $stream = [System.IO.FileStream]::new($DestinationPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    } finally {
        $stream.Dispose()
    }
}

function Restore-TransactionFile([object]$Entry) {
    $path = [string]$Entry.Path
    if (-not [bool]$Entry.Existed) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Remove-Item -LiteralPath $path -Force -ErrorAction Stop
        }
        return
    }

    $directory = [System.IO.Path]::GetDirectoryName($path)
    $restorePath = Join-Path $directory (".{0}.{1}.restore.tmp" -f [System.IO.Path]::GetFileName($path), [System.Guid]::NewGuid().ToString("N"))
    $discardPath = Join-Path $directory (".{0}.{1}.failed.tmp" -f [System.IO.Path]::GetFileName($path), [System.Guid]::NewGuid().ToString("N"))
    try {
        Copy-FileFlushed -SourcePath ([string]$Entry.SnapshotPath) -DestinationPath $restorePath
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            [System.IO.File]::Replace($restorePath, $path, $discardPath, $true)
        } else {
            [System.IO.File]::Move($restorePath, $path)
        }
    } finally {
        foreach ($temporaryPath in @($restorePath, $discardPath)) {
            if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
                Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
            }
        }
    }
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
            Write-Utf8LinesAtomic -Path $Path -Lines $lines
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
if (Test-JsonWithBackupExists $decisionFile) {
    Write-Host "Review decisions: $decisionFile"
    $decisionData = Read-JsonWithBackup $decisionFile
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
        if (-not (Test-TranslationStructureSafe -Row $row -Translation $candidateText)) {
            $skippedUnsafe++
            continue
        }
        if ($status -eq "translated" -and -not (Test-TranslationSafe -Row $row -Translation $candidateText)) {
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
            if (-not (Test-TranslationSafe -Row $row -Translation ([string]$row.candidate))) {
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
$writeJournal = New-Object "System.Collections.Generic.List[object]"
try {
    foreach ($targetPath in ($outputGroups.Keys | Sort-Object)) {
        if (-not $DryRun) {
            $existed = Test-Path -LiteralPath $targetPath -PathType Leaf
            $snapshotPath = ""
            if ($existed) {
                $directory = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($targetPath))
                $snapshotPath = Join-Path $directory (".{0}.{1}.transaction.bak" -f [System.IO.Path]::GetFileName($targetPath), [System.Guid]::NewGuid().ToString("N"))
                Copy-FileFlushed -SourcePath $targetPath -DestinationPath $snapshotPath
            }
            [void]$writeJournal.Add([pscustomobject]@{ Path = $targetPath; Existed = $existed; SnapshotPath = $snapshotPath })
        }

        $result = Write-LanguageFile -Path $targetPath -Entries $outputGroups[$targetPath] -Overwrite:$Overwrite
        $appliedEntries += $result.Applied
        $skippedExisting += $result.SkippedExisting
    }
} catch {
    $applyError = $_.Exception
    $rollbackErrors = New-Object "System.Collections.Generic.List[string]"
    for ($index = $writeJournal.Count - 1; $index -ge 0; $index--) {
        try {
            Restore-TransactionFile $writeJournal[$index]
        } catch {
            [void]$rollbackErrors.Add("$($writeJournal[$index].Path): $($_.Exception.Message)")
        }
    }
    if ($rollbackErrors.Count -gt 0) {
        throw "Translation apply failed and rollback was incomplete. Apply error: $($applyError.Message) Rollback errors: $([string]::Join(' | ', $rollbackErrors))"
    }
    throw "Translation apply failed; all files written by this run were rolled back. $($applyError.Message)"
} finally {
    foreach ($entry in $writeJournal) {
        if ($entry.SnapshotPath -and (Test-Path -LiteralPath ([string]$entry.SnapshotPath) -PathType Leaf)) {
            Remove-Item -LiteralPath ([string]$entry.SnapshotPath) -Force -ErrorAction SilentlyContinue
        }
    }
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
