function ConvertTo-RimWorldUiColor([string]$Hex) {
    if ([string]::IsNullOrWhiteSpace($Hex)) { throw "A UI color value is required." }
    Add-Type -AssemblyName System.Drawing -ErrorAction SilentlyContinue
    return [System.Drawing.ColorTranslator]::FromHtml($Hex)
}

function Get-RimWorldUiPresetCatalog {
    return @(
        [pscustomobject]@{ Id = "Professional"; Name = "프로페셔널"; Description = "중립적인 회색과 청록 포인트를 사용한 정돈된 업무 화면" },
        [pscustomobject]@{ Id = "SciFi"; Name = "사이파이"; Description = "차가운 금속색과 시안 포인트를 사용한 기술적인 화면" },
        [pscustomobject]@{ Id = "Vivid"; Name = "비비드"; Description = "코랄, 청록, 파랑 상태색이 선명한 활기 있는 화면" },
        [pscustomobject]@{ Id = "Studio"; Name = "스튜디오"; Description = "부드러운 중성색과 로즈 포인트를 사용한 차분한 화면" },
        [pscustomobject]@{ Id = "Frontier"; Name = "프런티어"; Description = "황동색과 자연색을 사용한 RimWorld풍 작업 화면" }
    )
}

function Get-RimWorldUiPresetColors([string]$Preset, [string]$Mode) {
    switch ("$Preset-$Mode") {
        "Professional-Dark" { return [ordered]@{
            Canvas = "#161B1E"; Surface = "#1D2428"; SurfaceRaised = "#273035"; SurfaceMuted = "#303A3F"
            Header = "#111619"; HeaderSecondary = "#252D31"; Text = "#F1F4F5"; TextMuted = "#B6C0C5"
            TextFaint = "#87949A"; Accent = "#5AA0B7"; AccentHover = "#71B4C8"; AccentPressed = "#3E7D92"
            AccentText = "#0E181C"; Success = "#66AB7E"; Warning = "#D7A457"; Danger = "#D16D68"
            Info = "#6EAAD0"; Border = "#4A565C"; BorderSoft = "#354046"; Focus = "#79BCE0"
            Selection = "#2E444C"; Disabled = "#667177"; Overlay = "#0B0F11"
        } }
        "SciFi-Light" { return [ordered]@{
            Canvas = "#EEF2F3"; Surface = "#FFFFFF"; SurfaceRaised = "#F8FBFC"; SurfaceMuted = "#DCE6E8"
            Header = "#172329"; HeaderSecondary = "#23363D"; Text = "#1D292E"; TextMuted = "#5B6C72"
            TextFaint = "#87979C"; Accent = "#087F99"; AccentHover = "#0A98B5"; AccentPressed = "#066476"
            AccentText = "#FFFFFF"; Success = "#27866B"; Warning = "#B97A29"; Danger = "#B44B50"
            Info = "#267FA8"; Border = "#AFC0C5"; BorderSoft = "#D1DCE0"; Focus = "#00A7CC"
            Selection = "#D2EEF2"; Disabled = "#95A2A6"; Overlay = "#0B171C"
        } }
        "SciFi-Dark" { return [ordered]@{
            Canvas = "#0E1519"; Surface = "#131E23"; SurfaceRaised = "#1B2A30"; SurfaceMuted = "#263940"
            Header = "#081014"; HeaderSecondary = "#17252B"; Text = "#EAF7F9"; TextMuted = "#A8BDC2"
            TextFaint = "#748A90"; Accent = "#24BCD2"; AccentHover = "#53D1E2"; AccentPressed = "#168697"
            AccentText = "#071416"; Success = "#49B997"; Warning = "#E0AD52"; Danger = "#E06A70"
            Info = "#55B5DF"; Border = "#3C5961"; BorderSoft = "#243B42"; Focus = "#71DEF0"
            Selection = "#183D46"; Disabled = "#5D7076"; Overlay = "#05090B"
        } }
        "Vivid-Light" { return [ordered]@{
            Canvas = "#F3F5F4"; Surface = "#FFFFFF"; SurfaceRaised = "#FFFDFB"; SurfaceMuted = "#E6EBE9"
            Header = "#263238"; HeaderSecondary = "#34454B"; Text = "#242B2E"; TextMuted = "#626D70"
            TextFaint = "#8C9698"; Accent = "#D85C54"; AccentHover = "#E4746C"; AccentPressed = "#A9433D"
            AccentText = "#FFFFFF"; Success = "#27846B"; Warning = "#B77921"; Danger = "#B64049"
            Info = "#347EB3"; Border = "#B8C1C0"; BorderSoft = "#D9DEDD"; Focus = "#168D9A"
            Selection = "#F3DED9"; Disabled = "#9CA5A4"; Overlay = "#182024"
        } }
        "Vivid-Dark" { return [ordered]@{
            Canvas = "#191D1E"; Surface = "#22282A"; SurfaceRaised = "#2D3537"; SurfaceMuted = "#394244"
            Header = "#111617"; HeaderSecondary = "#293235"; Text = "#F5F3EF"; TextMuted = "#BDC4C3"
            TextFaint = "#8F9998"; Accent = "#F0786E"; AccentHover = "#FF9288"; AccentPressed = "#BD554D"
            AccentText = "#1E1110"; Success = "#52B596"; Warning = "#E2AD55"; Danger = "#EE7078"
            Info = "#68AEDD"; Border = "#4D595A"; BorderSoft = "#364143"; Focus = "#57C0C9"
            Selection = "#493634"; Disabled = "#697373"; Overlay = "#0C1011"
        } }
        "Studio-Light" { return [ordered]@{
            Canvas = "#F4F5F3"; Surface = "#FFFFFF"; SurfaceRaised = "#FCFDFC"; SurfaceMuted = "#E8ECEA"
            Header = "#2B3030"; HeaderSecondary = "#3A4140"; Text = "#292D2C"; TextMuted = "#666E6B"
            TextFaint = "#909794"; Accent = "#A65F6B"; AccentHover = "#BB7480"; AccentPressed = "#804650"
            AccentText = "#FFFFFF"; Success = "#3E8066"; Warning = "#B07A38"; Danger = "#A84F56"
            Info = "#4D789B"; Border = "#BDC3C0"; BorderSoft = "#DBDFDD"; Focus = "#4F8D91"
            Selection = "#F1E2E4"; Disabled = "#A0A7A4"; Overlay = "#202424"
        } }
        "Studio-Dark" { return [ordered]@{
            Canvas = "#1A1D1D"; Surface = "#232827"; SurfaceRaised = "#2E3533"; SurfaceMuted = "#39413F"
            Header = "#121515"; HeaderSecondary = "#2A302F"; Text = "#F2F2EE"; TextMuted = "#BFC4C1"
            TextFaint = "#919996"; Accent = "#D28490"; AccentHover = "#E39AA5"; AccentPressed = "#A75F6A"
            AccentText = "#211214"; Success = "#67AA89"; Warning = "#D9A65C"; Danger = "#D36E73"
            Info = "#78A4C4"; Border = "#4E5754"; BorderSoft = "#38413F"; Focus = "#78B4B7"
            Selection = "#49393D"; Disabled = "#68716E"; Overlay = "#0D1010"
        } }
        "Frontier-Light" { return [ordered]@{
            Canvas = "#EFEEE8"; Surface = "#FAF9F4"; SurfaceRaised = "#FFFFFF"; SurfaceMuted = "#E3E7E1"
            Header = "#252A27"; HeaderSecondary = "#343A36"; Text = "#20251F"; TextMuted = "#636C65"
            TextFaint = "#899089"; Accent = "#B78342"; AccentHover = "#C99A58"; AccentPressed = "#8F632F"
            AccentText = "#FFFFFF"; Success = "#3E7A55"; Warning = "#A96E28"; Danger = "#A74A45"
            Info = "#357A99"; Border = "#B7BDB6"; BorderSoft = "#D4D8D2"; Focus = "#2E83B7"
            Selection = "#DCE8DF"; Disabled = "#A1A7A1"; Overlay = "#1D231F"
        } }
        "Frontier-Dark" { return [ordered]@{
            Canvas = "#151A18"; Surface = "#1D2421"; SurfaceRaised = "#28302C"; SurfaceMuted = "#323A35"
            Header = "#101512"; HeaderSecondary = "#242A26"; Text = "#F3F0E7"; TextMuted = "#B8B9AF"
            TextFaint = "#8E938B"; Accent = "#C08B46"; AccentHover = "#D3A05A"; AccentPressed = "#986832"
            AccentText = "#17130E"; Success = "#5FA577"; Warning = "#D6A34D"; Danger = "#C7655F"
            Info = "#61A4BE"; Border = "#4A524C"; BorderSoft = "#353D38"; Focus = "#72B5D0"
            Selection = "#34443B"; Disabled = "#646A65"; Overlay = "#0B0E0C"
        } }
        default { return [ordered]@{
            Canvas = "#F2F4F5"; Surface = "#FBFCFC"; SurfaceRaised = "#FFFFFF"; SurfaceMuted = "#E6EAEC"
            Header = "#20262A"; HeaderSecondary = "#30383D"; Text = "#20272B"; TextMuted = "#59666D"
            TextFaint = "#849097"; Accent = "#35738A"; AccentHover = "#43899F"; AccentPressed = "#28596C"
            AccentText = "#FFFFFF"; Success = "#3D7A59"; Warning = "#A96F2F"; Danger = "#A64B4B"
            Info = "#3F7599"; Border = "#B8C1C6"; BorderSoft = "#D7DDE0"; Focus = "#2C84B8"
            Selection = "#DCE9ED"; Disabled = "#9CA6AB"; Overlay = "#151B1E"
        } }
    }
}

