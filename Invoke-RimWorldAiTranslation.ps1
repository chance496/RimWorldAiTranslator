param(
    [Parameter(Mandatory = $true)]
    [string]$ModRoot,

    [string[]]$ApiKey = @(),

    [string]$BaseUrl = "https://api.cerebras.ai/v1/chat/completions",
    [string]$Model = "gemma-4-31b",
    [ValidateSet("Auto", "Cerebras", "OpenAICompatible", "Google")]
    [string]$TranslationProvider = "Auto",
    [string]$ProviderName = "Cerebras",
    [string]$GoogleTranslateUrl = "https://translate.googleapis.com/translate_a/single",
    [string]$LanguageFolderName = "Korean",
    [string]$SourceLanguageFolder = "Auto",

    [int]$RequestsPerMinutePerKey = 5,
    [int]$InputTokensPerMinutePerKey = 30000,
    [int]$DailyTokenBudgetPerKey = 1000000,
    [int]$BatchSize = 40,
    [int]$MaxInputCharsPerBatch = 12000,
    [int]$MaxInputTokensPerBatch = 5500,
    [int]$MaxCompletionTokens = 32000,
    [ValidateSet("max_completion_tokens", "max_tokens", "none")]
    [string]$CompletionTokenParameter = "max_completion_tokens",
    [ValidateSet("JsonSchema", "JsonObject", "PromptOnly")]
    [string]$ResponseFormatMode = "JsonSchema",
    [ValidateRange(-1, 2)]
    [double]$Temperature = 0.1,
    [ValidatePattern("^(|none|minimal|low|medium|high|xhigh|max)$")]
    [string]$ReasoningEffort = "",
    [int]$TimeoutSec = 180,
    [int]$MaxRetries = 4,
    [int]$Limit = 0,

    [switch]$IncludePatches,
    [switch]$Overwrite,
    [switch]$DryRun,
    [switch]$MockTranslations,
    [switch]$SourceOnly,
    [switch]$NoStructuredOutputs,
    [switch]$AllowInsecureLoopback,
    [switch]$ReviewOnly,

    [string]$ExistingLanguageRoot,
    [string[]]$ReferenceLanguageRoot = @(),
    [string]$ReferenceSourceWorkbook,
    [switch]$TranslateMissingOnly,
    [string]$PreserveTranslationFile,
    [string]$ReviewRoot,
    [string]$GeneratedGlossaryPath,
    [string]$CuratedGlossaryPath,
    [int]$MaxAlwaysGlossaryTerms = 180,
    [int]$MaxGeneratedGlossaryTermsPerBatch = 140,
    [switch]$UseCuratedGlossary,
    [string]$ExtraPrompt,
    [string]$ExtraPromptFile,

    [string]$CancellationFile,

    [string]$OutputFilePrefix = "CodexAI"
)

$ErrorActionPreference = "Stop"
$validationScriptPath = Join-Path $PSScriptRoot "RimWorldAiTranslator.Validation.ps1"
if (-not (Test-Path -LiteralPath $validationScriptPath -PathType Leaf)) { throw "Translation validation component was not found: $validationScriptPath" }
. $validationScriptPath
try {
    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $OutputEncoding = [System.Text.UTF8Encoding]::new($false)
} catch {
}

$script:RequestCompletionTokenParameter = $CompletionTokenParameter
$script:RequestTemperature = $Temperature
$script:RequestReasoningEffort = $ReasoningEffort
$script:RequestTimeoutSec = $TimeoutSec
$script:RequestMaxRetries = $MaxRetries
$script:DisableStructuredOutputs = [bool]$NoStructuredOutputs

$script:SkippedInternalLocalizationEntries = New-Object "System.Collections.Generic.List[object]"
$script:RmkWorkbookSourceLanguage = ""
$script:ActiveModContentRootsCache = @{}
$script:DisplayLocalizationFieldPattern = '^(label|labelshort|description|jobstring|reportstring|deathmessage|deathmessagefemale|deathmessagemale|letterlabel|lettertext|header|headertip|summary|formatstring|formatstringunfinalized|fixedname|reason|text|slateref)$'
$script:TechnicalLocalizationFieldPattern = '^(defname|parentname|classname|class|thingclass|workerclass|compclass|hediffclass|thoughtclass|abilityclass|worldobjectclass|nodeclass|debuglabel|tagdef|texpath|texname|graphicpath|shader|sound|sounddef|iconpath|packageid|xpath|operation|colorchannel|rendernode|rendertree|rendertreedef|bodypart|bodypartdef|bodytype|headtype|racedef|thingdef|pawnkinddef|jobdef|statdef|skilldef|hediffdef|genedef)$'
$script:DefFieldRulesInitialized = $false
$script:TranslatableDefFields = $null
$script:ExcludedDefSegments = $null
$script:CancellationFileFull = ""

function Resolve-FullPath([string]$Path) {
    return [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path)
}

function Get-FullPathAllowMissing([string]$Path) {
    return [System.IO.Path]::GetFullPath($Path)
}

function Test-TranslationCancellationRequested {
    return $script:CancellationFileFull -and (Test-Path -LiteralPath $script:CancellationFileFull -PathType Leaf)
}

function Assert-TranslationNotCancelled {
    if (Test-TranslationCancellationRequested) {
        throw [System.OperationCanceledException]::new("Translation was cancelled by the user. Completed batches remain in the review checkpoint.")
    }
}

function Wait-TranslationDelay([int]$Milliseconds) {
    $remaining = [Math]::Max(0, $Milliseconds)
    while ($remaining -gt 0) {
        Assert-TranslationNotCancelled
        $slice = [Math]::Min(200, $remaining)
        [System.Threading.Thread]::Sleep($slice)
        $remaining -= $slice
    }
    Assert-TranslationNotCancelled
}

