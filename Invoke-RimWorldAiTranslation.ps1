param(
    [Parameter(Mandatory = $true)]
    [string]$ModRoot,

    [string[]]$ApiKey = @(),
    [string]$ApiKeyFile,

    [string]$BaseUrl = "https://api.cerebras.ai/v1",
    [string]$Model = "gemma-4-31b",
    [string]$LanguageFolderName = "Korean",
    [string]$SourceLanguageFolder = "Auto",

    [int]$RequestsPerMinutePerKey = 5,
    [int]$InputTokensPerMinutePerKey = 30000,
    [int]$DailyTokenBudgetPerKey = 1000000,
    [int]$BatchSize = 80,
    [int]$MaxInputCharsPerBatch = 12000,
    [int]$MaxInputTokensPerBatch = 5500,
    [int]$MaxCompletionTokens = 32000,
    [int]$TimeoutSec = 180,
    [int]$MaxRetries = 4,
    [int]$Limit = 0,

    [switch]$IncludePatches,
    [switch]$Overwrite,
    [switch]$DryRun,
    [switch]$MockTranslations,
    [switch]$NoStructuredOutputs,
    [switch]$ReviewOnly,

    [string]$ExistingLanguageRoot,
    [string]$ReviewRoot,
    [string]$GeneratedGlossaryPath,
    [string]$CuratedGlossaryPath,
    [int]$MaxAlwaysGlossaryTerms = 180,
    [int]$MaxGeneratedGlossaryTermsPerBatch = 140,
    [switch]$UseCuratedGlossary,
    [string]$ExtraPrompt,
    [string]$ExtraPromptFile,

    [string]$OutputFilePrefix = "CodexAI"
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

function Get-FullPathAllowMissing([string]$Path) {
    return [System.IO.Path]::GetFullPath($Path)
}

function Get-ChatCompletionsUrl([string]$Url) {
    $trimmed = $Url.Trim().TrimEnd("/")
    if ($trimmed -match "/chat/completions$") { return $trimmed }
    if ($trimmed -match "/v1$") { return "$trimmed/chat/completions" }
    return "$trimmed/v1/chat/completions"
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

function Test-LooksLikeCodeOrPath([string]$Text) {
    $trim = $Text.Trim()
    if ($trim -match "^[\d\s.,:+\-/%\u00B0]+$") { return $true }
    if ($trim -match "^[A-Za-z_][A-Za-z0-9_.]*\.[A-Za-z_][A-Za-z0-9_.]*$") { return $true }
    if ($trim -match "^[A-Za-z0-9_./\\:-]+\.(png|jpg|jpeg|dds|tga|wav|ogg|mp3|dll|asset|shader|xml)$") { return $true }
    if ($trim -match "^[A-Za-z0-9_./\\:-]+/[A-Za-z0-9_./\\:-]+$") { return $true }
    if ($trim -match "^\{[A-Za-z0-9_:.-]+\}$") { return $true }
    return $false
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

    Get-ChildItem -LiteralPath $LanguageRoot -Recurse -File -Filter *.xml -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $doc = New-Object System.Xml.XmlDocument
            $doc.PreserveWhitespace = $false
            $doc.LoadXml([System.IO.File]::ReadAllText($_.FullName))
            if ($doc.DocumentElement -and $doc.DocumentElement.LocalName -eq "LanguageData") {
                foreach ($child in (Get-XmlElementChildren $doc.DocumentElement)) {
                    [void]$keys.Add($child.LocalName)
                }
            }
        } catch {
        }
    }
    return ,$keys
}

function Get-ExistingLanguageMap([string]$LanguageRoot) {
    $map = @{}
    if (-not (Test-Path -LiteralPath $LanguageRoot)) { return $map }

    Get-ChildItem -LiteralPath $LanguageRoot -Recurse -File -Filter *.xml -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $doc = New-Object System.Xml.XmlDocument
            $doc.PreserveWhitespace = $false
            $doc.LoadXml([System.IO.File]::ReadAllText($_.FullName))
            if ($doc.DocumentElement -and $doc.DocumentElement.LocalName -eq "LanguageData") {
                foreach ($child in (Get-XmlElementChildren $doc.DocumentElement)) {
                    if (-not $map.ContainsKey($child.LocalName)) {
                        $map[$child.LocalName] = ConvertTo-FlatString $child.InnerText
                    }
                }
            }
        } catch {
        }
    }
    return $map
}

