param(
    [string]$RimWorldDataRoot = "",
    [string]$RmkRoot = "",
    [string]$WorkshopRoot = "",
    [string]$GameVersion = "1.6",
    [string[]]$WorkshopId = @(),
    [string]$OutputPath,
    [string]$ConflictPath,
    [int]$MaxSourceChars = 80,
    [int]$MinRmkOccurrences = 1,
    [int]$MaxRmkMods = 0,
    [switch]$IncludeSentences,
    [switch]$IncludePatches,
    [switch]$IncludeRmk,
    [switch]$SkipOfficial,
    [switch]$SkipRmk
)

$ErrorActionPreference = "Stop"

function Resolve-FullPathAllowMissing([string]$Path) {
    return [System.IO.Path]::GetFullPath($Path)
}

function Add-UniqueExistingDirectory($List, $Seen, [string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    try { $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\') } catch { return }
    if (-not (Test-Path -LiteralPath $full -PathType Container)) { return }
    if ($Seen.Add($full)) { [void]$List.Add($full) }
}

function Get-SteamLibraryRoots {
    $roots = [System.Collections.Generic.List[string]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($regPath in @("HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam", "HKLM:\SOFTWARE\Valve\Steam")) {
        try {
            $props = Get-ItemProperty -LiteralPath $regPath -ErrorAction Stop
            Add-UniqueExistingDirectory $roots $seen $props.SteamPath
            Add-UniqueExistingDirectory $roots $seen $props.InstallPath
        } catch {}
    }
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, $env:LOCALAPPDATA)) {
        if ($base) { Add-UniqueExistingDirectory $roots $seen (Join-Path $base "Steam") }
    }
    foreach ($drive in [System.IO.DriveInfo]::GetDrives()) {
        if (-not $drive.IsReady -or $drive.DriveType -ne [System.IO.DriveType]::Fixed) { continue }
        Add-UniqueExistingDirectory $roots $seen (Join-Path $drive.RootDirectory.FullName "SteamLibrary")
        Add-UniqueExistingDirectory $roots $seen (Join-Path $drive.RootDirectory.FullName "Steam")
    }
    foreach ($root in @($roots.ToArray())) {
        foreach ($vdfPath in @((Join-Path $root "steamapps\libraryfolders.vdf"), (Join-Path $root "config\libraryfolders.vdf"))) {
            if (-not (Test-Path -LiteralPath $vdfPath -PathType Leaf)) { continue }
            try {
                $text = [System.IO.File]::ReadAllText($vdfPath)
                foreach ($match in [System.Text.RegularExpressions.Regex]::Matches($text, '"path"\s+"([^"]+)"')) {
                    Add-UniqueExistingDirectory $roots $seen ($match.Groups[1].Value -replace '\\\\', '\')
                }
                foreach ($match in [System.Text.RegularExpressions.Regex]::Matches($text, '(?m)^\s*"\d+"\s+"([^"]+)"')) {
                    Add-UniqueExistingDirectory $roots $seen ($match.Groups[1].Value -replace '\\\\', '\')
                }
            } catch {}
        }
    }
    return $roots.ToArray()
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

function ConvertTo-FlatString([object]$Value) {
    if ($null -eq $Value) { return "" }
    return ([string]$Value).Replace("`r`n", "`n").Replace("`r", "`n").Trim()
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

function Test-HasHumanText([string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }
    if ($Text -match "\p{L}") { return $true }
    return $false
}

function Test-ContainsKorean([string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }
    return $Text -match "[\uAC00-\uD7AF]"
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
    [System.Collections.Generic.List[object]]$Entries,
    [System.Collections.IDictionary]$WantedKeys
) {
    $children = Get-XmlElementChildren $Node
    if ($children.Count -eq 0) {
        if (Test-ExcludedDefPath $PathSegments) { return }
        if (-not (Test-TranslatableDefPath $PathSegments)) { return }

        $text = ConvertTo-FlatString $Node.InnerText
        if (-not (Test-HasHumanText $text)) { return }
        if (Test-LooksLikeCodeOrPath $text) { return }

        $path = [string]::Join(".", $PathSegments)
        $key = "$DefName.$path"
        if ($WantedKeys -and -not $WantedKeys.Contains($key)) { return }

        [void]$Entries.Add([pscustomobject]@{
            Key = $key
            Text = $text
            Kind = "DefInjected"
            TypeName = $TypeName
            RelativePath = $TargetRelativePath
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
            -Entries $Entries `
            -WantedKeys $WantedKeys
    }
}

function Import-DefEntriesFromDefs([string]$ContentRoot, [switch]$IncludePatches, [System.Collections.IDictionary]$WantedKeys) {
    $entries = New-Object "System.Collections.Generic.List[object]"
    $candidateRoots = New-Object "System.Collections.Generic.List[string]"
    $defsRoot = Join-Path $ContentRoot "Defs"
    if (Test-Path -LiteralPath $defsRoot) { [void]$candidateRoots.Add($defsRoot) }
    if ($IncludePatches) {
        $patchesRoot = Join-Path $ContentRoot "Patches"
        if (Test-Path -LiteralPath $patchesRoot) { [void]$candidateRoots.Add($patchesRoot) }
    }

    foreach ($root in $candidateRoots) {
        Get-ChildItem -LiteralPath $root -Recurse -File -Filter *.xml -ErrorAction SilentlyContinue | Sort-Object FullName | ForEach-Object {
            try {
                $doc = Read-SafeXmlDocument $_.FullName
            } catch {
                return
            }

            if (-not $doc.DocumentElement -or $doc.DocumentElement.LocalName -ne "Defs") { return }

            foreach ($def in (Get-XmlElementChildren $doc.DocumentElement)) {
                $defName = Get-DirectChildText -Node $def -Name "defName"
                if (-not $defName) { continue }
                $segment = ConvertTo-KeySegment $defName
                if (-not $segment) { continue }

                $typeName = $def.LocalName
                $targetRelative = Join-Path (Join-Path "DefInjected" $typeName) "Defs.xml"
                foreach ($child in (Get-XmlElementChildren $def)) {
                    if ($child.LocalName -eq "defName") { continue }
                    Add-DefInjectedLeafEntries `
                        -Node $child `
                        -PathSegments @($child.LocalName) `
                        -DefName $segment `
                        -TypeName $typeName `
                        -TargetRelativePath $targetRelative `
                        -SourceFile $_.FullName `
                        -Entries $entries `
                        -WantedKeys $WantedKeys
                }
            }
        }
    }
    return ,$entries
}

function Import-LanguageDataMap([string]$LanguageRoot, [System.Collections.IDictionary]$WantedKeys) {
    $map = @{}
    if (-not (Test-Path -LiteralPath $LanguageRoot)) { return $map }

    Get-ChildItem -LiteralPath $LanguageRoot -Recurse -File -Filter *.xml -ErrorAction SilentlyContinue | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($LanguageRoot.Length).TrimStart("\", "/")
        $parts = $relative -split "[\\/]"
        $kind = if ($parts.Count -gt 0) { $parts[0] } else { "" }
        $typeName = if ($kind -eq "DefInjected" -and $parts.Count -gt 1) { $parts[1] } else { "" }

        try {
            $doc = Read-SafeXmlDocument $_.FullName
        } catch {
            return
        }

        if (-not $doc.DocumentElement -or $doc.DocumentElement.LocalName -ne "LanguageData") { return }

        foreach ($child in (Get-XmlElementChildren $doc.DocumentElement)) {
            if ($WantedKeys -and -not $WantedKeys.Contains($child.LocalName)) { continue }
            $text = ConvertTo-FlatString $child.InnerText
            if (-not (Test-HasHumanText $text)) { continue }
            if (Test-LooksLikeCodeOrPath $text) { continue }
            if (-not $map.ContainsKey($child.LocalName)) {
                $map[$child.LocalName] = [pscustomobject]@{
                    Key = $child.LocalName
                    Text = $text
                    Kind = $kind
                    TypeName = $typeName
                    RelativePath = $relative
                    SourceFile = $_.FullName
                    Field = ($child.LocalName -replace "^.*\.", "")
                }
            }
        }
    }

    return $map
}

function Import-ModSourceMap([string]$ModRoot, [string]$GameVersion, [switch]$IncludePatches, [System.Collections.IDictionary]$WantedKeys) {
    $map = @{}
    foreach ($root in (Get-ModContentRoots -ModRoot $ModRoot -GameVersion $GameVersion)) {
        $englishRoot = Join-Path (Join-Path $root "Languages") "English"
        foreach ($pair in (Import-LanguageDataMap -LanguageRoot $englishRoot -WantedKeys $WantedKeys).GetEnumerator()) {
            if (-not $map.ContainsKey($pair.Key)) { $map[$pair.Key] = $pair.Value }
        }
        foreach ($entry in (Import-DefEntriesFromDefs -ContentRoot $root -IncludePatches:$IncludePatches -WantedKeys $WantedKeys)) {
            if (-not $map.ContainsKey($entry.Key)) { $map[$entry.Key] = $entry }
        }
    }
    return $map
}

function Test-HasModContent([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    if (Test-Path -LiteralPath (Join-Path $Path "Defs")) { return $true }
    if (Test-Path -LiteralPath (Join-Path $Path "Languages")) { return $true }
    return $false
}

function Get-ModContentRoots([string]$ModRoot, [string]$GameVersion) {
    $roots = New-Object "System.Collections.Generic.List[string]"
    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)

    function Add-Root([string]$Path) {
        if (-not (Test-HasModContent $Path)) { return }
        $full = [System.IO.Path]::GetFullPath($Path)
        if ($seen.Add($full)) { [void]$roots.Add($full) }
    }

    Add-Root $ModRoot
    foreach ($name in @("Common", $GameVersion, "1.6", "1.5", "1.4", "1.3")) {
        if ($name) { Add-Root (Join-Path $ModRoot $name) }
    }

    Get-ChildItem -LiteralPath $ModRoot -Directory -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -match "^\d+\.\d+$"
    } | Sort-Object Name -Descending | ForEach-Object {
        Add-Root $_.FullName
    }

    return ,$roots
}

function Test-LooksLikeGlossaryPair(
    [string]$Source,
    [string]$Korean,
    [int]$MaxSourceChars,
    [switch]$IncludeSentences
) {
    $src = ConvertTo-FlatString $Source
    $ko = ConvertTo-FlatString $Korean
    if (-not (Test-HasHumanText $src)) { return $false }
    if (-not (Test-ContainsKorean $ko)) { return $false }
    if ($src -eq $ko) { return $false }
    if (Test-LooksLikeCodeOrPath $src) { return $false }
    if (Test-LooksLikeCodeOrPath $ko) { return $false }
    if ($src.Length -gt $MaxSourceChars) { return $false }
    if ($ko.Length -gt [Math]::Max(120, $MaxSourceChars * 2)) { return $false }
    if ($src -match "[`r`n]" -or $ko -match "[`r`n]") { return $false }
    if ($src -match "\{[A-Za-z0-9_:.-]+\}|\[[A-Za-z0-9_:.-]+\]|\$[A-Za-z_]|<[^>]+>") { return $false }
    if (-not $IncludeSentences) {
        $wordCount = @($src -split "\s+" | Where-Object { $_ }).Count
        if ($wordCount -gt 10) { return $false }
        if ($src -match "[.!?]\s*$") { return $false }
        if ($ko -match "[.!?\u3002\uff01\uff1f]\s*$") { return $false }
    }
    return $true
}

function Add-Observation(
    [System.Collections.Generic.List[object]]$Observations,
    [object]$SourceEntry,
    [object]$KoreanEntry,
    [string]$Origin,
    [int]$Priority,
    [string]$WorkshopId,
    [string]$ModName
) {
    $sourceText = ConvertTo-FlatString $SourceEntry.Text
    $koText = ConvertTo-FlatString $KoreanEntry.Text
    if (-not (Test-LooksLikeGlossaryPair -Source $sourceText -Korean $koText -MaxSourceChars $MaxSourceChars -IncludeSentences:$IncludeSentences)) {
        return
    }

    [void]$Observations.Add([pscustomobject]@{
        source = $sourceText
        sourceKey = $sourceText.ToLowerInvariant()
        ko = $koText
        koKey = $koText.ToLowerInvariant()
        key = $SourceEntry.Key
        field = $SourceEntry.Field
        kind = $SourceEntry.Kind
        typeName = $SourceEntry.TypeName
        priority = $Priority
        origin = $Origin
        workshopId = $WorkshopId
        modName = $ModName
        sourceFile = $SourceEntry.SourceFile
        koreanFile = $KoreanEntry.SourceFile
    })
}

function Remove-TempDirectory([string]$Path) {
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return }
    $full = [System.IO.Path]::GetFullPath($Path)
    $temp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if (-not $full.StartsWith($temp, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a temp directory outside the OS temp root: $full"
    }
    Remove-Item -LiteralPath $full -Recurse -Force
}

function Expand-LanguageTar([string]$TarPath) {
    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("rimworld-ko-glossary-" + [System.Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    $tarExe = Join-Path $env:SystemRoot "System32\tar.exe"
    if (-not (Test-Path -LiteralPath $tarExe -PathType Leaf)) {
        Remove-TempDirectory $tempRoot
        throw "Windows tar.exe was not found at the expected system path: $tarExe"
    }
    & $tarExe -xf $TarPath -C $tempRoot
    if ($LASTEXITCODE -ne 0) {
        Remove-TempDirectory $tempRoot
        throw "tar failed for $TarPath"
    }
    return $tempRoot
}

function Import-OfficialObservations(
    [string]$RimWorldDataRoot,
    [System.Collections.Generic.List[object]]$Observations
) {
    if (-not (Test-Path -LiteralPath $RimWorldDataRoot)) { return 0 }
    $countBefore = $Observations.Count

    Get-ChildItem -LiteralPath $RimWorldDataRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object {
        $packRoot = $_.FullName
        $packName = $_.Name
        $englishRoot = Join-Path (Join-Path $packRoot "Languages") "English"
        if (-not (Test-Path -LiteralPath $englishRoot)) { return }
        $koreanTar = Get-ChildItem -LiteralPath (Join-Path $packRoot "Languages") -File -Filter "Korean*.tar" -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $koreanTar) { return }

        Write-Host "Official: $packName"
        $temp = $null
        try {
            $temp = Expand-LanguageTar $koreanTar.FullName
            $sourceMap = Import-LanguageDataMap $englishRoot
            $koreanMap = Import-LanguageDataMap $temp
            $priority = if ($packName -eq "Core") { 0 } else { 10 }
            $origin = if ($packName -eq "Core") { "official-core" } else { "official-dlc-$packName" }

            foreach ($key in $sourceMap.Keys) {
                if (-not $koreanMap.ContainsKey($key)) { continue }
                Add-Observation `
                    -Observations $Observations `
                    -SourceEntry $sourceMap[$key] `
                    -KoreanEntry $koreanMap[$key] `
                    -Origin $origin `
                    -Priority $priority `
                    -WorkshopId "" `
                    -ModName $packName
            }
        } finally {
            if ($temp) { Remove-TempDirectory $temp }
        }
    }

    return $Observations.Count - $countBefore
}

function Read-RmkModList([string]$RmkRoot) {
    $rows = New-Object "System.Collections.Generic.List[object]"
    $path = Join-Path $RmkRoot "ModList.tsv"
    if (-not (Test-Path -LiteralPath $path)) { return ,$rows }

    Get-Content -LiteralPath $path -Encoding UTF8 | ForEach-Object {
        if ([string]::IsNullOrWhiteSpace($_)) { return }
        $parts = $_ -split "`t"
        if ($parts.Count -lt 2) { return }
        if ($parts[0] -notmatch "^\d+$") { return }
        [void]$rows.Add([pscustomobject]@{
            workshopId = $parts[0].Trim()
            name = $parts[1].Trim()
            dataPath = if ($parts.Count -gt 2) { $parts[2].Trim() } else { "" }
            packageId = if ($parts.Count -gt 3) { $parts[3].Trim() } else { "" }
        })
    }

    return ,$rows
}

function Get-RmkKoreanRootIndex([string]$RmkRoot) {
    $index = @{}
    if (-not (Test-Path -LiteralPath $RmkRoot)) { return $index }

    Get-ChildItem -LiteralPath $RmkRoot -Recurse -Directory -Filter "Korean*" -ErrorAction SilentlyContinue | ForEach-Object {
        $path = $_.FullName
        if ($path -notmatch "[\\/]Languages[\\/]Korean") { return }
        if ($path -notmatch " - (?<id>\d{6,})([\\/]|$)") { return }
        $id = $matches["id"]
        if (-not $index.ContainsKey($id)) {
            $index[$id] = New-Object "System.Collections.Generic.List[string]"
        }
        [void]$index[$id].Add($path)
    }

    return $index
}

function Import-RmkObservations(
    [string]$RmkRoot,
    [string]$WorkshopRoot,
    [string]$GameVersion,
    [string[]]$WorkshopIds,
    [System.Collections.Generic.List[object]]$Observations,
    [int]$MaxRmkMods,
    [switch]$IncludePatches
) {
    if (-not (Test-Path -LiteralPath $RmkRoot)) { return [pscustomobject]@{ observations = 0; scannedMods = 0; pairedMods = 0; missingSource = 0; missingKorean = 0 } }
    $countBefore = $Observations.Count
    $rows = Read-RmkModList $RmkRoot
    $rootIndex = Get-RmkKoreanRootIndex $RmkRoot
    $scanned = 0
    $paired = 0
    $missingSource = 0
    $missingKorean = 0
    $wantedIds = @{}
    foreach ($id in $WorkshopIds) {
        if (-not [string]::IsNullOrWhiteSpace($id)) { $wantedIds[$id.Trim()] = $true }
    }

    foreach ($row in $rows) {
        if ($wantedIds.Count -gt 0 -and -not $wantedIds.ContainsKey($row.workshopId)) { continue }
        if ($MaxRmkMods -gt 0 -and $scanned -ge $MaxRmkMods) { break }
        $scanned++
        if (($scanned % 25) -eq 0) {
            Write-Host "RMK: scanned $scanned/$($rows.Count), observations $($Observations.Count - $countBefore)"
        }

        $modRoot = Join-Path $WorkshopRoot $row.workshopId
        if (-not (Test-Path -LiteralPath $modRoot)) {
            $missingSource++
            continue
        }
        if (-not $rootIndex.ContainsKey($row.workshopId)) {
            $missingKorean++
            continue
        }

        $koreanMap = @{}
        foreach ($koRoot in $rootIndex[$row.workshopId]) {
            foreach ($pair in (Import-LanguageDataMap $koRoot).GetEnumerator()) {
                if (-not $koreanMap.ContainsKey($pair.Key)) { $koreanMap[$pair.Key] = $pair.Value }
            }
        }
        if ($koreanMap.Count -eq 0) { continue }

        $sourceMap = Import-ModSourceMap -ModRoot $modRoot -GameVersion $GameVersion -IncludePatches:$IncludePatches -WantedKeys $koreanMap
        if ($sourceMap.Count -eq 0) { continue }

        $hadPair = $false
        foreach ($key in $sourceMap.Keys) {
            if (-not $koreanMap.ContainsKey($key)) { continue }
            Add-Observation `
                -Observations $Observations `
                -SourceEntry $sourceMap[$key] `
                -KoreanEntry $koreanMap[$key] `
                -Origin "rmk" `
                -Priority 100 `
                -WorkshopId $row.workshopId `
                -ModName $row.name
            $hadPair = $true
        }

        if ($hadPair) { $paired++ }
    }

    return [pscustomobject]@{
        observations = $Observations.Count - $countBefore
        scannedMods = $scanned
        pairedMods = $paired
        missingSource = $missingSource
        missingKorean = $missingKorean
    }
}

function ConvertTo-GlossaryTerms([object[]]$Observations, [int]$MinRmkOccurrences) {
    $terms = New-Object "System.Collections.Generic.List[object]"
    $conflicts = New-Object "System.Collections.Generic.List[object]"

    foreach ($sourceGroup in ($Observations | Group-Object sourceKey)) {
        $items = @($sourceGroup.Group)
        if ($items.Count -eq 0) { continue }

        $choices = New-Object "System.Collections.Generic.List[object]"
        foreach ($koGroup in ($items | Group-Object koKey)) {
            $koItems = @($koGroup.Group)
            $bestPriority = ($koItems | Measure-Object -Property priority -Minimum).Minimum
            $sample = $koItems | Sort-Object @{ Expression = "priority"; Ascending = $true }, @{ Expression = { $_.ko.Length }; Ascending = $true } | Select-Object -First 1
            [void]$choices.Add([pscustomobject]@{
                ko = $sample.ko
                count = $koItems.Count
                priority = [int]$bestPriority
                origins = @($koItems | Select-Object -ExpandProperty origin -Unique | Sort-Object)
                keys = @($koItems | Select-Object -ExpandProperty key -Unique | Sort-Object | Select-Object -First 12)
                workshopIds = @($koItems | Where-Object { $_.workshopId } | Select-Object -ExpandProperty workshopId -Unique | Sort-Object | Select-Object -First 12)
            })
        }

        $sourceWordCount = @($items[0].source -split "\s+" | Where-Object { $_ }).Count
        if ($sourceWordCount -gt 1) {
            $best = $choices |
                Sort-Object @{ Expression = "priority"; Ascending = $true }, @{ Expression = "count"; Descending = $true }, @{ Expression = { $_.ko.Length }; Descending = $true } |
                Select-Object -First 1
        } else {
            $best = $choices |
                Sort-Object @{ Expression = "priority"; Ascending = $true }, @{ Expression = "count"; Descending = $true }, @{ Expression = { $_.ko.Length }; Ascending = $true } |
                Select-Object -First 1
        }

        if (-not $best) { continue }
        if ($best.priority -ge 100 -and $best.count -lt $MinRmkOccurrences) { continue }

        $sourceSample = $items |
            Sort-Object @{ Expression = "priority"; Ascending = $true }, @{ Expression = { $_.source.Length }; Ascending = $true } |
            Select-Object -First 1
        $alternatives = @($choices |
            Where-Object { $_.ko -ne $best.ko } |
            Sort-Object @{ Expression = "priority"; Ascending = $true }, @{ Expression = "count"; Descending = $true } |
            Select-Object -First 8 |
            ForEach-Object {
                [ordered]@{
                    ko = $_.ko
                    count = $_.count
                    priority = $_.priority
                    origins = $_.origins
                }
            })

        $confidence = 0.70
        if ($best.priority -eq 0) { $confidence = 1.00 }
        elseif ($best.priority -lt 100) { $confidence = 0.95 }
        elseif ($best.count -ge 5 -and $alternatives.Count -eq 0) { $confidence = 0.88 }
        elseif ($best.count -ge 2) { $confidence = 0.80 }
        if ($alternatives.Count -gt 0 -and $confidence -gt 0.75) { $confidence = $confidence - 0.08 }

        [void]$terms.Add([ordered]@{
            source = $sourceSample.source
            ko = $best.ko
            priority = $best.priority
            origin = ($best.origins -join ",")
            confidence = [Math]::Round($confidence, 2)
            count = $best.count
            keys = $best.keys
            workshopIds = $best.workshopIds
            alternatives = $alternatives
        })

        if ($alternatives.Count -gt 0) {
            [void]$conflicts.Add([pscustomobject]@{
                source = $sourceSample.source
                chosenKo = $best.ko
                chosenPriority = $best.priority
                chosenCount = $best.count
                alternatives = (($alternatives | ForEach-Object { "$($_.ko) [$($_.count)]" }) -join " | ")
            })
        }
    }

    return [pscustomobject]@{
        terms = @($terms | Sort-Object @{ Expression = "priority"; Ascending = $true }, @{ Expression = "source"; Ascending = $true })
        conflicts = @($conflicts | Sort-Object source)
    }
}

function Write-Utf8Json([string]$Path, [object]$Value) {
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    $json = $Value | ConvertTo-Json -Depth 20
    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $json, $encoding)
}

function Write-Utf8Csv([string]$Path, [object[]]$Rows) {
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    $Rows | Export-Csv -LiteralPath $Path -NoTypeInformation -Encoding UTF8
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $OutputPath) { $OutputPath = Join-Path $scriptRoot "glossary.generated.ko.json" }
if (-not $ConflictPath) { $ConflictPath = [System.IO.Path]::ChangeExtension($OutputPath, ".conflicts.csv") }

$steamLibraries = @(Get-SteamLibraryRoots)
if (-not $RimWorldDataRoot) {
    foreach ($library in $steamLibraries) {
        $candidate = Join-Path $library "steamapps\common\RimWorld\Data"
        if (Test-Path -LiteralPath $candidate -PathType Container) { $RimWorldDataRoot = $candidate; break }
    }
}
if (-not $WorkshopRoot) {
    if ($RimWorldDataRoot) {
        $rimWorldRoot = Split-Path -Parent $RimWorldDataRoot
        $commonRoot = Split-Path -Parent $rimWorldRoot
        $steamAppsRoot = Split-Path -Parent $commonRoot
        $candidate = Join-Path $steamAppsRoot "workshop\content\294100"
        if (Test-Path -LiteralPath $candidate -PathType Container) { $WorkshopRoot = $candidate }
    }
    if (-not $WorkshopRoot) {
        foreach ($library in $steamLibraries) {
            $candidate = Join-Path $library "steamapps\workshop\content\294100"
            if (Test-Path -LiteralPath $candidate -PathType Container) { $WorkshopRoot = $candidate; break }
        }
    }
}
if (-not $RmkRoot) {
    foreach ($library in $steamLibraries) {
        foreach ($candidate in @(
            (Join-Path $library "steamapps\common\RimWorld\Mods\RMK"),
            (Join-Path $library "steamapps\workshop\content\294100\3079466972")
        )) {
            if (Test-Path -LiteralPath $candidate -PathType Container) { $RmkRoot = $candidate; break }
        }
        if ($RmkRoot) { break }
    }
}
if (-not $SkipOfficial -and -not $RimWorldDataRoot) {
    throw "RimWorld Data folder was not found. Pass -RimWorldDataRoot explicitly."
}
if ($IncludeRmk -and -not $SkipRmk -and (-not $RmkRoot -or -not $WorkshopRoot)) {
    throw "RMK or RimWorld Workshop folder was not found. Pass -RmkRoot and -WorkshopRoot explicitly."
}

if ($RimWorldDataRoot) { $RimWorldDataRoot = Resolve-FullPathAllowMissing $RimWorldDataRoot }
if ($RmkRoot) { $RmkRoot = Resolve-FullPathAllowMissing $RmkRoot }
if ($WorkshopRoot) { $WorkshopRoot = Resolve-FullPathAllowMissing $WorkshopRoot }
$OutputPath = Resolve-FullPathAllowMissing $OutputPath
$ConflictPath = Resolve-FullPathAllowMissing $ConflictPath

Write-Host "RimWorld data: $(if ($RimWorldDataRoot) { $RimWorldDataRoot } else { '(not used)' })"
Write-Host "RMK root: $(if ($RmkRoot) { $RmkRoot } else { '(not used)' })"
Write-Host "Workshop root: $(if ($WorkshopRoot) { $WorkshopRoot } else { '(not used)' })"
Write-Host "Output: $OutputPath"

$started = Get-Date
$observations = New-Object "System.Collections.Generic.List[object]"
$officialCount = 0
$rmkStats = [pscustomobject]@{ observations = 0; scannedMods = 0; pairedMods = 0; missingSource = 0; missingKorean = 0 }

if (-not $SkipOfficial) {
    $officialCount = Import-OfficialObservations -RimWorldDataRoot $RimWorldDataRoot -Observations $observations
}

if ($IncludeRmk -and -not $SkipRmk) {
    $rmkStats = Import-RmkObservations `
        -RmkRoot $RmkRoot `
        -WorkshopRoot $WorkshopRoot `
        -GameVersion $GameVersion `
        -WorkshopIds $WorkshopId `
        -Observations $observations `
        -MaxRmkMods $MaxRmkMods `
        -IncludePatches:$IncludePatches
} else {
    Write-Host "RMK scan: disabled. Official RimWorld/Core+DLC glossary only."
}

Write-Host "Merging observations: $($observations.Count)"
$merged = ConvertTo-GlossaryTerms -Observations $observations.ToArray() -MinRmkOccurrences $MinRmkOccurrences
$finished = Get-Date

$priorityOrder = @(
    [ordered]@{ priority = 0; origin = "official-core" },
    [ordered]@{ priority = 10; origin = "official-dlc-*" }
)
if ($IncludeRmk -and -not $SkipRmk) {
    $priorityOrder += [ordered]@{ priority = 100; origin = "rmk" }
}

$payload = [ordered]@{
    generatedAt = $finished.ToString("o")
    tool = "Build-RimWorldGlossary.ps1"
    rimWorldDataRoot = $RimWorldDataRoot
    rmkRoot = $RmkRoot
    workshopRoot = $WorkshopRoot
    gameVersion = $GameVersion
    priorityOrder = $priorityOrder
    filters = [ordered]@{
        maxSourceChars = $MaxSourceChars
        minRmkOccurrences = $MinRmkOccurrences
        includeSentences = [bool]$IncludeSentences
        includePatches = [bool]$IncludePatches
        includeRmk = [bool]($IncludeRmk -and -not $SkipRmk)
    }
    stats = [ordered]@{
        elapsedSeconds = [Math]::Round(($finished - $started).TotalSeconds, 3)
        observations = $observations.Count
        officialObservations = $officialCount
        rmkObservations = $rmkStats.observations
        rmkScannedMods = $rmkStats.scannedMods
        rmkPairedMods = $rmkStats.pairedMods
        rmkMissingSourceMods = $rmkStats.missingSource
        rmkMissingKoreanMods = $rmkStats.missingKorean
        terms = $merged.terms.Count
        conflicts = $merged.conflicts.Count
    }
    terms = $merged.terms
}

Write-Utf8Json -Path $OutputPath -Value $payload
Write-Utf8Csv -Path $ConflictPath -Rows $merged.conflicts

Write-Host "Done."
Write-Host "Terms: $($merged.terms.Count)"
Write-Host "Conflicts: $($merged.conflicts.Count)"
Write-Host "Elapsed seconds: $([Math]::Round(($finished - $started).TotalSeconds, 3))"
Write-Host "Conflict report: $ConflictPath"