function Assert-SafePathSegment([string]$Value, [string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -in @(".", "..") -or $Value.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0 -or $Value.Contains("\") -or $Value.Contains("/")) {
        throw "$Name must be a single safe folder or file-name segment."
    }
}

function Assert-HttpsUri([string]$Value, [string]$Name) {
    $uri = $null
    if (-not [System.Uri]::TryCreate($Value, [System.UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne [System.Uri]::UriSchemeHttps) {
        throw "$Name must be an absolute HTTPS URL."
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

function Get-PathInsideRoot([string]$Root, [string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [System.IO.Path]::IsPathRooted($RelativePath)) {
        throw "Relative output path is invalid: $RelativePath"
    }
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd("\", "/")
    $targetFull = [System.IO.Path]::GetFullPath((Join-Path $rootFull $RelativePath))
    $prefix = $rootFull + [System.IO.Path]::DirectorySeparatorChar
    if (-not $targetFull.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Output path escapes the language root: $RelativePath"
    }
    return $targetFull
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

function Get-ChatCompletionsUrl([string]$Url) {
    $trimmed = $Url.Trim().TrimEnd("/")
    if ($trimmed -match "/chat/completions$") { return $trimmed }
    if ($trimmed -match "/(v1|v1beta/openai|compatible-mode/v1|api/v1|paas/v4)$") { return "$trimmed/chat/completions" }
    return "$trimmed/v1/chat/completions"
}

function Initialize-RmkXlsxReader {
    if ("RimWorldTranslatorRmkXlsxReader" -as [type]) { return }

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
        throw "Native XML reader is missing. Reinstall the package: $assemblyPath"
    }

    if (-not ("RimWorldTranslatorRmkXlsxReader" -as [type])) {
        throw "Native XML reader failed to load: $assemblyPath"
    }
    Initialize-DefFieldRules
}

function Assert-ApiUri([string]$Value, [string]$Name, [switch]$AllowLoopbackHttp) {
    $uri = $null
    if (-not [System.Uri]::TryCreate($Value, [System.UriKind]::Absolute, [ref]$uri)) {
        throw "$Name must be an absolute URL."
    }
    if ($uri.Scheme -eq [System.Uri]::UriSchemeHttps) { return }
    if ($AllowLoopbackHttp -and $uri.Scheme -eq [System.Uri]::UriSchemeHttp -and $uri.IsLoopback) { return }
    throw "$Name must be an absolute HTTPS URL. Plain HTTP is allowed only for an explicitly enabled loopback test endpoint."
}

function Get-RmkWorkbookHistoryMap([string]$WorkbookPath) {
    $script:RmkWorkbookSourceLanguage = ""
    if ([string]::IsNullOrWhiteSpace($WorkbookPath)) { return @{} }
    if (-not (Test-Path -LiteralPath $WorkbookPath -PathType Leaf)) { throw "RMK source workbook not found: $WorkbookPath" }
    $workbookInfo = Get-Item -LiteralPath $WorkbookPath -ErrorAction Stop
    if ($workbookInfo.Extension -ine ".xlsx") { throw "RMK source workbook must be an .xlsx file." }
    if ($workbookInfo.Length -gt 268435456) { throw "RMK source workbook is too large: $WorkbookPath" }
    Initialize-RmkXlsxReader
    $data = [RimWorldTranslatorRmkXlsxReader]::Read($workbookInfo.FullName)
    $script:RmkWorkbookSourceLanguage = [string]$data.SourceLanguage
    return ,$data.Map
}

function Get-RmkEntryIdentifier([object]$Entry) {
    $className = if ([string]$Entry.Kind -eq "Keyed") { "Keyed" } else { [string]$Entry.TypeName }
    if (-not $className -or -not $Entry.Key) { return "" }
    return "$className+$([string]$Entry.Key)"
}

function Get-LocalizationIdentity([string]$Namespace, [string]$Key) {
    $namespaceText = ([string]$Namespace).Trim()
    $keyText = ([string]$Key).Trim()
    if (-not $namespaceText -or -not $keyText) { return "" }
    return "namespace:$namespaceText|key:$keyText"
}

function Get-EntryLocalizationIdentity([object]$Entry) {
    if (-not $Entry) { return "" }
    $namespace = if ([string]$Entry.Kind -eq "Keyed") { "Keyed" } else { [string]$Entry.TypeName }
    return Get-LocalizationIdentity -Namespace $namespace -Key ([string]$Entry.Key)
}

function Get-LocalizationNamespaceFromRelativePath([string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath)) { return "" }
    $parts = @($RelativePath.Replace('/', '\').Split(@('\'), [System.StringSplitOptions]::RemoveEmptyEntries))
    if ($parts.Count -eq 0) { return "" }
    if ($parts[0] -ieq "Keyed") { return "Keyed" }
    if ($parts[0] -ieq "DefInjected" -and $parts.Count -ge 2) { return [string]$parts[1] }
    return ""
}

function Test-SourceTextEqual([string]$Left, [string]$Right) {
    $leftText = ((ConvertTo-FlatString $Left) -replace "`r`n", "`n" -replace "`r", "`n").Trim()
    $rightText = ((ConvertTo-FlatString $Right) -replace "`r`n", "`n" -replace "`r", "`n").Trim()
    $leftText = [System.Text.RegularExpressions.Regex]::Replace($leftText, '[ \t\u00A0]+(?=\n|$)', '')
    $rightText = [System.Text.RegularExpressions.Regex]::Replace($rightText, '[ \t\u00A0]+(?=\n|$)', '')
    return [string]::Equals($leftText, $rightText, [System.StringComparison]::Ordinal)
}

function Get-ComparisonReferenceInfo(
    [object]$Entry,
    [object]$ExistingInfo,
    [object]$RmkHistoryMap,
    [hashtable]$RmkCurrentSourceMap,
    [string]$RmkWorkbook
) {
    $origin = if ($ExistingInfo) { [string]$ExistingInfo.Origin } else { "" }
    $translationUpdatedAt = if ($ExistingInfo) { [string]$ExistingInfo.TranslationUpdatedAt } else { "" }
    $identifier = Get-RmkEntryIdentifier $Entry
    $history = if ($identifier -and $RmkHistoryMap.ContainsKey($identifier)) { $RmkHistoryMap[$identifier] } else { $null }
    $historicalSource = if ($history) { ConvertTo-FlatString $history.Source } else { "" }
    $currentReferenceSource = if ($identifier -and $RmkCurrentSourceMap.ContainsKey($identifier)) { ConvertTo-FlatString $RmkCurrentSourceMap[$identifier] } else { "" }
    $sourceChanged = $false
    if ($history -and -not [string]::IsNullOrWhiteSpace($historicalSource) -and -not [string]::IsNullOrWhiteSpace($currentReferenceSource)) {
        $sourceChanged = -not (Test-SourceTextEqual -Left $currentReferenceSource -Right $historicalSource)
    }
    return [pscustomobject]@{
        ExistingOrigin = $origin
        TranslationUpdatedAt = $translationUpdatedAt
        RmkIdentifier = $identifier
        RmkHistoricalSource = $historicalSource
        RmkCurrentSource = $currentReferenceSource
        RmkSourceChanged = $sourceChanged
        RmkWorkbook = $RmkWorkbook
    }
}

function ConvertTo-FlatString([object]$Value) {
    if ($null -eq $Value) { return "" }
    return ([string]$Value).Replace("`r`n", "`n").Replace("`r", "`n")
}

function Estimate-TokenCount([string]$Text) {
    if ([string]::IsNullOrEmpty($Text)) { return 0 }
    return [int][Math]::Ceiling($Text.Length / 3.0)
}

function Test-HasHumanText([string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }
    if ($Text -match "\p{L}") { return $true }
    return $false
}

function Test-LooksLikeCodeOrPath([string]$Text, [string]$Field = "") {
    $trim = $Text.Trim()
    if ($trim -match "^[\d\s.,:+\-/%\u00B0]+$") { return $true }
    if ($trim -match "^[A-Za-z0-9_./\\:-]+\.(png|jpg|jpeg|dds|tga|wav|ogg|mp3|dll|asset|shader|xml)$") { return $true }
    if ($trim -match "^[A-Za-z0-9_./\\:-]+/[A-Za-z0-9_./\\:-]+$") { return $true }
    if ($trim -match "^\{[A-Za-z0-9_:.-]+\}$") { return $true }
    if ($trim -match "^[A-Za-z_][A-Za-z0-9_.]*\.[A-Za-z_][A-Za-z0-9_.]*$") {
        $acronymSegments = @($trim.Split('.') | Where-Object { $_ -cmatch '^[A-Z]$' }).Count
        if ($acronymSegments -ge 2) { return $false }
        if (-not [string]::IsNullOrWhiteSpace($Field)) {
            Initialize-DefFieldRules
            if ($script:TranslatableDefFields.Contains($Field)) { return $false }
        }
        return $true
    }
    return $false
}

function Get-InternalLocalizationIdentifierReason([string]$Key, [string]$Kind, [string]$TypeName, [string]$Field) {
    if ([string]::IsNullOrWhiteSpace($Key) -or $Kind -ne "DefInjected") { return "" }

    $keyLower = $Key.Trim().ToLowerInvariant()
    $typeLower = if ($TypeName) { $TypeName.Trim().ToLowerInvariant() } else { "" }
    $fieldLower = if ($Field) { $Field.Trim().ToLowerInvariant() } else { ($keyLower -replace "^.*\.", "") }
    Initialize-DefFieldRules
    $isDisplayField = $fieldLower -match $script:DisplayLocalizationFieldPattern
    if ($script:ExcludedDefSegments.Contains($fieldLower)) {
        return "RimWorld NoTranslate field '$fieldLower'"
    }
    if ($fieldLower -match $script:TechnicalLocalizationFieldPattern) {
        return "internal reference field '$fieldLower'"
    }
    if ($keyLower -match "\.alienrace\.generalsettings\.alienpartgenerator\.colorchannels\.") {
        return "AlienRace color-channel identifier"
    }
    if ($fieldLower -eq "name" -and $keyLower -match "\.alienrace\.") {
        return "AlienRace internal name"
    }
    if ($fieldLower -eq "name" -and $keyLower -match "\.(colorchannels|bodyaddons|powermodes)\.") {
        return "runtime list identifier"
    }
    if ($keyLower -match "\.(graphicpaths?|rendernodes?|rendertree)\." -and -not $isDisplayField) {
        return "rendering or graphic-path identifier"
    }
    if ($typeLower -match "pawnrendertreedef" -and -not $isDisplayField) {
        return "PawnRenderTreeDef internal identifier"
    }
    return ""
}

function Get-XmlElementChildren([System.Xml.XmlNode]$Node) {
    $children = New-Object "System.Collections.Generic.List[System.Xml.XmlNode]"
    foreach ($child in $Node.ChildNodes) {
        if ($child.NodeType -eq [System.Xml.XmlNodeType]::Element) {
            [void]$children.Add($child)
        }
    }
    return ,$children
}

function Get-DirectChildText([System.Xml.XmlNode]$Node, [string]$Name) {
    foreach ($child in (Get-XmlElementChildren $Node)) {
        if ($child.LocalName -eq $Name) {
            return $child.InnerText.Trim()
        }
    }
    return $null
}

function ConvertTo-KeySegment([string]$Text) {
    $candidate = $Text.Trim()
    if ($candidate -match "^[A-Za-z_][A-Za-z0-9_.-]*$") { return $candidate }
    return $null
}

function Get-ListItemSegment([System.Xml.XmlNode]$Node, [int]$Index) {
    foreach ($name in @("id", "defName", "key", "name")) {
        $value = Get-DirectChildText -Node $Node -Name $name
        if ($value) {
            $segment = ConvertTo-KeySegment $value
            if ($segment) { return $segment }
        }
    }
    return [string]$Index
}

function Get-ExistingLanguageKeys([string]$LanguageRoot) {
    $keys = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    if (-not (Test-Path -LiteralPath $LanguageRoot)) { return ,$keys }

    Get-ChildItem -LiteralPath $LanguageRoot -Recurse -File -Filter *.xml -ErrorAction SilentlyContinue | Sort-Object FullName | ForEach-Object {
        try {
            Initialize-RmkXlsxReader
            foreach ($raw in [RimWorldTranslatorRmkXlsxReader]::ReadLanguageData($_.FullName)) { [void]$keys.Add([string]$raw.Key) }
        } catch {
            Write-Warning "Skipping unreadable existing language XML: $($_.FullName). $(Format-CompactError $_)"
        }
    }
    return ,$keys
}

function Get-ExistingLanguageMap([string]$LanguageRoot) {
    $map = @{}
    if (-not (Test-Path -LiteralPath $LanguageRoot)) { return $map }
    $ambiguous = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    $rootFull = [System.IO.Path]::GetFullPath($LanguageRoot).TrimEnd("\", "/")
    $rootPrefix = $rootFull + [System.IO.Path]::DirectorySeparatorChar
    Get-ChildItem -LiteralPath $rootFull -Recurse -File -Filter *.xml -ErrorAction SilentlyContinue | Sort-Object FullName | ForEach-Object {
        try {
            $fileFull = [System.IO.Path]::GetFullPath($_.FullName)
            if (-not $fileFull.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { return }
            $relativePath = $fileFull.Substring($rootPrefix.Length)
            $namespace = Get-LocalizationNamespaceFromRelativePath $relativePath
            if (-not $namespace) { return }
            Initialize-RmkXlsxReader
            foreach ($raw in [RimWorldTranslatorRmkXlsxReader]::ReadLanguageData($_.FullName)) {
                $key = [string]$raw.Key
                $identity = Get-LocalizationIdentity -Namespace $namespace -Key $key
                if (-not $identity -or $ambiguous.Contains($identity)) { continue }
                if ($map.ContainsKey($identity)) {
                    $map.Remove($identity)
                    [void]$ambiguous.Add($identity)
                    Write-Warning "Ignoring duplicated existing localization identity: $namespace / $key"
                } else {
                    $map[$identity] = [pscustomobject]@{
                        Text = ConvertTo-FlatString $raw.Text
                        RelativePath = $relativePath
                    }
                }
            }
        } catch {
            Write-Warning "Skipping unreadable existing language XML: $($_.FullName). $(Format-CompactError $_)"
        }
    }
    return $map
}

function Import-LanguageDataEntries([string]$FilePath, [string]$TargetRelativePath, [string]$Kind, [string]$TypeName) {
    $entries = New-Object "System.Collections.Generic.List[object]"
    try {
        Initialize-RmkXlsxReader
        $rawEntries = [RimWorldTranslatorRmkXlsxReader]::ReadLanguageData($FilePath)
    } catch {
        Write-Warning "Skipping unreadable language XML: $FilePath. $(Format-CompactError $_)"
        return ,$entries
    }

    foreach ($raw in $rawEntries) {
        $text = ConvertTo-FlatString $raw.Text
        $key = [string]$raw.Key
        $field = [string]$raw.Field
        if (-not (Test-HasHumanText $text)) { continue }
        if (Test-LooksLikeCodeOrPath -Text $text -Field $field) { continue }
        $internalReason = Get-InternalLocalizationIdentifierReason -Key $key -Kind $Kind -TypeName $TypeName -Field $field
        if ($internalReason) {
            [void]$script:SkippedInternalLocalizationEntries.Add([pscustomobject]@{
                key = $key
                source = $text
                kind = $Kind
                defClass = $TypeName
                field = $field
                sourceFile = $FilePath
                target = $TargetRelativePath
                reason = $internalReason
            })
            continue
        }
        [void]$entries.Add([pscustomobject]@{
            Id = ""
            Key = $key
            Text = $text
            Kind = $Kind
            TypeName = $TypeName
            TargetRelativePath = $TargetRelativePath
            SourceFile = $FilePath
            Field = $field
        })
    }
    return ,$entries
}

function Initialize-DefFieldRules {
    if ($script:DefFieldRulesInitialized) { return }
    $allowed = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($field in @(
        "label", "labelshort", "description", "jobstring", "reportstring",
        "deathmessage", "deathmessagefemale", "deathmessagemale", "pawnsplural",
        "leadertitle", "arrivedletter", "customlabel", "gizmolabel", "gizmodescription",
        "commandlabel", "commanddescription", "letterlabel", "lettertext", "header",
        "headertip", "summary", "formatstring", "formatstringunfinalized", "fixedname", "reason"
    )) { [void]$allowed.Add($field) }

    $rulesPath = Join-Path $PSScriptRoot "rimworld-def-field-rules.txt"
    if (Test-Path -LiteralPath $rulesPath -PathType Leaf) {
        $loaded = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($line in [System.IO.File]::ReadAllLines($rulesPath, [System.Text.Encoding]::UTF8)) {
            if ($line -match '^\s*allow\t([A-Za-z_][A-Za-z0-9_]*)\s*$') { [void]$loaded.Add($matches[1]) }
        }
        if ($loaded.Count -gt 0) { $allowed = $loaded }
    }
    $script:TranslatableDefFields = $allowed

    $excluded = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($field in @(
        "defname", "parentname", "classname", "class", "thingclass", "workerclass",
        "compclass", "hediffclass", "thoughtclass", "abilityclass", "worldobjectclass",
        "nodeclass", "texpath", "texname", "graphicpath", "shader", "sound", "sounddef",
        "iconpath", "packageid", "xpath", "operation", "colorchannel", "rendernode",
        "rendertree", "rendertreedef", "bodypart", "bodypartdef", "bodytype", "headtype",
        "racedef", "thingdef", "pawnkinddef", "jobdef", "statdef", "skilldef", "hediffdef",
        "genedef", "tagdef", "debuglabel"
    )) { [void]$excluded.Add($field) }
    if (Test-Path -LiteralPath $rulesPath -PathType Leaf) {
        foreach ($line in [System.IO.File]::ReadAllLines($rulesPath, [System.Text.Encoding]::UTF8)) {
            if ($line -match '^\s*deny\t([A-Za-z_][A-Za-z0-9_]*)\s*$') { [void]$excluded.Add($matches[1]) }
        }
    }
    $script:ExcludedDefSegments = $excluded
    $script:DefFieldRulesInitialized = $true

    $readerType = "RimWorldTranslatorRmkXlsxReader" -as [type]
    if ($readerType -and (Test-Path -LiteralPath $rulesPath -PathType Leaf)) {
        $method = $readerType.GetMethod("LoadDefFieldRules", [System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Static)
        if ($method) { [void]$method.Invoke($null, [object[]]@([string]$rulesPath)) }
    }
}

function Test-TranslatableDefPath([string[]]$PathSegments) {
    if ($PathSegments.Count -eq 0) { return $false }
    Initialize-DefFieldRules
    $leaf = $PathSegments[$PathSegments.Count - 1].ToLowerInvariant()
    $full = ([string]::Join(".", $PathSegments)).ToLowerInvariant()
    if ($script:TranslatableDefFields.Contains($leaf)) { return $true }
    if ($leaf -eq "text" -and $full -match "(letter|message|scenario|quest|dialog|help|tip|inspect)") { return $true }
    if ($leaf -eq "slateref" -and $full -match "(letter|text|label|description|inspect|string)") { return $true }
    if ($leaf -eq "li" -and $full -match "(rulesstrings|tagsstrings)") { return $true }
    return $false
}

function Test-ExcludedDefPath([string[]]$PathSegments) {
    Initialize-DefFieldRules
    foreach ($segment in $PathSegments) {
        if ($script:ExcludedDefSegments.Contains([string]$segment)) { return $true }
    }
    return $false
}

function Add-DefInjectedLeafEntries(
    [System.Xml.XmlNode]$Node,
    [string[]]$PathSegments,
    [string]$DefName,
    [string]$TypeName,
    [string]$TargetRelativePath,
    [string]$SourceFile,
    [System.Collections.Generic.List[object]]$Entries
) {
    $children = Get-XmlElementChildren $Node
    if ($children.Count -eq 0) {
        if (Test-ExcludedDefPath $PathSegments) { return }
        if (-not (Test-TranslatableDefPath $PathSegments)) { return }

        $text = ConvertTo-FlatString $Node.InnerText
        if (-not (Test-HasHumanText $text)) { return }
        if (Test-LooksLikeCodeOrPath -Text $text -Field $PathSegments[$PathSegments.Count - 1]) { return }

        $path = [string]::Join(".", $PathSegments)
        [void]$Entries.Add([pscustomobject]@{
            Id = ""
            Key = "$DefName.$path"
            Text = $text
            Kind = "DefInjected"
            TypeName = $TypeName
            TargetRelativePath = $TargetRelativePath
            SourceFile = $SourceFile
            Field = $PathSegments[$PathSegments.Count - 1]
        })
        return
    }

    $listIndexByName = @{}
    foreach ($child in $children) {
        $name = $child.LocalName
        $segment = $name
        if ($name -eq "li") {
            $parent = if ($PathSegments.Count -gt 0) { $PathSegments[$PathSegments.Count - 1] } else { "li" }
            if (-not $listIndexByName.ContainsKey($parent)) { $listIndexByName[$parent] = 0 }
            $segment = Get-ListItemSegment -Node $child -Index $listIndexByName[$parent]
            $listIndexByName[$parent] = $listIndexByName[$parent] + 1
        }

        Add-DefInjectedLeafEntries `
            -Node $child `
            -PathSegments ($PathSegments + @($segment)) `
            -DefName $DefName `
            -TypeName $TypeName `
            -TargetRelativePath $TargetRelativePath `
            -SourceFile $SourceFile `
            -Entries $Entries
    }
}

function Import-DefEntriesFromDefs([string]$ModRoot, [string]$OutputFilePrefix, [switch]$IncludePatches) {
    $entries = New-Object "System.Collections.Generic.List[object]"
    $candidateRoots = New-Object "System.Collections.Generic.List[string]"
    $seenRoots = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    if ($IncludePatches) {
        Write-Warning "Patch XML translation is disabled because RimWorld patch conditions and list handles cannot be resolved safely outside the game. Defs and language files will still be processed."
    }
    foreach ($contentRoot in Get-ActiveModContentRoots $ModRoot) {
        $defsRoot = Join-Path $contentRoot "Defs"
        if ((Test-Path -LiteralPath $defsRoot -PathType Container) -and $seenRoots.Add([System.IO.Path]::GetFullPath($defsRoot))) { [void]$candidateRoots.Add($defsRoot) }
    }

    foreach ($root in $candidateRoots) {
        Get-ChildItem -LiteralPath $root -Recurse -File -Filter *.xml -ErrorAction SilentlyContinue | Sort-Object FullName | ForEach-Object {
            $sourceFile = $_.FullName
            try {
                Initialize-RmkXlsxReader
                $readResult = [RimWorldTranslatorRmkXlsxReader]::ReadDefsDetailed($sourceFile)
                $rawEntries = $readResult.Entries
            } catch {
                Write-Warning "Skipping unreadable Def XML: $sourceFile. $(Format-CompactError $_)"
                return
            }

            foreach ($raw in $readResult.Excluded) {
                $text = ConvertTo-FlatString $raw.Text
                if (-not (Test-HasHumanText $text) -or (Test-LooksLikeCodeOrPath -Text $text -Field ([string]$raw.Field))) { continue }
                $key = [string]$raw.Key
                $typeName = [string]$raw.TypeName
                $field = [string]$raw.Field
                $targetRelative = Join-Path (Join-Path "DefInjected" $typeName) "$OutputFilePrefix.xml"
                $internalReason = Get-InternalLocalizationIdentifierReason -Key $key -Kind "DefInjected" -TypeName $typeName -Field $field
                if (-not $internalReason) { $internalReason = "RimWorld NoTranslate or runtime field '$field'" }
                [void]$script:SkippedInternalLocalizationEntries.Add([pscustomobject]@{
                    key = $key
                    source = $text
                    kind = "DefInjected"
                    defClass = $typeName
                    field = $field
                    sourceFile = $sourceFile
                    target = $targetRelative
                    reason = $internalReason
                })
            }

            foreach ($raw in $rawEntries) {
                $text = ConvertTo-FlatString $raw.Text
                if (-not (Test-HasHumanText $text) -or (Test-LooksLikeCodeOrPath -Text $text -Field ([string]$raw.Field))) { continue }
                $key = [string]$raw.Key
                $typeName = [string]$raw.TypeName
                $field = [string]$raw.Field
                $targetRelative = Join-Path (Join-Path "DefInjected" $typeName) "$OutputFilePrefix.xml"
                $internalReason = Get-InternalLocalizationIdentifierReason -Key $key -Kind "DefInjected" -TypeName $typeName -Field $field
                if ($internalReason) {
                    [void]$script:SkippedInternalLocalizationEntries.Add([pscustomobject]@{
                        key = $key
                        source = $text
                        kind = "DefInjected"
                        defClass = $typeName
                        field = $field
                        sourceFile = $sourceFile
                        target = $targetRelative
                        reason = $internalReason
                    })
                    continue
                }
                [void]$entries.Add([pscustomobject]@{
                    Id = ""
                    Key = $key
                    Text = $text
                    Kind = "DefInjected"
                    TypeName = $typeName
                    TargetRelativePath = $targetRelative
                    SourceFile = $sourceFile
                    Field = $field
                })
            }
        }
    }
    return ,$entries
}

function Test-LanguageRootHasXml([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    return $null -ne (Get-ChildItem -LiteralPath $Path -Recurse -File -Filter *.xml -ErrorAction SilentlyContinue | Select-Object -First 1)
}

function Get-LoadFolderVersionScore([string]$Name) {
    $score = 0
    foreach ($number in [System.Text.RegularExpressions.Regex]::Matches($Name, '\d+')) { $score = ($score * 100) + [int]$number.Value }
    return $score
}

function Get-ActiveModContentRoots([string]$ModRoot) {
    $modFull = [System.IO.Path]::GetFullPath($ModRoot).TrimEnd("\", "/")
    $cacheKey = $modFull.ToLowerInvariant()
    if ($script:ActiveModContentRootsCache.ContainsKey($cacheKey)) { return $script:ActiveModContentRootsCache[$cacheKey] }

    $roots = New-Object "System.Collections.Generic.List[string]"
    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    [void]$seen.Add($modFull)
    [void]$roots.Add($modFull)
    $loadFoldersPath = Join-Path $modFull "LoadFolders.xml"
    if (Test-Path -LiteralPath $loadFoldersPath -PathType Leaf) {
        try {
            $doc = Read-SafeXmlDocument $loadFoldersPath
            $versionNode = $null
            $versionScore = -1
            $rootChildren = Get-XmlElementChildren $doc.DocumentElement
            foreach ($candidateNode in $rootChildren.ToArray()) {
                if ($candidateNode.LocalName -notmatch '^v\d') { continue }
                $candidateScore = Get-LoadFolderVersionScore $candidateNode.LocalName
                if ($candidateScore -gt $versionScore) {
                    $versionNode = $candidateNode
                    $versionScore = $candidateScore
                }
            }
            if ($versionNode) {
                $versionChildren = Get-XmlElementChildren $versionNode
                foreach ($item in $versionChildren.ToArray()) {
                    if ($item.LocalName -ne "li") { continue }
                    $relative = ([string]$item.InnerText).Trim()
                    if ([string]::IsNullOrWhiteSpace($relative)) { continue }
                    $candidate = if ($relative -in @("/", "\", ".")) { $modFull } else { Join-Path $modFull $relative }
                    if (-not (Test-Path -LiteralPath $candidate -PathType Container)) { continue }
                    $candidateFull = [System.IO.Path]::GetFullPath($candidate).TrimEnd("\", "/")
                    $prefix = $modFull + [System.IO.Path]::DirectorySeparatorChar
                    if (-not $candidateFull.Equals($modFull, [System.StringComparison]::OrdinalIgnoreCase) -and -not $candidateFull.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
                    if ($seen.Add($candidateFull)) { [void]$roots.Add($candidateFull) }
                }
            }
        } catch {
            Write-Warning "Could not read LoadFolders.xml; using the selected mod root only. $(Format-CompactError $_)"
        }
    }
    $result = $roots.ToArray()
    $script:ActiveModContentRootsCache[$cacheKey] = $result
    return $result
}

function Test-ExcludedSourceLanguageFolder([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) { return $true }
    if ($Name -match "^(Korean|KoreanLegacy|\uD55C\uAD6D)") { return $true }
    return $false
}

function Get-SourceLanguageRank([string]$Name) {
    if ($Name -eq "English") { return 0 }
    if ($Name -match "^ChineseSimplified") { return 10 }
    if ($Name -match "^ChineseTraditional") { return 11 }
    if ($Name -match "^Japanese") { return 20 }
    if ($Name -match "^Spanish") { return 40 }
    if ($Name -match "^French") { return 41 }
    if ($Name -match "^German") { return 42 }
    if ($Name -match "^Russian") { return 43 }
    return 100
}

function Get-SourceLanguageRoots([string]$ModRoot, [string]$SourceLanguageFolder) {
    $roots = New-Object "System.Collections.Generic.List[object]"
    $contentRoots = @(Get-ActiveModContentRoots $ModRoot)

    if ($SourceLanguageFolder -and $SourceLanguageFolder -ne "Auto") {
        $explicitRoots = New-Object "System.Collections.Generic.List[string]"
        if ([System.IO.Path]::IsPathRooted($SourceLanguageFolder)) {
            [void]$explicitRoots.Add($SourceLanguageFolder)
        } else {
            foreach ($contentRoot in $contentRoots) { [void]$explicitRoots.Add((Join-Path (Join-Path $contentRoot "Languages") $SourceLanguageFolder)) }
        }
        $seenExplicit = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($explicitRoot in $explicitRoots) {
            if (-not (Test-LanguageRootHasXml $explicitRoot)) { continue }
            $explicitFull = [System.IO.Path]::GetFullPath($explicitRoot)
            if (-not $seenExplicit.Add($explicitFull)) { continue }
            [void]$roots.Add([pscustomobject]@{
                Name = Split-Path -Leaf $explicitFull
                Path = $explicitFull
                Rank = -1
            })
        }
        if ($roots.Count -eq 0) { throw "Source language folder has no XML files: $SourceLanguageFolder" }
        return $roots.ToArray()
    }

    $candidates = New-Object "System.Collections.Generic.List[object]"
    $seenCandidates = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($contentRoot in $contentRoots) {
        $languagesRoot = Join-Path $contentRoot "Languages"
        if (-not (Test-Path -LiteralPath $languagesRoot -PathType Container)) { continue }
        Get-ChildItem -LiteralPath $languagesRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            if (Test-ExcludedSourceLanguageFolder $_.Name) { return }
            if (-not (Test-LanguageRootHasXml $_.FullName)) { return }
            $candidateFull = [System.IO.Path]::GetFullPath($_.FullName)
            if (-not $seenCandidates.Add($candidateFull)) { return }
            [void]$candidates.Add([pscustomobject]@{
                Name = $_.Name
                Path = $candidateFull
                Rank = Get-SourceLanguageRank $_.Name
            })
        }
    }

    $best = $candidates | Sort-Object Rank, Name | Select-Object -First 1
    if ($best) {
        foreach ($candidate in $candidates) {
            if ([string]$candidate.Name -eq [string]$best.Name) { [void]$roots.Add($candidate) }
        }
    }
    return $roots.ToArray()
}

function Import-SourceEntries([string]$ModRoot, [string]$OutputFilePrefix, [string]$SourceLanguageFolder, [switch]$IncludePatches) {
    $entries = New-Object "System.Collections.Generic.List[object]"
    $script:DetectedSourceLanguageRoots = @(Get-SourceLanguageRoots -ModRoot $ModRoot -SourceLanguageFolder $SourceLanguageFolder)
    foreach ($language in $script:DetectedSourceLanguageRoots) {
        $languageRoot = $language.Path
        Get-ChildItem -LiteralPath $languageRoot -Recurse -File -Filter *.xml -ErrorAction SilentlyContinue | Sort-Object FullName | ForEach-Object {
            $relative = $_.FullName.Substring($languageRoot.Length).TrimStart("\", "/")
            $parts = $relative -split "[\\/]"
            $kind = if ($parts.Count -gt 0) { $parts[0] } else { "Keyed" }
            $typeName = if ($kind -eq "DefInjected" -and $parts.Count -gt 1) { $parts[1] } else { "" }
            foreach ($entry in (Import-LanguageDataEntries -FilePath $_.FullName -TargetRelativePath $relative -Kind $kind -TypeName $typeName)) {
                [void]$entries.Add($entry)
            }
        }
    }

    foreach ($entry in (Import-DefEntriesFromDefs -ModRoot $ModRoot -OutputFilePrefix $OutputFilePrefix -IncludePatches:$IncludePatches)) {
        [void]$entries.Add($entry)
    }

    return ,$entries
}

function Import-SpecificLanguageEntries([string]$ModRoot, [string]$LanguageName) {
    $entries = New-Object "System.Collections.Generic.List[object]"
    if ([string]::IsNullOrWhiteSpace($LanguageName)) { return ,$entries }
    $languageRoots = New-Object "System.Collections.Generic.List[string]"
    $seenRoots = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($contentRoot in Get-ActiveModContentRoots $ModRoot) {
        $languagesRoot = Join-Path $contentRoot "Languages"
        if (-not (Test-Path -LiteralPath $languagesRoot -PathType Container)) { continue }
        $languageRoot = Join-Path $languagesRoot $LanguageName
        if (-not (Test-LanguageRootHasXml $languageRoot)) {
            $match = Get-ChildItem -LiteralPath $languagesRoot -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -eq $LanguageName -or $_.Name.StartsWith("$LanguageName ", [System.StringComparison]::OrdinalIgnoreCase) } |
                Sort-Object Name |
                Select-Object -First 1
            if (-not $match) { continue }
            $languageRoot = $match.FullName
        }
        $languageFull = [System.IO.Path]::GetFullPath($languageRoot)
        if ($seenRoots.Add($languageFull)) { [void]$languageRoots.Add($languageFull) }
    }
    foreach ($languageRoot in $languageRoots) {
        Get-ChildItem -LiteralPath $languageRoot -Recurse -File -Filter *.xml -ErrorAction SilentlyContinue | Sort-Object FullName | ForEach-Object {
            $relative = $_.FullName.Substring($languageRoot.Length).TrimStart("\", "/")
            $parts = $relative -split "[\\/]"
            $kind = if ($parts.Count -gt 0) { $parts[0] } else { "Keyed" }
            $typeName = if ($kind -eq "DefInjected" -and $parts.Count -gt 1) { $parts[1] } else { "" }
            foreach ($entry in (Import-LanguageDataEntries -FilePath $_.FullName -TargetRelativePath $relative -Kind $kind -TypeName $typeName)) {
                [void]$entries.Add($entry)
            }
        }
    }
    return ,$entries
}

function Import-GlossaryFile([string]$Path, [bool]$AlwaysInclude, [string]$Category) {
    $terms = New-Object "System.Collections.Generic.List[object]"
    if (-not (Test-Path -LiteralPath $Path)) { return ,$terms }

    $extension = [System.IO.Path]::GetExtension($Path).ToLowerInvariant()
    if ($extension -in @(".txt", ".tsv", ".csv")) {
        foreach ($line in [System.IO.File]::ReadAllLines((Resolve-Path -LiteralPath $Path).Path, [System.Text.Encoding]::UTF8)) {
            $trim = $line.Trim()
            if (-not $trim -or $trim.StartsWith("#") -or $trim.StartsWith("//")) { continue }

            $source = ""
            $ko = ""
            $note = ""
            if ($trim -match "^\s*(.+?)\s*=>\s*(.+?)\s*$") {
                $source = $matches[1].Trim()
                $ko = $matches[2].Trim()
            } elseif ($trim -match "^\s*(.+?)\s*=\s*(.+?)\s*$") {
                $source = $matches[1].Trim()
                $ko = $matches[2].Trim()
            } else {
                $parts = $trim -split "`t"
                if ($parts.Count -ge 2) {
                    $source = $parts[0].Trim()
                    $ko = $parts[1].Trim()
                    if ($parts.Count -ge 3) { $note = $parts[2].Trim() }
                }
            }

            if (-not $source -or -not $ko) { continue }
            [void]$terms.Add([pscustomobject]@{
                source = $source
                ko = $ko
                note = $note
                priority = 1000
                count = 1
                origin = "text-glossary"
                alwaysInclude = $AlwaysInclude
                category = $Category
            })
        }
        return ,$terms
    }

    $raw = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
    $json = $raw | ConvertFrom-Json
    foreach ($term in @($json.terms)) {
        if (-not $term.source -or -not $term.ko) { continue }
        [void]$terms.Add([pscustomobject]@{
            source = [string]$term.source
            ko = [string]$term.ko
            note = [string]$term.note
            priority = if ($null -ne $term.priority) { [int]$term.priority } else { 1000 }
            count = if ($null -ne $term.count) { [int]$term.count } else { 1 }
            origin = [string]$term.origin
            alwaysInclude = $AlwaysInclude
            category = $Category
        })
    }
    return ,$terms
}

function Import-Glossary([string]$ScriptRoot, [string]$GeneratedGlossaryPath, [string]$CuratedGlossaryPath, [switch]$UseCuratedGlossary) {
    $all = New-Object "System.Collections.Generic.List[object]"
    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    $officialSources = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)

    $generatedPath = if ($GeneratedGlossaryPath) { $GeneratedGlossaryPath } else { Join-Path $ScriptRoot "glossary.generated.ko.json" }
    foreach ($term in (Import-GlossaryFile -Path $generatedPath -AlwaysInclude $false -Category "official")) {
        $key = "$($term.source)|$($term.ko)"
        if ($seen.Add($key)) {
            [void]$all.Add($term)
            [void]$officialSources.Add([string]$term.source)
        }
    }

    if ($UseCuratedGlossary) {
        $curatedPath = if ($CuratedGlossaryPath) { $CuratedGlossaryPath } else { Join-Path $ScriptRoot "glossary.ko.json" }
        foreach ($term in (Import-GlossaryFile -Path $curatedPath -AlwaysInclude $true -Category "curated")) {
            if ($officialSources.Contains([string]$term.source)) { continue }
            $key = "$($term.source)|$($term.ko)"
            if ($seen.Add($key)) { [void]$all.Add($term) }
        }
    }

    return ,$all
}

function Test-GlossaryTermAppears([string]$TermSource, [string]$Text) {
    if ([string]::IsNullOrWhiteSpace($TermSource) -or [string]::IsNullOrWhiteSpace($Text)) { return $false }
    if ($TermSource.Trim().Length -lt 3) { return $false }
    return $Text.IndexOf($TermSource, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Initialize-GlossarySelectionIndex([object[]]$Terms) {
    $script:GlossaryAlwaysTerms = New-Object "System.Collections.Generic.List[object]"
    $script:GlossaryGeneratedTerms = New-Object "System.Collections.Generic.List[object]"
    $script:GlossaryGeneratedPrefixIndex = @{}
    $order = 0
    foreach ($term in @($Terms)) {
        if (-not $term -or [string]::IsNullOrWhiteSpace([string]$term.source)) { continue }
        $searchSource = ([string]$term.source).Trim().ToLowerInvariant()
        if ($term.alwaysInclude) {
            [void]$script:GlossaryAlwaysTerms.Add($term)
            $order++
            continue
        }
        if ($searchSource.Length -lt 3) { $order++; continue }
        $indexedTerm = [pscustomobject]@{ Term = $term; SearchSource = $searchSource; Order = $order }
        $order++
        [void]$script:GlossaryGeneratedTerms.Add($indexedTerm)
        $prefix = $searchSource.Substring(0, 3)
        if (-not $script:GlossaryGeneratedPrefixIndex.ContainsKey($prefix)) {
            $script:GlossaryGeneratedPrefixIndex[$prefix] = New-Object "System.Collections.Generic.List[object]"
        }
        [void]$script:GlossaryGeneratedPrefixIndex[$prefix].Add($indexedTerm)
    }
    $script:GlossarySelectionIndexReady = $true
}

function Select-GlossaryTermsForBatch([object[]]$Terms, [object[]]$Batch, [int]$MaxAlways, [int]$MaxGenerated) {
    if (-not $Terms -or $Terms.Count -eq 0) { return @() }
    if (-not $script:GlossarySelectionIndexReady) { Initialize-GlossarySelectionIndex $Terms }
    $selected = New-Object "System.Collections.Generic.List[object]"
    $selectedSources = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($term in @($script:GlossaryAlwaysTerms | Select-Object -First $MaxAlways)) {
        if ($selectedSources.Add([string]$term.source)) { [void]$selected.Add($term) }
    }

    if ($Batch -and $MaxGenerated -gt 0) {
        $textBlob = [string]::Join("`n", @($Batch | ForEach-Object { [string]$_.Text })).ToLowerInvariant()
        $prefixes = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::Ordinal)
        for ($i = 0; $i -le $textBlob.Length - 3; $i++) {
            [void]$prefixes.Add($textBlob.Substring($i, 3))
        }
        $matchedOrders = New-Object "System.Collections.Generic.HashSet[int]"
        foreach ($prefix in $prefixes) {
            if (-not $script:GlossaryGeneratedPrefixIndex.ContainsKey($prefix)) { continue }
            foreach ($indexedTerm in $script:GlossaryGeneratedPrefixIndex[$prefix]) {
                if ($selectedSources.Contains([string]$indexedTerm.Term.source)) { continue }
                if ($textBlob.Contains([string]$indexedTerm.SearchSource)) { [void]$matchedOrders.Add([int]$indexedTerm.Order) }
            }
        }
        $candidates = New-Object "System.Collections.Generic.List[object]"
        foreach ($indexedTerm in $script:GlossaryGeneratedTerms) {
            if ($matchedOrders.Contains([int]$indexedTerm.Order)) { [void]$candidates.Add($indexedTerm.Term) }
        }
        $generated = @($candidates |
            Sort-Object @{ Expression = "priority"; Ascending = $true }, @{ Expression = "count"; Descending = $true }, @{ Expression = { ([string]$_.source).Length }; Descending = $true } |
            Select-Object -First $MaxGenerated)

        foreach ($term in $generated) {
            if ($selectedSources.Add([string]$term.source)) { [void]$selected.Add($term) }
        }
    }

    return ,$selected
}

function Convert-GlossaryToPrompt([object[]]$Terms) {
    if (-not $Terms -or $Terms.Count -eq 0) { return "" }
    $lines = New-Object "System.Collections.Generic.List[string]"
    foreach ($term in $Terms) {
        $source = [string]$term.source
        $target = [string]$term.ko
        $note = [string]$term.note
        if ($note) {
            [void]$lines.Add("- $source => $target ($note)")
        } else {
            [void]$lines.Add("- $source => $target")
        }
    }
    return [string]::Join("`n", $lines)
}

function New-SystemPrompt([string]$GlossaryText, [string]$ExtraPrompt) {
    $extraBlock = ""
    if (-not [string]::IsNullOrWhiteSpace($ExtraPrompt)) {
        $extraBlock = @"

Additional user instructions:
$ExtraPrompt
"@
    }

    return @"
You translate RimWorld mod localization entries into natural Korean.
Return only JSON matching this shape: {"translations":[{"id":"same id","text":"Korean translation"}]}.

Rules:
- Translate only the text value. Never translate ids, XML keys, defNames, file names, class names, or paths.
 - Preserve placeholders, grammar-rule prefixes, and markup exactly: {0}, {PAWN_nameDef}, [pawn_nameDef], r_logentry->, `$variable, <color=...>, </color>, \n, %, and XML-like tags.
 - A grammar-rule prefix such as r_logentry-> must remain unchanged at the beginning of the translated value.
 - When a Korean particle follows a placeholder or dynamic noun, use RimWorld's automatic particle notation with the consonant-final form in parentheses first: `(은)는`, `(이)가`, `(을)를`, `(과)와`, `(으)로`.
 - Attach that notation directly to the placeholder, for example `[lodgersLabelSingOrPluralDef](이)가`. Never use reversed forms such as `은(는)`, `이(가)`, `을(를)`, `과(와)`, or `으로(로)`.
 - Keep label fields short, usually a noun phrase.
- Use polite declarative Korean for descriptions and letters when appropriate.
- Preserve meaningful line breaks, but never add padding blank lines or more than two consecutive \n escapes.
- Do not output repeated \u000a escapes.
- Keep RimWorld/DLC terms consistent with the glossary.
- If a value is already a proper noun, keep the proper noun or transliterate naturally.
- Do not add comments, explanations, markdown, or missing ids.

Glossary:
$GlossaryText
$extraBlock
"@
}

function New-UserPayload([object[]]$Batch) {
    $items = @($Batch | ForEach-Object {
        [ordered]@{
            id = $_.Id
            key = $_.Key
            kind = $_.Kind
            defType = $_.TypeName
            field = $_.Field
            text = $_.Text
        }
    })
    return ([ordered]@{ entries = $items } | ConvertTo-Json -Depth 10)
}

function New-RequestBody(
    [string]$Model,
    [string]$SystemPrompt,
    [string]$UserPayload,
    [int]$MaxCompletionTokens,
    [string]$CompletionTokenParameter,
    [string]$ResponseFormatMode,
    [double]$Temperature,
    [string]$ReasoningEffort,
    [switch]$NoStructuredOutputs
) {
    $body = [ordered]@{
        model = $Model
        messages = @(
            [ordered]@{ role = "system"; content = $SystemPrompt },
            [ordered]@{ role = "user"; content = $UserPayload }
        )
        stream = $false
    }

    if ($Temperature -ge 0) {
        $body.temperature = $Temperature
        $body.top_p = 0.9
    }
    if ($MaxCompletionTokens -gt 0 -and $CompletionTokenParameter -ne "none") {
        $body[$CompletionTokenParameter] = $MaxCompletionTokens
    }
    if (-not [string]::IsNullOrWhiteSpace($ReasoningEffort)) {
        $body.reasoning_effort = $ReasoningEffort.Trim()
    }

    if ($NoStructuredOutputs) { $ResponseFormatMode = "JsonObject" }
    if ($ResponseFormatMode -eq "JsonSchema") {
        $body.response_format = [ordered]@{
            type = "json_schema"
            json_schema = [ordered]@{
                name = "rimworld_translation_batch"
                strict = $true
                schema = [ordered]@{
                    type = "object"
                    additionalProperties = $false
                    required = @("translations")
                    properties = [ordered]@{
                        translations = [ordered]@{
                            type = "array"
                            items = [ordered]@{
                                type = "object"
                                additionalProperties = $false
                                required = @("id", "text")
                                properties = [ordered]@{
                                    id = [ordered]@{ type = "string" }
                                    text = [ordered]@{ type = "string" }
                                }
                            }
                        }
                    }
                }
            }
        }
    } elseif ($ResponseFormatMode -eq "JsonObject") {
        $body.response_format = [ordered]@{ type = "json_object" }
    }

    return $body
}

function Add-ApiKeyCandidate([System.Collections.Generic.List[string]]$List, [string]$Candidate) {
    if ([string]::IsNullOrWhiteSpace($Candidate)) { return }
    $trim = $Candidate.Trim()
    if (-not $trim -or $trim.StartsWith("#")) { return }

    if ($trim -match "^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.+)\s*$") {
        $name = $matches[1]
        $value = $matches[2].Trim()
        if ($name -notmatch "^(CEREBRAS_API_KEY|CEREBRAS_KEY|OPENAI_API_KEY|GEMINI_API_KEY|GOOGLE_API_KEY|DEEPSEEK_API_KEY|DASHSCOPE_API_KEY|GROQ_API_KEY|MISTRAL_API_KEY|OPENROUTER_API_KEY|ZAI_API_KEY|API_KEY|KEY|RIMWORLD_TRANSLATOR_API_KEYS)$") { return }
        $trim = $value
    }

    $trim = $trim.Trim('"').Trim("'")
    if ($trim.StartsWith("Bearer ", [System.StringComparison]::OrdinalIgnoreCase)) {
        $trim = $trim.Substring(7).Trim()
    }

    foreach ($part in ($trim -split "[,;]")) {
        $key = $part.Trim().Trim('"').Trim("'")
        if ($key) { [void]$List.Add($key) }
    }
}

function Get-ApiKeys([string[]]$ApiKey, [string]$Provider = "Auto") {
    $all = New-Object "System.Collections.Generic.List[string]"
    foreach ($key in $ApiKey) {
        Add-ApiKeyCandidate $all $key
    }
    if ($Provider -in @("Auto", "Cerebras") -and $env:CEREBRAS_API_KEY) { Add-ApiKeyCandidate $all $env:CEREBRAS_API_KEY }
    if ($env:RIMWORLD_TRANSLATOR_API_KEYS) {
        foreach ($key in ($env:RIMWORLD_TRANSLATOR_API_KEYS -split "[,;`n]")) {
            Add-ApiKeyCandidate $all $key
        }
    }

    $unique = New-Object "System.Collections.Generic.List[string]"
    $seen = New-Object "System.Collections.Generic.HashSet[string]"
    foreach ($key in $all) {
        if ($seen.Add($key)) { [void]$unique.Add($key) }
    }
    return $unique.ToArray()
}

function New-KeyStates([string[]]$Keys) {
    $states = New-Object "System.Collections.Generic.List[object]"
    for ($i = 0; $i -lt $Keys.Count; $i++) {
        $key = $Keys[$i]
        [void]$states.Add([pscustomobject]@{
            Key = $key
            Index = $i
            AvailableAt = [DateTime]::UtcNow
            InputWindowStart = [DateTime]::UtcNow
            InputTokensInWindow = 0
            DailyTokensUsed = 0
            Requests = 0
            Failures = 0
            Disabled = $false
        })
    }
    return $states.ToArray()
}

function Reset-InputTokenWindowIfNeeded([object]$State) {
    $now = [DateTime]::UtcNow
    if (($now - $State.InputWindowStart).TotalSeconds -ge 60) {
        $State.InputWindowStart = $now
        $State.InputTokensInWindow = 0
    }
}

function Get-KeyReadyAt([object]$State, [int]$EstimatedInputTokens, [int]$InputTokensPerMinutePerKey) {
    Reset-InputTokenWindowIfNeeded $State
    $readyAt = $State.AvailableAt
    if ($InputTokensPerMinutePerKey -gt 0 -and ($State.InputTokensInWindow + $EstimatedInputTokens) -gt $InputTokensPerMinutePerKey) {
        $tokenReadyAt = $State.InputWindowStart.AddSeconds(60)
        if ($tokenReadyAt -gt $readyAt) { $readyAt = $tokenReadyAt }
    }
    return $readyAt
}

function Get-NextKeyState([object[]]$States, [int]$RequestsPerMinutePerKey, [int]$InputTokensPerMinutePerKey, [int]$EstimatedInputTokens, [int]$DailyTokenBudgetPerKey) {
    if ($InputTokensPerMinutePerKey -gt 0 -and $EstimatedInputTokens -gt $InputTokensPerMinutePerKey) {
        throw "Estimated input tokens for one request ($EstimatedInputTokens) exceed the free-tier per-minute input limit ($InputTokensPerMinutePerKey). Lower -BatchSize or -MaxInputTokensPerBatch."
    }

    while ($true) {
        foreach ($state in $States) {
            if ($state.Disabled) { continue }
            if ($DailyTokenBudgetPerKey -gt 0 -and ($state.DailyTokensUsed + $EstimatedInputTokens) -gt $DailyTokenBudgetPerKey) {
                $state.Disabled = $true
                Write-Warning "API key disabled for this run because its estimated daily token budget would be exceeded."
            }
        }

        $active = @($States | Where-Object { -not $_.Disabled })
        if ($active.Count -eq 0) { throw "All API keys are disabled or exhausted." }

        $state = @($active | Sort-Object @{ Expression = { Get-KeyReadyAt $_ $EstimatedInputTokens $InputTokensPerMinutePerKey } }, Requests, Index | Select-Object -First 1)[0]
        $readyAt = Get-KeyReadyAt $state $EstimatedInputTokens $InputTokensPerMinutePerKey
        $now = [DateTime]::UtcNow
        if ($readyAt -gt $now) {
            $sleepMs = [int][Math]::Ceiling(($readyAt - $now).TotalMilliseconds)
            if ($sleepMs -gt 0) {
                Write-Host ("Waiting {0:n1}s for free-tier request/input-token limits..." -f ($sleepMs / 1000.0))
                Wait-TranslationDelay $sleepMs
            }
            continue
        }

        Reset-InputTokenWindowIfNeeded $state
        if ($RequestsPerMinutePerKey -gt 0) {
            $spacing = [Math]::Ceiling(60.0 / $RequestsPerMinutePerKey)
            $state.AvailableAt = [DateTime]::UtcNow.AddSeconds($spacing)
        } else {
            $state.AvailableAt = [DateTime]::UtcNow
        }
        $state.InputTokensInWindow += $EstimatedInputTokens
        $state.Requests++
        return $state
    }
}

function Update-KeyUsageFromResponse([object]$State, [object]$Response, [int]$EstimatedInputTokens, [int]$DailyTokenBudgetPerKey) {
    if ($null -eq $Response -or $null -eq $Response.usage) {
        $State.DailyTokensUsed += $EstimatedInputTokens
        return
    }

    $promptTokens = 0
    $totalTokens = 0
    if ($Response.usage.prompt_tokens) { $promptTokens = [int]$Response.usage.prompt_tokens }
    if ($Response.usage.total_tokens) { $totalTokens = [int]$Response.usage.total_tokens }

    if ($promptTokens -gt 0) {
        $delta = $promptTokens - $EstimatedInputTokens
        if ($delta -ne 0) {
            $State.InputTokensInWindow = [Math]::Max(0, $State.InputTokensInWindow + $delta)
        }
    }

    if ($totalTokens -gt 0) {
        $State.DailyTokensUsed += $totalTokens
    } else {
        $State.DailyTokensUsed += [Math]::Max($EstimatedInputTokens, $promptTokens)
    }

    if ($DailyTokenBudgetPerKey -gt 0 -and $State.DailyTokensUsed -ge $DailyTokenBudgetPerKey) {
        $State.Disabled = $true
    }
}

function ConvertTo-SafeHttpErrorSummary([string]$Body) {
    if ([string]::IsNullOrWhiteSpace($Body)) { return "" }
    $trimmed = $Body.Trim()
    try {
        $parsed = $trimmed | ConvertFrom-Json -ErrorAction Stop
        $errorValue = if ($parsed.PSObject.Properties["error"]) { $parsed.error } else { $parsed }
        $message = if ($errorValue -is [string]) { [string]$errorValue } elseif ($errorValue.PSObject.Properties["message"]) { [string]$errorValue.message } else { "" }
        $code = if ($errorValue -isnot [string] -and $errorValue.PSObject.Properties["code"]) { [string]$errorValue.code } else { "" }
        $type = if ($errorValue -isnot [string] -and $errorValue.PSObject.Properties["type"]) { [string]$errorValue.type } else { "" }
        $parts = @($message, $type, $code) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Select-Object -Unique
        if ($parts.Count -gt 0) { $trimmed = [string]::Join(" | ", $parts) }
    } catch {
    }
    $trimmed = [System.Text.RegularExpressions.Regex]::Replace($trimmed, "[\r\n\t]+", " ")
    $trimmed = [System.Text.RegularExpressions.Regex]::Replace($trimmed, "\s{2,}", " ").Trim()
    if ($trimmed.Length -gt 1200) { $trimmed = $trimmed.Substring(0, 1200) + "..." }
    return $trimmed
}

function Get-HttpErrorDetail([System.Management.Automation.ErrorRecord]$ErrorRecord) {
    $code = $null
    $body = ""
    $response = $null
    $stream = $null
    $reader = $null
    try {
        if ($ErrorRecord.Exception.Response) {
            $response = $ErrorRecord.Exception.Response
            $code = [int]$response.StatusCode
            $stream = $response.GetResponseStream()
            if ($stream) {
                $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
                $buffer = New-Object char[] 8192
                $read = $reader.ReadBlock($buffer, 0, $buffer.Length)
                if ($read -gt 0) { $body = New-Object string($buffer, 0, $read) }
            }
        }
    } catch {
    } finally {
        if ($reader) { $reader.Dispose() }
        elseif ($stream) { $stream.Dispose() }
        if ($response) { $response.Close() }
    }
    if (-not $body -and $ErrorRecord.ErrorDetails) { $body = $ErrorRecord.ErrorDetails.Message }
    return [pscustomobject]@{ Code = $code; Body = ConvertTo-SafeHttpErrorSummary $body }
}

function Invoke-JsonPostUtf8([string]$Uri, [hashtable]$Headers, [byte[]]$BodyBytes, [int]$TimeoutSec) {
    $request = [System.Net.WebRequest]::Create($Uri)
    $request.Method = "POST"
    $request.ContentType = "application/json; charset=utf-8"
    $request.Accept = "application/json"
    $request.Timeout = $TimeoutSec * 1000
    $request.ReadWriteTimeout = $TimeoutSec * 1000
    foreach ($name in $Headers.Keys) {
        $request.Headers[$name] = $Headers[$name]
    }
    $request.ContentLength = $BodyBytes.Length

    $requestStream = $request.GetRequestStream()
    try {
        $requestStream.Write($BodyBytes, 0, $BodyBytes.Length)
    } finally {
        $requestStream.Close()
    }

    $response = $request.GetResponse()
    $responseStream = $null
    $reader = $null
    try {
        $responseStream = $response.GetResponseStream()
        $reader = New-Object System.IO.StreamReader($responseStream, [System.Text.Encoding]::UTF8)
        $raw = $reader.ReadToEnd()
        return $raw | ConvertFrom-Json
    } finally {
        if ($reader) { $reader.Dispose() }
        elseif ($responseStream) { $responseStream.Dispose() }
        $response.Close()
    }
}

function Invoke-OpenAICompatibleChat([string]$ChatUrl, [object]$Body, [object[]]$KeyStates, [int]$RequestsPerMinutePerKey, [int]$InputTokensPerMinutePerKey, [int]$DailyTokenBudgetPerKey, [int]$TimeoutSec, [int]$MaxRetries) {
    $json = $Body | ConvertTo-Json -Depth 30 -Compress
    $estimatedInputTokens = (Estimate-TokenCount $json) + 256
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $lastError = $null

    for ($attempt = 1; $attempt -le $MaxRetries; $attempt++) {
        Assert-TranslationNotCancelled
        $state = Get-NextKeyState `
            -States $KeyStates `
            -RequestsPerMinutePerKey $RequestsPerMinutePerKey `
            -InputTokensPerMinutePerKey $InputTokensPerMinutePerKey `
            -EstimatedInputTokens $estimatedInputTokens `
            -DailyTokenBudgetPerKey $DailyTokenBudgetPerKey
        $headers = @{
            Authorization = "Bearer $($state.Key)"
        }

        try {
            $response = Invoke-JsonPostUtf8 `
                -Uri $ChatUrl `
                -Headers $headers `
                -BodyBytes $bytes `
                -TimeoutSec $TimeoutSec
            Update-KeyUsageFromResponse `
                -State $state `
                -Response $response `
                -EstimatedInputTokens $estimatedInputTokens `
                -DailyTokenBudgetPerKey $DailyTokenBudgetPerKey
            return $response
        } catch {
            if (Test-TranslationCancellationRequested -or $_.Exception -is [System.OperationCanceledException]) { throw }
            $detail = Get-HttpErrorDetail $_
            $lastError = "HTTP $($detail.Code): $($detail.Body)"
            $state.Failures++

            if ($detail.Code -eq 401 -or $detail.Code -eq 403) {
                $state.Disabled = $true
                Write-Warning "API key was rejected and disabled for this run."
                continue
            }
            if ($detail.Code -eq 429) {
                $state.AvailableAt = [DateTime]::UtcNow.AddSeconds(60)
                $state.InputWindowStart = [DateTime]::UtcNow
                $state.InputTokensInWindow = $InputTokensPerMinutePerKey
                Write-Warning "Rate limited; rotating key and waiting before this key is reused."
                continue
            }
            if ($detail.Code -ge 500 -or $null -eq $detail.Code) {
                Wait-TranslationDelay ([Math]::Min(30000, 2000 * $attempt))
                continue
            }
            throw $lastError
        }
    }

    throw "Request failed after $MaxRetries attempts. Last error: $lastError"
}

function ConvertFrom-ModelJson([string]$Content) {
    $trimmed = $Content.Trim()
    if ($trimmed -match '^```') {
        $trimmed = $trimmed -replace '^```(?:json)?\s*', ''
        $trimmed = $trimmed -replace '\s*```$', ''
    }
    return $trimmed | ConvertFrom-Json
}

function Format-CompactError([object]$ErrorRecord) {
    $message = if ($ErrorRecord -and $ErrorRecord.Exception) { [string]$ErrorRecord.Exception.Message } else { [string]$ErrorRecord }
    if ([string]::IsNullOrWhiteSpace($message)) { return "unknown error" }
    $message = [System.Text.RegularExpressions.Regex]::Replace($message, "\\u000a(\\u000a)+", "\\u000a...")
    $message = [System.Text.RegularExpressions.Regex]::Replace($message, "[\r\n\t]+", " ")
    $message = [System.Text.RegularExpressions.Regex]::Replace($message, "\s{2,}", " ").Trim()
    if ($message.Length -gt 320) {
        $message = $message.Substring(0, 320) + "..."
    }
    return $message
}

function ConvertTo-TranslationMap([object]$Response) {
    $contentValue = $Response.choices[0].message.content
    if ($contentValue -is [string]) {
        $content = [string]$contentValue
    } else {
        $parts = New-Object "System.Collections.Generic.List[string]"
        foreach ($part in @($contentValue)) {
            if ($part -is [string]) {
                [void]$parts.Add([string]$part)
            } elseif ($part -and $part.PSObject.Properties["text"]) {
                [void]$parts.Add([string]$part.text)
            }
        }
        $content = [string]::Join("", $parts)
    }
    if ([string]::IsNullOrWhiteSpace($content)) { throw "Model response did not contain text content." }
    $parsed = ConvertFrom-ModelJson $content
    $map = @{}

    if ($parsed.translations) {
        foreach ($item in @($parsed.translations)) {
            $map[[string]$item.id] = ConvertTo-FlatString $item.text
        }
        return $map
    }

    foreach ($prop in $parsed.PSObject.Properties) {
        $map[[string]$prop.Name] = ConvertTo-FlatString $prop.Value
    }
    return $map
}

function Get-ProtectedTokenCounts([string]$Text) {
    return Get-RimWorldProtectedTokenCounts $Text
}

function Get-ProtectedTokens([string]$Text) {
    $counts = Get-ProtectedTokenCounts $Text
    return @($counts.Keys)
}

function Get-TokenPreservationIssues([string]$Source, [string]$Target) {
    return Get-RimWorldTokenPreservationIssues -Source $Source -Target $Target
}

function ConvertTo-GoogleProtectedText([string]$Text) {
    $protected = [ordered]@{}
    $result = [string]$Text
    $index = 0
    foreach ($token in (@(Get-ProtectedTokens $Text) | Sort-Object Length -Descending)) {
        $placeholder = "ZXQPROTECTED{0:D3}ZXQ" -f $index
        $protected[$placeholder] = $token
        $result = $result.Replace($token, $placeholder)
        $index++
    }
    return [pscustomobject]@{ Text = $result; Map = $protected }
}

function Restore-GoogleProtectedText([string]$Text, [object]$Map) {
    $result = [string]$Text
    if ($Map) {
        foreach ($item in $Map.GetEnumerator()) {
            $result = $result.Replace([string]$item.Key, [string]$item.Value)
        }
    }
    return $result
}

function Split-GoogleTextChunks([string]$Text, [int]$MaxChars = 3500) {
    $chunks = New-Object "System.Collections.Generic.List[string]"
    $remaining = [string]$Text
    while ($remaining.Length -gt $MaxChars) {
        $breakAt = $remaining.LastIndexOf("`n", $MaxChars)
        if ($breakAt -lt [int]($MaxChars * 0.45)) { $breakAt = $remaining.LastIndexOf(". ", $MaxChars) }
        if ($breakAt -lt [int]($MaxChars * 0.45)) { $breakAt = $remaining.LastIndexOf(" ", $MaxChars) }
        if ($breakAt -lt 1) { $breakAt = $MaxChars }
        [void]$chunks.Add($remaining.Substring(0, $breakAt + 1))
        $remaining = $remaining.Substring($breakAt + 1)
    }
    if ($remaining.Length -gt 0) { [void]$chunks.Add($remaining) }
    return $chunks.ToArray()
}

function Invoke-JsonGetUtf8([string]$Uri, [int]$TimeoutSec) {
    $request = [System.Net.WebRequest]::Create($Uri)
    $request.Method = "GET"
    $request.Accept = "application/json"
    $request.Timeout = $TimeoutSec * 1000
    $request.ReadWriteTimeout = $TimeoutSec * 1000
    $request.UserAgent = "Mozilla/5.0 RimWorldAiTranslator"

    $response = $request.GetResponse()
    try {
        $responseStream = $response.GetResponseStream()
        $reader = New-Object System.IO.StreamReader($responseStream, [System.Text.Encoding]::UTF8)
        $raw = $reader.ReadToEnd()
        return $raw | ConvertFrom-Json
    } finally {
        $response.Close()
    }
}

function ConvertFrom-GoogleTranslateResponse([object]$Response) {
    $parts = New-Object "System.Collections.Generic.List[string]"
    foreach ($segment in @($Response[0])) {
        if ($segment -and $segment.Count -gt 0 -and $null -ne $segment[0]) {
            [void]$parts.Add([string]$segment[0])
        }
    }
    return [string]::Join("", $parts)
}

function Invoke-GoogleTranslateText([string]$Text, [string]$Endpoint, [int]$TimeoutSec, [int]$MaxRetries) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return "" }

    $protected = ConvertTo-GoogleProtectedText $Text
    $translatedChunks = New-Object "System.Collections.Generic.List[string]"
    foreach ($chunk in (Split-GoogleTextChunks $protected.Text)) {
        $lastError = $null
        for ($attempt = 1; $attempt -le $MaxRetries; $attempt++) {
            Assert-TranslationNotCancelled
            try {
                $query = [System.Uri]::EscapeDataString($chunk)
                $url = "$Endpoint`?client=gtx&sl=auto&tl=ko&dt=t&q=$query"
                $response = Invoke-JsonGetUtf8 -Uri $url -TimeoutSec $TimeoutSec
                [void]$translatedChunks.Add((ConvertFrom-GoogleTranslateResponse $response))
                $lastError = $null
                break
            } catch {
                if (Test-TranslationCancellationRequested -or $_.Exception -is [System.OperationCanceledException]) { throw }
                $lastError = $_
                if ($attempt -lt $MaxRetries) {
                    Wait-TranslationDelay ([Math]::Min(10000, 1000 * (1 + $attempt)))
                }
            }
        }
        if ($lastError) { throw $lastError }
        Wait-TranslationDelay 120
    }

    $translated = [string]::Join("", $translatedChunks)
    return Restore-GoogleProtectedText -Text (ConvertTo-FlatString $translated) -Map $protected.Map
}

function Invoke-GoogleTranslateBatch([object[]]$Batch, [string]$Label) {
    $map = @{}
    $index = 0
    foreach ($entry in $Batch) {
        $index++
        try {
            $map[$entry.Id] = Invoke-GoogleTranslateText -Text ([string]$entry.Text) -Endpoint $GoogleTranslateUrl -TimeoutSec $script:RequestTimeoutSec -MaxRetries $script:RequestMaxRetries
        } catch {
            Write-Warning ("Google Translate failed for {0} entry {1}/{2}; keeping source text. {3}" -f $Label, $index, $Batch.Count, (Format-CompactError $_))
            $map[$entry.Id] = [string]$entry.Text
        }
    }
    return $map
}

function Split-IntoBatches([object[]]$Entries, [int]$BatchSize, [int]$MaxInputCharsPerBatch, [int]$MaxInputTokensPerBatch, [int]$FixedPromptTokens) {
    $batches = New-Object "System.Collections.Generic.List[object]"
    $current = New-Object "System.Collections.Generic.List[object]"
    $chars = 0
    $tokens = $FixedPromptTokens

    foreach ($entry in $Entries) {
        $entryChars = ([string]$entry.Text).Length + ([string]$entry.Key).Length + 80
        $entryTokens = (Estimate-TokenCount ([string]$entry.Text)) + (Estimate-TokenCount ([string]$entry.Key)) + 40
        $wouldExceedChars = $MaxInputCharsPerBatch -gt 0 -and ($chars + $entryChars) -gt $MaxInputCharsPerBatch
        $wouldExceedTokens = $MaxInputTokensPerBatch -gt 0 -and ($tokens + $entryTokens) -gt $MaxInputTokensPerBatch
        if ($current.Count -gt 0 -and ($current.Count -ge $BatchSize -or $wouldExceedChars -or $wouldExceedTokens)) {
            [void]$batches.Add($current.ToArray())
            $current = New-Object "System.Collections.Generic.List[object]"
            $chars = 0
            $tokens = $FixedPromptTokens
        }
        [void]$current.Add($entry)
        $chars += $entryChars
        $tokens += $entryTokens
    }
    if ($current.Count -gt 0) { [void]$batches.Add($current.ToArray()) }
    return ,$batches
}

function Read-LanguageFile([string]$Path) {
    $map = [ordered]@{}
    if (-not (Test-Path -LiteralPath $Path)) { return $map }

    $doc = Read-SafeXmlDocument $Path
    if (-not $doc.DocumentElement -or $doc.DocumentElement.LocalName -ne "LanguageData") {
        throw "Target XML is not LanguageData: $Path"
    }
    foreach ($child in (Get-XmlElementChildren $doc.DocumentElement)) {
        $map[$child.LocalName] = ConvertTo-FlatString $child.InnerText
    }
    return $map
}

function Remove-InvalidXmlChars([string]$Text) {
    if ($null -eq $Text) { return "" }
    return [System.Text.RegularExpressions.Regex]::Replace($Text, "[^\u0009\u000A\u000D\u0020-\uD7FF\uE000-\uFFFD]", "")
}

function Test-PathologicalTranslation([string]$Text) {
    return Test-RimWorldPathologicalTranslation $Text
}

function Get-InvalidKoreanParticleNotations([string]$Text) {
    return @(Get-RimWorldInvalidKoreanParticleNotations $Text)
}

function Escape-XmlText([string]$Text) {
    return [System.Security.SecurityElement]::Escape((Remove-InvalidXmlChars $Text))
}

function Write-Utf8TextAtomic([string]$Path, [string]$Text) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $directory = [System.IO.Path]::GetDirectoryName($fullPath)
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    $temporaryPath = Join-Path $directory (".{0}.{1}.tmp" -f [System.IO.Path]::GetFileName($fullPath), [System.Guid]::NewGuid().ToString("N"))
    try {
        $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($Text)
        $stream = [System.IO.FileStream]::new($temporaryPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        } finally {
            $stream.Dispose()
        }
        if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            [System.IO.File]::Replace($temporaryPath, $fullPath, "$fullPath.bak", $true)
        } else {
            [System.IO.File]::Move($temporaryPath, $fullPath)
        }
    } finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Write-Utf8LinesAtomic([string]$Path, [System.Collections.IEnumerable]$Lines) {
    $text = [string]::Join([Environment]::NewLine, @($Lines)) + [Environment]::NewLine
    Write-Utf8TextAtomic -Path $Path -Text $text
}

function Write-LanguageFile([string]$Path, [hashtable]$Entries, [switch]$Overwrite) {
    $existing = Read-LanguageFile $Path
    foreach ($key in ($Entries.Keys | Sort-Object)) {
        if (-not (Test-ValidXmlElementName ([string]$key))) {
            throw "Refusing to write an invalid XML localization key: $key"
        }
        if ($Overwrite -or -not $existing.Contains($key)) {
            $existing[$key] = $Entries[$key]
        }
    }

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
    Write-Utf8LinesAtomic -Path $Path -Lines $lines
}

function Write-AuditFile([string]$Path, [object[]]$Rows) {
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    $json = ConvertTo-Json -InputObject @($Rows) -Depth 8 -Compress
    Write-Utf8TextAtomic -Path $Path -Text $json
}

function Write-CsvFile([string]$Path, [object[]]$Rows) {
    $csv = @($Rows | ConvertTo-Csv -NoTypeInformation)
    Write-Utf8LinesAtomic -Path $Path -Lines $csv
}

function Write-TranslationCheckpoint(
    [string]$AuditBase,
    [object]$TranslatedRows,
    [object]$ComparisonRows,
    [object]$Warnings,
    [int]$CompletedBatches,
    [int]$TotalBatches,
    [bool]$Complete
) {
    $translatedArray = @(foreach ($row in $TranslatedRows) { $row })
    $comparisonArray = @(foreach ($row in $ComparisonRows) { $row })
    $warningArray = @(foreach ($row in $Warnings) { $row })
    Write-AuditFile -Path "$AuditBase-translated.json" -Rows $translatedArray
    Write-AuditFile -Path "$AuditBase-comparison.json" -Rows $comparisonArray
    Write-CsvFile -Path "$AuditBase-comparison.csv" -Rows $comparisonArray
    Write-AuditFile -Path "$AuditBase-token-warnings.json" -Rows $warningArray
    Write-AuditFile -Path "$AuditBase-progress.json" -Rows @([pscustomobject]@{
        version = 1
        completedBatches = $CompletedBatches
        totalBatches = $TotalBatches
        complete = $Complete
        updatedAt = [DateTime]::UtcNow.ToString("o")
    })
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

function Restore-TranslationTransactionFile([object]$Entry) {
    $path = [string]$Entry.Path
    if (-not [bool]$Entry.Existed) {
        if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force -ErrorAction Stop }
        return
    }
    $directory = [System.IO.Path]::GetDirectoryName($path)
    $restorePath = Join-Path $directory (".{0}.{1}.restore.tmp" -f [System.IO.Path]::GetFileName($path), [Guid]::NewGuid().ToString("N"))
    $discardPath = Join-Path $directory (".{0}.{1}.failed.tmp" -f [System.IO.Path]::GetFileName($path), [Guid]::NewGuid().ToString("N"))
    try {
        Copy-FileFlushed -SourcePath ([string]$Entry.SnapshotPath) -DestinationPath $restorePath
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            [System.IO.File]::Replace($restorePath, $path, $discardPath, $true)
        } else {
            [System.IO.File]::Move($restorePath, $path)
        }
    } finally {
        foreach ($temporaryPath in @($restorePath, $discardPath)) {
            if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) { Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue }
        }
    }
}