function Import-LanguageDataEntries([string]$FilePath, [string]$TargetRelativePath, [string]$Kind, [string]$TypeName) {
    $entries = New-Object "System.Collections.Generic.List[object]"
    try {
        $doc = New-Object System.Xml.XmlDocument
        $doc.PreserveWhitespace = $false
        $doc.LoadXml([System.IO.File]::ReadAllText($FilePath))
    } catch {
        Write-Warning "Skipping invalid XML: $FilePath"
        return ,$entries
    }

    if (-not $doc.DocumentElement -or $doc.DocumentElement.LocalName -ne "LanguageData") { return ,$entries }

    foreach ($child in (Get-XmlElementChildren $doc.DocumentElement)) {
        $text = ConvertTo-FlatString $child.InnerText
        if (-not (Test-HasHumanText $text)) { continue }
        if (Test-LooksLikeCodeOrPath $text) { continue }
        [void]$entries.Add([pscustomobject]@{
            Id = ""
            Key = $child.LocalName
            Text = $text
            Kind = $Kind
            TypeName = $TypeName
            TargetRelativePath = $TargetRelativePath
            SourceFile = $FilePath
            Field = ($child.LocalName -replace "^.*\.", "")
        })
    }
    return ,$entries
}

function Test-TranslatableDefPath([string[]]$PathSegments) {
    if ($PathSegments.Count -eq 0) { return $false }
    $leaf = $PathSegments[$PathSegments.Count - 1].ToLowerInvariant()
    $full = ([string]::Join(".", $PathSegments)).ToLowerInvariant()

    $exactLeafs = @(
        "label", "labelshort", "description", "jobstring", "reportstring",
        "deathmessage", "deathmessagefemale", "deathmessagemale",
        "pawnsplural", "leadertitle", "arrivedletter", "customlabel",
        "gizmolabel", "gizmodescription", "commandlabel", "commanddescription",
        "letterlabel", "lettertext", "header", "headertip", "summary",
        "formatstring", "formatstringunfinalized", "fixedname", "reason"
    )
    if ($exactLeafs -contains $leaf) { return $true }
    if ($leaf -eq "text" -and $full -match "(letter|message|scenario|quest|dialog|help|tip|inspect)") { return $true }
    if ($leaf -eq "slateref" -and $full -match "(letter|text|label|description|inspect|string)") { return $true }
    if ($leaf -eq "li" -and $full -match "(rulesstrings|tagsstrings)") { return $true }
    return $false
}