function Get-RimWorldUiTokens {
    param(
        [ValidateSet("Light", "Dark")]
        [string]$Mode = "Light",
        [ValidateSet("Professional", "SciFi", "Vivid", "Studio", "Frontier")]
        [string]$Preset = "Professional",
        [switch]$HighContrast
    )

    if ($HighContrast) {
        $colors = [ordered]@{
            Canvas = "#000000"; Surface = "#000000"; SurfaceRaised = "#101010"; SurfaceMuted = "#202020"
            Header = "#000000"; HeaderSecondary = "#171717"; Text = "#FFFFFF"; TextMuted = "#E6E6E6"
            TextFaint = "#CCCCCC"; Accent = "#FFD166"; AccentHover = "#FFE095"; AccentPressed = "#D9A834"
            AccentText = "#000000"; Success = "#76E39B"; Warning = "#FFD166"; Danger = "#FF7B72"
            Info = "#78D6FF"; Border = "#FFFFFF"; BorderSoft = "#BFBFBF"; Focus = "#78D6FF"
            Selection = "#343434"; Disabled = "#757575"; Overlay = "#000000"
        }
    } else {
        $colors = Get-RimWorldUiPresetColors -Preset $Preset -Mode $Mode
    }

    return [pscustomobject]@{
        Mode = $Mode
        Preset = $Preset
        HighContrast = [bool]$HighContrast
        Colors = [pscustomobject]$colors
        Spacing = [pscustomobject]@{ Xs = 4; Sm = 8; Md = 12; Lg = 16; Xl = 24; Xxl = 32 }
        Metrics = [pscustomobject]@{
            ButtonHeight = 38
            CompactButtonHeight = 32
            InputHeight = 34
            HeaderHeight = 72
            SidebarMinimum = 280
            DetailMinimum = 420
            ContentMaximum = 1440
            BorderWidth = 1
        }
    }
}

