param(
    [Parameter(Mandatory = $true)]
    [string]$RmkEntryRoot,

    [Parameter(Mandatory = $true)]
    [string]$ReviewRoot,

    [string]$ReviewLanguageFolderName = "Korean",
    [string]$RmkLanguageFolderName = ("Korean (" + [char]0xD55C + [char]0xAD6D + [char]0xC5B4 + ")"),
    [ValidateSet("ApprovedOnly", "TranslatedAndApproved")]
    [string]$ApplyStatus = "ApprovedOnly",
    [switch]$Overwrite,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
try {
    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $OutputEncoding = [System.Text.UTF8Encoding]::new($false)
} catch {
}

$script:DisplayLocalizationFieldPattern = '^(label|labelshort|description|jobstring|reportstring|deathmessage|deathmessagefemale|deathmessagemale|letterlabel|lettertext|header|headertip|summary|formatstring|formatstringunfinalized|fixedname|reason|text|slateref)$'
$script:TechnicalLocalizationFieldPattern = '^(defname|parentname|classname|class|thingclass|workerclass|compclass|hediffclass|thoughtclass|abilityclass|worldobjectclass|texpath|texname|graphicpath|shader|sound|sounddef|iconpath|packageid|xpath|operation|colorchannel|rendernode|rendertree|rendertreedef|bodypart|bodypartdef|bodytype|headtype|racedef|thingdef|pawnkinddef|jobdef|statdef|skilldef|hediffdef|genedef)$'

function Resolve-FullPath([string]$Path) {
    return [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path)
}

function Assert-SafePathSegment([string]$Value, [string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -in @(".", "..") -or
        $Value.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
        $Value.Contains("\") -or $Value.Contains("/")) {
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

function Get-XmlElementChildren([System.Xml.XmlNode]$Node) {
    return @($Node.ChildNodes | Where-Object { $_.NodeType -eq [System.Xml.XmlNodeType]::Element })
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

function Get-TextFingerprint([string]$Text) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-FlatString $Text))
        return ([BitConverter]::ToString($sha.ComputeHash($bytes)).Replace("-", "")).ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}

function Get-ProtectedTokens([string]$Text) {
    $tokens = New-Object "System.Collections.Generic.HashSet[string]"
    $pattern = '(\{[^}]+\}|\[[A-Za-z0-9_.:;''" -]+\]|<[^>]+>|\$[A-Za-z_][A-Za-z0-9_]*|%[0-9.]*[sdif]|\b[A-Z]{2,}_[A-Z0-9_]+\b|\b[A-Za-z][A-Za-z0-9_]*->)'
    foreach ($match in [System.Text.RegularExpressions.Regex]::Matches($Text, $pattern)) {
        [void]$tokens.Add($match.Value)
    }
    return @($tokens)
}

function Test-InternalLocalizationIdentifierRow([object]$Row) {
    if (-not $Row -or [string]::IsNullOrWhiteSpace([string]$Row.key) -or [string]$Row.kind -ne "DefInjected") { return $false }
    $keyLower = ([string]$Row.key).Trim().ToLowerInvariant()
    $typeLower = if ($Row.PSObject.Properties["defClass"] -and $Row.defClass) { ([string]$Row.defClass).Trim().ToLowerInvariant() } else { "" }
    $fieldLower = if ($Row.PSObject.Properties["field"] -and $Row.field) { ([string]$Row.field).Trim().ToLowerInvariant() } else { ($keyLower -replace "^.*\.", "") }
    $isDisplayField = $fieldLower -match $script:DisplayLocalizationFieldPattern
    if ($fieldLower -match $script:TechnicalLocalizationFieldPattern) { return $true }
    if ($keyLower -match "\.alienrace\.generalsettings\.alienpartgenerator\.colorchannels\.") { return $true }
    if ($fieldLower -eq "name" -and $keyLower -match "\.alienrace\.") { return $true }
    if ($keyLower -match "\.(graphicpaths?|rendernodes?|rendertree)\." -and -not $isDisplayField) { return $true }
    return $typeLower -match "pawnrendertreedef" -and -not $isDisplayField
}

function Test-InvalidKoreanParticleNotation([string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }
    return $Text -match '(은\(는\)|는\(은\)|이\(가\)|가\(이\)|을\(를\)|를\(을\)|과\(와\)|와\(과\)|으로\(로\)|로\(으로\)|(?:\[[^\]\r\n]+\]|\{[^}\r\n]+\}|\$[A-Za-z_][A-Za-z0-9_]*)(?:으로|은|는|이|가|을|를|과|와|로)(?=$|[\s.,!?…:;，。！？、]))'
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

function Test-TranslationStructureSafe([object]$Row, [string]$Translation) {
    $source = ConvertTo-FlatString $Row.source
    $translationText = ConvertTo-FlatString $Translation
    if (Test-InternalLocalizationIdentifierRow $Row) { return $false }
    if ([string]::IsNullOrWhiteSpace($translationText)) { return $false }
    if (Test-InvalidKoreanParticleNotation $translationText) { return $false }
    if ($translationText -match "(\r?\n\s*){8,}" -or $translationText -match "(\\u000a\s*){8,}") { return $false }
    $newlineCount = [System.Text.RegularExpressions.Regex]::Matches($translationText, "\r?\n").Count
    if ($newlineCount -ge 20 -and $translationText.Length -lt 4000) { return $false }
    foreach ($token in (Get-ProtectedTokens $source)) {
        if (-not $translationText.Contains($token)) { return $false }
    }
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

function Get-RelativePathFromReviewTarget([string]$TargetPath, [string]$ReviewLanguageRoot) {
    if ([string]::IsNullOrWhiteSpace($TargetPath)) { return $null }
    $fullTarget = [System.IO.Path]::GetFullPath($TargetPath)
    $fullReviewRoot = [System.IO.Path]::GetFullPath($ReviewLanguageRoot).TrimEnd("\", "/")
    $prefix = $fullReviewRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullTarget.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { return $null }
    return $fullTarget.Substring($prefix.Length)
}

function Get-PathInsideRoot([string]$Root, [string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [System.IO.Path]::IsPathRooted($RelativePath)) {
        throw "Relative RMK output path is invalid: $RelativePath"
    }
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd("\", "/")
    $targetFull = [System.IO.Path]::GetFullPath((Join-Path $rootFull $RelativePath))
    $prefix = $rootFull + [System.IO.Path]::DirectorySeparatorChar
    if (-not $targetFull.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "RMK output path escapes the language root: $RelativePath"
    }
    return $targetFull
}

function Remove-InvalidXmlChars([string]$Text) {
    if ($null -eq $Text) { return "" }
    return [System.Text.RegularExpressions.Regex]::Replace($Text, "[^\u0009\u000A\u000D\u0020-\uD7FF\uE000-\uFFFD]", "")
}

function Escape-XmlText([string]$Text) {
    return [System.Security.SecurityElement]::Escape((Remove-InvalidXmlChars $Text))
}

function New-FileState([string]$Path) {
    $entries = [ordered]@{}
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $doc = Read-SafeXmlDocument $Path
        if (-not $doc.DocumentElement -or $doc.DocumentElement.LocalName -ne "LanguageData") {
            throw "RMK target XML is not LanguageData: $Path"
        }
        foreach ($child in Get-XmlElementChildren $doc.DocumentElement) {
            $entries[$child.LocalName] = ConvertTo-FlatString $child.InnerText
        }
    }
    return [pscustomobject]@{
        Path = [System.IO.Path]::GetFullPath($Path)
        Entries = $entries
        Dirty = $false
    }
}

function Write-FileState([object]$State) {
    $lines = New-Object "System.Collections.Generic.List[string]"
    [void]$lines.Add('<?xml version="1.0" encoding="utf-8"?>')
    [void]$lines.Add('<LanguageData>')
    foreach ($key in @($State.Entries.Keys | Sort-Object)) {
        if (-not (Test-ValidXmlElementName ([string]$key))) {
            throw "Refusing to write an invalid XML localization key: $key"
        }
        [void]$lines.Add("  <$key>$(Escape-XmlText ([string]$State.Entries[$key]))</$key>")
    }
    [void]$lines.Add('</LanguageData>')
    if ($DryRun) { return }
    $directory = Split-Path -Parent $State.Path
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }
    [System.IO.File]::WriteAllLines($State.Path, $lines, [System.Text.UTF8Encoding]::new($false))
}

$rmkEntryFull = Resolve-FullPath $RmkEntryRoot
$reviewFull = Resolve-FullPath $ReviewRoot
Assert-SafePathSegment -Value $ReviewLanguageFolderName -Name "ReviewLanguageFolderName"
Assert-SafePathSegment -Value $RmkLanguageFolderName -Name "RmkLanguageFolderName"
$reviewLanguageRoot = Join-Path (Join-Path $reviewFull "Languages") $ReviewLanguageFolderName
$rmkLanguageRoot = Join-Path (Join-Path $rmkEntryFull "Languages") $RmkLanguageFolderName
$auditRoot = Join-Path $reviewFull "_TranslationAudit"
$decisionPath = Join-Path $reviewFull "review-decisions.json"

if (-not (Test-Path -LiteralPath $auditRoot -PathType Container)) { throw "Review audit folder not found: $auditRoot" }
if (-not (Test-Path -LiteralPath $decisionPath -PathType Leaf)) { throw "Review decisions not found: $decisionPath" }

$comparisonFile = Get-ChildItem -LiteralPath $auditRoot -File -Filter "*-comparison.json" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $comparisonFile) { throw "Comparison JSON not found in: $auditRoot" }

$parsedRows = [System.IO.File]::ReadAllText($comparisonFile.FullName, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$rows = @($parsedRows)
$decisionData = [System.IO.File]::ReadAllText($decisionPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$rowByTargetKey = @{}
$rowById = @{}
$uniqueKeyRows = @{}
$ambiguousRowKeys = @{}
foreach ($row in $rows) {
    $relative = Get-RelativePathFromReviewTarget -TargetPath ([string]$row.target) -ReviewLanguageRoot $reviewLanguageRoot
    if ($relative -and $row.key) { $rowByTargetKey["target:$relative|key:$($row.key)"] = $row }
    if ($row.id) { $rowById["id:$($row.id)"] = $row }
    if ($row.key) {
        $plainKey = "key:$($row.key)"
        if ($ambiguousRowKeys.ContainsKey($plainKey)) { continue }
        if ($uniqueKeyRows.ContainsKey($plainKey)) {
            $uniqueKeyRows.Remove($plainKey)
            $ambiguousRowKeys[$plainKey] = $true
        } else {
            $uniqueKeyRows[$plainKey] = $row
        }
    }
}

$fileStates = @{}
$keyFiles = @{}
if (Test-Path -LiteralPath $rmkLanguageRoot -PathType Container) {
    foreach ($file in Get-ChildItem -LiteralPath $rmkLanguageRoot -Recurse -File -Filter "*.xml" -ErrorAction Stop) {
        $state = New-FileState $file.FullName
        $fileStates[$state.Path] = $state
        foreach ($key in $state.Entries.Keys) {
            if (-not $keyFiles.ContainsKey($key)) {
                $keyFiles[$key] = New-Object "System.Collections.Generic.List[string]"
            }
            [void]$keyFiles[$key].Add($state.Path)
        }
    }
}

$eligible = 0
$updatedExisting = 0
$addedNew = 0
$skippedStatus = 0
$skippedChanged = 0
$skippedUnsafe = 0
$skippedUnmapped = 0
$skippedAmbiguous = 0

foreach ($item in @($decisionData.items)) {
    $status = if ($item.status) { [string]$item.status } else { "pending" }
    if ($status -eq "reviewed") { $status = "approved" }
    if (-not (Test-StatusIncluded -Status $status -Mode $ApplyStatus)) {
        $skippedStatus++
        continue
    }

    $row = $null
    if ($item.target -and $item.key) {
        $targetKey = "target:$($item.target)|key:$($item.key)"
        if ($rowByTargetKey.ContainsKey($targetKey)) { $row = $rowByTargetKey[$targetKey] }
    }
    if (-not $row -and $item.id -and $rowById.ContainsKey("id:$($item.id)")) {
        $row = $rowById["id:$($item.id)"]
    }
    if (-not $row -and $item.key -and $uniqueKeyRows.ContainsKey("key:$($item.key)")) {
        $row = $uniqueKeyRows["key:$($item.key)"]
    }
    if (-not $row -or -not $row.key -or -not (Test-ValidXmlElementName ([string]$row.key))) {
        $skippedUnmapped++
        continue
    }

    $sourceChanged = ConvertTo-BoolValue $item.sourceChanged
    if (-not $sourceChanged -and $item.sourceHash) {
        $sourceChanged = ([string]$item.sourceHash) -ne (Get-TextFingerprint (ConvertTo-FlatString $row.source))
    }
    if ($sourceChanged) {
        $skippedChanged++
        continue
    }

    $translation = ConvertTo-FlatString $item.text
    if (-not (Test-TranslationSafe -Row $row -Translation $translation)) {
        $skippedUnsafe++
        continue
    }
    $eligible++

    $targetPath = ""
    $key = [string]$row.key
    if ($keyFiles.ContainsKey($key)) {
        $paths = @($keyFiles[$key])
        if ($paths.Count -gt 1) {
            Write-Warning "Skipping duplicated RMK key '$key' found in $($paths.Count) files."
            $skippedAmbiguous++
            continue
        }
        if (-not $Overwrite) {
            $skippedUnmapped++
            continue
        }
        $targetPath = [string]$paths[0]
        $updatedExisting++
    } else {
        $relative = Get-RelativePathFromReviewTarget -TargetPath ([string]$row.target) -ReviewLanguageRoot $reviewLanguageRoot
        if (-not $relative) {
            $skippedUnmapped++
            continue
        }
        $targetPath = Get-PathInsideRoot -Root $rmkLanguageRoot -RelativePath $relative
        $addedNew++
    }

    if (-not $fileStates.ContainsKey($targetPath)) {
        $fileStates[$targetPath] = New-FileState $targetPath
    }
    $state = $fileStates[$targetPath]
    $state.Entries[$key] = $translation
    $state.Dirty = $true
}

$writtenFiles = 0
foreach ($state in @($fileStates.Values | Where-Object { $_.Dirty } | Sort-Object Path)) {
    Write-FileState $state
    $writtenFiles++
}

Write-Host "RMK entry root: $rmkEntryFull"
Write-Host "RMK language root: $rmkLanguageRoot"
Write-Host "Apply status: $ApplyStatus"
Write-Host "Eligible entries: $eligible"
Write-Host "Updated existing keys: $updatedExisting"
Write-Host "Added new keys: $addedNew"
Write-Host "Written files: $writtenFiles"
Write-Host "Skipped status: $skippedStatus"
Write-Host "Skipped source-changed: $skippedChanged"
Write-Host "Skipped unsafe: $skippedUnsafe"
Write-Host "Skipped unmapped: $skippedUnmapped"
Write-Host "Skipped duplicate RMK keys: $skippedAmbiguous"
if ($DryRun) { Write-Host "Dry run: no RMK files were written." }
