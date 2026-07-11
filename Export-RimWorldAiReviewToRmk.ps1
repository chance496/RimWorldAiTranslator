param(
    [Parameter(Mandatory = $true)]
    [string]$RmkEntryRoot,

    [Parameter(Mandatory = $true)]
    [string]$ReviewRoot,

    [string]$ReviewLanguageFolderName = "Korean",
    [string]$RmkLanguageFolderName = ("Korean (" + [char]0xD55C + [char]0xAD6D + [char]0xC5B4 + ")"),
    [string]$SourceLanguage = "English",
    [string]$WorkbookPath,
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

function Test-PathInsideRoot([string]$Path, [string]$Root) {
    try {
        $pathFull = [System.IO.Path]::GetFullPath($Path)
        $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd("\", "/")
        $prefix = $rootFull + [System.IO.Path]::DirectorySeparatorChar
        return $pathFull.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
    } catch {
        return $false
    }
}

function Initialize-RmkXlsxSupport {
    if (("RimWorldTranslatorRmkXlsxReader" -as [type]) -and ("RimWorldTranslatorRmkXlsxWriter" -as [type])) { return }
    $assemblyPath = Join-Path $PSScriptRoot "RimWorldAiTranslator.Native.dll"
    $sourcePath = Join-Path $PSScriptRoot "native\RimWorldTranslatorNative.cs"
    $useSource = (Test-Path -LiteralPath $sourcePath -PathType Leaf) -and
        ((-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) -or
         (Get-Item -LiteralPath $sourcePath).LastWriteTimeUtc -gt (Get-Item -LiteralPath $assemblyPath).LastWriteTimeUtc)
    if ($useSource) {
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        Add-Type -AssemblyName System.Xml.Linq
        $references = @(
            [System.IO.Compression.ZipArchive].Assembly.Location,
            [System.IO.Compression.ZipFile].Assembly.Location,
            [System.Xml.Linq.XDocument].Assembly.Location,
            [System.Xml.XmlDocument].Assembly.Location,
            [System.Linq.Enumerable].Assembly.Location
        ) | Select-Object -Unique
        Add-Type -LiteralPath $sourcePath -ReferencedAssemblies $references -ErrorAction Stop
    } elseif (Test-Path -LiteralPath $assemblyPath -PathType Leaf) {
        Add-Type -LiteralPath $assemblyPath -ErrorAction Stop
    } else {
        throw "RMK XLSX support is missing. Reinstall the package."
    }
    if (-not ("RimWorldTranslatorRmkXlsxReader" -as [type]) -or -not ("RimWorldTranslatorRmkXlsxWriter" -as [type])) {
        throw "RMK XLSX support failed to load."
    }
}

function Get-RmkWorkbookOutputPath([string]$RequestedPath, [string]$EntryRoot) {
    $entryFull = [System.IO.Path]::GetFullPath($EntryRoot).TrimEnd("\", "/")
    $candidate = ""
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $candidate = if ([System.IO.Path]::IsPathRooted($RequestedPath)) { $RequestedPath } else { Join-Path $entryFull $RequestedPath }
    } else {
        $existing = Get-ChildItem -LiteralPath $entryFull -File -Filter "*.xlsx" -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -First 1
        if ($existing) {
            $candidate = $existing.FullName
        } else {
            $workshopId = ""
            $cursor = $entryFull
            for ($depth = 0; $depth -lt 3 -and $cursor; $depth++) {
                $leaf = Split-Path -Leaf $cursor
                if ($leaf -match ' - (\d+)$') { $workshopId = $matches[1]; break }
                $parent = Split-Path -Parent $cursor
                if ($parent -eq $cursor) { break }
                $cursor = $parent
            }
            $fileName = if ($workshopId) { "$workshopId.xlsx" } else { "RimWorldTranslation.xlsx" }
            $candidate = Join-Path $entryFull $fileName
        }
    }
    $full = [System.IO.Path]::GetFullPath($candidate)
    if (-not (Test-PathInsideRoot -Path $full -Root $entryFull) -or [System.IO.Path]::GetExtension($full) -ine ".xlsx") {
        throw "RMK workbook path must be an XLSX file inside the RMK entry root: $full"
    }
    return $full
}

function Get-RmkHistoryIdentifier([object]$Row) {
    if (-not $Row) { return "" }
    $kind = if ($Row.PSObject.Properties["kind"]) { [string]$Row.kind } else { "" }
    $className = if ($kind -eq "Keyed") { "Keyed" } elseif ($Row.PSObject.Properties["defClass"] -and $Row.defClass) { [string]$Row.defClass } else { $kind }
    $node = if ($Row.PSObject.Properties["node"] -and $Row.node) { [string]$Row.node } else { [string]$Row.key }
    if ([string]::IsNullOrWhiteSpace($className) -or [string]::IsNullOrWhiteSpace($node)) { return "" }
    return "$className+$node"
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
    $temporaryPath = Join-Path $directory (".{0}.{1}.tmp" -f [System.IO.Path]::GetFileName([string]$State.Path), [System.Guid]::NewGuid().ToString("N"))
    try {
        $text = [string]::Join([Environment]::NewLine, @($lines)) + [Environment]::NewLine
        $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($text)
        $stream = [System.IO.FileStream]::new($temporaryPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        } finally {
            $stream.Dispose()
        }
        if (Test-Path -LiteralPath $State.Path -PathType Leaf) {
            [System.IO.File]::Replace($temporaryPath, [string]$State.Path, "$($State.Path).bak", $true)
        } else {
            [System.IO.File]::Move($temporaryPath, [string]$State.Path)
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

function New-TransactionFileEntry([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $existed = Test-Path -LiteralPath $fullPath -PathType Leaf
    $snapshotPath = ""
    if ($existed) {
        $directory = [System.IO.Path]::GetDirectoryName($fullPath)
        $snapshotPath = Join-Path $directory (".{0}.{1}.transaction.bak" -f [System.IO.Path]::GetFileName($fullPath), [System.Guid]::NewGuid().ToString("N"))
        Copy-FileFlushed -SourcePath $fullPath -DestinationPath $snapshotPath
    }
    return [pscustomobject]@{ Path = $fullPath; Existed = $existed; SnapshotPath = $snapshotPath }
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

$rmkEntryFull = Resolve-FullPath $RmkEntryRoot
$reviewFull = Resolve-FullPath $ReviewRoot
Assert-SafePathSegment -Value $ReviewLanguageFolderName -Name "ReviewLanguageFolderName"
Assert-SafePathSegment -Value $RmkLanguageFolderName -Name "RmkLanguageFolderName"
$reviewLanguageRoot = Join-Path (Join-Path $reviewFull "Languages") $ReviewLanguageFolderName
$rmkLanguageRoot = Join-Path (Join-Path $rmkEntryFull "Languages") $RmkLanguageFolderName
$auditRoot = Join-Path $reviewFull "_TranslationAudit"
$decisionPath = Join-Path $reviewFull "review-decisions.json"

if (-not (Test-Path -LiteralPath $auditRoot -PathType Container)) { throw "Review audit folder not found: $auditRoot" }
if (-not (Test-JsonWithBackupExists $decisionPath)) { throw "Review decisions not found: $decisionPath" }

$comparisonFile = Get-ChildItem -LiteralPath $auditRoot -File -Filter "*-comparison.json" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $comparisonFile) { throw "Comparison JSON not found in: $auditRoot" }

$parsedRows = [System.IO.File]::ReadAllText($comparisonFile.FullName, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$rows = @($parsedRows | Where-Object { -not (Test-InternalLocalizationIdentifierRow $_) })
$decisionData = Read-JsonWithBackup $decisionPath
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

function Resolve-ReviewDecisionRow([object]$Item) {
    if (-not $Item) { return $null }
    if ($Item.target -and $Item.key) {
        $targetKey = "target:$($Item.target)|key:$($Item.key)"
        if ($rowByTargetKey.ContainsKey($targetKey)) { return $rowByTargetKey[$targetKey] }
    }
    if ($Item.id -and $rowById.ContainsKey("id:$($Item.id)")) { return $rowById["id:$($Item.id)"] }
    if ($Item.key -and $uniqueKeyRows.ContainsKey("key:$($Item.key)")) { return $uniqueKeyRows["key:$($Item.key)"] }
    return $null
}

$decisionByHistoryIdentifier = @{}
foreach ($item in @($decisionData.items)) {
    $row = Resolve-ReviewDecisionRow $item
    $identifier = Get-RmkHistoryIdentifier $row
    if ($identifier -and -not $decisionByHistoryIdentifier.ContainsKey($identifier)) {
        $decisionByHistoryIdentifier[$identifier] = $item
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
$exportedTranslationByIdentifier = @{}

foreach ($item in @($decisionData.items)) {
    $status = if ($item.status) { [string]$item.status } else { "pending" }
    if ($status -eq "reviewed") { $status = "approved" }
    if (-not (Test-StatusIncluded -Status $status -Mode $ApplyStatus)) {
        $skippedStatus++
        continue
    }

    $row = Resolve-ReviewDecisionRow $item
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
    $historyIdentifier = Get-RmkHistoryIdentifier $row
    if ($historyIdentifier) { $exportedTranslationByIdentifier[$historyIdentifier] = $translation }
}

Initialize-RmkXlsxSupport
$workbookFull = Get-RmkWorkbookOutputPath -RequestedPath $WorkbookPath -EntryRoot $rmkEntryFull
$workbookExists = Test-Path -LiteralPath $workbookFull -PathType Leaf
$workbookData = if ($workbookExists) { [RimWorldTranslatorRmkXlsxReader]::Read($workbookFull) } else { $null }
$effectiveSourceLanguage = if ($workbookData -and $workbookData.SourceLanguage) {
    [string]$workbookData.SourceLanguage
} elseif ($SourceLanguage -and $SourceLanguage -ne "Auto") {
    $SourceLanguage
} else {
    "English"
}
$selectedSourceLanguage = if ($SourceLanguage) { $SourceLanguage } else { "" }
$workbookUsesDifferentSourceLanguage = $workbookData -and $workbookData.SourceLanguage -and $selectedSourceLanguage -and
    -not [string]::Equals([string]$workbookData.SourceLanguage, $selectedSourceLanguage, [System.StringComparison]::OrdinalIgnoreCase)
$historyRowsByIdentifier = @{}
$historyOrder = New-Object "System.Collections.Generic.List[string]"
if ($workbookData) {
    foreach ($historyRow in $workbookData.Rows) {
        $identifier = [string]$historyRow.Identifier
        if ([string]::IsNullOrWhiteSpace($identifier) -or $historyRowsByIdentifier.ContainsKey($identifier)) { continue }
        $copy = New-Object RimWorldTranslatorRmkHistoryRow
        $copy.Identifier = $identifier
        $copy.ClassName = [string]$historyRow.ClassName
        $copy.Key = [string]$historyRow.Key
        $copy.RequiredMods = [string]$historyRow.RequiredMods
        $copy.Source = ConvertTo-FlatString $historyRow.Source
        $copy.Translation = ConvertTo-FlatString $historyRow.Translation
        $historyRowsByIdentifier[$identifier] = $copy
        [void]$historyOrder.Add($identifier)
    }
}

foreach ($row in $rows) {
    $identifier = Get-RmkHistoryIdentifier $row
    if (-not $identifier) { continue }
    $kind = if ($row.PSObject.Properties["kind"]) { [string]$row.kind } else { "" }
    $className = if ($kind -eq "Keyed") { "Keyed" } elseif ($row.PSObject.Properties["defClass"] -and $row.defClass) { [string]$row.defClass } else { $kind }
    $node = if ($row.PSObject.Properties["node"] -and $row.node) { [string]$row.node } else { [string]$row.key }
    $currentSource = ConvertTo-FlatString $row.source
    $referenceCurrentSource = if ($row.PSObject.Properties["rmkCurrentSource"]) { ConvertTo-FlatString $row.rmkCurrentSource } else { "" }
    $workbookCurrentSource = if ($referenceCurrentSource) {
        $referenceCurrentSource
    } elseif (-not $workbookUsesDifferentSourceLanguage) {
        $currentSource
    } else {
        ""
    }
    $requiredMods = if ($row.PSObject.Properties["requiredMods"]) { [string]$row.requiredMods } else { "" }
    $decision = if ($decisionByHistoryIdentifier.ContainsKey($identifier)) { $decisionByHistoryIdentifier[$identifier] } else { $null }
    $sourceChanged = $row.PSObject.Properties["rmkSourceChanged"] -and (ConvertTo-BoolValue $row.rmkSourceChanged)
    if ($decision) {
        $sourceChanged = $sourceChanged -or ($decision.PSObject.Properties["sourceChanged"] -and (ConvertTo-BoolValue $decision.sourceChanged))
        if (-not $sourceChanged -and $decision.PSObject.Properties["sourceHash"] -and $decision.sourceHash) {
            $sourceChanged = ([string]$decision.sourceHash) -ne (Get-TextFingerprint $currentSource)
        }
    }

    if ($historyRowsByIdentifier.ContainsKey($identifier)) {
        $historyRow = $historyRowsByIdentifier[$identifier]
        if (-not $sourceChanged -and $workbookCurrentSource) { $historyRow.Source = $workbookCurrentSource }
        if ([string]::IsNullOrWhiteSpace([string]$historyRow.ClassName)) { $historyRow.ClassName = $className }
        if ([string]::IsNullOrWhiteSpace([string]$historyRow.Key)) { $historyRow.Key = $node }
        if ([string]::IsNullOrWhiteSpace([string]$historyRow.RequiredMods) -and $requiredMods) { $historyRow.RequiredMods = $requiredMods }
    } else {
        if (-not $workbookCurrentSource -and $workbookUsesDifferentSourceLanguage) { continue }
        $historicalSource = ""
        if ($sourceChanged -and $decision -and $decision.PSObject.Properties["previousSourceText"]) {
            $historicalSource = ConvertTo-FlatString $decision.previousSourceText
        }
        if (-not $historicalSource -and $sourceChanged -and $row.PSObject.Properties["rmkHistoricalSource"]) {
            $historicalSource = ConvertTo-FlatString $row.rmkHistoricalSource
        }
        $historyRow = New-Object RimWorldTranslatorRmkHistoryRow
        $historyRow.Identifier = $identifier
        $historyRow.ClassName = $className
        $historyRow.Key = $node
        $historyRow.RequiredMods = $requiredMods
        $historyRow.Source = if ($historicalSource) { $historicalSource } else { $workbookCurrentSource }
        $existingOrigin = if ($row.PSObject.Properties["existingOrigin"]) { [string]$row.existingOrigin } else { "" }
        $historyRow.Translation = if ($existingOrigin -eq "rmk") { ConvertTo-FlatString $row.existing } else { "" }
        $historyRowsByIdentifier[$identifier] = $historyRow
        [void]$historyOrder.Add($identifier)
    }
    if ($exportedTranslationByIdentifier.ContainsKey($identifier)) {
        $historyRow.Translation = ConvertTo-FlatString $exportedTranslationByIdentifier[$identifier]
        if ($workbookCurrentSource) { $historyRow.Source = $workbookCurrentSource }
    }
}

$historyRows = New-Object "System.Collections.Generic.List[RimWorldTranslatorRmkHistoryRow]"
foreach ($identifier in $historyOrder) {
    if ($historyRowsByIdentifier.ContainsKey($identifier)) { [void]$historyRows.Add($historyRowsByIdentifier[$identifier]) }
}
$dirtyStates = @($fileStates.Values | Where-Object { $_.Dirty } | Sort-Object Path)
$writtenFiles = 0
$writeJournal = New-Object "System.Collections.Generic.List[object]"
try {
    if (-not $DryRun) {
        [void]$writeJournal.Add((New-TransactionFileEntry $workbookFull))
        foreach ($state in $dirtyStates) {
            [void]$writeJournal.Add((New-TransactionFileEntry ([string]$state.Path)))
        }
        [RimWorldTranslatorRmkXlsxWriter]::Write($workbookFull, $historyRows, $effectiveSourceLanguage)
    }
    foreach ($state in $dirtyStates) {
        Write-FileState $state
        $writtenFiles++
    }
} catch {
    $exportError = $_.Exception
    $rollbackErrors = New-Object "System.Collections.Generic.List[string]"
    for ($index = $writeJournal.Count - 1; $index -ge 0; $index--) {
        try {
            Restore-TransactionFile $writeJournal[$index]
        } catch {
            [void]$rollbackErrors.Add("$($writeJournal[$index].Path): $($_.Exception.Message)")
        }
    }
    if ($rollbackErrors.Count -gt 0) {
        throw "RMK export failed and rollback was incomplete. Export error: $($exportError.Message) Rollback errors: $([string]::Join(' | ', $rollbackErrors))"
    }
    throw "RMK export failed; the workbook and XML files written by this run were rolled back. $($exportError.Message)"
} finally {
    foreach ($entry in $writeJournal) {
        if ($entry.SnapshotPath -and (Test-Path -LiteralPath ([string]$entry.SnapshotPath) -PathType Leaf)) {
            Remove-Item -LiteralPath ([string]$entry.SnapshotPath) -Force -ErrorAction SilentlyContinue
        }
    }
}

Write-Host "RMK entry root: $rmkEntryFull"
Write-Host "RMK language root: $rmkLanguageRoot"
Write-Host "RMK workbook: $workbookFull"
Write-Host "RMK workbook rows: $($historyRows.Count)"
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