function Test-ExcludedDefPath([string[]]$PathSegments) {
    $full = ([string]::Join(".", $PathSegments)).ToLowerInvariant()
    $excluded = @(
        "defname", "parentname", "classname", "thingclass", "workerclass",
        "compclass", "hediffclass", "thoughtclass", "abilityclass",
        "texpath", "texname", "graphicpath", "shader", "sound", "iconpath",
        "modextension", "li.class", "packageid", "xpath", "operation"
    )
    foreach ($item in $excluded) {
        if ($full -eq $item -or $full -match "(^|\.)$([regex]::Escape($item))($|\.)") { return $true }
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
        if (Test-LooksLikeCodeOrPath $text) { return }

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
    $defsRoot = Join-Path $ModRoot "Defs"
    if (Test-Path -LiteralPath $defsRoot) { [void]$candidateRoots.Add($defsRoot) }
    if ($IncludePatches) {
        $patchesRoot = Join-Path $ModRoot "Patches"
        if (Test-Path -LiteralPath $patchesRoot) { [void]$candidateRoots.Add($patchesRoot) }
    }

    foreach ($root in $candidateRoots) {
        Get-ChildItem -LiteralPath $root -Recurse -File -Filter *.xml -ErrorAction SilentlyContinue | Sort-Object FullName | ForEach-Object {
            try {
                $doc = New-Object System.Xml.XmlDocument
                $doc.PreserveWhitespace = $false
                $doc.LoadXml([System.IO.File]::ReadAllText($_.FullName))
            } catch {
                Write-Warning "Skipping invalid XML: $($_.FullName)"
                return
            }

            if (-not $doc.DocumentElement -or $doc.DocumentElement.LocalName -ne "Defs") { return }

            foreach ($def in (Get-XmlElementChildren $doc.DocumentElement)) {
                $defName = Get-DirectChildText -Node $def -Name "defName"
                if (-not $defName) { continue }
                $segment = ConvertTo-KeySegment $defName
                if (-not $segment) { continue }

                $typeName = $def.LocalName
                $targetRelative = Join-Path (Join-Path "DefInjected" $typeName) "$OutputFilePrefix.xml"
                foreach ($child in (Get-XmlElementChildren $def)) {
                    if ($child.LocalName -eq "defName") { continue }
                    Add-DefInjectedLeafEntries `
                        -Node $child `
                        -PathSegments @($child.LocalName) `
                        -DefName $segment `
                        -TypeName $typeName `
                        -TargetRelativePath $targetRelative `
                        -SourceFile $_.FullName `
                        -Entries $entries
                }
            }
        }
    }
    return ,$entries
}

function Test-LanguageRootHasXml([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    return $null -ne (Get-ChildItem -LiteralPath $Path -Recurse -File -Filter *.xml -ErrorAction SilentlyContinue | Select-Object -First 1)
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
    $languagesRoot = Join-Path $ModRoot "Languages"
    if (-not (Test-Path -LiteralPath $languagesRoot)) { return ,$roots }

    if ($SourceLanguageFolder -and $SourceLanguageFolder -ne "Auto") {
        $explicitRoot = if ([System.IO.Path]::IsPathRooted($SourceLanguageFolder)) {
            $SourceLanguageFolder
        } else {
            Join-Path $languagesRoot $SourceLanguageFolder
        }
        if (-not (Test-LanguageRootHasXml $explicitRoot)) {
            throw "Source language folder has no XML files: $explicitRoot"
        }
        [void]$roots.Add([pscustomobject]@{
            Name = Split-Path -Leaf $explicitRoot
            Path = [System.IO.Path]::GetFullPath($explicitRoot)
            Rank = -1
        })
        return ,$roots
    }

    $candidates = New-Object "System.Collections.Generic.List[object]"
    Get-ChildItem -LiteralPath $languagesRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        if (Test-ExcludedSourceLanguageFolder $_.Name) { return }
        if (-not (Test-LanguageRootHasXml $_.FullName)) { return }
        [void]$candidates.Add([pscustomobject]@{
            Name = $_.Name
            Path = $_.FullName
            Rank = Get-SourceLanguageRank $_.Name
        })
    }

    $best = $candidates | Sort-Object Rank, Name | Select-Object -First 1
    if ($best) { [void]$roots.Add($best) }
    return ,$roots
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

function Select-GlossaryTermsForBatch([object[]]$Terms, [object[]]$Batch, [int]$MaxAlways, [int]$MaxGenerated) {
    if (-not $Terms -or $Terms.Count -eq 0) { return @() }
    $selected = New-Object "System.Collections.Generic.List[object]"
    $selectedSources = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($term in @($Terms | Where-Object { $_.alwaysInclude } | Select-Object -First $MaxAlways)) {
        if ($selectedSources.Add([string]$term.source)) { [void]$selected.Add($term) }
    }

    if ($Batch -and $MaxGenerated -gt 0) {
        $textBlob = [string]::Join("`n", @($Batch | ForEach-Object { [string]$_.Text }))
        $generated = @($Terms |
            Where-Object { -not $_.alwaysInclude -and -not $selectedSources.Contains([string]$_.source) -and (Test-GlossaryTermAppears -TermSource ([string]$_.source) -Text $textBlob) } |
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
- Preserve placeholders and markup exactly: {0}, {PAWN_nameDef}, [pawn_nameDef], `$variable, <color=...>, </color>, \n, %, and XML-like tags.
- Keep label fields short, usually a noun phrase.
- Use polite declarative Korean for descriptions and letters when appropriate.
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

function New-RequestBody([string]$Model, [string]$SystemPrompt, [string]$UserPayload, [int]$MaxCompletionTokens, [switch]$NoStructuredOutputs) {
    $body = [ordered]@{
        model = $Model
        messages = @(
            [ordered]@{ role = "system"; content = $SystemPrompt },
            [ordered]@{ role = "user"; content = $UserPayload }
        )
        temperature = 0.1
        top_p = 0.9
        stream = $false
        max_completion_tokens = $MaxCompletionTokens
        reasoning_effort = "none"
    }

    if (-not $NoStructuredOutputs) {
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
    } else {
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
        if ($name -notmatch "^(CEREBRAS_API_KEY|CEREBRAS_KEY|API_KEY|KEY|RIMWORLD_TRANSLATOR_API_KEYS)$") { return }
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

function Get-ApiKeys([string[]]$ApiKey, [string]$ApiKeyFile) {
    $all = New-Object "System.Collections.Generic.List[string]"
    foreach ($key in $ApiKey) {
        Add-ApiKeyCandidate $all $key
    }
    if ($ApiKeyFile) {
        if (-not (Test-Path -LiteralPath $ApiKeyFile)) { throw "API key file not found: $ApiKeyFile" }
        foreach ($line in [System.IO.File]::ReadAllLines((Resolve-Path -LiteralPath $ApiKeyFile).Path)) {
            Add-ApiKeyCandidate $all $line
        }
    }
    if ($env:CEREBRAS_API_KEY) { Add-ApiKeyCandidate $all $env:CEREBRAS_API_KEY }
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
    return ,$unique
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
    return ,$states
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
                Start-Sleep -Milliseconds $sleepMs
            }
            continue
        }

        Reset-InputTokenWindowIfNeeded $state
        $spacing = [Math]::Ceiling(60.0 / [Math]::Max(1, $RequestsPerMinutePerKey))
        $state.AvailableAt = [DateTime]::UtcNow.AddSeconds($spacing)
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

function Get-HttpErrorDetail([System.Management.Automation.ErrorRecord]$ErrorRecord) {
    $code = $null
    $body = ""
    try {
        if ($ErrorRecord.Exception.Response) {
            $code = [int]$ErrorRecord.Exception.Response.StatusCode
            $stream = $ErrorRecord.Exception.Response.GetResponseStream()
            if ($stream) {
                $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
                $body = $reader.ReadToEnd()
            }
        }
    } catch {
    }
    if (-not $body -and $ErrorRecord.ErrorDetails) { $body = $ErrorRecord.ErrorDetails.Message }
    return [pscustomobject]@{ Code = $code; Body = $body }
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
    try {
        $responseStream = $response.GetResponseStream()
        $reader = New-Object System.IO.StreamReader($responseStream, [System.Text.Encoding]::UTF8)
        $raw = $reader.ReadToEnd()
        return $raw | ConvertFrom-Json
    } finally {
        $response.Close()
    }
}

function Invoke-CerebrasChat([string]$ChatUrl, [object]$Body, [object[]]$KeyStates, [int]$RequestsPerMinutePerKey, [int]$InputTokensPerMinutePerKey, [int]$DailyTokenBudgetPerKey, [int]$TimeoutSec, [int]$MaxRetries) {
    $json = $Body | ConvertTo-Json -Depth 30 -Compress
    $estimatedInputTokens = (Estimate-TokenCount $json) + 256
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $lastError = $null

    for ($attempt = 1; $attempt -le $MaxRetries; $attempt++) {
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
            if ($detail.Code -ge 500 -or $detail.Code -eq $null) {
                Start-Sleep -Seconds ([Math]::Min(30, 2 * $attempt))
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

function ConvertTo-TranslationMap([object]$Response) {
    $content = [string]$Response.choices[0].message.content
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

function Get-ProtectedTokens([string]$Text) {
    $tokens = New-Object "System.Collections.Generic.HashSet[string]"
    $pattern = '(\{[^}]+\}|\[[A-Za-z0-9_.:;''" -]+\]|<[^>]+>|\$[A-Za-z_][A-Za-z0-9_]*|%[0-9.]*[sdif]|\b[A-Z]{2,}_[A-Z0-9_]+\b)'
    foreach ($match in [System.Text.RegularExpressions.Regex]::Matches($Text, $pattern)) {
        [void]$tokens.Add($match.Value)
    }
    $result = New-Object string[] $tokens.Count
    $tokens.CopyTo($result)
    return $result
}

function Test-TokenPreservation([string]$Source, [string]$Target) {
    $missing = New-Object "System.Collections.Generic.List[string]"
    foreach ($token in (Get-ProtectedTokens $Source)) {
        if (-not $Target.Contains($token)) { [void]$missing.Add($token) }
    }
    return $missing.ToArray()
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

    $doc = New-Object System.Xml.XmlDocument
    $doc.PreserveWhitespace = $false
    $doc.LoadXml([System.IO.File]::ReadAllText($Path))
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

function Escape-XmlText([string]$Text) {
    return [System.Security.SecurityElement]::Escape((Remove-InvalidXmlChars $Text))
}

function Write-LanguageFile([string]$Path, [hashtable]$Entries, [switch]$Overwrite) {
    $existing = Read-LanguageFile $Path
    foreach ($key in ($Entries.Keys | Sort-Object)) {
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
    [System.IO.File]::WriteAllLines($Path, $lines, [System.Text.UTF8Encoding]::new($false))
}

function Write-AuditFile([string]$Path, [object[]]$Rows) {
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    $Rows | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Write-CsvFile([string]$Path, [object[]]$Rows) {
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    $Rows | Export-Csv -LiteralPath $Path -NoTypeInformation -Encoding UTF8
}

function Test-ContainsKorean([string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }
    return $Text -match "[\uAC00-\uD7AF]"
}

function New-ReviewRunRoot([string]$BaseRoot, [string]$ModFullPath, [string]$Stamp) {
    $leaf = Split-Path -Leaf $ModFullPath
    if (-not $leaf) { $leaf = "mod" }
    $safeLeaf = $leaf -replace "[^A-Za-z0-9_.-]", "_"
    return Join-Path $BaseRoot "$safeLeaf-$Stamp"
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$modFull = Resolve-FullPath $ModRoot
$languageRoot = Join-Path (Join-Path $modFull "Languages") $LanguageFolderName
$existingLanguageFull = if ($ExistingLanguageRoot) { Resolve-FullPath $ExistingLanguageRoot } else { $languageRoot }
$chatUrl = Get-ChatCompletionsUrl $BaseUrl
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
$glossary = Import-Glossary -ScriptRoot $scriptRoot -GeneratedGlossaryPath $GeneratedGlossaryPath -CuratedGlossaryPath $CuratedGlossaryPath -UseCuratedGlossary:$UseCuratedGlossary
$baseGlossary = Select-GlossaryTermsForBatch -Terms $glossary -Batch @() -MaxAlways $MaxAlwaysGlossaryTerms -MaxGenerated 0
$systemPrompt = New-SystemPrompt -GlossaryText (Convert-GlossaryToPrompt $baseGlossary) -ExtraPrompt $ExtraPrompt
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
$auditBase = Join-Path $auditRoot "cerebras-$auditStamp"

Write-Host "Mod root: $modFull"
Write-Host "Output language: $outputLanguageRoot"
Write-Host "Existing translation root: $existingLanguageFull"
if ($ReviewOnly) { Write-Host "Review-only mode: source/existing translations will not be modified." }
Write-Host "Cerebras chat endpoint: $chatUrl"
Write-Host "Model: $Model"
Write-Host "Free-tier guardrails: $RequestsPerMinutePerKey requests/min/key, $InputTokensPerMinutePerKey input tokens/min/key, $DailyTokenBudgetPerKey total tokens/day/key, $MaxCompletionTokens max output tokens"
Write-Host "Glossary terms loaded: $($glossary.Count) total, $(@($glossary | Where-Object { $_.alwaysInclude }).Count) always-on, $MaxGeneratedGlossaryTermsPerBatch generated terms max/batch"

$keys = Get-ApiKeys -ApiKey $ApiKey -ApiKeyFile $ApiKeyFile
if (-not $DryRun -and -not $MockTranslations -and $keys.Count -eq 0) {
    throw "No API key provided. Use -ApiKey, -ApiKeyFile, CEREBRAS_API_KEY, or RIMWORLD_TRANSLATOR_API_KEYS."
}
if ($keys.Count -gt 0) { Write-Host "API keys loaded: $($keys.Count)" }
if ($keys.Count -gt 1) { Write-Host "API key rotation: input order, balanced by per-key request/token availability." }
$keyStates = New-KeyStates $keys

$existingKeys = Get-ExistingLanguageKeys $existingLanguageFull
$existingMap = Get-ExistingLanguageMap $existingLanguageFull
$sourceEntries = Import-SourceEntries -ModRoot $modFull -OutputFilePrefix $OutputFilePrefix -SourceLanguageFolder $SourceLanguageFolder -IncludePatches:$IncludePatches
$dedupe = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
$pending = New-Object "System.Collections.Generic.List[object]"
$skippedExisting = 0
$skippedDuplicate = 0

foreach ($entry in $sourceEntries) {
    if ($existingKeys.Contains($entry.Key) -and -not $Overwrite -and -not $ReviewOnly) {
        $skippedExisting++
        continue
    }
    $identity = $entry.Key
    if (-not $dedupe.Add($identity)) {
        $skippedDuplicate++
        continue
    }
    if ($Limit -gt 0 -and $pending.Count -ge $Limit) { break }
    $entry.Id = "E{0:d6}" -f ($pending.Count + 1)
    [void]$pending.Add($entry)
}

if ($script:DetectedSourceLanguageRoots -and $script:DetectedSourceLanguageRoots.Count -gt 0) {
    Write-Host "Detected source language: $([string]::Join(', ', @($script:DetectedSourceLanguageRoots | ForEach-Object { $_.Name })))"
} else {
    Write-Host "Detected source language: none; using Defs text only."
}
Write-Host "Source entries: $($sourceEntries.Count)"
Write-Host "Pending entries: $($pending.Count)"
Write-Host "Skipped existing: $skippedExisting"
Write-Host "Skipped duplicate: $skippedDuplicate"

if ($pending.Count -eq 0) {
    Write-Host "Nothing to translate."
    return
}

if ($DryRun) {
    Write-Host "Dry run complete. No API calls or translation files were written."
    return
}

Write-AuditFile -Path "$auditBase-source.json" -Rows $pending

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

for ($i = 0; $i -lt $batches.Count; $i++) {
    $batch = @($batches[$i])
    Write-Host ("Translating batch {0}/{1} ({2} entries)..." -f ($i + 1), $batches.Count, $batch.Count)

    $map = $null
    if ($MockTranslations) {
        $map = @{}
        foreach ($entry in $batch) {
            $map[$entry.Id] = "MOCK: $($entry.Text)"
        }
    } else {
        $body = New-RequestBody `
            -Model $Model `
            -SystemPrompt (New-SystemPrompt -GlossaryText (Convert-GlossaryToPrompt (Select-GlossaryTermsForBatch -Terms $glossary -Batch $batch -MaxAlways $MaxAlwaysGlossaryTerms -MaxGenerated $MaxGeneratedGlossaryTermsPerBatch)) -ExtraPrompt $ExtraPrompt) `
            -UserPayload (New-UserPayload $batch) `
            -MaxCompletionTokens $MaxCompletionTokens `
            -NoStructuredOutputs:$NoStructuredOutputs

        for ($attempt = 1; $attempt -le $MaxRetries; $attempt++) {
            try {
                $response = Invoke-CerebrasChat `
                    -ChatUrl $chatUrl `
                    -Body $body `
                    -KeyStates $keyStates `
                    -RequestsPerMinutePerKey $RequestsPerMinutePerKey `
                    -InputTokensPerMinutePerKey $InputTokensPerMinutePerKey `
                    -DailyTokenBudgetPerKey $DailyTokenBudgetPerKey `
                    -TimeoutSec $TimeoutSec `
                    -MaxRetries $MaxRetries
                $map = ConvertTo-TranslationMap $response
                $missingIds = @($batch | Where-Object { -not $map.ContainsKey($_.Id) })
                if ($missingIds.Count -eq 0) { break }
                Write-Warning "Model response missed $($missingIds.Count) ids; retrying batch."
            } catch {
                if ($attempt -ge $MaxRetries) { throw }
                Write-Warning "Batch parse/request failed on attempt $attempt; retrying. $($_.Exception.Message)"
                Start-Sleep -Seconds ([Math]::Min(30, 2 * $attempt))
            }
        }
    }

    foreach ($entry in $batch) {
        $translated = if ($map -and $map.ContainsKey($entry.Id)) { [string]$map[$entry.Id] } else { [string]$entry.Text }
        $translated = Remove-InvalidXmlChars $translated
        $missingTokens = @(Test-TokenPreservation -Source ([string]$entry.Text) -Target $translated)
        $isBlankCandidate = [string]::IsNullOrWhiteSpace($translated)
        if ($missingTokens.Count -gt 0) {
            [void]$warnings.Add([pscustomobject]@{
                id = $entry.Id
                key = $entry.Key
                source = $entry.Text
                translation = $translated
                missingTokens = $missingTokens
            })
        }

        $existingTranslation = if ($existingMap.ContainsKey($entry.Key)) { [string]$existingMap[$entry.Key] } else { "" }
        $targetPath = Join-Path $outputLanguageRoot $entry.TargetRelativePath
        $safeToWrite = -not $isBlankCandidate -and $missingTokens.Count -eq 0
        if ($ReviewOnly -or $safeToWrite) {
            if (-not $outputGroups.ContainsKey($targetPath)) { $outputGroups[$targetPath] = @{} }
            $outputGroups[$targetPath][$entry.Key] = $translated
        } else {
            $skippedUnsafe++
        }

        [void]$translatedRows.Add([pscustomobject]@{
            id = $entry.Id
            key = $entry.Key
            target = $targetPath
            source = $entry.Text
            translation = $translated
        })

        [void]$comparisonRows.Add([pscustomobject]@{
            id = $entry.Id
            key = $entry.Key
            target = $targetPath
            source = $entry.Text
            existing = $existingTranslation
            candidate = $translated
            existingPresent = -not [string]::IsNullOrWhiteSpace($existingTranslation)
            existingHasKorean = Test-ContainsKorean $existingTranslation
            candidateHasKorean = Test-ContainsKorean $translated
            existingSameAsSource = $existingTranslation -eq ([string]$entry.Text)
            candidateSameAsSource = $translated -eq ([string]$entry.Text)
            candidateBlank = $isBlankCandidate
            missingTokens = [string]::Join("|", $missingTokens)
            safeToApply = $safeToWrite
        })
    }
}

foreach ($targetPath in ($outputGroups.Keys | Sort-Object)) {
    Write-LanguageFile -Path $targetPath -Entries $outputGroups[$targetPath] -Overwrite:$Overwrite
}

Write-AuditFile -Path "$auditBase-translated.json" -Rows $translatedRows
Write-AuditFile -Path "$auditBase-comparison.json" -Rows $comparisonRows
Write-CsvFile -Path "$auditBase-comparison.csv" -Rows $comparisonRows
Write-AuditFile -Path "$auditBase-token-warnings.json" -Rows $warnings

$writtenFiles = @($outputGroups.Keys | Sort-Object)
Write-Host "Done."
Write-Host "Written/updated files: $($writtenFiles.Count)"
Write-Host "Translated entries: $($translatedRows.Count)"
Write-Host "Token warnings: $($warnings.Count)"
Write-Host "Skipped unsafe writes: $skippedUnsafe"
if ($ReviewOnly) { Write-Host "Review output: $reviewRunRoot" }
Write-Host "Audit: $auditBase-*.json"