function Write-TranslationFilesTransaction([hashtable]$OutputGroups, [switch]$Overwrite) {
    $journal = New-Object "System.Collections.Generic.List[object]"
    try {
        foreach ($targetPath in ($OutputGroups.Keys | Sort-Object)) {
            $existed = Test-Path -LiteralPath $targetPath -PathType Leaf
            $snapshotPath = ""
            if ($existed) {
                $directory = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($targetPath))
                $snapshotPath = Join-Path $directory (".{0}.{1}.transaction.bak" -f [System.IO.Path]::GetFileName($targetPath), [Guid]::NewGuid().ToString("N"))
                Copy-FileFlushed -SourcePath $targetPath -DestinationPath $snapshotPath
            }
            [void]$journal.Add([pscustomobject]@{ Path = $targetPath; Existed = $existed; SnapshotPath = $snapshotPath })
            Write-LanguageFile -Path $targetPath -Entries $OutputGroups[$targetPath] -Overwrite:$Overwrite
        }
    } catch {
        $writeError = $_.Exception
        $rollbackErrors = New-Object "System.Collections.Generic.List[string]"
        for ($index = $journal.Count - 1; $index -ge 0; $index--) {
            try { Restore-TranslationTransactionFile $journal[$index] } catch { [void]$rollbackErrors.Add("$($journal[$index].Path): $($_.Exception.Message)") }
        }
        if ($rollbackErrors.Count -gt 0) {
            throw "Translation output failed and rollback was incomplete. Write error: $($writeError.Message) Rollback errors: $([string]::Join(' | ', $rollbackErrors))"
        }
        throw "Translation output failed; all files written by this run were rolled back. $($writeError.Message)"
    } finally {
        foreach ($entry in $journal) {
            if ($entry.SnapshotPath -and (Test-Path -LiteralPath ([string]$entry.SnapshotPath) -PathType Leaf)) {
                Remove-Item -LiteralPath ([string]$entry.SnapshotPath) -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

function Test-ContainsKorean([string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }
    return $Text -match "[\uAC00-\uD7AF]"
}

function Invoke-TranslationBatchWithSplit([object[]]$Batch, [string]$Label, [int]$Depth = 0) {
    $batch = @($Batch)
    if ($batch.Count -eq 0) { return @{} }

    if ($MockTranslations) {
        $mockMap = @{}
        foreach ($entry in $batch) {
            $mockMap[$entry.Id] = "MOCK: $($entry.Text)"
        }
        return $mockMap
    }

    if ($script:ActiveTranslationProvider -eq "Google") {
        return Invoke-GoogleTranslateBatch -Batch $batch -Label $Label
    }

    $body = New-RequestBody `
        -Model $Model `
        -SystemPrompt (New-SystemPrompt -GlossaryText (Convert-GlossaryToPrompt (Select-GlossaryTermsForBatch -Terms $glossary -Batch $batch -MaxAlways $MaxAlwaysGlossaryTerms -MaxGenerated $MaxGeneratedGlossaryTermsPerBatch)) -ExtraPrompt $ExtraPrompt) `
        -UserPayload (New-UserPayload $batch) `
        -MaxCompletionTokens $MaxCompletionTokens `
        -CompletionTokenParameter $script:RequestCompletionTokenParameter `
        -ResponseFormatMode $ResponseFormatMode `
        -Temperature $script:RequestTemperature `
        -ReasoningEffort $script:RequestReasoningEffort `
        -NoStructuredOutputs:$script:DisableStructuredOutputs

    $lastError = $null
    for ($attempt = 1; $attempt -le $script:RequestMaxRetries; $attempt++) {
        Assert-TranslationNotCancelled
        try {
            $response = Invoke-OpenAICompatibleChat `
                -ChatUrl $chatUrl `
                -Body $body `
                -KeyStates $keyStates `
                -RequestsPerMinutePerKey $RequestsPerMinutePerKey `
                -InputTokensPerMinutePerKey $InputTokensPerMinutePerKey `
                -DailyTokenBudgetPerKey $DailyTokenBudgetPerKey `
                -TimeoutSec $script:RequestTimeoutSec `
                -MaxRetries 1
            Assert-TranslationNotCancelled
            $map = ConvertTo-TranslationMap $response
            $missingIds = @($batch | Where-Object { -not $map.ContainsKey($_.Id) })
            if ($missingIds.Count -eq 0) { return $map }

            $sampleMissing = [string]::Join(", ", @($missingIds | Select-Object -First 5 | ForEach-Object { $_.Id }))
            throw "Model response missed $($missingIds.Count) ids in $Label. Missing sample: $sampleMissing"
        } catch {
            if (Test-TranslationCancellationRequested -or $_.Exception -is [System.OperationCanceledException]) { throw }
            $lastError = $_
            if ($attempt -lt $script:RequestMaxRetries) {
                Write-Warning "Batch $Label failed on attempt $attempt; retrying. $(Format-CompactError $_)"
                Wait-TranslationDelay ([Math]::Min(30000, 2000 * $attempt))
            }
        }
    }

    if ($batch.Count -gt 1) {
        $leftCount = [int][Math]::Ceiling($batch.Count / 2.0)
        $left = @($batch[0..($leftCount - 1)])
        $right = @($batch[$leftCount..($batch.Count - 1)])
        Write-Warning ("Batch {0} failed after {1} attempts; splitting {2} entries into {3}+{4}. {5}" -f $Label, $script:RequestMaxRetries, $batch.Count, $left.Count, $right.Count, (Format-CompactError $lastError))

        $merged = @{}
        $leftMap = Invoke-TranslationBatchWithSplit -Batch $left -Label "$Label.1" -Depth ($Depth + 1)
        foreach ($key in $leftMap.Keys) { $merged[$key] = $leftMap[$key] }

        $rightMap = Invoke-TranslationBatchWithSplit -Batch $right -Label "$Label.2" -Depth ($Depth + 1)
        foreach ($key in $rightMap.Keys) { $merged[$key] = $rightMap[$key] }

        return $merged
    }

    throw "Batch $Label failed at single-entry fallback. $(Format-CompactError $lastError)"
}

function New-ReviewRunRoot([string]$BaseRoot, [string]$ModFullPath, [string]$Stamp) {
    $leaf = Split-Path -Leaf $ModFullPath
    if (-not $leaf) { $leaf = "mod" }
    $safeLeaf = $leaf -replace "[^A-Za-z0-9_.-]", "_"
    return Join-Path $BaseRoot "$safeLeaf-$Stamp"
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$modFull = Resolve-FullPath $ModRoot
$script:CancellationFileFull = if ($CancellationFile) { Get-FullPathAllowMissing $CancellationFile } else { "" }
if ($script:CancellationFileFull -and (Test-Path -LiteralPath $script:CancellationFileFull -PathType Container)) {
    throw "CancellationFile must be a file path, not a directory."
}
Assert-TranslationNotCancelled
Assert-SafePathSegment -Value $LanguageFolderName -Name "LanguageFolderName"
Assert-SafePathSegment -Value $OutputFilePrefix -Name "OutputFilePrefix"
$languageRoot = Join-Path (Join-Path $modFull "Languages") $LanguageFolderName
$existingLanguageFull = if ($ExistingLanguageRoot) { Resolve-FullPath $ExistingLanguageRoot } else { $languageRoot }
$referenceLanguageFull = New-Object "System.Collections.Generic.List[string]"
$referenceLanguageSeen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($referenceRoot in @($ReferenceLanguageRoot)) {
    if ([string]::IsNullOrWhiteSpace($referenceRoot)) { continue }
    $resolvedReference = Resolve-FullPath $referenceRoot
    if ($referenceLanguageSeen.Add($resolvedReference)) { [void]$referenceLanguageFull.Add($resolvedReference) }
}
$referenceSourceWorkbookFull = ""
if ($ReferenceSourceWorkbook) {
    $referenceSourceWorkbookFull = Resolve-FullPath $ReferenceSourceWorkbook
    $referenceWorkbookInfo = Get-Item -LiteralPath $referenceSourceWorkbookFull -ErrorAction Stop
    if ($referenceWorkbookInfo.Extension -ine ".xlsx" -or $referenceWorkbookInfo.Length -gt 268435456) {
        throw "ReferenceSourceWorkbook must be an XLSX file no larger than 256 MB."
    }
}
$preserveTranslationFull = ""
if ($PreserveTranslationFile) {
    $preserveTranslationFull = Resolve-FullPath $PreserveTranslationFile
    $preserveFileInfo = Get-Item -LiteralPath $preserveTranslationFull -ErrorAction Stop
    if ($preserveFileInfo.Length -gt 67108864) { throw "Preserved translation file is too large: $preserveTranslationFull" }
}
$chatUrl = ""
if ($ExtraPromptFile) {
    if (-not (Test-Path -LiteralPath $ExtraPromptFile)) { throw "Extra prompt file not found: $ExtraPromptFile" }
    $filePrompt = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $ExtraPromptFile).Path, [System.Text.Encoding]::UTF8)
    if (-not [string]::IsNullOrWhiteSpace($filePrompt)) {
        if ([string]::IsNullOrWhiteSpace($ExtraPrompt)) {
            $ExtraPrompt = $filePrompt
        } else {
            $ExtraPrompt = "$ExtraPrompt`n$filePrompt"
        }
    }
}
if ($SourceOnly) {
    $glossary = @()
    $baseGlossary = @()
    $systemPrompt = ""
} else {
    $glossary = Import-Glossary -ScriptRoot $scriptRoot -GeneratedGlossaryPath $GeneratedGlossaryPath -CuratedGlossaryPath $CuratedGlossaryPath -UseCuratedGlossary:$UseCuratedGlossary
    $baseGlossary = Select-GlossaryTermsForBatch -Terms $glossary -Batch @() -MaxAlways $MaxAlwaysGlossaryTerms -MaxGenerated 0
    $systemPrompt = New-SystemPrompt -GlossaryText (Convert-GlossaryToPrompt $baseGlossary) -ExtraPrompt $ExtraPrompt
}
$auditStamp = Get-Date -Format "yyyyMMdd-HHmmss"

if ($ReviewOnly) {
    $reviewBaseRoot = if ($ReviewRoot) { Get-FullPathAllowMissing $ReviewRoot } else { Join-Path $scriptRoot "reviews" }
    $reviewRunRoot = New-ReviewRunRoot -BaseRoot $reviewBaseRoot -ModFullPath $modFull -Stamp $auditStamp
    $outputLanguageRoot = Join-Path (Join-Path $reviewRunRoot "Languages") $LanguageFolderName
    $auditRoot = Join-Path $reviewRunRoot "_TranslationAudit"
} else {
    $reviewRunRoot = $null
    $outputLanguageRoot = $languageRoot
    $auditRoot = Join-Path $modFull "_TranslationAudit"
}

$keys = Get-ApiKeys -ApiKey $ApiKey -Provider $TranslationProvider
if ($SourceOnly) {
    $script:ActiveTranslationProvider = "SourceOnly"
} elseif ($MockTranslations) {
    $script:ActiveTranslationProvider = "Mock"
} elseif ($TranslationProvider -eq "Auto") {
    $script:ActiveTranslationProvider = if ($keys.Count -gt 0) { "OpenAICompatible" } else { "Google" }
} else {
    $script:ActiveTranslationProvider = $TranslationProvider
}

$usesChatApi = $script:ActiveTranslationProvider -in @("Cerebras", "OpenAICompatible")
if ($usesChatApi) {
    Assert-ApiUri -Value $BaseUrl -Name "BaseUrl" -AllowLoopbackHttp:$AllowInsecureLoopback
    $chatUrl = Get-ChatCompletionsUrl $BaseUrl
} elseif ($script:ActiveTranslationProvider -eq "Google") {
    Assert-ApiUri -Value $GoogleTranslateUrl -Name "GoogleTranslateUrl" -AllowLoopbackHttp:$AllowInsecureLoopback
}

if ($usesChatApi) {
    $ProviderName = $ProviderName.Trim()
    if ([string]::IsNullOrWhiteSpace($ProviderName) -or $ProviderName.Length -gt 80 -or $ProviderName -match "[\x00-\x1F\x7F]") {
        throw "ProviderName is invalid."
    }
    $Model = $Model.Trim()
    if ([string]::IsNullOrWhiteSpace($Model) -or $Model.Length -gt 200 -or $Model -match "[\x00-\x1F\x7F]") {
        throw "Model is invalid."
    }
}

if (-not $DryRun -and -not $MockTranslations -and $usesChatApi -and $keys.Count -eq 0) {
    throw "No API key provided for $ProviderName. Enter one or more keys in the GUI, or select Google Translate."
}

$keyStates = if ($usesChatApi) { New-KeyStates $keys } else { @() }
$auditProvider = if ($usesChatApi) { ($ProviderName.ToLowerInvariant() -replace "[^a-z0-9_.-]", "-") } else { $script:ActiveTranslationProvider.ToLowerInvariant() }
if (-not $auditProvider) { $auditProvider = "api" }
$auditBase = Join-Path $auditRoot "$auditProvider-$auditStamp"

Write-Host "Mod root: $modFull"
Write-Host "Output language: $outputLanguageRoot"
if ($ReviewOnly) { Write-Host "Review output: $reviewRunRoot" }
Write-Host "Existing translation root: $existingLanguageFull"
foreach ($referenceRoot in $referenceLanguageFull) { Write-Host "Reference translation root: $referenceRoot" }
if ($referenceSourceWorkbookFull) { Write-Host "RMK source history workbook: $referenceSourceWorkbookFull" }
if ($TranslateMissingOnly) { Write-Host "Existing translation policy: translate missing keys only." }
if ($ReviewOnly) { Write-Host "Review-only mode: source/existing translations will not be modified." }
Write-Host "Translation provider: $(if ($usesChatApi) { $ProviderName } else { $script:ActiveTranslationProvider })"
if ($usesChatApi) {
    Write-Host "Chat endpoint: $chatUrl"
    Write-Host "Model: $Model"
    if ($RequestsPerMinutePerKey -gt 0 -or $InputTokensPerMinutePerKey -gt 0 -or $DailyTokenBudgetPerKey -gt 0) {
        Write-Host "Rate guardrails: $RequestsPerMinutePerKey requests/min/key, $InputTokensPerMinutePerKey input tokens/min/key, $DailyTokenBudgetPerKey total tokens/day/key"
    }
    Write-Host "Output mode: $ResponseFormatMode; max output tokens: $MaxCompletionTokens"
    if ($keys.Count -gt 0) { Write-Host "API keys loaded: $($keys.Count)" }
    if ($keys.Count -gt 1) { Write-Host "API key rotation: input order, balanced by per-key request/token availability." }
} elseif ($script:ActiveTranslationProvider -eq "Google") {
    Write-Host "Google Translate endpoint: $GoogleTranslateUrl"
    Write-Host "No Cerebras API key is required. Glossary and extra prompt are not applied by Google Translate."
} elseif ($script:ActiveTranslationProvider -eq "SourceOnly") {
    Write-Host "Source-only mode: no AI/API calls; translation candidates stay blank."
}
if ($SourceOnly) {
    Write-Host "Glossary loading skipped for source-only refresh."
} else {
    Write-Host "Glossary terms loaded: $($glossary.Count) total, $(@($glossary | Where-Object { $_.alwaysInclude }).Count) always-on, $MaxGeneratedGlossaryTermsPerBatch generated terms max/batch"
}

$rmkHistoryMap = Get-RmkWorkbookHistoryMap $referenceSourceWorkbookFull
if ($referenceSourceWorkbookFull) { Write-Host "RMK source history entries: $($rmkHistoryMap.Count)" }
$existingMap = @{}
$existingOriginMap = @{}
$existingTranslationUpdatedAtMap = @{}
$existingTargetMap = @{}
$legacyExistingMap = @{}
$legacyExistingOriginMap = @{}
$legacyExistingTranslationUpdatedAtMap = @{}
foreach ($referenceRoot in $referenceLanguageFull) {
    $referenceMap = Get-ExistingLanguageMap $referenceRoot
    foreach ($identity in $referenceMap.Keys) {
        if (-not $existingMap.ContainsKey($identity)) {
            $existingMap[$identity] = [string]$referenceMap[$identity].Text
            $existingOriginMap[$identity] = "rmk"
        }
    }
}
foreach ($history in $rmkHistoryMap.Values) {
    $key = ([string]$history.Key).Trim()
    $namespace = ([string]$history.ClassName).Trim()
    if (-not $namespace -and $history.Identifier) {
        $separator = ([string]$history.Identifier).IndexOf('+')
        if ($separator -gt 0) { $namespace = ([string]$history.Identifier).Substring(0, $separator) }
    }
    $identity = Get-LocalizationIdentity -Namespace $namespace -Key $key
    $translation = ConvertTo-FlatString $history.Translation
    if (-not $identity -or -not (Test-ValidXmlElementName $key) -or [string]::IsNullOrWhiteSpace($translation) -or $existingMap.ContainsKey($identity)) { continue }
    $existingMap[$identity] = $translation
    $existingOriginMap[$identity] = "rmk"
}
$primaryExistingMap = Get-ExistingLanguageMap $existingLanguageFull
foreach ($identity in $primaryExistingMap.Keys) {
    $existingMap[$identity] = [string]$primaryExistingMap[$identity].Text
    $existingOriginMap[$identity] = "mod"
    $existingTargetMap[$identity] = [string]$primaryExistingMap[$identity].RelativePath
}
$preservedTranslationCount = 0
if ($preserveTranslationFull) {
    $preservedData = [System.IO.File]::ReadAllText($preserveTranslationFull, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    foreach ($item in @($preservedData.items)) {
        $key = ([string]$item.key).Trim()
        $text = ConvertTo-FlatString $item.text
        if (-not (Test-ValidXmlElementName $key) -or [string]::IsNullOrWhiteSpace($text)) { continue }
        $namespace = if ($item.PSObject.Properties["namespace"] -and $item.namespace) {
            [string]$item.namespace
        } elseif ($item.PSObject.Properties["kind"] -and [string]$item.kind -eq "Keyed") {
            "Keyed"
        } elseif ($item.PSObject.Properties["defClass"] -and $item.defClass) {
            [string]$item.defClass
        } elseif ($item.PSObject.Properties["target"] -and $item.target) {
            Get-LocalizationNamespaceFromRelativePath ([string]$item.target)
        } else { "" }
        $identity = Get-LocalizationIdentity -Namespace $namespace -Key $key
        $origin = if ($item.PSObject.Properties["origin"] -and $item.origin) { [string]$item.origin } else { "local" }
        $updatedAt = if ($item.PSObject.Properties["translationUpdatedAt"] -and $item.translationUpdatedAt) { [string]$item.translationUpdatedAt } else { "" }
        if ($identity) {
            $existingMap[$identity] = $text
            $existingOriginMap[$identity] = $origin
            if ($item.PSObject.Properties["target"] -and $item.target) { $existingTargetMap[$identity] = [string]$item.target }
            if ($updatedAt) { $existingTranslationUpdatedAtMap[$identity] = $updatedAt }
        } else {
            $legacyExistingMap[$key] = $text
            $legacyExistingOriginMap[$key] = $origin
            if ($updatedAt) { $legacyExistingTranslationUpdatedAtMap[$key] = $updatedAt }
        }
        $preservedTranslationCount++
    }
    Write-Host "Preserved review translations: $preservedTranslationCount"
}
$sourceEntries = Import-SourceEntries -ModRoot $modFull -OutputFilePrefix $OutputFilePrefix -SourceLanguageFolder $SourceLanguageFolder -IncludePatches:$IncludePatches
$sourceNamespacesByKey = @{}
foreach ($entry in $sourceEntries) {
    $key = [string]$entry.Key
    if (-not $sourceNamespacesByKey.ContainsKey($key)) {
        $sourceNamespacesByKey[$key] = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    }
    $namespace = if ([string]$entry.Kind -eq "Keyed") { "Keyed" } else { [string]$entry.TypeName }
    if ($namespace) { [void]$sourceNamespacesByKey[$key].Add($namespace) }
}

function Get-ExistingEntryInfo([object]$Entry) {
    $identity = Get-EntryLocalizationIdentity $Entry
    $key = [string]$Entry.Key
    if ($identity -and $existingMap.ContainsKey($identity)) {
        return [pscustomobject]@{
            Present = $true
            Text = [string]$existingMap[$identity]
            Origin = if ($existingOriginMap.ContainsKey($identity)) { [string]$existingOriginMap[$identity] } else { "" }
            TranslationUpdatedAt = if ($existingTranslationUpdatedAtMap.ContainsKey($identity)) { [string]$existingTranslationUpdatedAtMap[$identity] } else { "" }
            TargetRelativePath = if ($existingTargetMap.ContainsKey($identity)) { [string]$existingTargetMap[$identity] } else { "" }
        }
    }
    if ($legacyExistingMap.ContainsKey($key) -and $sourceNamespacesByKey.ContainsKey($key) -and $sourceNamespacesByKey[$key].Count -eq 1) {
        return [pscustomobject]@{
            Present = $true
            Text = [string]$legacyExistingMap[$key]
            Origin = if ($legacyExistingOriginMap.ContainsKey($key)) { [string]$legacyExistingOriginMap[$key] } else { "" }
            TranslationUpdatedAt = if ($legacyExistingTranslationUpdatedAtMap.ContainsKey($key)) { [string]$legacyExistingTranslationUpdatedAtMap[$key] } else { "" }
            TargetRelativePath = ""
        }
    }
    return [pscustomobject]@{ Present = $false; Text = ""; Origin = ""; TranslationUpdatedAt = ""; TargetRelativePath = "" }
}

function Get-EntryOutputRelativePath([object]$Entry, [object]$ExistingInfo) {
    if ($ExistingInfo -and [string]$ExistingInfo.Origin -in @("mod", "local") -and -not [string]::IsNullOrWhiteSpace([string]$ExistingInfo.TargetRelativePath)) {
        return [string]$ExistingInfo.TargetRelativePath
    }
    return [string]$Entry.TargetRelativePath
}
$rmkCurrentSourceMap = @{}
if ($rmkHistoryMap.Count -gt 0 -and $script:RmkWorkbookSourceLanguage) {
    $selectedSourceNames = @($script:DetectedSourceLanguageRoots | ForEach-Object { [string]$_.Name })
    $usesSelectedSource = @($selectedSourceNames | Where-Object { $_ -eq $script:RmkWorkbookSourceLanguage -or $_.StartsWith("$($script:RmkWorkbookSourceLanguage) ", [System.StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
    if ($usesSelectedSource) {
        $referenceSourceEntries = $sourceEntries
    } else {
        $languageEntries = New-Object "System.Collections.Generic.List[object]"
        foreach ($entry in @(Import-SpecificLanguageEntries -ModRoot $modFull -LanguageName $script:RmkWorkbookSourceLanguage)) { [void]$languageEntries.Add($entry) }
        $definitionRoots = @((Join-Path $modFull "Defs"), (Join-Path $modFull "Patches")) | ForEach-Object {
            try { [System.IO.Path]::GetFullPath($_).TrimEnd("\", "/") + [System.IO.Path]::DirectorySeparatorChar } catch { "" }
        } | Where-Object { $_ }
        foreach ($entry in $sourceEntries) {
            $sourceFile = try { [System.IO.Path]::GetFullPath([string]$entry.SourceFile) } catch { "" }
            if (-not $sourceFile) { continue }
            if (@($definitionRoots | Where-Object { $sourceFile.StartsWith($_, [System.StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
                [void]$languageEntries.Add($entry)
            }
        }
        $referenceSourceEntries = $languageEntries
    }
    foreach ($entry in $referenceSourceEntries) {
        $identifier = Get-RmkEntryIdentifier $entry
        if ($identifier -and -not $rmkCurrentSourceMap.ContainsKey($identifier)) { $rmkCurrentSourceMap[$identifier] = ConvertTo-FlatString $entry.Text }
    }
    Write-Host "RMK source comparison language: $($script:RmkWorkbookSourceLanguage); current entries: $($rmkCurrentSourceMap.Count)"
}
$dedupe = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
$reviewEntries = New-Object "System.Collections.Generic.List[object]"
$pending = New-Object "System.Collections.Generic.List[object]"
$skippedExisting = 0
$skippedDuplicate = 0

foreach ($entry in $sourceEntries) {
    $identity = Get-EntryLocalizationIdentity $entry
    if (-not $identity) {
        $skippedDuplicate++
        continue
    }
    if (-not $dedupe.Add($identity)) {
        $skippedDuplicate++
        continue
    }
    if ($Limit -gt 0 -and $reviewEntries.Count -ge $Limit) { break }
    $existingInfo = Get-ExistingEntryInfo $entry
    $hasExistingKey = [bool]$existingInfo.Present
    $hasExistingTranslation = $hasExistingKey -and -not [string]::IsNullOrWhiteSpace([string]$existingInfo.Text)
    $existingOrigin = [string]$existingInfo.Origin
    $rmkIdentifier = Get-RmkEntryIdentifier $entry
    $rmkStaleTranslation = $false
    if ($existingOrigin -eq "rmk" -and $rmkIdentifier -and $rmkHistoryMap.ContainsKey($rmkIdentifier) -and $rmkCurrentSourceMap.ContainsKey($rmkIdentifier)) {
        $historicalSource = ConvertTo-FlatString $rmkHistoryMap[$rmkIdentifier].Source
        $currentReferenceSource = ConvertTo-FlatString $rmkCurrentSourceMap[$rmkIdentifier]
        if ($historicalSource -and $currentReferenceSource) { $rmkStaleTranslation = -not (Test-SourceTextEqual -Left $historicalSource -Right $currentReferenceSource) }
    }
    if ($hasExistingKey -and -not $Overwrite -and -not $ReviewOnly) {
        $skippedExisting++
        continue
    }
    $entry.Id = "E{0:d6}" -f ($reviewEntries.Count + 1)
    [void]$reviewEntries.Add($entry)
    if ($TranslateMissingOnly -and $hasExistingTranslation -and -not $rmkStaleTranslation) {
        $skippedExisting++
    } else {
        [void]$pending.Add($entry)
    }
}

if ($script:DetectedSourceLanguageRoots -and $script:DetectedSourceLanguageRoots.Count -gt 0) {
    Write-Host "Detected source language: $([string]::Join(', ', @($script:DetectedSourceLanguageRoots | ForEach-Object { $_.Name })))"
} else {
    Write-Host "Detected source language: none; using Defs text only."
}
Write-Host "Source entries: $($sourceEntries.Count)"
Write-Host "Review entries: $($reviewEntries.Count)"
Write-Host "Pending translation entries: $($pending.Count)"
Write-Host "Reused existing entries: $skippedExisting"
Write-Host "Skipped duplicate: $skippedDuplicate"
Write-Host "Excluded internal identifiers: $($script:SkippedInternalLocalizationEntries.Count)"

if ($reviewEntries.Count -eq 0) {
    Write-Host "Nothing to review or translate."
    return
}

if ($DryRun) {
    Write-Host "Dry run complete. No API calls or translation files were written."
    return
}

if ($ReviewOnly -and -not (Test-Path -LiteralPath $outputLanguageRoot -PathType Container)) {
    New-Item -ItemType Directory -Force -Path $outputLanguageRoot | Out-Null
}

Write-AuditFile -Path "$auditBase-source.json" -Rows $reviewEntries
Write-AuditFile -Path "$auditBase-skipped-internal-identifiers.json" -Rows $script:SkippedInternalLocalizationEntries

if ($SourceOnly) {
    $comparisonRows = New-Object "System.Collections.Generic.List[object]"
    foreach ($entry in $reviewEntries) {
        $existingInfo = Get-ExistingEntryInfo $entry
        $existingTranslation = [string]$existingInfo.Text
        $referenceInfo = Get-ComparisonReferenceInfo -Entry $entry -ExistingInfo $existingInfo -RmkHistoryMap $rmkHistoryMap -RmkCurrentSourceMap $rmkCurrentSourceMap -RmkWorkbook $referenceSourceWorkbookFull
        $targetPath = Get-PathInsideRoot -Root $outputLanguageRoot -RelativePath (Get-EntryOutputRelativePath -Entry $entry -ExistingInfo $existingInfo)
        [void]$comparisonRows.Add([pscustomobject]@{
            id = $entry.Id
            key = $entry.Key
            kind = $entry.Kind
            defClass = $entry.TypeName
            node = $entry.Key
            field = $entry.Field
            target = $targetPath
            source = $entry.Text
            existing = $existingTranslation
            candidate = ""
            existingOrigin = $referenceInfo.ExistingOrigin
            translationOrigin = $referenceInfo.ExistingOrigin
            translationUpdatedAt = $referenceInfo.TranslationUpdatedAt
            rmkIdentifier = $referenceInfo.RmkIdentifier
            rmkHistoricalSource = $referenceInfo.RmkHistoricalSource
            rmkCurrentSource = $referenceInfo.RmkCurrentSource
            rmkSourceChanged = $referenceInfo.RmkSourceChanged
            rmkWorkbook = $referenceInfo.RmkWorkbook
            existingPresent = -not [string]::IsNullOrWhiteSpace($existingTranslation)
            existingHasKorean = Test-ContainsKorean $existingTranslation
            candidateHasKorean = $false
            existingSameAsSource = $existingTranslation -eq ([string]$entry.Text)
            candidateSameAsSource = $false
            candidateBlank = $true
            missingTokens = ""
            pathologicalCandidate = $false
            invalidKoreanParticles = ""
            safeToApply = $false
        })
    }
    Write-AuditFile -Path "$auditBase-translated.json" -Rows @()
    Write-AuditFile -Path "$auditBase-comparison.json" -Rows $comparisonRows
    Write-CsvFile -Path "$auditBase-comparison.csv" -Rows $comparisonRows
    Write-AuditFile -Path "$auditBase-token-warnings.json" -Rows @()
    Write-Host "Done."
    Write-Host "Written/updated files: 0"
    Write-Host "Translated entries: 0"
    Write-Host "Token warnings: 0"
    Write-Host "Skipped unsafe writes: 0"
    if ($ReviewOnly) { Write-Host "Review output: $reviewRunRoot" }
    Write-Host "Audit: $auditBase-*.json"
    return
}

$fixedPromptTokens = (Estimate-TokenCount $systemPrompt) + 1800 + ($MaxGeneratedGlossaryTermsPerBatch * 14)
$batches = Split-IntoBatches `
    -Entries $pending.ToArray() `
    -BatchSize $BatchSize `
    -MaxInputCharsPerBatch $MaxInputCharsPerBatch `
    -MaxInputTokensPerBatch $MaxInputTokensPerBatch `
    -FixedPromptTokens $fixedPromptTokens
$translatedRows = New-Object "System.Collections.Generic.List[object]"
$comparisonRows = New-Object "System.Collections.Generic.List[object]"
$warnings = New-Object "System.Collections.Generic.List[object]"
$outputGroups = @{}
$skippedUnsafe = 0
$pendingIds = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $pending) { [void]$pendingIds.Add([string]$entry.Id) }

foreach ($entry in $reviewEntries) {
    if ($pendingIds.Contains([string]$entry.Id)) { continue }
    $existingInfo = Get-ExistingEntryInfo $entry
    $existingTranslation = [string]$existingInfo.Text
    $referenceInfo = Get-ComparisonReferenceInfo -Entry $entry -ExistingInfo $existingInfo -RmkHistoryMap $rmkHistoryMap -RmkCurrentSourceMap $rmkCurrentSourceMap -RmkWorkbook $referenceSourceWorkbookFull
    $targetPath = Get-PathInsideRoot -Root $outputLanguageRoot -RelativePath (Get-EntryOutputRelativePath -Entry $entry -ExistingInfo $existingInfo)
    [void]$comparisonRows.Add([pscustomobject]@{
        id = $entry.Id
        key = $entry.Key
        kind = $entry.Kind
        defClass = $entry.TypeName
        node = $entry.Key
        field = $entry.Field
        target = $targetPath
        source = $entry.Text
        existing = $existingTranslation
        candidate = ""
        existingOrigin = $referenceInfo.ExistingOrigin
        translationOrigin = $referenceInfo.ExistingOrigin
        translationUpdatedAt = $referenceInfo.TranslationUpdatedAt
        rmkIdentifier = $referenceInfo.RmkIdentifier
        rmkHistoricalSource = $referenceInfo.RmkHistoricalSource
        rmkCurrentSource = $referenceInfo.RmkCurrentSource
        rmkSourceChanged = $referenceInfo.RmkSourceChanged
        rmkWorkbook = $referenceInfo.RmkWorkbook
        existingPresent = -not [string]::IsNullOrWhiteSpace($existingTranslation)
        existingHasKorean = Test-ContainsKorean $existingTranslation
        candidateHasKorean = $false
        existingSameAsSource = $existingTranslation -eq ([string]$entry.Text)
        candidateSameAsSource = $false
        candidateBlank = $true
        missingTokens = ""
        pathologicalCandidate = $false
        invalidKoreanParticles = ""
        safeToApply = $false
    })
}

Write-TranslationCheckpoint -AuditBase $auditBase -TranslatedRows $translatedRows -ComparisonRows $comparisonRows -Warnings $warnings -CompletedBatches 0 -TotalBatches $batches.Count -Complete $false

for ($i = 0; $i -lt $batches.Count; $i++) {
    Assert-TranslationNotCancelled
    $batch = @($batches[$i])
    Write-Host ("Translating batch {0}/{1} ({2} entries)..." -f ($i + 1), $batches.Count, $batch.Count)
    $map = Invoke-TranslationBatchWithSplit -Batch $batch -Label ("{0}/{1}" -f ($i + 1), $batches.Count)

    foreach ($entry in $batch) {
        $translated = if ($map -and $map.ContainsKey($entry.Id)) { [string]$map[$entry.Id] } else { [string]$entry.Text }
        $translated = Remove-InvalidXmlChars $translated
        $tokenIssues = Get-TokenPreservationIssues -Source ([string]$entry.Text) -Target $translated
        $missingTokens = @($tokenIssues.MissingTokens)
        $unexpectedTokens = @($tokenIssues.UnexpectedTokens)
        $tokenCountMismatches = @($tokenIssues.TokenCountMismatches)
        $isBlankCandidate = [string]::IsNullOrWhiteSpace($translated)
        $isPathologicalCandidate = Test-PathologicalTranslation $translated
        $invalidKoreanParticles = @(Get-InvalidKoreanParticleNotations $translated)
        if ($missingTokens.Count -gt 0) {
            [void]$warnings.Add([pscustomobject]@{
                id = $entry.Id
                key = $entry.Key
                source = $entry.Text
                translation = $translated
                missingTokens = $missingTokens
                reason = "missing_tokens"
            })
        }
        if ($unexpectedTokens.Count -gt 0 -or $tokenCountMismatches.Count -gt 0 -or $tokenIssues.GrammarPrefixMoved) {
            [void]$warnings.Add([pscustomobject]@{
                id = $entry.Id
                key = $entry.Key
                source = $entry.Text
                translation = $translated
                missingTokens = $missingTokens
                unexpectedTokens = $unexpectedTokens
                tokenCountMismatches = $tokenCountMismatches
                grammarPrefixMoved = [bool]$tokenIssues.GrammarPrefixMoved
                reason = "token_structure_changed"
            })
        }
        if ($isPathologicalCandidate) {
            [void]$warnings.Add([pscustomobject]@{
                id = $entry.Id
                key = $entry.Key
                source = $entry.Text
                translation = $translated
                missingTokens = @()
                reason = "pathological_newlines"
            })
        }
        if ($invalidKoreanParticles.Count -gt 0) {
            [void]$warnings.Add([pscustomobject]@{
                id = $entry.Id
                key = $entry.Key
                source = $entry.Text
                translation = $translated
                missingTokens = @()
                invalidKoreanParticles = $invalidKoreanParticles
                reason = "invalid_korean_particle_notation"
            })
        }

        $existingInfo = Get-ExistingEntryInfo $entry
        $existingTranslation = [string]$existingInfo.Text
        $referenceInfo = Get-ComparisonReferenceInfo -Entry $entry -ExistingInfo $existingInfo -RmkHistoryMap $rmkHistoryMap -RmkCurrentSourceMap $rmkCurrentSourceMap -RmkWorkbook $referenceSourceWorkbookFull
        $targetPath = Get-PathInsideRoot -Root $outputLanguageRoot -RelativePath (Get-EntryOutputRelativePath -Entry $entry -ExistingInfo $existingInfo)
        $candidateHasKorean = Test-ContainsKorean $translated
        $candidateSameAsSource = [string]::Equals($translated, [string]$entry.Text, [System.StringComparison]::Ordinal)
        $safeToWrite = -not $isBlankCandidate -and -not $isPathologicalCandidate -and $missingTokens.Count -eq 0 -and $unexpectedTokens.Count -eq 0 -and $tokenCountMismatches.Count -eq 0 -and -not $tokenIssues.GrammarPrefixMoved -and $invalidKoreanParticles.Count -eq 0 -and $candidateHasKorean -and -not $candidateSameAsSource
        if ($ReviewOnly -or $safeToWrite) {
            if (-not $outputGroups.ContainsKey($targetPath)) { $outputGroups[$targetPath] = @{} }
            $outputGroups[$targetPath][$entry.Key] = $translated
        } else {
            $skippedUnsafe++
        }

        [void]$translatedRows.Add([pscustomobject]@{
            id = $entry.Id
            key = $entry.Key
            kind = $entry.Kind
            defClass = $entry.TypeName
            node = $entry.Key
            field = $entry.Field
            target = $targetPath
            source = $entry.Text
            translation = $translated
            translationOrigin = "ai"
        })

        [void]$comparisonRows.Add([pscustomobject]@{
            id = $entry.Id
            key = $entry.Key
            kind = $entry.Kind
            defClass = $entry.TypeName
            node = $entry.Key
            field = $entry.Field
            target = $targetPath
            source = $entry.Text
            existing = $existingTranslation
            candidate = $translated
            existingOrigin = $referenceInfo.ExistingOrigin
            translationOrigin = "ai"
            translationUpdatedAt = ""
            rmkIdentifier = $referenceInfo.RmkIdentifier
            rmkHistoricalSource = $referenceInfo.RmkHistoricalSource
            rmkCurrentSource = $referenceInfo.RmkCurrentSource
            rmkSourceChanged = $referenceInfo.RmkSourceChanged
            rmkWorkbook = $referenceInfo.RmkWorkbook
            existingPresent = -not [string]::IsNullOrWhiteSpace($existingTranslation)
            existingHasKorean = Test-ContainsKorean $existingTranslation
            candidateHasKorean = $candidateHasKorean
            existingSameAsSource = $existingTranslation -eq ([string]$entry.Text)
            candidateSameAsSource = $candidateSameAsSource
            candidateBlank = $isBlankCandidate
            missingTokens = [string]::Join("|", $missingTokens)
            unexpectedTokens = [string]::Join("|", $unexpectedTokens)
            tokenCountMismatches = [string]::Join("|", $tokenCountMismatches)
            grammarPrefixMoved = [bool]$tokenIssues.GrammarPrefixMoved
            pathologicalCandidate = $isPathologicalCandidate
            invalidKoreanParticles = [string]::Join("|", $invalidKoreanParticles)
            safeToApply = $safeToWrite
        })
    }
    Write-TranslationCheckpoint -AuditBase $auditBase -TranslatedRows $translatedRows -ComparisonRows $comparisonRows -Warnings $warnings -CompletedBatches ($i + 1) -TotalBatches $batches.Count -Complete $false
}

Assert-TranslationNotCancelled
Write-TranslationFilesTransaction -OutputGroups $outputGroups -Overwrite:$Overwrite

Write-TranslationCheckpoint -AuditBase $auditBase -TranslatedRows $translatedRows -ComparisonRows $comparisonRows -Warnings $warnings -CompletedBatches $batches.Count -TotalBatches $batches.Count -Complete $true

$writtenFiles = @($outputGroups.Keys | Sort-Object)
Write-Host "Done."
Write-Host "Written/updated files: $($writtenFiles.Count)"
Write-Host "Translated entries: $($translatedRows.Count)"
Write-Host "Token warnings: $($warnings.Count)"
Write-Host "Skipped unsafe writes: $skippedUnsafe"
if ($ReviewOnly) { Write-Host "Review output: $reviewRunRoot" }
Write-Host "Audit: $auditBase-*.json"
