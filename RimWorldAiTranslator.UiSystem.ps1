function ConvertTo-RimWorldUiColor([string]$Hex) {
    if ([string]::IsNullOrWhiteSpace($Hex)) { throw "A UI color value is required." }
    Add-Type -AssemblyName System.Drawing -ErrorAction SilentlyContinue
    return [System.Drawing.ColorTranslator]::FromHtml($Hex)
}

function Get-RimWorldUiTokens {
    param(
        [ValidateSet("Light", "Dark")]
        [string]$Mode = "Light",
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
    } elseif ($Mode -eq "Dark") {
        $colors = [ordered]@{
            Canvas = "#151A18"; Surface = "#1D2421"; SurfaceRaised = "#28302C"; SurfaceMuted = "#323A35"
            Header = "#101512"; HeaderSecondary = "#242A26"; Text = "#F3F0E7"; TextMuted = "#B8B9AF"
            TextFaint = "#8E938B"; Accent = "#C08B46"; AccentHover = "#D3A05A"; AccentPressed = "#986832"
            AccentText = "#17130E"; Success = "#5FA577"; Warning = "#D6A34D"; Danger = "#C7655F"
            Info = "#61A4BE"; Border = "#4A524C"; BorderSoft = "#353D38"; Focus = "#72B5D0"
            Selection = "#34443B"; Disabled = "#646A65"; Overlay = "#0B0E0C"
        }
    } else {
        $colors = [ordered]@{
            Canvas = "#EFEEE8"; Surface = "#FAF9F4"; SurfaceRaised = "#FFFFFF"; SurfaceMuted = "#E3E7E1"
            Header = "#252A27"; HeaderSecondary = "#343A36"; Text = "#20251F"; TextMuted = "#636C65"
            TextFaint = "#899089"; Accent = "#B78342"; AccentHover = "#C99A58"; AccentPressed = "#8F632F"
            AccentText = "#FFFFFF"; Success = "#3E7A55"; Warning = "#A96E28"; Danger = "#A74A45"
            Info = "#357A99"; Border = "#B7BDB6"; BorderSoft = "#D4D8D2"; Focus = "#2E83B7"
            Selection = "#DCE8DF"; Disabled = "#A1A7A1"; Overlay = "#1D231F"
        }
    }

    return [pscustomobject]@{
        Mode = $Mode
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