function Get-RimWorldTranslationEstimate {
    param(
        [AllowEmptyCollection()]
        [object[]]$Entries = @(),
        [ValidateRange(1, 1000)]
        [int]$BatchSize = 40
    )

    $entryCount = @($Entries).Count
    $characters = 0L
    $translated = 0
    foreach ($entry in @($Entries)) {
        $source = ""
        foreach ($name in @("source", "Source", "sourceText", "SourceText")) {
            if ($entry -and $entry.PSObject.Properties[$name]) { $source = [string]$entry.$name; break }
        }
        $translation = ""
        foreach ($name in @("translation", "Translation", "text", "Text")) {
            if ($entry -and $entry.PSObject.Properties[$name]) { $translation = [string]$entry.$name; break }
        }
        $characters += $source.Length
        if (-not [string]::IsNullOrWhiteSpace($translation)) { $translated++ }
    }
    $batches = if ($entryCount -eq 0) { 0 } else { [int][Math]::Ceiling($entryCount / [double]$BatchSize) }
    $low = if ($entryCount -eq 0) { 0L } else { [long][Math]::Ceiling(($characters / 4.0) + ($batches * 220)) }
    $high = if ($entryCount -eq 0) { 0L } else { [long][Math]::Ceiling(($characters * 1.2) + ($batches * 480)) }
    return [pscustomobject]@{
        Entries = $entryCount
        TranslatedEntries = $translated
        MissingEntries = $entryCount - $translated
        SourceCharacters = $characters
        BatchSize = $BatchSize
        Batches = $batches
        EstimatedInputTokensLow = $low
        EstimatedInputTokensHigh = $high
        IsEstimate = $true
    }
}

function Get-RimWorldSimpleDiff {
    param(
        [AllowNull()][string]$Before,
        [AllowNull()][string]$After
    )

    $left = [string]$Before
    $right = [string]$After
    $prefixLength = 0
    $maximumPrefix = [Math]::Min($left.Length, $right.Length)
    while ($prefixLength -lt $maximumPrefix -and $left[$prefixLength] -eq $right[$prefixLength]) { $prefixLength++ }

    $suffixLength = 0
    $maximumSuffix = [Math]::Min($left.Length - $prefixLength, $right.Length - $prefixLength)
    while ($suffixLength -lt $maximumSuffix -and
        $left[$left.Length - 1 - $suffixLength] -eq $right[$right.Length - 1 - $suffixLength]) {
        $suffixLength++
    }

    $beforeChangedLength = $left.Length - $prefixLength - $suffixLength
    $afterChangedLength = $right.Length - $prefixLength - $suffixLength
    return [pscustomobject]@{
        Changed = -not [string]::Equals($left, $right, [System.StringComparison]::Ordinal)
        Prefix = if ($prefixLength -gt 0) { $left.Substring(0, $prefixLength) } else { "" }
        BeforeChanged = if ($beforeChangedLength -gt 0) { $left.Substring($prefixLength, $beforeChangedLength) } else { "" }
        AfterChanged = if ($afterChangedLength -gt 0) { $right.Substring($prefixLength, $afterChangedLength) } else { "" }
        Suffix = if ($suffixLength -gt 0) { $left.Substring($left.Length - $suffixLength) } else { "" }
        PrefixLength = $prefixLength
        SuffixLength = $suffixLength
    }
}

function Get-RimWorldOperationStateFromLine([string]$Line) {
    $text = ([string]$Line).Trim()
    $text = [System.Text.RegularExpressions.Regex]::Replace($text, '^\[[0-9]{2}:[0-9]{2}:[0-9]{2}\]\s*', '')
    $state = [ordered]@{
        Stage = "activity"
        Kind = "info"
        Label = "작업 준비"
        Current = 0
        Total = 0
        IsDeterminate = $false
        CanCancel = $true
        Detail = $text
    }

    if ($text -match '^Translating batch\s+(\d+)/(\d+)\s+\((\d+)\s+entries\)') {
        $state.Stage = "translate"; $state.Kind = "progress"; $state.Label = "번역 배치 처리"
        $state.Current = [int]$matches[1]; $state.Total = [int]$matches[2]; $state.IsDeterminate = $state.Total -gt 0
    } elseif ($text -match '^Pending(?: translation)? entries:\s*(\d+)') {
        $state.Stage = "prepare"; $state.Kind = "info"; $state.Label = "번역 대상 준비"; $state.Total = [int]$matches[1]
    } elseif ($text -match '^Source entries:\s*(\d+)') {
        $state.Stage = "scan"; $state.Kind = "info"; $state.Label = "원문 분석"; $state.Total = [int]$matches[1]
    } elseif ($text -match '^Detected source language:\s*(.+)') {
        $state.Stage = "source"; $state.Kind = "info"; $state.Label = "원문 언어 확인"
    } elseif ($text -match '^Waiting\s+([0-9.]+)s\s+for') {
        $state.Stage = "rate-limit"; $state.Kind = "waiting"; $state.Label = "API 사용 한도 대기"
    } elseif ($text -match '(?i)batch\s+.+failed.+retrying|retrying') {
        $state.Stage = "retry"; $state.Kind = "retry"; $state.Label = "실패한 배치 재시도"
    } elseif ($text -match '^Token warnings:\s*(\d+)') {
        $state.Stage = "validate"; $state.Kind = if ([int]$matches[1] -gt 0) { "warning" } else { "info" }; $state.Label = "토큰 안전성 검사"
        $state.Total = [int]$matches[1]
    } elseif ($text -match '^Review output:\s*(.+)') {
        $state.Stage = "result"; $state.Kind = "result"; $state.Label = "검수 프로젝트 생성"
    } elseif ($text -match '^Done\.$') {
        $state.Stage = "complete"; $state.Kind = "completed"; $state.Label = "번역 작업 완료"; $state.CanCancel = $false
    } elseif ($text -match '(?i)cancelled|canceled|취소') {
        $state.Stage = "cancelled"; $state.Kind = "cancelled"; $state.Label = "작업 취소됨"; $state.CanCancel = $false
    } elseif ($text -match '(?i)^error\b|failed after|exception|종료.*ExitCode=[1-9]') {
        $state.Stage = "error"; $state.Kind = "error"; $state.Label = "작업 오류"; $state.CanCancel = $false
    } elseif ($text -match '^Translation provider:\s*(.+)') {
        $state.Stage = "provider"; $state.Kind = "info"; $state.Label = "번역 엔진 확인"
    } elseif ($text -match '^Mod root:\s*(.+)') {
        $state.Stage = "initialize"; $state.Kind = "info"; $state.Label = "모드 작업공간 확인"
    }
    return [pscustomobject]$state
}
