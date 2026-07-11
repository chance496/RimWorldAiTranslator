param(
    [string]$ReviewRoot = "",
    [string]$LayoutSnapshotPath = "",
    [int]$LayoutSnapshotWidth = 0,
    [int]$LayoutSnapshotHeight = 0,
    [string]$InitialDashboardTab = "",
    [string]$PreviewTheme = "",
    [int]$PreviewTextSize = 0,
    [switch]$PreviewHighContrast
)

$systemPowerShell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
if (-not (Test-Path -LiteralPath $systemPowerShell -PathType Leaf)) {
    throw "Windows PowerShell was not found at the expected system path: $systemPowerShell"
}

if ([System.Threading.Thread]::CurrentThread.ApartmentState -ne "STA") {
    $self = $PSCommandPath
    if (-not $self) { $self = $MyInvocation.MyCommand.Path }
    $relaunchArguments = @("-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-File", "`"$self`"")
    if ($ReviewRoot) { $relaunchArguments += @("-ReviewRoot", "`"$ReviewRoot`"") }
    if ($LayoutSnapshotPath) { $relaunchArguments += @("-LayoutSnapshotPath", "`"$LayoutSnapshotPath`"") }
    if ($LayoutSnapshotWidth -gt 0) { $relaunchArguments += @("-LayoutSnapshotWidth", [string]$LayoutSnapshotWidth) }
    if ($LayoutSnapshotHeight -gt 0) { $relaunchArguments += @("-LayoutSnapshotHeight", [string]$LayoutSnapshotHeight) }
    if ($InitialDashboardTab) { $relaunchArguments += @("-InitialDashboardTab", "`"$InitialDashboardTab`"") }
    if ($PreviewTheme) { $relaunchArguments += @("-PreviewTheme", "`"$PreviewTheme`"") }
    if ($PreviewTextSize -gt 0) { $relaunchArguments += @("-PreviewTextSize", [string]$PreviewTextSize) }
    if ($PreviewHighContrast) { $relaunchArguments += "-PreviewHighContrast" }
    Start-Process -FilePath $systemPowerShell -ArgumentList $relaunchArguments -WindowStyle Hidden
    return
}

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$nativeAssemblyPath = Join-Path $scriptRoot "RimWorldAiTranslator.Native.dll"
if ((Test-Path -LiteralPath $nativeAssemblyPath -PathType Leaf) -and -not ("RimWorldTranslatorNativeMethods" -as [type])) {
    Add-Type -LiteralPath $nativeAssemblyPath -ErrorAction Stop
}
if (-not ("RimWorldTranslatorNativeMethods" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class RimWorldTranslatorNativeMethods
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
}
"@
}
[System.Windows.Forms.Application]::EnableVisualStyles()

$script:powershellExe = $systemPowerShell
$script:explorerExe = Join-Path $env:SystemRoot "explorer.exe"
foreach ($systemExecutable in @($script:powershellExe, $script:explorerExe)) {
    if (-not (Test-Path -LiteralPath $systemExecutable -PathType Leaf)) {
        throw "Required Windows executable was not found: $systemExecutable"
    }
}
$script:initialReviewRoot = $ReviewRoot
$script:layoutSnapshotPath = $LayoutSnapshotPath
$script:layoutSnapshotTimer = $null
$script:startupCatalogTimer = $null
$script:reviewRoot = ""
$script:comparisonFile = ""
$script:rows = @()
$script:decisions = @{}
$script:validateLoadedDecisionSources = $false
$script:validationCache = @{}
$script:fileGroups = @()
$script:fileGroupMap = @{}
$script:sourceRowIndex = $null
$script:relativeTargetCache = @{}
$script:textFingerprintSha256 = $null
$script:reviewStats = $null
$script:projectStatsCache = @{}
$script:projectStatsCacheDirty = $false
$script:dashboardProjectsDirty = $true
$script:lastDashboardRenderKey = ""
$script:appliedThemeSignature = ""
$script:windowsDarkModeCacheValid = $false
$script:windowsDarkModeCached = $false
$script:windowsDarkModeCheckedAt = [datetime]::MinValue
$script:visibleRowIndexes = @()
$script:visibleRowPositionMap = [int[]]@()
$script:syncingItemSelection = $false
$script:currentRowIndex = -1
$script:currentFile = "__ALL__"
$script:loading = $false
$script:layouting = $false
$script:loadingProjectList = $false
$script:loadingDashboard = $false
$script:syncingSettings = $false
$script:dirty = $false
$script:translationEditedByUser = $false
$script:translationEditBaseline = ""
$script:translationEditorOrigin = ""
$script:settingTranslationOrigin = $false
$script:glossary = @()
$script:glossaryLoaded = $false
$script:glossaryIndexedTerms = @()
$script:glossaryPrefixIndex = @{}
$script:DisplayLocalizationFieldPattern = '^(label|labelshort|description|jobstring|reportstring|deathmessage|deathmessagefemale|deathmessagemale|letterlabel|lettertext|header|headertip|summary|formatstring|formatstringunfinalized|fixedname|reason|text|slateref)$'
$script:TechnicalLocalizationFieldPattern = '^(defname|parentname|classname|class|thingclass|workerclass|compclass|hediffclass|thoughtclass|abilityclass|worldobjectclass|texpath|texname|graphicpath|shader|sound|sounddef|iconpath|packageid|xpath|operation|colorchannel|rendernode|rendertree|rendertreedef|bodypart|bodypartdef|bodytype|headtype|racedef|thingdef|pawnkinddef|jobdef|statdef|skilldef|hediffdef|genedef)$'
$script:appDataRoot = Join-Path $env:LOCALAPPDATA "RimWorldAiTranslator"
$script:projectStorePath = Join-Path $script:appDataRoot "projects.json"
$script:settingsPath = Join-Path $script:appDataRoot "settings.json"
$script:modCatalogCachePath = Join-Path $script:appDataRoot "mod-catalog.json"
$script:projectStatsCachePath = Join-Path $script:appDataRoot "project-stats.json"
$script:appReviewRoot = Join-Path $script:appDataRoot "reviews"
$script:rmkWorkspaceRoot = ""
$script:rmkReferenceRoot = ""
$script:rmkUseExisting = $true
$script:rmkIndexCache = @{}
$script:rmkTargetCache = @{}
$script:rmkCurrentTarget = $null
$script:themeMode = "System"
$script:textSize = 10
$script:highContrast = $false
$script:autoSave = $true
$script:settingsLoading = $false
$script:accentColor = [System.Drawing.Color]::FromArgb(166, 124, 70)
$script:surfaceColor = [System.Drawing.Color]::White
$script:textColor = [System.Drawing.Color]::FromArgb(47, 44, 38)
$script:mutedColor = [System.Drawing.Color]::FromArgb(103, 97, 86)
$script:translatorScript = Join-Path $scriptRoot "Invoke-RimWorldAiTranslation.ps1"
$script:translationRunnerScript = Join-Path $scriptRoot "Run-RimWorldAiTranslation.ps1"
$script:reviewApplyScript = Join-Path $scriptRoot "Apply-RimWorldAiReviewResults.ps1"
$script:rmkExportScript = Join-Path $scriptRoot "Export-RimWorldAiReviewToRmk.ps1"
$script:modCatalog = @()
$script:projects = @()
$script:selectedModRoot = ""
$script:selectedProjectId = ""
$script:lastReviewOutputPath = ""
$script:lastProvider = ""
$script:activeAiTranslationMode = ""
$script:process = $null
$script:processExitHandled = $false
$script:translationLogFile = ""
$script:translationLogOffset = 0L
$script:translationLogPartial = ""
$script:tempFiles = New-Object "System.Collections.Generic.List[string]"
$script:startedAt = $null
$script:stopRequested = $false
$script:itemCardBack = [System.Drawing.Color]::FromArgb(255, 255, 255)
$script:itemCardSelected = [System.Drawing.Color]::FromArgb(232, 237, 244)
$script:itemText = [System.Drawing.Color]::FromArgb(25, 35, 45)
$script:itemMuted = [System.Drawing.Color]::FromArgb(93, 107, 122)
$script:itemSubtle = [System.Drawing.Color]::FromArgb(126, 139, 153)
$script:tabBack = [System.Drawing.Color]::FromArgb(245, 243, 236)
$script:tabActive = [System.Drawing.Color]::FromArgb(92, 79, 58)
$script:tabText = [System.Drawing.Color]::FromArgb(78, 74, 66)
$script:tabActiveText = [System.Drawing.Color]::White
$script:apiProviders = @(
    [pscustomobject]@{ Id = "Cerebras"; Name = "Cerebras"; Description = "Gemma 4와 초고속 추론 모델"; Url = "https://api.cerebras.ai/v1/chat/completions"; Models = @("gemma-4-31b", "gpt-oss-120b"); Model = "gemma-4-31b"; Provider = "OpenAICompatible"; ResponseFormat = "JsonSchema"; TokenParameter = "max_completion_tokens"; ReasoningEffort = "none"; Rpm = 5; InputTpm = 30000; DailyTokens = 1000000; MaxOutput = 32000; NeedsKey = $true },
    [pscustomobject]@{ Id = "OpenAI"; Name = "OpenAI"; Description = "GPT 계열 공식 API"; Url = "https://api.openai.com/v1/chat/completions"; Models = @("gpt-5.6", "gpt-5.5", "gpt-5.4", "gpt-5"); Model = "gpt-5.6"; Temperature = -1; Provider = "OpenAICompatible"; ResponseFormat = "JsonSchema"; TokenParameter = "max_completion_tokens"; ReasoningEffort = "none"; Rpm = 0; InputTpm = 0; DailyTokens = 0; MaxOutput = 16000; NeedsKey = $true },
    [pscustomobject]@{ Id = "Gemini"; Name = "Google Gemini"; Description = "Gemini OpenAI 호환 API"; Url = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions"; Models = @("gemini-3.5-flash", "gemini-3.5-pro", "gemini-flash-latest"); Model = "gemini-3.5-flash"; Provider = "OpenAICompatible"; ResponseFormat = "JsonSchema"; TokenParameter = "max_completion_tokens"; ReasoningEffort = ""; Rpm = 0; InputTpm = 0; DailyTokens = 0; MaxOutput = 16000; NeedsKey = $true },
    [pscustomobject]@{ Id = "DeepSeek"; Name = "DeepSeek"; Description = "DeepSeek V4 공식 API"; Url = "https://api.deepseek.com/chat/completions"; Models = @("deepseek-v4-flash", "deepseek-v4-pro"); Model = "deepseek-v4-flash"; Provider = "OpenAICompatible"; ResponseFormat = "JsonObject"; TokenParameter = "max_tokens"; ReasoningEffort = ""; Rpm = 0; InputTpm = 0; DailyTokens = 0; MaxOutput = 16000; NeedsKey = $true },
    [pscustomobject]@{ Id = "Qwen"; Name = "Qwen"; Description = "Alibaba Cloud Model Studio"; Url = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1/chat/completions"; Models = @("qwen3.7-plus", "qwen3.7-max", "qwen3.6-flash"); Model = "qwen3.7-plus"; Provider = "OpenAICompatible"; ResponseFormat = "JsonObject"; TokenParameter = "max_tokens"; ReasoningEffort = ""; Rpm = 0; InputTpm = 0; DailyTokens = 0; MaxOutput = 16000; NeedsKey = $true },
    [pscustomobject]@{ Id = "Groq"; Name = "Groq"; Description = "빠른 오픈 모델 추론"; Url = "https://api.groq.com/openai/v1/chat/completions"; Models = @("openai/gpt-oss-120b", "llama-3.3-70b-versatile", "openai/gpt-oss-20b"); Model = "openai/gpt-oss-120b"; Provider = "OpenAICompatible"; ResponseFormat = "JsonSchema"; TokenParameter = "max_completion_tokens"; ReasoningEffort = ""; Rpm = 0; InputTpm = 0; DailyTokens = 0; MaxOutput = 16000; NeedsKey = $true },
    [pscustomobject]@{ Id = "Mistral"; Name = "Mistral AI"; Description = "Mistral 공식 Chat API"; Url = "https://api.mistral.ai/v1/chat/completions"; Models = @("mistral-small-latest", "mistral-medium-latest", "mistral-large-latest"); Model = "mistral-small-latest"; Provider = "OpenAICompatible"; ResponseFormat = "JsonObject"; TokenParameter = "max_tokens"; ReasoningEffort = ""; Rpm = 0; InputTpm = 0; DailyTokens = 0; MaxOutput = 16000; NeedsKey = $true },
    [pscustomobject]@{ Id = "OpenRouter"; Name = "OpenRouter"; Description = "여러 모델을 한 API로 사용"; Url = "https://openrouter.ai/api/v1/chat/completions"; Models = @("~openai/gpt-latest", "openai/gpt-5.4", "google/gemini-3.5-flash"); Model = "~openai/gpt-latest"; Provider = "OpenAICompatible"; ResponseFormat = "JsonObject"; TokenParameter = "max_tokens"; ReasoningEffort = ""; Rpm = 0; InputTpm = 0; DailyTokens = 0; MaxOutput = 16000; NeedsKey = $true },
    [pscustomobject]@{ Id = "ZAI"; Name = "BigModel / Z.AI"; Description = "GLM 계열 공식 API"; Url = "https://api.z.ai/api/paas/v4/chat/completions"; Models = @("glm-5.1", "glm-5", "glm-4.7"); Model = "glm-5.1"; Provider = "OpenAICompatible"; ResponseFormat = "JsonObject"; TokenParameter = "max_tokens"; ReasoningEffort = ""; Rpm = 0; InputTpm = 0; DailyTokens = 0; MaxOutput = 16000; NeedsKey = $true },
    [pscustomobject]@{ Id = "Custom"; Name = "사용자 지정"; Description = "OpenAI 호환 Chat Completions"; Url = ""; Models = @(); Model = ""; Provider = "OpenAICompatible"; ResponseFormat = "JsonObject"; TokenParameter = "max_tokens"; ReasoningEffort = ""; Rpm = 0; InputTpm = 0; DailyTokens = 0; MaxOutput = 16000; NeedsKey = $true },
    [pscustomobject]@{ Id = "Google"; Name = "Google 번역"; Description = "API 키 없이 빠른 기계 번역"; Url = "https://translate.googleapis.com/translate_a/single"; Models = @("Google Translate"); Model = "Google Translate"; Provider = "Google"; ResponseFormat = "PromptOnly"; TokenParameter = "none"; ReasoningEffort = ""; Rpm = 0; InputTpm = 0; DailyTokens = 0; MaxOutput = 0; NeedsKey = $false }
)
$script:selectedApiProviderId = "Cerebras"
$script:apiProviderConfigs = @{}
$script:apiProviderKeys = @{}
$script:apiProviderButtons = @{}
$script:syncingApiProvider = $false
$script:fontCache = @{}

function New-Font([float]$Size, [System.Drawing.FontStyle]$Style = [System.Drawing.FontStyle]::Regular) {
    $sizeKey = $Size.ToString("0.##", [System.Globalization.CultureInfo]::InvariantCulture)
    $key = "$sizeKey|$([int]$Style)"
    if (-not $script:fontCache.ContainsKey($key)) {
        $script:fontCache[$key] = [System.Drawing.Font]::new("Malgun Gothic", $Size, $Style)
    }
    return $script:fontCache[$key]
}

function New-Label([string]$Text, [int]$X, [int]$Y, [int]$W, [int]$H, [System.Drawing.Color]$Color, [float]$Size = 9, [System.Drawing.FontStyle]$Style = [System.Drawing.FontStyle]::Regular) {
    $label = [System.Windows.Forms.Label]::new()
    $label.Text = $Text
    $label.Location = [System.Drawing.Point]::new($X, $Y)
    $label.Size = [System.Drawing.Size]::new($W, $H)
    $label.Font = New-Font $Size $Style
    $label.ForeColor = $Color
    $label.BackColor = [System.Drawing.Color]::Transparent
    return $label
}

function New-TextBox([switch]$Multiline) {
    $box = [System.Windows.Forms.TextBox]::new()
    $box.Font = New-Font 9.5
    $box.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
    $box.BackColor = [System.Drawing.Color]::White
    if ($Multiline) {
        $box.Multiline = $true
        $box.AcceptsReturn = $true
        $box.AcceptsTab = $true
        $box.ScrollBars = [System.Windows.Forms.ScrollBars]::Vertical
    }
    return $box
}

function Set-CueBanner([System.Windows.Forms.TextBox]$TextBox, [string]$Text) {
    if (-not $TextBox -or $TextBox.Multiline) { return }
    [void][RimWorldTranslatorNativeMethods]::SendMessage($TextBox.Handle, 0x1501, [IntPtr]1, $Text)
}

function New-Button([string]$Text, [System.Drawing.Color]$BackColor) {
    $button = [System.Windows.Forms.Button]::new()
    $button.Text = $Text
    $button.Font = New-Font 9 ([System.Drawing.FontStyle]::Bold)
    $button.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $button.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(76, 86, 96)
    $button.FlatAppearance.BorderSize = 1
    $button.BackColor = $BackColor
    $button.ForeColor = [System.Drawing.Color]::FromArgb(28, 35, 42)
    $button.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
    $button.AutoEllipsis = $true
    return $button
}

function Set-AccessibleControl([object]$Control, [string]$Name, [string]$Description = "", [int]$TabIndex = -1) {
    if (-not $Control) { return }
    $Control.AccessibleName = $Name
    if ($Description) { $Control.AccessibleDescription = $Description }
    if ($TabIndex -ge 0 -and $Control.PSObject.Properties["TabIndex"]) {
        $Control.TabIndex = $TabIndex
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

function Get-AccessibilityAuditRows([System.Windows.Forms.Control]$Parent, [string]$ParentPath = "Form") {
    $rows = New-Object "System.Collections.Generic.List[object]"
    $index = 0
    foreach ($control in $Parent.Controls) {
        $typeName = $control.GetType().Name
        $controlPath = "$ParentPath/$typeName`[$index`]"
        $interactive = $control.TabStop -or $control -is [System.Windows.Forms.Button] -or $control -is [System.Windows.Forms.TextBoxBase] -or $control -is [System.Windows.Forms.ComboBox] -or $control -is [System.Windows.Forms.CheckBox] -or $control -is [System.Windows.Forms.TabControl] -or $control -is [System.Windows.Forms.ListView]
        $layoutDetail = ""
        if ($control -is [System.Windows.Forms.TabControl]) {
            $layoutDetail = "client=$($control.ClientSize.Width)x$($control.ClientSize.Height);item=$($control.ItemSize.Width)x$($control.ItemSize.Height);pages=$($control.TabPages.Count)"
        }
        [void]$rows.Add([pscustomobject]@{
            path = $controlPath
            type = $typeName
            visible = [bool]$control.Visible
            enabled = [bool]$control.Enabled
            interactive = [bool]$interactive
            tabStop = [bool]$control.TabStop
            tabIndex = [int]$control.TabIndex
            accessibleName = [string]$control.AccessibleName
            accessibleDescription = [string]$control.AccessibleDescription
            bounds = "$($control.Left),$($control.Top),$($control.Width),$($control.Height)"
            layoutDetail = $layoutDetail
        })
        foreach ($child in @(Get-AccessibilityAuditRows -Parent $control -ParentPath $controlPath)) {
            [void]$rows.Add($child)
        }
        $index++
    }
    return $rows.ToArray()
}

function Get-IsWindowsDarkMode {
    if ($script:themeMode -eq "Dark") { return $true }
    if ($script:themeMode -eq "Light") { return $false }
    if ($script:windowsDarkModeCacheValid -and ((Get-Date) - $script:windowsDarkModeCheckedAt).TotalSeconds -lt 5) {
        return $script:windowsDarkModeCached
    }
    try {
        $value = (Get-ItemProperty -LiteralPath "HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize" -Name AppsUseLightTheme -ErrorAction Stop).AppsUseLightTheme
        $script:windowsDarkModeCached = [int]$value -eq 0
    } catch {
        $script:windowsDarkModeCached = $false
    }
    $script:windowsDarkModeCheckedAt = Get-Date
    $script:windowsDarkModeCacheValid = $true
    return $script:windowsDarkModeCached
}

function Ensure-AppDataStore {
    foreach ($dir in @($script:appDataRoot, $script:appReviewRoot)) {
        if (-not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
        }
    }
}

function Write-Utf8JsonFile([string]$Path, [object]$Value, [int]$Depth = 8) {
    $json = ConvertTo-Json -InputObject $Value -Depth $Depth -Compress
    [System.IO.File]::WriteAllText($Path, $json, [System.Text.UTF8Encoding]::new($false))
}

function Reset-ApiProviderSettings {
    $script:selectedApiProviderId = "Cerebras"
    $script:apiProviderConfigs = @{}
    $script:apiProviderKeys = @{}
    foreach ($providerProfile in $script:apiProviders) {
        $script:apiProviderConfigs[[string]$providerProfile.Id] = [pscustomobject]@{
            name = [string]$providerProfile.Name
            url = [string]$providerProfile.Url
            model = [string]$providerProfile.Model
            temperature = if ($providerProfile.PSObject.Properties["Temperature"]) { [double]$providerProfile.Temperature } else { 0.1 }
        }
        $script:apiProviderKeys[[string]$providerProfile.Id] = ""
    }
}

function Get-ApiProviderProfile([string]$Id = "") {
    $target = if ($Id) { $Id } else { $script:selectedApiProviderId }
    return @($script:apiProviders | Where-Object { $_.Id -eq $target } | Select-Object -First 1)[0]
}

function Get-SelectedApiProviderConfig {
    if (-not $script:apiProviderConfigs.ContainsKey($script:selectedApiProviderId)) { return $null }
    return $script:apiProviderConfigs[$script:selectedApiProviderId]
}

function Load-AppSettings {
    Reset-ApiProviderSettings
    $script:themeMode = "System"
    $script:textSize = 10
    $script:highContrast = $false
    $script:autoSave = $true
    $script:rmkWorkspaceRoot = ""
    $script:rmkUseExisting = $true
    if (-not (Test-Path -LiteralPath $script:settingsPath -PathType Leaf)) { return }
    try {
        $settings = [System.IO.File]::ReadAllText($script:settingsPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        if ([string]$settings.themeMode -in @("System", "Light", "Dark")) {
            $script:themeMode = [string]$settings.themeMode
        }
        if ($null -ne $settings.textSize) {
            $script:textSize = [Math]::Max(9, [Math]::Min(12, [int]$settings.textSize))
        }
        if ($null -ne $settings.highContrast) { $script:highContrast = [bool]$settings.highContrast }
        if ($null -ne $settings.autoSave) { $script:autoSave = [bool]$settings.autoSave }
        if ($settings.PSObject.Properties["rmkWorkspaceRoot"]) { $script:rmkWorkspaceRoot = [string]$settings.rmkWorkspaceRoot }
        if ($settings.PSObject.Properties["rmkUseExisting"]) { $script:rmkUseExisting = [bool]$settings.rmkUseExisting }
        if ($settings.PSObject.Properties["apiProviderId"] -and (Get-ApiProviderProfile ([string]$settings.apiProviderId))) {
            $script:selectedApiProviderId = [string]$settings.apiProviderId
        }
        if ($settings.PSObject.Properties["apiProviders"] -and $settings.apiProviders) {
            foreach ($property in @($settings.apiProviders.PSObject.Properties)) {
                $id = [string]$property.Name
                if (-not $script:apiProviderConfigs.ContainsKey($id)) { continue }
                $saved = $property.Value
                $config = $script:apiProviderConfigs[$id]
                if ($saved.PSObject.Properties["name"] -and -not [string]::IsNullOrWhiteSpace([string]$saved.name)) { $config.name = [string]$saved.name }
                if ($saved.PSObject.Properties["url"]) { $config.url = [string]$saved.url }
                if ($saved.PSObject.Properties["model"]) { $config.model = [string]$saved.model }
                if ($saved.PSObject.Properties["temperature"]) { $config.temperature = [Math]::Max(-1, [Math]::Min(2, [double]$saved.temperature)) }
            }
        }
    } catch {
        $script:themeMode = "System"
        $script:textSize = 10
        $script:highContrast = $false
        $script:autoSave = $true
        $script:rmkWorkspaceRoot = ""
        $script:rmkUseExisting = $true
        Reset-ApiProviderSettings
    }
}

function Save-AppSettings {
    Ensure-AppDataStore
    $settings = [ordered]@{
        version = 2
        themeMode = $script:themeMode
        textSize = $script:textSize
        highContrast = $script:highContrast
        autoSave = $script:autoSave
        rmkWorkspaceRoot = $script:rmkWorkspaceRoot
        rmkUseExisting = $script:rmkUseExisting
        apiProviderId = $script:selectedApiProviderId
        apiProviders = [ordered]@{}
    }
    foreach ($providerProfile in $script:apiProviders) {
        $config = $script:apiProviderConfigs[[string]$providerProfile.Id]
        $settings.apiProviders[[string]$providerProfile.Id] = [ordered]@{
            name = [string]$config.name
            url = [string]$config.url
            model = [string]$config.model
            temperature = [double]$config.temperature
        }
    }
    Write-Utf8JsonFile -Path $script:settingsPath -Value $settings -Depth 6
}

function Quote-WindowsProcessArgument([string]$Value) {
    if ($null -eq $Value -or $Value.Length -eq 0) { return '""' }

    $quoted = New-Object System.Text.StringBuilder
    [void]$quoted.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq [char]92) {
            $backslashes++
            continue
        }
        if ($character -eq '"') {
            [void]$quoted.Append([char]92, (($backslashes * 2) + 1))
            [void]$quoted.Append('"')
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0) {
            [void]$quoted.Append([char]92, $backslashes)
            $backslashes = 0
        }
        [void]$quoted.Append($character)
    }
    if ($backslashes -gt 0) {
        [void]$quoted.Append([char]92, ($backslashes * 2))
    }
    [void]$quoted.Append('"')
    return $quoted.ToString()
}

function Get-NormalizedDirectoryPath([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootPath = [System.IO.Path]::GetPathRoot($fullPath)
    while ($fullPath.Length -gt $rootPath.Length -and ($fullPath.EndsWith("\") -or $fullPath.EndsWith("/"))) {
        $fullPath = $fullPath.Substring(0, $fullPath.Length - 1)
    }
    return $fullPath
}

function New-TempFilePath([string]$Prefix, [string]$Extension) {
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) "RimWorldAiTranslatorGui"
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    return Join-Path $dir ("{0}-{1}{2}" -f $Prefix, [System.Guid]::NewGuid().ToString("N"), $Extension)
}

function Remove-TempFiles {
    foreach ($path in @($script:tempFiles)) {
        try {
            if ($path -and (Test-Path -LiteralPath $path)) {
                Remove-Item -LiteralPath $path -Force
            }
        } catch {
        }
    }
    $script:tempFiles.Clear()
}

function Get-ApiKeyLines([string]$Text) {
    $keys = New-Object "System.Collections.Generic.List[string]"
    foreach ($line in ([System.Text.RegularExpressions.Regex]::Split($Text, "\r\n|\n|\r"))) {
        $trim = $line.Trim()
        if (-not $trim -or $trim.StartsWith("#")) { continue }
        if ($trim -match "^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.+)\s*$") {
            $trim = $matches[2].Trim()
        }
        $trim = $trim.Trim('"').Trim("'")
        if ($trim.StartsWith("Bearer ", [System.StringComparison]::OrdinalIgnoreCase)) {
            $trim = $trim.Substring(7).Trim()
        }
        foreach ($part in ($trim -split "[,;]")) {
            $key = $part.Trim().Trim('"').Trim("'")
            if ($key) { [void]$keys.Add($key) }
        }
    }
    return $keys.ToArray()
}

function Stop-ProcessTree([int]$ProcessId) {
    if ($ProcessId -le 0) { return }
    $children = @()
    try {
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $ProcessId" -ErrorAction SilentlyContinue)
    } catch {
        $children = @()
    }
    foreach ($child in $children) {
        Stop-ProcessTree ([int]$child.ProcessId)
    }
    try {
        $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($proc -and -not $proc.HasExited) {
            Stop-Process -Id $ProcessId -Force -ErrorAction Stop
        }
    } catch {
    }
}

function ConvertTo-GuiLogLine([string]$Text) {
    if ($null -eq $Text) { return "" }
    $line = $Text.TrimEnd()
    if ([string]::IsNullOrWhiteSpace($line)) { return "" }
    if ($line -match '^\s*\{"translations"\s*:\s*\[') {
        return "[로그 축약] 모델 응답 원문은 숨겼습니다."
    }
    if ($line -match '(\\u000a\s*){6,}') {
        return "[로그 축약] 반복 개행 escape를 숨겼습니다."
    }
    if ($line.Length -gt 1000) {
        return $line.Substring(0, 1000) + "... [로그 축약]"
    }
    return $line
}

function Add-Log([string]$Text) {
    if (-not $txtLog) { return }
    $line = ConvertTo-GuiLogLine $Text
    if ([string]::IsNullOrWhiteSpace($line)) { return }
    $stamp = Get-Date -Format "HH:mm:ss"
    $txtLog.AppendText("[$stamp] $line`r`n")
    $txtLog.SelectionStart = $txtLog.TextLength
    $txtLog.ScrollToCaret()
}

function Read-NewProcessLogLines {
    $lines = New-Object "System.Collections.Generic.List[string]"
    if (-not $script:translationLogFile -or -not (Test-Path -LiteralPath $script:translationLogFile)) { return $lines.ToArray() }
    try {
        $fs = [System.IO.File]::Open($script:translationLogFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            if ($fs.Length -le $script:translationLogOffset) { return $lines.ToArray() }
            [void]$fs.Seek($script:translationLogOffset, [System.IO.SeekOrigin]::Begin)
            $count = [int]($fs.Length - $script:translationLogOffset)
            $bytes = New-Object byte[] $count
            $read = $fs.Read($bytes, 0, $count)
            $script:translationLogOffset += $read
            if ($read -le 0) { return $lines.ToArray() }
            $text = [System.Text.Encoding]::UTF8.GetString($bytes, 0, $read)
            $combined = $script:translationLogPartial + $text
            $parts = [System.Text.RegularExpressions.Regex]::Split($combined, "\r?\n")
            if ($combined -match "\r?\n$") {
                $script:translationLogPartial = ""
                $complete = $parts
            } else {
                $script:translationLogPartial = $parts[$parts.Count - 1]
                $complete = if ($parts.Count -gt 1) { $parts[0..($parts.Count - 2)] } else { @() }
            }
            foreach ($line in $complete) {
                if ($line.Length -gt 0) { [void]$lines.Add($line) }
            }
        } finally {
            $fs.Close()
        }
    } catch {
    }
    return $lines.ToArray()
}

function Update-ProgressFromLine([string]$Line) {
    if ($Line -match "Translating batch\s+(\d+)/(\d+)\s+\((\d+)\s+entries\)") {
        $current = [int]$matches[1]
        $total = [int]$matches[2]
        if ($total -gt 0) {
            $progressRun.Maximum = $total
            $progressRun.Value = [Math]::Min($current, $total)
            $lblRunStatus.Text = "번역 배치 $current / $total"
        }
    } elseif ($Line -match "^Review output:\s+(.+)$") {
        $script:lastReviewOutputPath = $matches[1].Trim()
        $lblRunStatus.Text = "검수 결과 생성됨"
    } elseif ($Line -match "^Translation provider:\s+(.+)$") {
        $script:lastProvider = $matches[1].Trim()
        $lblRunStatus.Text = "번역 엔진: $($matches[1])"
    } elseif ($Line -match "^Detected source language:\s+(.+)$") {
        $lblRunStatus.Text = "원문 언어: $($matches[1])"
    } elseif ($Line -match "^Pending(?: translation)? entries:\s+(.+)$") {
        $lblRunStatus.Text = "번역 대상: $($matches[1])개"
    } elseif ($Line -match "^Done\.$") {
        if ($progressRun.Maximum -gt 0) { $progressRun.Value = $progressRun.Maximum }
        $lblRunStatus.Text = "완료"
    }
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

function Get-StableId([string]$Text) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text.ToLowerInvariant())
        $hash = $sha.ComputeHash($bytes)
        return ([BitConverter]::ToString($hash).Replace("-", "").Substring(0, 16)).ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}

function Get-TextFingerprint([string]$Text) {
    if (-not $script:textFingerprintSha256) { $script:textFingerprintSha256 = [System.Security.Cryptography.SHA256]::Create() }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-FlatString $Text))
    return ([BitConverter]::ToString($script:textFingerprintSha256.ComputeHash($bytes)).Replace("-", "")).ToLowerInvariant()
}

function Get-RowRuntimeCache([object]$Row) {
    if ($Row.PSObject.Properties["_runtimeCache"] -and $Row._runtimeCache) { return $Row._runtimeCache }
    $cache = [pscustomobject]@{
        Identity = ""
        RelativeTarget = ""
        SourceFingerprint = ""
        Decision = $null
        DefContext = $null
    }
    $Row | Add-Member -NotePropertyName _runtimeCache -NotePropertyValue $cache
    return $cache
}

function Invalidate-DashboardProjectData([string]$ProjectId = "") {
    $script:dashboardProjectsDirty = $true
    if ($ProjectId -and $script:projectStatsCache.ContainsKey($ProjectId)) {
        $script:projectStatsCache.Remove($ProjectId)
        $script:projectStatsCacheDirty = $true
    }
}

function Load-ProjectStatsCache {
    $script:projectStatsCache = @{}
    $script:projectStatsCacheDirty = $false
    if (-not (Test-Path -LiteralPath $script:projectStatsCachePath -PathType Leaf)) { return }
    try {
        $json = [System.IO.File]::ReadAllText($script:projectStatsCachePath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        if ([int]$json.version -ne 1) { return }
        foreach ($entry in @($json.entries)) {
            if (-not $entry -or [string]::IsNullOrWhiteSpace([string]$entry.projectId) -or
                [string]::IsNullOrWhiteSpace([string]$entry.stamp) -or -not $entry.stats) { continue }
            $script:projectStatsCache[[string]$entry.projectId] = [pscustomobject]@{
                Stamp = [string]$entry.stamp
                Stats = $entry.stats
            }
        }
    } catch {
        $script:projectStatsCache = @{}
    }
}

function Save-ProjectStatsCache {
    if (-not $script:projectStatsCacheDirty) { return }
    try {
        Ensure-AppDataStore
        $entries = foreach ($projectId in @($script:projectStatsCache.Keys | Sort-Object)) {
            $entry = $script:projectStatsCache[$projectId]
            if (-not $entry -or -not $entry.Stats -or [string]::IsNullOrWhiteSpace([string]$entry.Stamp)) { continue }
            [ordered]@{
                projectId = [string]$projectId
                stamp = [string]$entry.Stamp
                stats = $entry.Stats
            }
        }
        $payload = [ordered]@{ version = 1; entries = @($entries) }
        Write-Utf8JsonFile -Path $script:projectStatsCachePath -Value $payload -Depth 7
        $script:projectStatsCacheDirty = $false
    } catch {
    }
}

function Load-ProjectStore {
    Ensure-AppDataStore
    $script:dashboardProjectsDirty = $true
    $script:projects = @()
    if (Test-Path -LiteralPath $script:projectStorePath -PathType Leaf) {
        try {
            $json = [System.IO.File]::ReadAllText($script:projectStorePath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
            $script:projects = @($json.projects)
        } catch {
            $script:projects = @()
        }
    }
    $storeChanged = $false
    foreach ($project in @($script:projects)) {
        if ($project -and -not $project.PSObject.Properties["sourceLanguageFolder"]) {
            $project | Add-Member -NotePropertyName sourceLanguageFolder -NotePropertyValue "Auto"
            $storeChanged = $true
        }
        if ($project -and $project.modRoot -and (Test-Path -LiteralPath ([string]$project.modRoot) -PathType Container)) {
            try {
                $resolvedContentRoot = Resolve-RimWorldModContentRoot ([string]$project.modRoot)
                $storedRoot = [System.IO.Path]::GetFullPath([string]$project.modRoot).TrimEnd("\", "/")
                if ($resolvedContentRoot -and -not $storedRoot.Equals($resolvedContentRoot.TrimEnd("\", "/"), [System.StringComparison]::OrdinalIgnoreCase)) {
                    $project.modRoot = $resolvedContentRoot
                    $project.updatedAt = (Get-Date).ToString("o")
                    $storeChanged = $true
                }
            } catch {
            }
        }
    }

    Load-ProjectStatsCache
    $activeProjectIds = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($project in @($script:projects)) {
        if ($project -and $project.id) { [void]$activeProjectIds.Add([string]$project.id) }
    }
    foreach ($projectId in @($script:projectStatsCache.Keys)) {
        if (-not $activeProjectIds.Contains([string]$projectId)) {
            $script:projectStatsCache.Remove($projectId)
            $script:projectStatsCacheDirty = $true
        }
    }
    Save-ProjectStatsCache
    if ($storeChanged) { Save-ProjectStore }
}

function Get-RowSourceFingerprint([object]$Row) {
    $cache = Get-RowRuntimeCache $Row
    if ($cache.SourceFingerprint) { return [string]$cache.SourceFingerprint }
    $hash = Get-TextFingerprint (ConvertTo-FlatString $Row.source)
    $cache.SourceFingerprint = $hash
    return $hash
}

function Save-ProjectStore {
    Ensure-AppDataStore
    $payload = [ordered]@{
        version = 2
        updatedAt = (Get-Date).ToString("o")
        projects = @($script:projects)
    }
    Write-Utf8JsonFile -Path $script:projectStorePath -Value $payload -Depth 12
    $script:dashboardProjectsDirty = $true
}

function Get-ProjectIdForMod([string]$ModRoot, [string]$PackageId = "", [string]$WorkshopId = "") {
    $basis = if ($PackageId) { "pkg:$PackageId" } elseif ($WorkshopId) { "ws:$WorkshopId" } else { [System.IO.Path]::GetFullPath($ModRoot).TrimEnd("\", "/") }
    return Get-StableId $basis
}

function Get-WorkshopIdFromPath([string]$Path) {
    $match = [System.Text.RegularExpressions.Regex]::Match($Path, '\\workshop\\content\\294100\\(\d+)')
    if ($match.Success) { return $match.Groups[1].Value }
    return ""
}

function Get-OrCreateProject([object]$ModInfo, [string]$SourceLanguageFolder = "") {
    $modRoot = Get-NormalizedDirectoryPath ([string]$ModInfo.Path)
    $projectId = Get-ProjectIdForMod -ModRoot $modRoot -PackageId ([string]$ModInfo.PackageId) -WorkshopId ([string]$ModInfo.WorkshopId)
    $existing = @($script:projects | Where-Object { $_.id -eq $projectId } | Select-Object -First 1)
    if ($existing.Count -gt 0) {
        $project = $existing[0]
        $project.name = [string]$ModInfo.Name
        $project.modRoot = $modRoot
        $project.packageId = [string]$ModInfo.PackageId
        $project.workshopId = [string]$ModInfo.WorkshopId
        if (-not $project.PSObject.Properties["sourceLanguageFolder"]) {
            $project | Add-Member -NotePropertyName sourceLanguageFolder -NotePropertyValue $(if ($SourceLanguageFolder) { $SourceLanguageFolder } else { "Auto" })
        }
        $project.updatedAt = (Get-Date).ToString("o")
        return $project
    }
    $project = [pscustomobject]@{
        id = $projectId
        name = [string]$ModInfo.Name
        modRoot = $modRoot
        packageId = [string]$ModInfo.PackageId
        workshopId = [string]$ModInfo.WorkshopId
        sourceLanguageFolder = if ($SourceLanguageFolder) { $SourceLanguageFolder } else { "Auto" }
        latestReviewRoot = ""
        latestReviewAt = ""
        lastAppliedAt = ""
        createdAt = (Get-Date).ToString("o")
        updatedAt = (Get-Date).ToString("o")
        runs = @()
    }
    $script:projects = @($script:projects) + $project
    return $project
}

function Get-SelectedProject {
    if (-not $script:selectedProjectId) { return $null }
    foreach ($project in @($script:projects)) {
        if ([string]$project.id -eq [string]$script:selectedProjectId) { return $project }
    }
    return $null
}

function Test-ReviewRootBelongsToProject([object]$Project, [string]$ReviewRoot) {
    if (-not $Project -or [string]::IsNullOrWhiteSpace($ReviewRoot)) { return $false }
    try {
        $reviewFull = [System.IO.Path]::GetFullPath($ReviewRoot).TrimEnd("\", "/")
    } catch {
        return $false
    }

    $candidateRoots = New-Object "System.Collections.Generic.List[string]"
    if ($Project.latestReviewRoot) { [void]$candidateRoots.Add([string]$Project.latestReviewRoot) }
    foreach ($run in @($Project.runs)) {
        if ($run -and $run.reviewRoot) { [void]$candidateRoots.Add([string]$run.reviewRoot) }
    }
    foreach ($candidateRoot in $candidateRoots) {
        try {
            $candidateFull = [System.IO.Path]::GetFullPath($candidateRoot).TrimEnd("\", "/")
            if ($reviewFull.Equals($candidateFull, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
        } catch {
        }
    }
    return $false
}

function Get-ActiveProjectModRoot([switch]$Require) {
    $path = ""
    $project = Get-SelectedProject
    if ($project -and $project.modRoot) {
        $path = [string]$project.modRoot
    } elseif ($script:selectedModRoot) {
        $path = [string]$script:selectedModRoot
    }

    if (-not [string]::IsNullOrWhiteSpace($path)) {
        try { $path = Get-NormalizedDirectoryPath $path } catch { $path = "" }
    }

    if ($path -and (Test-Path -LiteralPath $path -PathType Container)) {
        $script:selectedModRoot = $path
        return $path
    }

    if ($Require) {
        throw "프로젝트에 연결된 모드 폴더를 찾을 수 없습니다."
    }
    return ""
}

function Set-ActiveProject([object]$Project) {
    if (-not $Project) { return }
    $script:selectedProjectId = [string]$Project.id
    if ($Project.modRoot) {
        try { $script:selectedModRoot = Get-NormalizedDirectoryPath ([string]$Project.modRoot) } catch { $script:selectedModRoot = [string]$Project.modRoot }
    } else {
        $script:selectedModRoot = ""
    }

    if ($lblProject) { $lblProject.Text = if ($Project.name) { [string]$Project.name } else { "RimWorld AI Translator" } }
    if ($lblPath) { $lblPath.Text = $script:selectedModRoot }
    Update-SearchCrumb

    $hasProjectMod = [bool](Get-ActiveProjectModRoot)
    if ($btnTranslate) { $btnTranslate.Enabled = $hasProjectMod }
    if ($btnApply) { $btnApply.Enabled = [bool]($script:reviewRoot -and (Test-Path -LiteralPath $script:reviewRoot) -and $hasProjectMod) }
    if ($btnApplyTranslated) { $btnApplyTranslated.Enabled = [bool]($script:reviewRoot -and (Test-Path -LiteralPath $script:reviewRoot) -and $hasProjectMod) }
    Refresh-ProjectList
    if ($tabs -and $tabRmk -and $tabs.SelectedTab -eq $tabRmk) { Refresh-RmkPanel }
}

function Test-PathStrictlyInsideRoot([string]$Path, [string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($Root)) { return $false }
    try {
        $pathFull = [System.IO.Path]::GetFullPath($Path).TrimEnd("\", "/")
        $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd("\", "/")
        $prefix = $rootFull + [System.IO.Path]::DirectorySeparatorChar
        return $pathFull.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
    } catch {
        return $false
    }
}

function Test-PathContainsReparsePoint([string]$Path, [string]$StopRoot) {
    try {
        $current = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
        $stopFull = [System.IO.Path]::GetFullPath($StopRoot).TrimEnd("\", "/")
        while ($current) {
            if (($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { return $true }
            $currentFull = [System.IO.Path]::GetFullPath($current.FullName).TrimEnd("\", "/")
            if ($currentFull.Equals($stopFull, [System.StringComparison]::OrdinalIgnoreCase)) { break }
            $parentPath = Split-Path -Parent $current.FullName
            if (-not $parentPath) { break }
            $current = Get-Item -LiteralPath $parentPath -Force -ErrorAction Stop
        }
    } catch {
        return $true
    }
    return $false
}

function Get-AppOwnedReviewRoots {
    $roots = New-Object "System.Collections.Generic.List[string]"
    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($candidate in @($script:appReviewRoot, (Join-Path $scriptRoot "reviews"))) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        try { $full = [System.IO.Path]::GetFullPath($candidate).TrimEnd("\", "/") } catch { continue }
        if ($seen.Add($full)) { [void]$roots.Add($full) }
    }
    return $roots.ToArray()
}

function Get-AppOwnedReviewDirectory([string]$Path, [object]$Project = $null) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Container)) { return "" }
    try { $full = [System.IO.Path]::GetFullPath($Path).TrimEnd("\", "/") } catch { return "" }
    if ($Project -and $Project.modRoot) {
        $modRoot = [string]$Project.modRoot
        try { $modFull = [System.IO.Path]::GetFullPath($modRoot).TrimEnd("\", "/") } catch { return "" }
        if ($full.Equals($modFull, [System.StringComparison]::OrdinalIgnoreCase) -or
            (Test-PathStrictlyInsideRoot -Path $full -Root $modRoot)) {
            return ""
        }
    }
    foreach ($root in Get-AppOwnedReviewRoots) {
        if (-not (Test-PathStrictlyInsideRoot -Path $full -Root $root)) { continue }
        if (Test-PathContainsReparsePoint -Path $full -StopRoot $root) { return "" }
        return $full
    }
    return ""
}

function Write-ProjectReviewMarker([object]$Project, [string]$ReviewRoot) {
    if (-not $Project -or -not $Project.id) { return }
    $safeRoot = Get-AppOwnedReviewDirectory -Path $ReviewRoot -Project $Project
    if (-not $safeRoot) { return }
    try {
        $marker = [ordered]@{
            version = 1
            projectId = [string]$Project.id
            modRoot = [string]$Project.modRoot
            workshopId = [string]$Project.workshopId
            createdAt = (Get-Date).ToString("o")
        }
        Write-Utf8JsonFile -Path (Join-Path $safeRoot ".rimworld-ai-project.json") -Value $marker -Depth 4
    } catch {
        Add-Log "프로젝트 검수 기록 표식 저장 실패: $($_.Exception.Message)"
    }
}

function Get-ProjectCleanupPlan([object]$Project) {
    $safePaths = New-Object "System.Collections.Generic.List[string]"
    $unsafePaths = New-Object "System.Collections.Generic.List[string]"
    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    $recorded = New-Object "System.Collections.Generic.List[string]"
    if ($Project.latestReviewRoot) { [void]$recorded.Add([string]$Project.latestReviewRoot) }
    foreach ($run in @($Project.runs)) {
        if ($run -and $run.reviewRoot) { [void]$recorded.Add([string]$run.reviewRoot) }
    }

    foreach ($path in $recorded) {
        if (-not (Test-Path -LiteralPath $path -PathType Container)) { continue }
        try { $full = [System.IO.Path]::GetFullPath($path).TrimEnd("\", "/") } catch { [void]$unsafePaths.Add($path); continue }
        if (-not $seen.Add($full)) { continue }
        $safe = Get-AppOwnedReviewDirectory -Path $full -Project $Project
        if ($safe) { [void]$safePaths.Add($safe) } else { [void]$unsafePaths.Add($full) }
    }

    foreach ($root in Get-AppOwnedReviewRoots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        foreach ($directory in Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue) {
            $markerPath = Join-Path $directory.FullName ".rimworld-ai-project.json"
            if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) { continue }
            try {
                $marker = [System.IO.File]::ReadAllText($markerPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
                if ([string]$marker.projectId -ne [string]$Project.id) { continue }
                $safe = Get-AppOwnedReviewDirectory -Path $directory.FullName -Project $Project
                if ($safe -and $seen.Add($safe)) { [void]$safePaths.Add($safe) }
            } catch {
            }
        }
    }
    return [pscustomobject]@{ SafePaths = $safePaths.ToArray(); UnsafePaths = $unsafePaths.ToArray() }
}

function Remove-AppOwnedProjectReviewDirectories([object]$Project, [string[]]$Paths) {
    $failures = New-Object "System.Collections.Generic.List[string]"
    foreach ($path in @($Paths)) {
        $verified = Get-AppOwnedReviewDirectory -Path $path -Project $Project
        if (-not $verified) { [void]$failures.Add("안전 경계 재확인 실패: $path"); continue }
        try {
            Remove-Item -LiteralPath $verified -Recurse -Force -ErrorAction Stop
        } catch {
            [void]$failures.Add("$verified : $($_.Exception.Message)")
        }
    }
    return $failures.ToArray()
}

function Remove-TranslationProject([object]$Project) {
    if (-not $Project -or -not $Project.id) { return }
    if ($script:process -and -not $script:process.HasExited -and [string]$Project.id -eq [string]$script:selectedProjectId) {
        [System.Windows.Forms.MessageBox]::Show("이 프로젝트의 번역이 실행 중입니다. 먼저 번역을 중지한 뒤 삭제하세요.", "프로젝트 삭제") | Out-Null
        return
    }

    $plan = Get-ProjectCleanupPlan $Project
    if (@($plan.UnsafePaths).Count -gt 0) {
        $unsafeText = [string]::Join("`r`n", @($plan.UnsafePaths | Select-Object -First 5))
        [System.Windows.Forms.MessageBox]::Show("앱 전용 검수 폴더 밖의 기록이 있어 안전하게 삭제할 수 없습니다.`r`n`r`n$unsafeText", "프로젝트 삭제 중단", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
        return
    }

    $name = if ($Project.name) { [string]$Project.name } else { "이름 없는 프로젝트" }
    $message = "'$name' 프로젝트를 삭제합니다.`r`n`r`n삭제 항목:`r`n- 프로젝트 등록 정보`r`n- 로컬 검수 작업 폴더 $(@($plan.SafePaths).Count)개`r`n`r`n보존 항목:`r`n- 원본 모드 폴더 전체`r`n- 모드의 Languages\Korean 번역`r`n`r`n계속할까요?"
    $answer = [System.Windows.Forms.MessageBox]::Show($message, "프로젝트 삭제", [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Warning, [System.Windows.Forms.MessageBoxDefaultButton]::Button2)
    if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return }

    $failures = @(Remove-AppOwnedProjectReviewDirectories -Project $Project -Paths @($plan.SafePaths))
    if ($failures.Count -gt 0) {
        [System.Windows.Forms.MessageBox]::Show("일부 검수 폴더를 삭제하지 못해 프로젝트 등록은 유지했습니다.`r`n`r`n$([string]::Join("`r`n", $failures))", "프로젝트 삭제 실패", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
        return
    }

    $script:projects = @($script:projects | Where-Object { [string]$_.id -ne [string]$Project.id })
    if ([string]$script:selectedProjectId -eq [string]$Project.id) {
        $script:selectedProjectId = ""
        $script:selectedModRoot = ""
        $script:reviewRoot = ""
        $script:comparisonFile = ""
        $script:rows = @()
        $script:decisions = @{}
        $script:reviewStats = $null
        $script:currentRowIndex = -1
        if ($btnApply) { $btnApply.Enabled = $false }
        if ($btnApplyTranslated) { $btnApplyTranslated.Enabled = $false }
    }
    Invalidate-DashboardProjectData ([string]$Project.id)
    Save-ProjectStore
    Save-ProjectStatsCache
    Refresh-ProjectList
    Refresh-DashboardProjects
    Refresh-DashboardActivity
    Add-Log "프로젝트 삭제 완료: $name (원본 모드와 Korean 폴더 보존)"
}

function Register-ProjectRun([string]$ReviewRoot, [string]$Provider = "") {
    $project = Get-SelectedProject
    if (-not $project) {
        if (-not $script:selectedModRoot) { return }
        $info = Get-RimWorldModInfo -ModPath $script:selectedModRoot -Source "Selected"
        if (-not $info) {
            $info = [pscustomobject]@{
                Display = Split-Path -Leaf $script:selectedModRoot
                Name = Split-Path -Leaf $script:selectedModRoot
                Path = $script:selectedModRoot
                Source = "Selected"
                Folder = Split-Path -Leaf $script:selectedModRoot
                PackageId = ""
                WorkshopId = Get-WorkshopIdFromPath $script:selectedModRoot
                Search = $script:selectedModRoot.ToLowerInvariant()
            }
        }
        $project = Get-OrCreateProject $info
    }
    $script:selectedProjectId = $project.id
    if ($ReviewRoot) {
        $project.latestReviewRoot = [System.IO.Path]::GetFullPath($ReviewRoot)
        $project.latestReviewAt = (Get-Date).ToString("o")
        $existingRun = @($project.runs | Where-Object { $_.reviewRoot -eq $project.latestReviewRoot } | Select-Object -First 1)
        if ($existingRun.Count -eq 0) {
            $run = [pscustomobject]@{
                reviewRoot = $project.latestReviewRoot
                createdAt = $project.latestReviewAt
                provider = $Provider
            }
            $project.runs = @($project.runs) + $run
        } else {
            $existingRun[0].createdAt = $project.latestReviewAt
            $existingRun[0].provider = $Provider
        }
        if (@($project.runs).Count -gt 40) {
            $project.runs = @($project.runs | Select-Object -Last 40)
        }
        Write-ProjectReviewMarker -Project $project -ReviewRoot $project.latestReviewRoot
    }
    $project.updatedAt = (Get-Date).ToString("o")
    Save-ProjectStore
    Refresh-ProjectList
    Invalidate-DashboardProjectData ([string]$project.id)
    if ($dashboardPanel -and $dashboardPanel.Visible) {
        Refresh-DashboardProjects
        Refresh-DashboardActivity
    }
}

function Mark-ProjectApplied {
    if (-not $script:selectedProjectId) { return }
    foreach ($project in $script:projects) {
        if ($project.id -eq $script:selectedProjectId) {
            $project.lastAppliedAt = (Get-Date).ToString("o")
            $project.updatedAt = (Get-Date).ToString("o")
            break
        }
    }
    Save-ProjectStore
    Refresh-ProjectList
    Invalidate-DashboardProjectData ([string]$script:selectedProjectId)
    if ($dashboardPanel -and $dashboardPanel.Visible) {
        Refresh-DashboardProjects
        Refresh-DashboardActivity
    }
}

function Add-UniqueExistingDirectory($List, $Seen, [string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    try { $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\') } catch { return }
    if (-not (Test-Path -LiteralPath $full -PathType Container)) { return }
    if ($Seen.Add($full)) { [void]$List.Add($full) }
}

function Get-SteamRootCandidates {
    $roots = New-Object "System.Collections.Generic.List[string]"
    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($regPath in @("HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam", "HKLM:\SOFTWARE\Valve\Steam")) {
        try {
            $props = Get-ItemProperty -LiteralPath $regPath -ErrorAction Stop
            Add-UniqueExistingDirectory $roots $seen $props.SteamPath
            Add-UniqueExistingDirectory $roots $seen $props.InstallPath
        } catch {
        }
    }
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, $env:LOCALAPPDATA)) {
        if ([string]::IsNullOrWhiteSpace($base)) { continue }
        Add-UniqueExistingDirectory $roots $seen (Join-Path $base "Steam")
    }
    foreach ($drive in [System.IO.DriveInfo]::GetDrives()) {
        if (-not $drive.IsReady -or $drive.DriveType -ne [System.IO.DriveType]::Fixed) { continue }
        Add-UniqueExistingDirectory $roots $seen (Join-Path $drive.RootDirectory.FullName "SteamLibrary")
        Add-UniqueExistingDirectory $roots $seen (Join-Path $drive.RootDirectory.FullName "Steam")
    }
    return $roots.ToArray()
}

function Get-SteamLibrariesFromVdf([string]$SteamRoot) {
    $libs = New-Object "System.Collections.Generic.List[string]"
    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($vdfPath in @((Join-Path $SteamRoot "steamapps\libraryfolders.vdf"), (Join-Path $SteamRoot "config\libraryfolders.vdf"))) {
        if (-not (Test-Path -LiteralPath $vdfPath -PathType Leaf)) { continue }
        try {
            $text = [System.IO.File]::ReadAllText($vdfPath)
            foreach ($match in [System.Text.RegularExpressions.Regex]::Matches($text, '"path"\s+"([^"]+)"')) {
                Add-UniqueExistingDirectory $libs $seen ($match.Groups[1].Value -replace '\\\\', '\')
            }
            foreach ($match in [System.Text.RegularExpressions.Regex]::Matches($text, '(?m)^\s*"\d+"\s+"([^"]+)"')) {
                Add-UniqueExistingDirectory $libs $seen ($match.Groups[1].Value -replace '\\\\', '\')
            }
        } catch {
        }
    }
    return $libs.ToArray()
}

function Get-SteamLibraryRoots {
    $roots = New-Object "System.Collections.Generic.List[string]"
    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($root in (Get-SteamRootCandidates)) { Add-UniqueExistingDirectory $roots $seen $root }
    foreach ($root in @($roots.ToArray())) {
        foreach ($lib in (Get-SteamLibrariesFromVdf $root)) { Add-UniqueExistingDirectory $roots $seen $lib }
    }
    return $roots.ToArray()
}

function Get-RimWorldModContainers {
    $containers = New-Object "System.Collections.Generic.List[object]"
    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($root in (Get-SteamLibraryRoots)) {
        $workshop = Join-Path $root "steamapps\workshop\content\294100"
        if ((Test-Path -LiteralPath $workshop -PathType Container) -and $seen.Add($workshop)) {
            [void]$containers.Add([pscustomobject]@{ Path = $workshop; Source = "Workshop" })
        }
        $localMods = Join-Path $root "steamapps\common\RimWorld\Mods"
        if ((Test-Path -LiteralPath $localMods -PathType Container) -and $seen.Add($localMods)) {
            [void]$containers.Add([pscustomobject]@{ Path = $localMods; Source = "Local" })
        }
    }
    return $containers.ToArray()
}

function Get-ModAboutPath([string]$ModPath) {
    $direct = Join-Path $ModPath "About\About.xml"
    if (Test-Path -LiteralPath $direct -PathType Leaf) { return $direct }
    try {
        foreach ($child in Get-ChildItem -LiteralPath $ModPath -Directory -ErrorAction SilentlyContinue) {
            $nested = Join-Path $child.FullName "About\About.xml"
            if (Test-Path -LiteralPath $nested -PathType Leaf) { return $nested }
        }
    } catch {
    }
    return $null
}

function Get-PreferredLoadFolderRoot([string]$ModPath) {
    $loadFoldersPath = Join-Path $ModPath "LoadFolders.xml"
    if (-not (Test-Path -LiteralPath $loadFoldersPath -PathType Leaf)) { return $null }
    try {
        $doc = Read-SafeXmlDocument $loadFoldersPath
        $versionNodes = @($doc.DocumentElement.ChildNodes |
            Where-Object { $_.NodeType -eq [System.Xml.XmlNodeType]::Element -and $_.LocalName -match '^v\d' } |
            Sort-Object @{ Expression = {
                $numbers = [System.Text.RegularExpressions.Regex]::Matches($_.LocalName, '\d+')
                $score = 0
                foreach ($number in $numbers) { $score = ($score * 100) + [int]$number.Value }
                return $score
            }; Descending = $true })
        foreach ($node in $versionNodes) {
            foreach ($li in @($node.li)) {
                if ($li -is [System.Xml.XmlElement] -and ($li.HasAttribute("IfModActive") -or $li.HasAttribute("IfModNotActive"))) { continue }
                $relative = if ($li -is [System.Xml.XmlElement]) { ([string]$li.InnerText).Trim() } else { ([string]$li).Trim() }
                if ([string]::IsNullOrWhiteSpace($relative)) { continue }
                $candidate = if ($relative -in @("/", "\", ".")) { $ModPath } else { Join-Path $ModPath $relative }
                if (-not (Test-Path -LiteralPath $candidate -PathType Container)) { continue }
                $modFull = [System.IO.Path]::GetFullPath($ModPath).TrimEnd("\", "/")
                $candidateFull = [System.IO.Path]::GetFullPath($candidate).TrimEnd("\", "/")
                $modPrefix = $modFull + [System.IO.Path]::DirectorySeparatorChar
                if (-not $candidateFull.Equals($modFull, [System.StringComparison]::OrdinalIgnoreCase) -and
                    -not $candidateFull.StartsWith($modPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
                if ((Test-Path -LiteralPath (Join-Path $candidate "Defs") -PathType Container) -or
                    (Test-Path -LiteralPath (Join-Path $candidate "Languages") -PathType Container)) {
                    return $candidateFull
                }
            }
        }
    } catch {
    }
    return $null
}

function Resolve-RimWorldModContentRoot([string]$ModPath) {
    $full = Get-NormalizedDirectoryPath $ModPath
    if ((Test-Path -LiteralPath (Join-Path $full "Defs") -PathType Container) -or
        (Test-Path -LiteralPath (Join-Path $full "Languages") -PathType Container)) {
        return $full
    }
    $preferred = Get-PreferredLoadFolderRoot $full
    if ($preferred) { return Get-NormalizedDirectoryPath $preferred }
    return $full
}

function Get-RimWorldModInfo([string]$ModPath, [string]$Source) {
    if (-not (Test-Path -LiteralPath $ModPath -PathType Container)) { return $null }
    $aboutPath = Get-ModAboutPath $ModPath
    $preferredLoadRoot = Get-PreferredLoadFolderRoot $ModPath
    $effectiveModPath = if ($preferredLoadRoot) { $preferredLoadRoot } else { $ModPath }
    if ($aboutPath -and -not $preferredLoadRoot) {
        try {
            $aboutDir = Split-Path -Parent $aboutPath
            $candidateRoot = Split-Path -Parent $aboutDir
            if ($candidateRoot -and ([System.IO.Path]::GetFullPath($candidateRoot).TrimEnd('\') -ne [System.IO.Path]::GetFullPath($ModPath).TrimEnd('\'))) {
                $effectiveModPath = $candidateRoot
            }
        } catch {
        }
    }
    $hasContent = $aboutPath -or
        (Test-Path -LiteralPath (Join-Path $effectiveModPath "Defs") -PathType Container) -or
        (Test-Path -LiteralPath (Join-Path $effectiveModPath "Languages") -PathType Container)
    if (-not $hasContent) { return $null }
    $name = Split-Path -Leaf $effectiveModPath
    $packageId = ""
    if ($aboutPath) {
        try {
            $about = Read-SafeXmlDocument $aboutPath
            $aboutName = [string]$about.ModMetaData.name
            $aboutPackageId = [string]$about.ModMetaData.packageId
            if (-not [string]::IsNullOrWhiteSpace($aboutName)) { $name = $aboutName.Trim() }
            if (-not [string]::IsNullOrWhiteSpace($aboutPackageId)) { $packageId = $aboutPackageId.Trim() }
        } catch {
        }
    }
    $folderName = Split-Path -Leaf $ModPath
    $effectiveLeaf = Split-Path -Leaf $effectiveModPath
    $workshopId = Get-WorkshopIdFromPath $ModPath
    $tag = if ($workshopId) {
        if ($effectiveLeaf -and $effectiveLeaf -ne $folderName) { "W:$workshopId\$effectiveLeaf" } else { "W:$workshopId" }
    } elseif ($Source -eq "Local") {
        "Local"
    } else {
        $Source
    }
    $displayName = $name
    if ($displayName.Length -gt 44) { $displayName = $displayName.Substring(0, 41) + "..." }
    return [pscustomobject]@{
        Display = "$displayName [$tag]"
        Name = $name
        Path = [System.IO.Path]::GetFullPath($effectiveModPath)
        Source = $Source
        Folder = $folderName
        PackageId = $packageId
        WorkshopId = $workshopId
        Search = ("$name $folderName $effectiveLeaf $packageId $workshopId $ModPath $effectiveModPath").ToLowerInvariant()
    }
}

function Get-ProjectSourceLanguageRank([string]$Name) {
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

function Get-ProjectSourceLanguageName([string]$Folder) {
    switch -Regex ($Folder) {
        '^English' { return "영어" }
        '^ChineseSimplified' { return "중국어 간체" }
        '^ChineseTraditional' { return "중국어 번체" }
        '^Japanese' { return "일본어" }
        '^Spanish' { return "스페인어" }
        '^French' { return "프랑스어" }
        '^German' { return "독일어" }
        '^Russian' { return "러시아어" }
        '^Portuguese' { return "포르투갈어" }
        '^Polish' { return "폴란드어" }
        default { return $Folder }
    }
}

function Get-ModSourceLanguageOptions([string]$ModRoot) {
    $languagesRoot = Join-Path $ModRoot "Languages"
    if (-not (Test-Path -LiteralPath $languagesRoot -PathType Container)) { return @() }
    $options = New-Object "System.Collections.Generic.List[object]"
    foreach ($directory in Get-ChildItem -LiteralPath $languagesRoot -Directory -ErrorAction SilentlyContinue) {
        if ($directory.Name -match '^(Korean|KoreanLegacy|한국)') { continue }
        $xmlCount = @(Get-ChildItem -LiteralPath $directory.FullName -Recurse -File -Filter "*.xml" -ErrorAction SilentlyContinue).Count
        if ($xmlCount -eq 0) { continue }
        $languageName = Get-ProjectSourceLanguageName $directory.Name
        [void]$options.Add([pscustomobject]@{
            Folder = $directory.Name
            Path = [System.IO.Path]::GetFullPath($directory.FullName)
            Display = "$languageName  ·  $($directory.Name)  ·  XML ${xmlCount}개"
            Rank = Get-ProjectSourceLanguageRank $directory.Name
            XmlCount = $xmlCount
        })
    }
    return @($options.ToArray() | Sort-Object Rank, Folder)
}

function Select-ProjectSourceLanguage([object]$ModInfo) {
    $options = @(Get-ModSourceLanguageOptions ([string]$ModInfo.Path))
    if ($options.Count -eq 0) { return "Auto" }
    if ($options.Count -eq 1) { return [string]$options[0].Folder }

    $dialog = [System.Windows.Forms.Form]::new()
    $dialog.Text = "원문 언어 선택"
    $dialog.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterParent
    $dialog.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $dialog.ClientSize = [System.Drawing.Size]::new(570, 360)
    $dialog.MinimizeBox = $false
    $dialog.MaximizeBox = $false
    $dialog.ShowInTaskbar = $false
    $dialog.ShowIcon = $false
    $dialog.BackColor = $script:surfaceColor
    $dialog.Font = New-Font 9
    $dialog.Tag = ""

    $accent = [System.Windows.Forms.Panel]::new()
    $accent.SetBounds(0, 0, 570, 4)
    $accent.BackColor = $script:accentColor
    $title = New-Label "번역 기준 원문을 선택하세요" 28 24 514 30 $script:textColor 13 ([System.Drawing.FontStyle]::Bold)
    $body = New-Label "'$($ModInfo.Name)' 모드에 원문 언어가 여러 개 있습니다. 이 프로젝트에서 계속 사용할 언어를 선택하세요." 28 62 514 54 $script:mutedColor 9.5

    $list = [System.Windows.Forms.ListBox]::new()
    $list.DisplayMember = "Display"
    $list.Font = New-Font 10
    $list.BackColor = $script:surfaceColor
    $list.ForeColor = $script:textColor
    $list.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
    $list.SetBounds(28, 122, 514, 142)
    foreach ($option in $options) { [void]$list.Items.Add($option) }
    $list.SelectedIndex = 0

    $btnSelect = New-Button "이 언어로 프로젝트 만들기" ([System.Drawing.Color]::FromArgb(42, 139, 86))
    $btnSelect.ForeColor = [System.Drawing.Color]::White
    $btnSelect.SetBounds(252, 288, 208, 44)
    $btnCancel = New-Button "취소" $script:surfaceColor
    $btnCancel.ForeColor = $script:textColor
    $btnCancel.FlatAppearance.BorderColor = $script:mutedColor
    $btnCancel.FlatAppearance.BorderSize = 1
    $btnCancel.SetBounds(470, 288, 72, 44)

    Set-AccessibleControl $list "원문 언어 목록" "프로젝트에서 번역 기준으로 사용할 원문 언어를 선택합니다." 0
    Set-AccessibleControl $btnSelect "선택한 원문 언어로 프로젝트 만들기" "선택한 언어를 프로젝트에 저장하고 프로젝트를 만듭니다." 1
    Set-AccessibleControl $btnCancel "프로젝트 생성 취소" "프로젝트를 만들지 않고 창을 닫습니다." 2

    $selectAction = {
        if ($list.SelectedItem) {
            $dialog.Tag = [string]$list.SelectedItem.Folder
            $dialog.Close()
        }
    }
    $btnSelect.Add_Click($selectAction)
    $list.Add_DoubleClick($selectAction)
    $btnCancel.Add_Click({ $dialog.Tag = ""; $dialog.Close() })
    $dialog.AcceptButton = $btnSelect
    $dialog.CancelButton = $btnCancel
    $dialog.Controls.AddRange(@($accent, $title, $body, $list, $btnSelect, $btnCancel))
    try {
        if ($form -and -not $form.IsDisposed -and $form.Visible) {
            [void]$dialog.ShowDialog($form)
        } else {
            [void]$dialog.ShowDialog()
        }
        return [string]$dialog.Tag
    } finally {
        $dialog.Dispose()
    }
}

function Get-SelectedProjectSourceLanguage {
    $project = Get-SelectedProject
    if (-not $project -or -not $project.PSObject.Properties["sourceLanguageFolder"]) { return "Auto" }
    $folder = ([string]$project.sourceLanguageFolder).Trim()
    if (-not $folder -or $folder -eq "Auto") { return "Auto" }
    if (
        [System.IO.Path]::IsPathRooted($folder) -or
        $folder.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
        $folder.Contains("\") -or
        $folder.Contains("/") -or
        $folder -in @(".", "..") -or
        $folder -match '^(Korean|KoreanLegacy|한국)'
    ) { return "Auto" }
    $modRoot = Get-ActiveProjectModRoot
    if (-not $modRoot) { return "Auto" }
    $languagesRoot = [System.IO.Path]::GetFullPath((Join-Path $modRoot "Languages"))
    $candidateRoot = [System.IO.Path]::GetFullPath((Join-Path $languagesRoot $folder))
    $prefix = $languagesRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $candidateRoot.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { return "Auto" }
    if (-not (Test-Path -LiteralPath $candidateRoot -PathType Container)) { return "Auto" }
    $firstXml = Get-ChildItem -LiteralPath $candidateRoot -Recurse -File -Filter "*.xml" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $firstXml) { return "Auto" }
    return $folder
}

function Find-RimWorldMods {
    $mods = New-Object "System.Collections.Generic.List[object]"
    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($container in (Get-RimWorldModContainers)) {
        try {
            foreach ($dir in Get-ChildItem -LiteralPath $container.Path -Directory -ErrorAction SilentlyContinue) {
                $info = Get-RimWorldModInfo $dir.FullName $container.Source
                if ($info -and $seen.Add($info.Path)) { [void]$mods.Add($info) }
            }
        } catch {
        }
    }
    return @($mods | Sort-Object @{ Expression = "Name"; Ascending = $true }, @{ Expression = "Folder"; Ascending = $true })
}

function Get-ModContainerState {
    $states = New-Object "System.Collections.Generic.List[object]"
    foreach ($container in @(Get-RimWorldModContainers)) {
        try {
            $item = Get-Item -LiteralPath $container.Path -ErrorAction Stop
            $directoryCount = @(Get-ChildItem -LiteralPath $container.Path -Directory -Name -ErrorAction Stop).Count
            [void]$states.Add([pscustomobject]@{
                path = [System.IO.Path]::GetFullPath($item.FullName)
                source = [string]$container.Source
                lastWriteUtc = $item.LastWriteTimeUtc.ToString("o")
                directoryCount = $directoryCount
            })
        } catch {
        }
    }
    return @($states.ToArray() | Sort-Object path)
}

function Test-ModContainerState([object[]]$Cached, [object[]]$Current) {
    $cachedRows = @($Cached)
    $currentRows = @($Current)
    if ($cachedRows.Count -ne $currentRows.Count) { return $false }
    foreach ($row in $currentRows) {
        $match = @($cachedRows | Where-Object { [string]$_.path -eq [string]$row.path } | Select-Object -First 1)
        if ($match.Count -eq 0) { return $false }
        if ([string]$match[0].lastWriteUtc -ne [string]$row.lastWriteUtc) { return $false }
        if ([int]$match[0].directoryCount -ne [int]$row.directoryCount) { return $false }
    }
    return $true
}

function Test-ModContainerStateFast([object[]]$Cached) {
    $cachedRows = @($Cached)
    if ($cachedRows.Count -eq 0) { return $false }
    foreach ($row in $cachedRows) {
        $path = [string]$row.path
        if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path -PathType Container)) { return $false }
        try {
            $item = Get-Item -LiteralPath $path -ErrorAction Stop
            if ([string]$row.lastWriteUtc -ne $item.LastWriteTimeUtc.ToString("o")) { return $false }
        } catch {
            return $false
        }
    }
    return $true
}

function Save-ModCatalogCache {
    try {
        Ensure-AppDataStore
        $payload = [ordered]@{
            version = 2
            updatedAt = (Get-Date).ToString("o")
            containers = @(Get-ModContainerState)
            mods = @($script:modCatalog | Select-Object Display, Name, Path, Source, Folder, PackageId, WorkshopId, Search)
        }
        Write-Utf8JsonFile -Path $script:modCatalogCachePath -Value $payload -Depth 7
    } catch {
        Add-Log "모드 목록 캐시 저장 실패: $($_.Exception.Message)"
    }
}

function Try-LoadModCatalogCache([switch]$FastValidation) {
    if (-not (Test-Path -LiteralPath $script:modCatalogCachePath -PathType Leaf)) { return $false }
    try {
        $cache = [System.IO.File]::ReadAllText($script:modCatalogCachePath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        if ([int]$cache.version -ne 2) { return $false }
        if ($FastValidation) {
            if (-not (Test-ModContainerStateFast -Cached @($cache.containers))) { return $false }
        } else {
            $currentState = @(Get-ModContainerState)
            if (-not (Test-ModContainerState -Cached @($cache.containers) -Current $currentState)) { return $false }
        }
        $script:modCatalog = @($cache.mods | Sort-Object @{ Expression = "Name"; Ascending = $true }, @{ Expression = "Folder"; Ascending = $true })
        return $true
    } catch {
        return $false
    }
}

function Test-RmkRoot([string]$Path, [switch]$RequireGit) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    try { $full = Get-NormalizedDirectoryPath $Path } catch { return $false }
    foreach ($required in @("Data", "LoadFoldersBuilder.exe", "ModList.tsv")) {
        if (-not (Test-Path -LiteralPath (Join-Path $full $required))) { return $false }
    }
    if ($RequireGit -and -not (Test-Path -LiteralPath (Join-Path $full ".git"))) { return $false }
    return $true
}

function Get-RmkGitDirectory([string]$Root) {
    $gitPath = Join-Path $Root ".git"
    if (Test-Path -LiteralPath $gitPath -PathType Container) { return [System.IO.Path]::GetFullPath($gitPath) }
    if (-not (Test-Path -LiteralPath $gitPath -PathType Leaf)) { return "" }
    try {
        $line = ([System.IO.File]::ReadAllText($gitPath, [System.Text.Encoding]::UTF8)).Trim()
        if ($line -notmatch '^gitdir:\s*(.+)$') { return "" }
        $candidate = $matches[1].Trim()
        if (-not [System.IO.Path]::IsPathRooted($candidate)) { $candidate = Join-Path $Root $candidate }
        if (Test-Path -LiteralPath $candidate -PathType Container) { return [System.IO.Path]::GetFullPath($candidate) }
    } catch {
    }
    return ""
}

function Get-RmkBranchName([string]$Root) {
    $gitDirectory = Get-RmkGitDirectory $Root
    if (-not $gitDirectory) { return "" }
    $headPath = Join-Path $gitDirectory "HEAD"
    if (-not (Test-Path -LiteralPath $headPath -PathType Leaf)) { return "" }
    try {
        $head = ([System.IO.File]::ReadAllText($headPath, [System.Text.Encoding]::ASCII)).Trim()
        if ($head -match '^ref:\s+refs/heads/(.+)$') { return $matches[1] }
        if ($head -match '^[0-9a-f]{40,64}$') { return "detached" }
    } catch {
    }
    return ""
}

function Get-RmkWorkshopReferenceRoot {
    foreach ($steamRoot in Get-SteamLibraryRoots) {
        $candidate = Join-Path $steamRoot "steamapps\workshop\content\294100\3079466972"
        if (Test-RmkRoot $candidate) { return Get-NormalizedDirectoryPath $candidate }
    }
    return ""
}

function Find-RmkWorkspaceRoot {
    foreach ($container in @(Get-RimWorldModContainers | Where-Object { $_.Source -eq "Local" })) {
        try {
            foreach ($directory in Get-ChildItem -LiteralPath $container.Path -Directory -ErrorAction SilentlyContinue) {
                if (Test-RmkRoot -Path $directory.FullName -RequireGit) { return Get-NormalizedDirectoryPath $directory.FullName }
            }
        } catch {
        }
    }
    return ""
}

function Refresh-RmkRoots([switch]$Force) {
    $workspace = ""
    if ($script:rmkWorkspaceRoot -and (Test-RmkRoot -Path $script:rmkWorkspaceRoot -RequireGit)) {
        $workspace = Get-NormalizedDirectoryPath $script:rmkWorkspaceRoot
    } elseif ($Force -or [string]::IsNullOrWhiteSpace($script:rmkWorkspaceRoot)) {
        $workspace = Find-RmkWorkspaceRoot
        if ($workspace) { $script:rmkWorkspaceRoot = $workspace }
    }
    $script:rmkWorkspaceRoot = $workspace
    $script:rmkReferenceRoot = Get-RmkWorkshopReferenceRoot
    if ($Force) {
        $script:rmkIndexCache = @{}
        $script:rmkTargetCache = @{}
    }
    Update-RmkControls
}

function Get-RelativePathPortable([string]$Root, [string]$Path) {
    try {
        $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd("\", "/") + [System.IO.Path]::DirectorySeparatorChar
        $pathFull = [System.IO.Path]::GetFullPath($Path)
        $rootUri = New-Object System.Uri($rootFull)
        $pathUri = New-Object System.Uri($pathFull)
        return [System.Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString()).Replace('/', '\')
    } catch {
        return $Path
    }
}

function Get-RmkModListEntries([string]$Root) {
    if (-not (Test-RmkRoot $Root)) { return @() }
    $modListPath = Join-Path $Root "ModList.tsv"
    try {
        $item = Get-Item -LiteralPath $modListPath -ErrorAction Stop
        $cacheKey = [System.IO.Path]::GetFullPath($Root).ToLowerInvariant()
        $stamp = "$($item.LastWriteTimeUtc.Ticks):$($item.Length)"
        if ($script:rmkIndexCache.ContainsKey($cacheKey) -and $script:rmkIndexCache[$cacheKey].Stamp -eq $stamp) {
            return @($script:rmkIndexCache[$cacheKey].Entries)
        }
        $entries = New-Object "System.Collections.Generic.List[object]"
        foreach ($line in [System.IO.File]::ReadAllLines($modListPath, [System.Text.Encoding]::UTF8)) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $columns = $line.Split("`t")
            if ($columns.Count -lt 4) { continue }
            [void]$entries.Add([pscustomobject]@{
                WorkshopId = $columns[0].Trim()
                ModName = $columns[1].Trim()
                RelativeLocation = $columns[2].Trim()
                PackageId = $columns[3].Trim()
            })
        }
        $result = $entries.ToArray()
        $script:rmkIndexCache[$cacheKey] = [pscustomobject]@{ Stamp = $stamp; Entries = $result }
        return @($result)
    } catch {
        return @()
    }
}

function Get-RimWorldVersionForMod([string]$ModRoot) {
    foreach ($steamRoot in Get-SteamLibraryRoots) {
        $workshopRoot = Join-Path $steamRoot "steamapps\workshop\content\294100"
        $localRoot = Join-Path $steamRoot "steamapps\common\RimWorld\Mods"
        $belongs = $false
        try {
            $full = [System.IO.Path]::GetFullPath($ModRoot)
            foreach ($container in @($workshopRoot, $localRoot)) {
                if (-not (Test-Path -LiteralPath $container -PathType Container)) { continue }
                $prefix = [System.IO.Path]::GetFullPath($container).TrimEnd("\", "/") + [System.IO.Path]::DirectorySeparatorChar
                if ($full.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { $belongs = $true; break }
            }
        } catch {
        }
        if (-not $belongs) { continue }
        $versionPath = Join-Path $steamRoot "steamapps\common\RimWorld\Version.txt"
        if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf)) { continue }
        try {
            $text = [System.IO.File]::ReadAllText($versionPath, [System.Text.Encoding]::UTF8)
            if ($text -match '(\d+\.\d+)') { return $matches[1] }
        } catch {
        }
    }
    $aboutPath = Get-ModAboutPath $ModRoot
    if ($aboutPath) {
        try {
            $about = Read-SafeXmlDocument $aboutPath
            $versions = @($about.ModMetaData.supportedVersions.li | ForEach-Object { [string]$_ } | Where-Object { $_ -match '^\d+\.\d+$' })
            if ($versions.Count -gt 0) { return @($versions | Sort-Object { [version]$_ } -Descending)[0] }
        } catch {
        }
    }
    return "1.6"
}

function Get-RmkYamlInfo([string]$YamlPath) {
    try {
        $text = [System.IO.File]::ReadAllText($YamlPath, [System.Text.Encoding]::UTF8)
        $workshopId = ""
        $modName = ""
        $defaultVersion = ""
        if ($text -match '(?mi)^\s*WorkshopID:\s*["'']?([^"''\s#]+)') { $workshopId = $matches[1].Trim() }
        if ($text -match '(?mi)^\s*ModName:\s*["'']?(.+?)["'']?\s*$') { $modName = $matches[1].Trim().Trim('"').Trim("'") }
        if ($text -match '(?mi)^\s*Default:\s*["'']?([^"''\s#]+)') { $defaultVersion = $matches[1].Trim() }
        $root = Split-Path -Parent $YamlPath
        $leaf = Split-Path -Leaf $root
        $version = if ($leaf -match '^\d+\.\d+$') { $leaf } else { $defaultVersion }
        $workbook = Get-ChildItem -LiteralPath $root -File -Filter "*.xlsx" -ErrorAction SilentlyContinue |
            Sort-Object @{ Expression = { if ($workshopId -and $_.BaseName -match [System.Text.RegularExpressions.Regex]::Escape($workshopId)) { 0 } else { 1 } } }, Name |
            Select-Object -First 1
        return [pscustomobject]@{
            Root = [System.IO.Path]::GetFullPath($root)
            YamlPath = [System.IO.Path]::GetFullPath($YamlPath)
            LanguageRoot = Join-Path (Join-Path $root "Languages") "Korean (한국어)"
            WorkbookPath = if ($workbook) { [System.IO.Path]::GetFullPath($workbook.FullName) } else { "" }
            WorkshopId = $workshopId
            ModName = $modName
            Version = $version
            Text = $text
        }
    } catch {
        return $null
    }
}

function Find-RmkTargets([string]$Root, [object]$Project) {
    if (-not (Test-RmkRoot $Root) -or -not $Project) { return @() }
    $workshopId = ([string]$Project.workshopId).Trim()
    $packageId = ([string]$Project.packageId).Trim()
    if (-not $workshopId -and -not $packageId) { return @() }
    $rootFull = Get-NormalizedDirectoryPath $Root
    $cacheKey = "$($rootFull.ToLowerInvariant())|$($workshopId.ToLowerInvariant())|$($packageId.ToLowerInvariant())"
    if ($script:rmkTargetCache.ContainsKey($cacheKey)) { return @($script:rmkTargetCache[$cacheKey]) }

    $rows = @(Get-RmkModListEntries $rootFull)
    $targetMatches = if ($workshopId) { @($rows | Where-Object { $_.WorkshopId -eq $workshopId }) } else { @() }
    if ($targetMatches.Count -eq 0 -and $packageId) {
        $targetMatches = @($rows | Where-Object { $_.PackageId -ieq $packageId })
    }

    $yamlPaths = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    $dataRoot = Join-Path $rootFull "Data"
    foreach ($row in $targetMatches) {
        $relativeLocation = ([string]$row.RelativeLocation).Replace('/', '\').TrimStart('\')
        $location = [System.IO.Path]::GetFullPath((Join-Path $rootFull $relativeLocation))
        if (-not (Test-PathStrictlyInsideRoot -Path $location -Root $rootFull)) { continue }
        $candidateDirectories = New-Object "System.Collections.Generic.List[string]"
        if ($workshopId) {
            $expected = Join-Path $location "$($row.ModName) - $workshopId"
            if (Test-Path -LiteralPath $expected -PathType Container) { [void]$candidateDirectories.Add($expected) }
            if (Test-Path -LiteralPath $location -PathType Container) {
                foreach ($directory in Get-ChildItem -LiteralPath $location -Directory -ErrorAction SilentlyContinue) {
                    if ($directory.Name -match " - $([System.Text.RegularExpressions.Regex]::Escape($workshopId))$") { [void]$candidateDirectories.Add($directory.FullName) }
                }
            }
        }
        foreach ($directory in @($candidateDirectories | Select-Object -Unique)) {
            foreach ($yaml in Get-ChildItem -LiteralPath $directory -Recurse -File -Filter "LoadFolders.Build.yaml" -ErrorAction SilentlyContinue) {
                [void]$yamlPaths.Add($yaml.FullName)
            }
        }
    }

    if ($yamlPaths.Count -eq 0 -and (Test-Path -LiteralPath $dataRoot -PathType Container)) {
        foreach ($yaml in Get-ChildItem -LiteralPath $dataRoot -Recurse -File -Filter "LoadFolders.Build.yaml" -ErrorAction SilentlyContinue) {
            $pathMatches = $workshopId -and $yaml.FullName -match " - $([System.Text.RegularExpressions.Regex]::Escape($workshopId))(\\|$)"
            if ($pathMatches) { [void]$yamlPaths.Add($yaml.FullName); continue }
            if (-not $workshopId -and $packageId) {
                try {
                    $yamlText = [System.IO.File]::ReadAllText($yaml.FullName, [System.Text.Encoding]::UTF8)
                    if ($yamlText.IndexOf($packageId, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { [void]$yamlPaths.Add($yaml.FullName) }
                } catch {
                }
            }
        }
    }

    $targets = New-Object "System.Collections.Generic.List[object]"
    $seenRoots = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($yamlPath in $yamlPaths) {
        $info = Get-RmkYamlInfo $yamlPath
        if (-not $info) { continue }
        $idMatch = $workshopId -and $info.WorkshopId -eq $workshopId
        $packageMatch = $packageId -and $info.Text.IndexOf($packageId, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
        if (-not $idMatch -and -not $packageMatch) { continue }
        if ($seenRoots.Add($info.Root)) { [void]$targets.Add($info) }
    }
    $result = $targets.ToArray()
    $script:rmkTargetCache[$cacheKey] = $result
    return @($result)
}

function Select-RmkTarget([object[]]$Targets, [string]$Version) {
    $rows = @($Targets)
    if ($rows.Count -eq 0) { return $null }
    $exact = @($rows | Where-Object { $_.Version -eq $Version })
    if ($exact.Count -gt 0) { return $exact[0] }
    $unversioned = @($rows | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.Version) })
    if ($unversioned.Count -gt 0) { return $unversioned[0] }
    return @($rows | Sort-Object @{ Expression = {
        try { return [version]$_.Version } catch { return [version]"0.0" }
    }; Descending = $true })[0]
}

function Get-RmkReferenceTarget([object]$Project = $null) {
    if (-not $script:rmkUseExisting) { return $null }
    if (-not $Project) { $Project = Get-SelectedProject }
    if (-not $Project) { return $null }
    Refresh-RmkRoots
    $version = Get-RimWorldVersionForMod ([string]$Project.modRoot)
    $roots = New-Object "System.Collections.Generic.List[object]"
    if ($script:rmkWorkspaceRoot -and (Test-RmkRoot -Path $script:rmkWorkspaceRoot -RequireGit)) {
        [void]$roots.Add([pscustomobject]@{ Path = $script:rmkWorkspaceRoot; Kind = "작업 클론" })
    }
    if ($script:rmkReferenceRoot -and $script:rmkReferenceRoot -ne $script:rmkWorkspaceRoot) {
        [void]$roots.Add([pscustomobject]@{ Path = $script:rmkReferenceRoot; Kind = "구독본" })
    }
    foreach ($root in $roots) {
        $target = Select-RmkTarget -Targets @(Find-RmkTargets -Root $root.Path -Project $Project) -Version $version
        if (-not $target -or -not (Test-Path -LiteralPath $target.LanguageRoot -PathType Container)) { continue }
        $target | Add-Member -NotePropertyName SourceRoot -NotePropertyValue ([string]$root.Path) -Force
        $target | Add-Member -NotePropertyName SourceKind -NotePropertyValue ([string]$root.Kind) -Force
        return $target
    }
    return $null
}

function Get-RmkReferenceLanguageRoot([object]$Project = $null) {
    $target = Get-RmkReferenceTarget $Project
    if ($target) { return [string]$target.LanguageRoot }
    return ""
}

function Get-RmkGitStatusText([string]$Root) {
    $branch = Get-RmkBranchName $Root
    $lines = New-Object "System.Collections.Generic.List[string]"
    if ($branch) { [void]$lines.Add("브랜치: $branch") }
    $git = Get-Command git.exe -ErrorAction SilentlyContinue
    if (-not $git) { $git = Get-Command git -ErrorAction SilentlyContinue }
    if ($git) {
        try {
            $status = @(& $git.Source -C $Root status --short --branch 2>&1)
            foreach ($line in @($status | Select-Object -First 60)) { [void]$lines.Add([string]$line) }
            if ($status.Count -gt 60) { [void]$lines.Add("... $($status.Count - 60)개 변경 더 있음") }
        } catch {
            [void]$lines.Add("Git 상태를 읽지 못했습니다.")
        }
    } else {
        [void]$lines.Add("Git 명령을 찾지 못해 파일 상태는 표시하지 않습니다.")
    }
    return [string]::Join("`r`n", $lines)
}

function Update-RmkControls {
    $workspaceText = if ($script:rmkWorkspaceRoot) { $script:rmkWorkspaceRoot } else { "RMK Git 클론을 찾지 못했습니다." }
    $referenceText = if ($script:rmkReferenceRoot) { "구독본: $script:rmkReferenceRoot" } else { "RMK 구독본을 찾지 못했습니다." }
    if ($txtDashboardRmkWorkspace) { $txtDashboardRmkWorkspace.Text = $workspaceText }
    if ($lblDashboardRmkReference) { $lblDashboardRmkReference.Text = $referenceText }
    if ($chkDashboardRmkUseExisting -and $chkDashboardRmkUseExisting.Checked -ne $script:rmkUseExisting) {
        $chkDashboardRmkUseExisting.Checked = $script:rmkUseExisting
    }
    $hasWorkspace = $script:rmkWorkspaceRoot -and (Test-RmkRoot -Path $script:rmkWorkspaceRoot -RequireGit)
    foreach ($button in @($btnRmkBuild)) {
        if ($button) { $button.Enabled = [bool]$hasWorkspace }
    }
}

function Refresh-RmkPanel([switch]$Force) {
    if (-not $lblRmkStatus -or -not $txtRmkDetails) { return }
    Refresh-RmkRoots -Force:$Force
    $project = Get-SelectedProject
    if (-not $project) {
        $script:rmkCurrentTarget = $null
        $lblRmkStatus.Text = "프로젝트를 열면 RMK 번역을 찾습니다."
        $txtRmkDetails.Text = ""
        return
    }
    $referenceTarget = Get-RmkReferenceTarget $project
    $script:rmkCurrentTarget = $referenceTarget
    if ($referenceTarget) {
        $relative = Get-RelativePathPortable -Root $referenceTarget.SourceRoot -Path $referenceTarget.Root
        try {
            $lblRmkStatus.Text = "기존 번역 연결됨 · $($referenceTarget.SourceKind) · $(@(Get-ChildItem -LiteralPath $referenceTarget.LanguageRoot -Recurse -File -Filter "*.xml" -ErrorAction Stop).Count)개 XML"
        } catch {
            $lblRmkStatus.Text = "기존 번역 연결됨 · $($referenceTarget.SourceKind) · 0개 XML"
        }
        $txtRmkDetails.Text = "참조 경로`r`n$relative`r`n`r`n버전 $($referenceTarget.Version)`r`n$($referenceTarget.LanguageRoot)"
    } else {
        $lblRmkStatus.Text = "RMK 기존 번역 없음"
        $txtRmkDetails.Text = "Workshop ID $($project.workshopId)`r`nPackage ID $($project.packageId)`r`n`r`n작업 클론이 있으면 내보낼 때 새 RMK 항목을 만들 수 있습니다."
    }
    if ($script:rmkWorkspaceRoot) {
        $gitStatus = Get-RmkGitStatusText $script:rmkWorkspaceRoot
        if ($gitStatus) { $txtRmkDetails.AppendText("`r`n`r`n작업 클론`r`n$gitStatus") }
    }
    Update-RmkControls
}

function Choose-RmkWorkspace {
    $dialog = [System.Windows.Forms.FolderBrowserDialog]::new()
    $dialog.Description = "RMK Git 클론의 루트 폴더를 선택하세요."
    if ($script:rmkWorkspaceRoot -and (Test-Path -LiteralPath $script:rmkWorkspaceRoot -PathType Container)) {
        $dialog.SelectedPath = $script:rmkWorkspaceRoot
    }
    if ($dialog.ShowDialog($form) -ne [System.Windows.Forms.DialogResult]::OK) { return }
    if (-not (Test-RmkRoot -Path $dialog.SelectedPath -RequireGit)) {
        [System.Windows.Forms.MessageBox]::Show("Data, ModList.tsv, LoadFoldersBuilder.exe와 .git이 있는 RMK 클론 루트를 선택하세요.", "RMK 작업 폴더") | Out-Null
        return
    }
    $script:rmkWorkspaceRoot = Get-NormalizedDirectoryPath $dialog.SelectedPath
    Save-AppSettings
    Refresh-RmkPanel -Force
}

function AutoFind-RmkWorkspace {
    $script:rmkWorkspaceRoot = ""
    Refresh-RmkRoots -Force
    Save-AppSettings
    Refresh-RmkPanel
    if (-not $script:rmkWorkspaceRoot) {
        [System.Windows.Forms.MessageBox]::Show("RimWorld Mods 폴더에서 RMK Git 클론을 찾지 못했습니다. 폴더 선택으로 직접 지정할 수 있습니다.", "RMK 자동 찾기") | Out-Null
    }
}

function Open-RmkFolder {
    $path = if ($script:rmkCurrentTarget -and (Test-Path -LiteralPath $script:rmkCurrentTarget.Root -PathType Container)) {
        [string]$script:rmkCurrentTarget.Root
    } elseif ($script:rmkWorkspaceRoot) {
        $script:rmkWorkspaceRoot
    } else {
        $script:rmkReferenceRoot
    }
    if ($path -and (Test-Path -LiteralPath $path -PathType Container)) {
        Start-Process -FilePath $script:explorerExe -ArgumentList "`"$path`""
    }
}

function Get-RmkSafeFolderName([string]$Name) {
    $safe = $Name
    foreach ($character in [System.IO.Path]::GetInvalidFileNameChars()) { $safe = $safe.Replace([string]$character, "_") }
    $safe = $safe.Trim().TrimEnd('.', ' ')
    if ([string]::IsNullOrWhiteSpace($safe)) { $safe = "RimWorld Mod" }
    if ($safe.Length -gt 100) { $safe = $safe.Substring(0, 100).TrimEnd('.', ' ') }
    return $safe
}

function ConvertTo-RmkYamlString([string]$Value) {
    $backslash = [string][char]92
    $escaped = (ConvertTo-FlatString $Value).Replace($backslash, $backslash + $backslash).Replace('"', '\"').Replace("`n", " ")
    return $escaped
}

function Get-NewRmkTargetPath([object]$Project, [string]$WorkspaceRoot) {
    $version = Get-RimWorldVersionForMod ([string]$Project.modRoot)
    $folder = "$(Get-RmkSafeFolderName ([string]$Project.name)) - $($Project.workshopId)"
    return Join-Path (Join-Path (Join-Path $WorkspaceRoot "Data") $folder) $version
}

function New-RmkTarget([object]$Project, [string]$WorkspaceRoot) {
    if (-not $Project.workshopId -or -not $Project.packageId) {
        throw "새 RMK 항목을 만들려면 Workshop ID와 Package ID가 모두 필요합니다."
    }
    $targetRoot = [System.IO.Path]::GetFullPath((Get-NewRmkTargetPath -Project $Project -WorkspaceRoot $WorkspaceRoot))
    $dataRoot = Join-Path $WorkspaceRoot "Data"
    if (-not (Test-PathStrictlyInsideRoot -Path $targetRoot -Root $dataRoot)) { throw "RMK Data 밖에는 항목을 만들 수 없습니다." }
    $languageRoot = Join-Path (Join-Path $targetRoot "Languages") "Korean (한국어)"
    New-Item -ItemType Directory -Force -Path $languageRoot | Out-Null
    $yamlPath = Join-Path $targetRoot "LoadFolders.Build.yaml"
    if (-not (Test-Path -LiteralPath $yamlPath -PathType Leaf)) {
        $version = Get-RimWorldVersionForMod ([string]$Project.modRoot)
        $packageId = ConvertTo-RmkYamlString ([string]$Project.packageId)
        $modName = ConvertTo-RmkYamlString ([string]$Project.name)
        $yaml = @"
BuildRule:
  Binding:
    PackageID: ["$packageId"]
    Mode: "None"
    Dependency: "Independent"
  Order:
    After:
    Before:
  Version:
    Default: "$version"
    LeftBoundary:
    RightBoundary:
    Designate:
    Ban:
Metadata:
  WorkshopID: "$($Project.workshopId)"
  ModName: "$modName"
"@
        [System.IO.File]::WriteAllText($yamlPath, $yaml.TrimStart(), [System.Text.UTF8Encoding]::new($false))
    }
    $script:rmkTargetCache = @{}
    $target = Get-RmkYamlInfo $yamlPath
    $target | Add-Member -NotePropertyName SourceRoot -NotePropertyValue $WorkspaceRoot -Force
    $target | Add-Member -NotePropertyName SourceKind -NotePropertyValue "작업 클론" -Force
    return $target
}

function Get-RmkEligibleCount([string]$ApplyStatus) {
    $count = 0
    foreach ($row in $script:rows) {
        $decision = Get-Decision $row
        $status = [string]$decision.status
        if ((-not (ConvertTo-BoolValue $decision.sourceChanged)) -and
            ($status -eq "approved" -or ($ApplyStatus -eq "TranslatedAndApproved" -and $status -eq "translated"))) {
            $count++
        }
    }
    return $count
}

function Invoke-RmkBuilder([string]$WorkspaceRoot) {
    $builder = Join-Path $WorkspaceRoot "LoadFoldersBuilder.exe"
    if (-not (Test-Path -LiteralPath $builder -PathType Leaf)) { throw "LoadFoldersBuilder.exe를 찾을 수 없습니다." }
    $loadFoldersPath = Join-Path $WorkspaceRoot "LoadFolders.xml"
    $modListPath = Join-Path $WorkspaceRoot "ModList.tsv"
    $beforeState = @{}
    foreach ($path in @($loadFoldersPath, $modListPath)) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $item = Get-Item -LiteralPath $path -ErrorAction Stop
            $beforeState[$path] = "$($item.LastWriteTimeUtc.Ticks):$($item.Length)"
        }
    }
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $builder
    $info.WorkingDirectory = $WorkspaceRoot
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.CreateNoWindow = $true
    try {
        $builderEncoding = [System.Text.Encoding]::GetEncoding(949)
        $info.StandardOutputEncoding = $builderEncoding
        $info.StandardErrorEncoding = $builderEncoding
    } catch {
    }
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $info
    [void]$process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.StandardInput.WriteLine("-build")
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(120000)) {
        Stop-ProcessTree $process.Id
        throw "RMK Builder가 120초 안에 끝나지 않아 중지했습니다."
    }
    $process.WaitForExit()
    $output = $stdoutTask.Result + $stderrTask.Result
    $output = [System.Text.RegularExpressions.Regex]::Replace($output, "$([char]27)\[[0-9;?]*[ -/]*[@-~]", "")
    $outputsUpdated = $true
    foreach ($path in @($loadFoldersPath, $modListPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            $outputsUpdated = $false
            continue
        }
        $item = Get-Item -LiteralPath $path -ErrorAction Stop
        $after = "$($item.LastWriteTimeUtc.Ticks):$($item.Length)"
        if ($beforeState.ContainsKey($path) -and $beforeState[$path] -eq $after) { $outputsUpdated = $false }
    }
    $outputsValid = $false
    try {
        $loadFolders = Read-SafeXmlDocument $loadFoldersPath
        $modListLines = [System.IO.File]::ReadAllLines($modListPath, [System.Text.Encoding]::UTF8)
        $outputsValid = $loadFolders.DocumentElement -and
            $loadFolders.DocumentElement.LocalName -eq "loadFolders" -and
            @($modListLines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0
    } catch {
        $outputsValid = $false
    }
    $failureReason = if ($process.ExitCode -ne 0) {
        "ExitCode=$($process.ExitCode)"
    } elseif (-not $outputsUpdated) {
        "LoadFolders.xml 또는 ModList.tsv가 재생성되지 않았습니다."
    } elseif (-not $outputsValid) {
        "생성된 LoadFolders.xml 또는 ModList.tsv 형식이 올바르지 않습니다."
    } else {
        ""
    }
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        Output = $output.Trim()
        OutputsUpdated = $outputsUpdated
        OutputsValid = $outputsValid
        Success = ($process.ExitCode -eq 0 -and $outputsUpdated -and $outputsValid)
        FailureReason = $failureReason
    }
}

function Export-ReviewedTranslationsToRmk([string]$ApplyStatus = "ApprovedOnly") {
    $project = Get-SelectedProject
    if (-not $project -or -not $script:reviewRoot -or $script:rows.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show("먼저 프로젝트의 원문과 검수 데이터를 불러오세요.", "RMK 내보내기") | Out-Null
        return
    }
    Refresh-RmkRoots
    if (-not (Test-RmkRoot -Path $script:rmkWorkspaceRoot -RequireGit)) {
        [System.Windows.Forms.MessageBox]::Show("설정에서 RMK Git 클론 폴더를 지정하세요. Steam 구독본은 기존 번역 참조에만 사용합니다.", "RMK 내보내기") | Out-Null
        return
    }
    $branch = Get-RmkBranchName $script:rmkWorkspaceRoot
    if ($branch -ne "bus") {
        [System.Windows.Forms.MessageBox]::Show("RMK 작업은 bus 브랜치에서 진행해야 합니다.`r`n현재 브랜치: $branch", "RMK 브랜치 확인") | Out-Null
        return
    }
    if (-not (Test-Path -LiteralPath $script:rmkExportScript -PathType Leaf)) {
        [System.Windows.Forms.MessageBox]::Show("RMK 내보내기 스크립트를 찾을 수 없습니다.", "RMK 내보내기") | Out-Null
        return
    }
    Save-ReviewWithDuplicatePrompt
    $eligibleCount = Get-RmkEligibleCount $ApplyStatus
    if ($eligibleCount -eq 0) {
        [System.Windows.Forms.MessageBox]::Show("RMK로 내보낼 번역이 없습니다.", "RMK 내보내기") | Out-Null
        return
    }
    $version = Get-RimWorldVersionForMod ([string]$project.modRoot)
    $target = Select-RmkTarget -Targets @(Find-RmkTargets -Root $script:rmkWorkspaceRoot -Project $project) -Version $version
    $newTarget = -not $target
    $targetPath = if ($target) { [string]$target.Root } else { Get-NewRmkTargetPath -Project $project -WorkspaceRoot $script:rmkWorkspaceRoot }
    if ($newTarget -and (-not $project.workshopId -or -not $project.packageId)) {
        [System.Windows.Forms.MessageBox]::Show("RMK에 새 항목을 만들려면 모드의 Workshop ID와 Package ID가 필요합니다.", "RMK 내보내기") | Out-Null
        return
    }
    $modeLabel = if ($ApplyStatus -eq "TranslatedAndApproved") { "번역됨과 검토됨" } else { "검토됨" }
    $creationText = if ($newTarget) { "새 RMK 항목과 LoadFolders.Build.yaml을 만듭니다." } else { "기존 RMK 항목에 키 기준으로 병합합니다." }
    $message = "$modeLabel $eligibleCount개를 RMK 작업 클론으로 내보냅니다.`r`n`r`n$creationText`r`n대상: $targetPath`r`n`r`n완료 후 LoadFoldersBuilder를 실행하지만 Git 커밋이나 푸시는 하지 않습니다."
    $answer = [System.Windows.Forms.MessageBox]::Show($message, "RMK 내보내기", [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Information, [System.Windows.Forms.MessageBoxDefaultButton]::Button2)
    if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return }

    Set-TranslationRunning $true
    $lblRunStatus.Text = "RMK 내보내기 중"
    try {
        if (-not $target) { $target = New-RmkTarget -Project $project -WorkspaceRoot $script:rmkWorkspaceRoot }
        $exportArguments = @(
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $script:rmkExportScript,
            "-RmkEntryRoot", [string]$target.Root,
            "-ReviewRoot", $script:reviewRoot,
            "-ReviewLanguageFolderName", "Korean",
            "-RmkLanguageFolderName", "Korean (한국어)",
            "-ApplyStatus", $ApplyStatus,
            "-Overwrite"
        )
        Add-Log "RMK 내보내기 시작: $($target.Root)"
        $output = @(& $script:powershellExe @exportArguments 2>&1)
        $exitCode = $LASTEXITCODE
        foreach ($line in $output) { Add-Log ([string]$line) }
        if ($exitCode -ne 0) { throw "RMK 번역 병합이 실패했습니다. ExitCode=$exitCode" }

        $lblRunStatus.Text = "RMK Builder 실행 중"
        $builderResult = Invoke-RmkBuilder $script:rmkWorkspaceRoot
        foreach ($line in [System.Text.RegularExpressions.Regex]::Split($builderResult.Output, "\r?\n")) {
            if (-not [string]::IsNullOrWhiteSpace($line)) { Add-Log "RMK: $line" }
        }
        if (-not $builderResult.Success) {
            throw "번역은 병합했지만 RMK Builder 완료를 확인하지 못했습니다. $($builderResult.FailureReason)"
        }
        $script:rmkIndexCache = @{}
        $script:rmkTargetCache = @{}
        $script:rmkCurrentTarget = $target
        $lblRunStatus.Text = "RMK 내보내기 완료"
        Refresh-RmkPanel -Force
        if ($tabs -and $tabRmk) { $tabs.SelectedTab = $tabRmk }
        [System.Windows.Forms.MessageBox]::Show("RMK 로컬 내보내기와 LoadFolders 빌드가 완료됐습니다.`r`nGit 커밋·푸시는 하지 않았습니다.", "RMK 내보내기") | Out-Null
    } catch {
        Add-Log "RMK 내보내기 실패: $($_.Exception.Message)"
        $lblRunStatus.Text = "RMK 내보내기 실패"
        [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, "RMK 내보내기 실패", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    } finally {
        Set-TranslationRunning $false
    }
}

function Build-RmkWorkspace {
    Refresh-RmkRoots
    if (-not (Test-RmkRoot -Path $script:rmkWorkspaceRoot -RequireGit)) {
        [System.Windows.Forms.MessageBox]::Show("설정에서 RMK Git 클론 폴더를 지정하세요.", "RMK Builder") | Out-Null
        return
    }
    $branch = Get-RmkBranchName $script:rmkWorkspaceRoot
    if ($branch -ne "bus") {
        [System.Windows.Forms.MessageBox]::Show("RMK Builder는 bus 브랜치에서 실행하세요.`r`n현재 브랜치: $branch", "RMK Builder") | Out-Null
        return
    }
    Set-TranslationRunning $true
    $lblRunStatus.Text = "RMK Builder 실행 중"
    try {
        $result = Invoke-RmkBuilder $script:rmkWorkspaceRoot
        foreach ($line in [System.Text.RegularExpressions.Regex]::Split($result.Output, "\r?\n")) {
            if (-not [string]::IsNullOrWhiteSpace($line)) { Add-Log "RMK: $line" }
        }
        if (-not $result.Success) { throw "RMK Builder 완료를 확인하지 못했습니다. $($result.FailureReason)" }
        $script:rmkIndexCache = @{}
        $script:rmkTargetCache = @{}
        $lblRunStatus.Text = "RMK Builder 완료"
        Refresh-RmkPanel -Force
    } catch {
        Add-Log "RMK Builder 실패: $($_.Exception.Message)"
        $lblRunStatus.Text = "RMK Builder 실패"
        [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, "RMK Builder 실패") | Out-Null
    } finally {
        Set-TranslationRunning $false
    }
}

function Get-RowIdentity([object]$Row) {
    $cache = Get-RowRuntimeCache $Row
    if (-not $cache.Identity) { $cache.Identity = if ($Row.id) { "id:$($Row.id)" } else { "key:$($Row.key)" } }
    return [string]$cache.Identity
}

function Get-DecisionPath {
    if (-not $script:reviewRoot) { return "" }
    return Join-Path $script:reviewRoot "review-decisions.json"
}

function Get-StatusText([string]$Status) {
    switch ($Status) {
        "translated" { return "번역됨" }
        "approved" { return "검토됨" }
        "rejected" { return "반려" }
        "hold" { return "보류" }
        default { return "미번역" }
    }
}

function Get-StatusColor([string]$Status) {
    $isDark = Get-IsWindowsDarkMode
    if ($script:highContrast) {
        switch ($Status) {
            "translated" { return $(if ($isDark) { [System.Drawing.Color]::FromArgb(116, 190, 255) } else { [System.Drawing.Color]::FromArgb(18, 86, 153) }) }
            "approved" { return $(if ($isDark) { [System.Drawing.Color]::FromArgb(105, 211, 139) } else { [System.Drawing.Color]::FromArgb(24, 105, 55) }) }
            "rejected" { return $(if ($isDark) { [System.Drawing.Color]::FromArgb(255, 124, 124) } else { [System.Drawing.Color]::FromArgb(151, 32, 32) }) }
            "hold" { return $(if ($isDark) { [System.Drawing.Color]::FromArgb(255, 198, 92) } else { [System.Drawing.Color]::FromArgb(135, 82, 0) }) }
            default { return $(if ($isDark) { [System.Drawing.Color]::FromArgb(224, 224, 224) } else { [System.Drawing.Color]::FromArgb(70, 70, 70) }) }
        }
    }
    switch ($Status) {
        "translated" { return $(if ($isDark) { [System.Drawing.Color]::FromArgb(107, 177, 236) } else { [System.Drawing.Color]::FromArgb(36, 105, 170) }) }
        "approved" { return $(if ($isDark) { [System.Drawing.Color]::FromArgb(99, 191, 124) } else { [System.Drawing.Color]::FromArgb(42, 116, 67) }) }
        "rejected" { return $(if ($isDark) { [System.Drawing.Color]::FromArgb(232, 116, 116) } else { [System.Drawing.Color]::FromArgb(164, 57, 57) }) }
        "hold" { return $(if ($isDark) { [System.Drawing.Color]::FromArgb(229, 176, 77) } else { [System.Drawing.Color]::FromArgb(145, 94, 20) }) }
        default { return $(if ($isDark) { [System.Drawing.Color]::FromArgb(190, 190, 184) } else { [System.Drawing.Color]::FromArgb(91, 91, 86) }) }
    }
}

function Get-UpdateColor {
    $isDark = Get-IsWindowsDarkMode
    if ($script:highContrast) {
        return $(if ($isDark) { [System.Drawing.Color]::FromArgb(255, 202, 96) } else { [System.Drawing.Color]::FromArgb(128, 72, 0) })
    }
    return $(if ($isDark) { [System.Drawing.Color]::FromArgb(232, 177, 82) } else { [System.Drawing.Color]::FromArgb(174, 105, 24) })
}

function Update-StopButtonAppearance {
    if (-not $btnStop) { return }
    $isDark = Get-IsWindowsDarkMode
    if ($btnStop.Enabled) {
        $btnStop.BackColor = $(if ($isDark) { [System.Drawing.Color]::FromArgb(174, 76, 73) } else { [System.Drawing.Color]::FromArgb(153, 67, 64) })
        $btnStop.ForeColor = [System.Drawing.Color]::White
    } else {
        $btnStop.BackColor = $(if ($isDark) { [System.Drawing.Color]::FromArgb(48, 53, 49) } else { [System.Drawing.Color]::FromArgb(53, 59, 54) })
        $btnStop.ForeColor = [System.Drawing.Color]::FromArgb(185, 192, 184)
    }
    $btnStop.FlatAppearance.BorderColor = $btnStop.BackColor
    $btnStop.FlatAppearance.BorderSize = 0
}

function Get-CurrentDisplayName {
    if ($script:selectedProjectId) {
        $project = Get-SelectedProject
        if ($project -and -not [string]::IsNullOrWhiteSpace([string]$project.name)) {
            return [string]$project.name
        }
    }
    if ($script:selectedModRoot) {
        $info = Get-RimWorldModInfo -ModPath $script:selectedModRoot -Source "Selected"
        if ($info -and -not [string]::IsNullOrWhiteSpace([string]$info.Name)) { return [string]$info.Name }
        return Split-Path -Leaf $script:selectedModRoot
    }
    if ($script:reviewRoot) { return Split-Path -Leaf $script:reviewRoot }
    return "모드"
}

function Update-SearchCrumb {
    if ($lblSearchCrumb) {
        $name = Get-CurrentDisplayName
        if ($name.Length -gt 44) { $name = $name.Substring(0, 41) + "..." }
        $fileLabel = if ($script:currentFile -and $script:currentFile -ne "__ALL__") {
            Split-Path -Leaf $script:currentFile
        } else {
            "전체 문자열"
        }
        if ($fileLabel.Length -gt 40) { $fileLabel = $fileLabel.Substring(0, 37) + "..." }
        $statusLabel = if ($cmbStatus -and $cmbStatus.SelectedItem -and [string]$cmbStatus.SelectedItem -ne "전체") {
            [string]$cmbStatus.SelectedItem
        } else {
            "모든 상태"
        }
        $lblSearchCrumb.Text = "$name`r`n$fileLabel  ·  $statusLabel"
    }
}

function Refresh-StatusFilterButtons {
    if (-not $cmbStatus -or -not $statusFilterButtons) { return }
    $selected = if ($cmbStatus.SelectedItem) { [string]$cmbStatus.SelectedItem } else { "전체" }
    $line = if (Get-IsWindowsDarkMode) { [System.Drawing.Color]::FromArgb(68, 74, 69) } else { [System.Drawing.Color]::FromArgb(204, 209, 203) }
    foreach ($button in $statusFilterButtons) {
        $active = [string]$button.Tag -eq $selected
        if ($active) {
            $button.BackColor = $script:accentColor
            $button.ForeColor = [System.Drawing.Color]::White
            $button.FlatAppearance.BorderColor = $script:accentColor
            $button.FlatAppearance.BorderSize = 0
        } else {
            $button.BackColor = $script:surfaceColor
            $button.ForeColor = $script:mutedColor
            $button.FlatAppearance.BorderColor = $line
            $button.FlatAppearance.BorderSize = 1
        }
    }
}

function Get-ProtectedTokensForValidation([string]$Text) {
    $tokens = New-Object "System.Collections.Generic.HashSet[string]"
    $pattern = '(\{[^}]+\}|\[[A-Za-z0-9_.:;''" -]+\]|<[^>]+>|\$[A-Za-z_][A-Za-z0-9_]*|%[0-9.]*[sdif]|\b[A-Z]{2,}_[A-Z0-9_]+\b|\b[A-Za-z][A-Za-z0-9_]*->)'
    foreach ($match in [System.Text.RegularExpressions.Regex]::Matches($Text, $pattern)) {
        [void]$tokens.Add($match.Value)
    }
    $result = New-Object string[] $tokens.Count
    $tokens.CopyTo($result)
    return $result
}

function Get-MissingTranslationTokens([string]$Source, [string]$Translation) {
    $missing = New-Object "System.Collections.Generic.List[string]"
    foreach ($token in (Get-ProtectedTokensForValidation $Source)) {
        if (-not $Translation.Contains($token)) { [void]$missing.Add($token) }
    }
    $grammarPrefix = [System.Text.RegularExpressions.Regex]::Match($Source, '^\s*([A-Za-z][A-Za-z0-9_]*->)')
    if ($grammarPrefix.Success -and -not [System.Text.RegularExpressions.Regex]::IsMatch($Translation, ('^\s*' + [regex]::Escape($grammarPrefix.Groups[1].Value)))) {
        if (-not $missing.Contains($grammarPrefix.Groups[1].Value)) { [void]$missing.Add($grammarPrefix.Groups[1].Value) }
    }
    return $missing.ToArray()
}

function Get-RmkReferenceWorkbookPath([object]$Project = $null) {
    $target = Get-RmkReferenceTarget $Project
    if ($target -and $target.PSObject.Properties["WorkbookPath"] -and (Test-Path -LiteralPath $target.WorkbookPath -PathType Leaf)) {
        return [string]$target.WorkbookPath
    }
    return ""
}

function Get-InternalLocalizationIdentifierReason([object]$Row) {
    if (-not $Row -or [string]::IsNullOrWhiteSpace([string]$Row.key) -or [string]$Row.kind -ne "DefInjected") { return "" }

    $keyLower = ([string]$Row.key).Trim().ToLowerInvariant()
    $typeLower = if ($Row.PSObject.Properties["defClass"] -and $Row.defClass) { ([string]$Row.defClass).Trim().ToLowerInvariant() } else { "" }
    $fieldLower = if ($Row.PSObject.Properties["field"] -and $Row.field) { ([string]$Row.field).Trim().ToLowerInvariant() } else { ($keyLower -replace "^.*\.", "") }
    $isDisplayField = $fieldLower -match $script:DisplayLocalizationFieldPattern
    if ($fieldLower -match $script:TechnicalLocalizationFieldPattern) { return "내부 참조 필드 '$fieldLower'" }
    if ($keyLower -match "\.alienrace\.generalsettings\.alienpartgenerator\.colorchannels\.") { return "AlienRace 색상 채널 식별자" }
    if ($fieldLower -eq "name" -and $keyLower -match "\.alienrace\.") { return "AlienRace 내부 이름" }
    if ($keyLower -match "\.(graphicpaths?|rendernodes?|rendertree)\." -and -not $isDisplayField) { return "렌더링 또는 그래픽 경로 식별자" }
    if ($typeLower -match "pawnrendertreedef" -and -not $isDisplayField) { return "PawnRenderTreeDef 내부 식별자" }
    return ""
}

function Test-PathologicalTranslationText([string]$Text) {
    if ([string]::IsNullOrEmpty($Text)) { return $false }
    if ($Text -match "(\r?\n\s*){8,}") { return $true }
    if ($Text -match "(\\u000a\s*){8,}") { return $true }
    $newlineCount = [System.Text.RegularExpressions.Regex]::Matches($Text, "\r?\n").Count
    return $newlineCount -ge 20 -and $Text.Length -lt 4000
}

function Test-ContainsKoreanText([string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }
    return $Text -match "[\uAC00-\uD7AF]"
}

function Get-InvalidKoreanParticleNotations([string]$Text) {
    $result = New-Object "System.Collections.Generic.List[string]"
    if ([string]::IsNullOrWhiteSpace($Text)) { return $result.ToArray() }
    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::Ordinal)
    $pattern = '(은\(는\)|는\(은\)|이\(가\)|가\(이\)|을\(를\)|를\(을\)|과\(와\)|와\(과\)|으로\(로\)|로\(으로\)|(?:\[[^\]\r\n]+\]|\{[^}\r\n]+\}|\$[A-Za-z_][A-Za-z0-9_]*)(?:으로|은|는|이|가|을|를|과|와|로)(?=$|[\s.,!?…:;，。！？、]))'
    foreach ($match in [System.Text.RegularExpressions.Regex]::Matches($Text, $pattern)) {
        if ($seen.Add($match.Value)) { [void]$result.Add($match.Value) }
    }
    return $result.ToArray()
}

function Get-TranslationValidation([object]$Row, [string]$Translation) {
    $source = ConvertTo-FlatString $Row.source
    $translationText = ConvertTo-FlatString $Translation
    $isBlank = [string]::IsNullOrWhiteSpace($translationText)
    $isPathological = Test-PathologicalTranslationText $translationText
    $missingTokens = @(Get-MissingTranslationTokens -Source $source -Translation $translationText)
    $sameAsSource = [string]::Equals($source, $translationText, [System.StringComparison]::Ordinal)
    $hasKorean = Test-ContainsKoreanText $translationText
    $invalidKoreanParticles = @(Get-InvalidKoreanParticleNotations $translationText)
    $safeToApply = -not $isBlank -and -not $isPathological -and $missingTokens.Count -eq 0 -and $invalidKoreanParticles.Count -eq 0 -and -not $sameAsSource -and $hasKorean
    return [pscustomobject]@{
        SafeToApply = $safeToApply
        IsBlank = $isBlank
        IsPathological = $isPathological
        MissingTokens = $missingTokens
        InvalidKoreanParticles = $invalidKoreanParticles
        SameAsSource = $sameAsSource
        HasKorean = $hasKorean
    }
}

function Get-TranslationValidationCacheEntry([object]$Row, [string]$Translation) {
    $identity = "$([string]$script:reviewRoot)|$(Get-RowIdentity $Row)"
    $source = ConvertTo-FlatString $Row.source
    $translationText = ConvertTo-FlatString $Translation
    if ($script:validationCache.ContainsKey($identity)) {
        $cached = $script:validationCache[$identity]
        if (
            [string]::Equals([string]$cached.Source, $source, [System.StringComparison]::Ordinal) -and
            [string]::Equals([string]$cached.Translation, $translationText, [System.StringComparison]::Ordinal)
        ) {
            return $cached
        }
    }

    $entry = [pscustomobject]@{
        Source = $source
        Translation = $translationText
        Validation = Get-TranslationValidation -Row $Row -Translation $translationText
        Warnings = $null
    }
    $script:validationCache[$identity] = $entry
    return $entry
}

function Get-CachedTranslationValidation([object]$Row, [string]$Translation) {
    return (Get-TranslationValidationCacheEntry -Row $Row -Translation $Translation).Validation
}

function Get-RowWarnings([object]$Row, [string]$Translation) {
    $cacheEntry = Get-TranslationValidationCacheEntry -Row $Row -Translation $Translation
    if ($null -ne $cacheEntry.Warnings) { return @($cacheEntry.Warnings) }

    $validation = $cacheEntry.Validation
    $warnings = New-Object "System.Collections.Generic.List[string]"
    if (-not $validation.SafeToApply) { [void]$warnings.Add("안전 적용 아님") }
    if ($validation.IsBlank) { [void]$warnings.Add("빈 번역") }
    if ($validation.IsPathological) { [void]$warnings.Add("비정상 개행") }
    if ($validation.MissingTokens.Count -gt 0) { [void]$warnings.Add("토큰 누락: $([string]::Join('|', $validation.MissingTokens))") }
    if ($validation.InvalidKoreanParticles.Count -gt 0) { [void]$warnings.Add("림월드 조사 표기 오류: $([string]::Join('|', $validation.InvalidKoreanParticles))") }
    if ($validation.SameAsSource) { [void]$warnings.Add("원문과 동일") }
    if (-not $validation.HasKorean) { [void]$warnings.Add("한글 없음") }
    $cacheEntry.Warnings = $warnings.ToArray()
    return @($cacheEntry.Warnings)
}

function Get-RelativeTarget([object]$Row) {
    $target = [string]$Row.target
    if ($script:relativeTargetCache.ContainsKey($target)) { return [string]$script:relativeTargetCache[$target] }
    $result = if (-not $target) { "(unknown)" } else { $target }
    if ($target -and $script:reviewRoot) {
        try {
            $reviewFull = [System.IO.Path]::GetFullPath($script:reviewRoot).TrimEnd("\", "/")
            $reviewPrefix = $reviewFull + [System.IO.Path]::DirectorySeparatorChar
            $targetFull = [System.IO.Path]::GetFullPath($target)
            if ($targetFull.StartsWith($reviewPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                $result = $targetFull.Substring($reviewPrefix.Length)
                $prefix = "Languages\Korean\"
                if ($result.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $result = $result.Substring($prefix.Length)
                }
            }
        } catch {
        }
    }
    $script:relativeTargetCache[$target] = $result
    return $result
}

function Get-OptionalRowText([object]$Row, [string[]]$Names) {
    if (-not $Row) { return "" }
    foreach ($name in $Names) {
        $property = $Row.PSObject.Properties[$name]
        if (-not $property) { continue }
        $value = ConvertTo-FlatString $property.Value
        if (-not [string]::IsNullOrWhiteSpace($value)) { return $value.Trim() }
    }
    return ""
}

function Get-DefClassDescription([string]$DefClass) {
    $normalized = if ($DefClass) { $DefClass.ToLowerInvariant() } else { "" }
    switch ($normalized) {
        "abilitydef" { return "능력의 이름, 설명과 작동 정보를 정의합니다" }
        "biomedef" { return "생태계와 지형 환경을 정의합니다" }
        "damagedef" { return "피해 유형, 방어 판정과 사망 메시지를 정의합니다" }
        "factiondef" { return "세력의 이름, 성격과 관계 정보를 정의합니다" }
        "genedef" { return "유전자와 그 효과를 정의합니다" }
        "hediffdef" { return "부상, 질병과 건강 상태를 정의합니다" }
        "incidentdef" { return "습격이나 사건 같은 월드 이벤트를 정의합니다" }
        "jobdef" { return "폰이 수행하는 작업 행동을 정의합니다" }
        "letterdef" { return "게임 내 편지와 알림 형식을 정의합니다" }
        "memedef" { return "이데올로기 밈과 관련 규칙을 정의합니다" }
        "pawnkinddef" { return "등장하는 폰이나 생물의 종류를 정의합니다" }
        "preceptdef" { return "이데올로기 규율과 의례를 정의합니다" }
        "questscriptdef" { return "퀘스트의 생성 규칙과 문구를 정의합니다" }
        "recipedef" { return "제작, 가공과 수술 작업을 정의합니다" }
        "researchprojectdef" { return "연구 과제와 해금 내용을 정의합니다" }
        "rulepackdef" { return "이름이나 문장을 조합하는 언어 규칙을 정의합니다" }
        "terraindef" { return "바닥과 지형의 표시 및 속성을 정의합니다" }
        "thingdef" { return "아이템, 건물, 식물과 생물 같은 게임 대상을 정의합니다" }
        "thoughtdef" { return "폰의 기분에 영향을 주는 생각을 정의합니다" }
        "traitdef" { return "폰의 특성과 단계별 효과를 정의합니다" }
        default {
            if ([string]::IsNullOrWhiteSpace($DefClass)) { return "RimWorld 번역 문자열의 분류를 확인하지 못했습니다" }
            return "RimWorld의 $DefClass 유형 데이터를 정의합니다"
        }
    }
}

function Get-NodeDescription([string]$Field) {
    $leaf = if ($Field) { $Field.ToLowerInvariant() } else { "" }
    switch -Regex ($leaf) {
        "^label" { return "화면에 표시되는 짧은 이름입니다" }
        "^(description|desc)$" { return "정보 창이나 도움말에 표시되는 상세 설명입니다" }
        "deathmessage$" { return "해당 원인으로 죽었을 때 표시되는 사망 문구입니다" }
        "reportstring$" { return "폰의 현재 행동으로 표시되는 문구입니다" }
        "jobstring$" { return "작업 상태에 표시되는 문구입니다" }
        "inspectstring$" { return "대상을 선택했을 때 정보 창에 표시되는 문구입니다" }
        "^letterlabel$" { return "편지 창의 제목입니다" }
        "^lettertext$" { return "편지 창의 본문입니다" }
        "message$" { return "게임 알림이나 메시지에 표시되는 문구입니다" }
        "^(gerund|verb)$" { return "행동이나 작업을 나타내는 문구입니다" }
        "^name$" { return "화면에 표시되는 이름입니다" }
        default { return "해당 Def 안에서 번역할 XML 값의 경로입니다" }
    }
}

function Get-RowDefContext([object]$Row) {
    $cache = Get-RowRuntimeCache $Row
    if ($cache.DefContext) { return $cache.DefContext }
    $relative = Get-RelativeTarget $Row
    $kind = Get-OptionalRowText -Row $Row -Names @("kind", "Kind")
    $defClass = Get-OptionalRowText -Row $Row -Names @("defClass", "defType", "typeName", "TypeName")
    $node = Get-OptionalRowText -Row $Row -Names @("node", "key", "Key")
    $field = Get-OptionalRowText -Row $Row -Names @("field", "Field")

    $defMatch = [System.Text.RegularExpressions.Regex]::Match($relative, "(?:^|[\\/])DefInjected[\\/]([^\\/]+)(?:[\\/]|$)", [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $defClass -and $defMatch.Success) { $defClass = $defMatch.Groups[1].Value }
    if (-not $kind) {
        if ($defMatch.Success) { $kind = "DefInjected" }
        elseif ($relative -match "(?:^|[\\/])Keyed(?:[\\/]|$)") { $kind = "Keyed" }
    }

    $segments = @($node -split "\.")
    if (-not $field -and $segments.Count -gt 0) { $field = $segments[$segments.Count - 1] }
    $defName = if ($kind -eq "DefInjected" -and $segments.Count -gt 1) { $segments[0] } else { "" }
    $fieldPath = if ($kind -eq "DefInjected" -and $segments.Count -gt 1) { [string]::Join(".", @($segments[1..($segments.Count - 1)])) } else { $field }

    if ($kind -eq "Keyed" -or (-not $defClass -and $relative -match "(?:^|[\\/])Keyed(?:[\\/]|$)")) {
        $displayClass = "Keyed"
        $classDescription = "Def에 속하지 않는 일반 UI/알림 문자열입니다"
        $nodeDescription = "코드가 이 문구를 찾는 고유 키입니다"
        $explanation = "Keyed: $classDescription. Node '$node': $nodeDescription."
    } else {
        $displayClass = if ($defClass) { $defClass } else { "알 수 없음" }
        $classDescription = Get-DefClassDescription $defClass
        $nodeDescription = Get-NodeDescription $field
        if ($defName) {
            $explanation = "${displayClass}는 $classDescription.`r`n'$defName' Def의 '$fieldPath' 노드는 $nodeDescription."
        } else {
            $nodeLabel = if ($node) { "'$node' Node" } else { "Node" }
            $explanation = "${displayClass}는 $classDescription.`r`n${nodeLabel}는 번역할 XML 값의 경로입니다."
        }
    }

    $context = [pscustomobject]@{
        DefClass = $displayClass
        Node = $node
        DefName = $defName
        Field = $fieldPath
        ClassDescription = $classDescription
        NodeDescription = $nodeDescription
        Explanation = $explanation
    }
    $cache.DefContext = $context
    return $context
}

function Get-DefaultTranslationForRow([object]$Row) {
    $candidate = ConvertTo-FlatString $Row.candidate
    if (-not [string]::IsNullOrWhiteSpace($candidate)) { return $candidate }
    $existing = ConvertTo-FlatString $Row.existing
    if (-not [string]::IsNullOrWhiteSpace($existing)) { return $existing }
    return ""
}

function Get-DefaultTranslationOriginForRow([object]$Row) {
    $candidate = ConvertTo-FlatString $Row.candidate
    if (-not [string]::IsNullOrWhiteSpace($candidate)) {
        $candidateOrigin = Get-OptionalRowText -Row $Row -Names @("translationOrigin")
        return $(if ($candidateOrigin) { $candidateOrigin } else { "ai" })
    }
    $existing = ConvertTo-FlatString $Row.existing
    if (-not [string]::IsNullOrWhiteSpace($existing)) {
        $existingOrigin = Get-OptionalRowText -Row $Row -Names @("existingOrigin")
        return $(if ($existingOrigin) { $existingOrigin } else { "existing" })
    }
    return ""
}

function Get-InferredTranslationOrigin([object]$Row, [object]$Decision) {
    if ($Decision.PSObject.Properties["translationOrigin"] -and -not [string]::IsNullOrWhiteSpace([string]$Decision.translationOrigin)) {
        return ([string]$Decision.translationOrigin).ToLowerInvariant()
    }
    $text = ConvertTo-FlatString $Decision.text
    if ([string]::IsNullOrWhiteSpace($text)) { return "" }
    $candidate = ConvertTo-FlatString $Row.candidate
    if ($candidate -and [string]::Equals($text, $candidate, [System.StringComparison]::Ordinal)) { return "ai" }
    $existing = ConvertTo-FlatString $Row.existing
    if ($existing -and [string]::Equals($text, $existing, [System.StringComparison]::Ordinal)) {
        $existingOrigin = Get-OptionalRowText -Row $Row -Names @("existingOrigin")
        return $(if ($existingOrigin) { $existingOrigin.ToLowerInvariant() } else { "existing" })
    }
    if ($Decision.PSObject.Properties["updatedAt"] -and $Decision.updatedAt) { return "local" }
    return Get-DefaultTranslationOriginForRow $Row
}

function Get-TranslationOriginText([string]$Origin) {
    $normalized = if ($Origin) { $Origin.ToLowerInvariant() } else { "" }
    switch ($normalized) {
        "rmk" { return "RMK 가져옴" }
        "local" { return "내 번역" }
        "ai" { return "AI 초벌" }
        "mod" { return "모드 기존" }
        "existing" { return "기존 번역" }
        default { return "출처 없음" }
    }
}

function Get-TranslationOriginShortText([string]$Origin) {
    $normalized = if ($Origin) { $Origin.ToLowerInvariant() } else { "" }
    switch ($normalized) {
        "rmk" { return "RMK" }
        "local" { return "내 번역" }
        "ai" { return "AI" }
        "mod" { return "모드 기존" }
        "existing" { return "기존" }
        default { return "" }
    }
}

function Format-TranslationTimestamp([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return "" }
    $parsed = [datetime]::MinValue
    if ([datetime]::TryParse($Value, [ref]$parsed)) { return $parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm") }
    return ""
}

function New-Decision([object]$Row) {
    $defaultTranslation = Get-DefaultTranslationForRow $Row
    $translationOrigin = Get-DefaultTranslationOriginForRow $Row
    $source = ConvertTo-FlatString $Row.source
    $rmkSourceChanged = $translationOrigin -eq "rmk" -and $Row.PSObject.Properties["rmkSourceChanged"] -and (ConvertTo-BoolValue $Row.rmkSourceChanged)
    $rmkHistoricalSource = if ($rmkSourceChanged -and $Row.PSObject.Properties["rmkHistoricalSource"]) { ConvertTo-FlatString $Row.rmkHistoricalSource } else { "" }
    $translationUpdatedAt = Get-OptionalRowText -Row $Row -Names @("translationUpdatedAt")
    return [pscustomobject]@{
        id = [string]$Row.id
        key = [string]$Row.key
        target = Get-RelativeTarget $Row
        status = if ([string]::IsNullOrWhiteSpace($defaultTranslation) -or $rmkSourceChanged) { "pending" } else { "translated" }
        text = $defaultTranslation
        note = ""
        translationOrigin = $translationOrigin
        translationUpdatedAt = $translationUpdatedAt
        sourceHash = ""
        sourceText = $source
        sourceChanged = $rmkSourceChanged
        previousSourceText = $rmkHistoricalSource
        updatedAt = ""
    }
}

function Get-Decision([object]$Row) {
    $cache = Get-RowRuntimeCache $Row
    if ($cache.Decision) { return $cache.Decision }
    $identity = Get-RowIdentity $Row
    if (-not $script:decisions.ContainsKey($identity)) {
        $script:decisions[$identity] = New-Decision $Row
    } elseif ($script:validateLoadedDecisionSources) {
        Normalize-DecisionForRow -Row $Row -Decision $script:decisions[$identity]
    }
    $cache.Decision = $script:decisions[$identity]
    return $cache.Decision
}

function Normalize-DecisionForRow([object]$Row, [object]$Decision) {
    $source = ConvertTo-FlatString $Row.source
    $sourceHash = Get-RowSourceFingerprint $Row
    $defaultTranslation = Get-DefaultTranslationForRow $Row
    $status = [string]$Decision.status
    if ([string]::IsNullOrWhiteSpace($status)) { $status = if ($defaultTranslation) { "translated" } else { "pending" } }
    if ($status -eq "reviewed") { $status = "approved" }

    $translationOrigin = Get-InferredTranslationOrigin -Row $Row -Decision $Decision
    if ($Decision.PSObject.Properties["translationOrigin"]) {
        $Decision.translationOrigin = $translationOrigin
    } else {
        $Decision | Add-Member -NotePropertyName translationOrigin -NotePropertyValue $translationOrigin
    }
    if (-not $Decision.PSObject.Properties["translationUpdatedAt"]) {
        $legacyTranslationTime = if ($translationOrigin -eq "local" -and $Decision.PSObject.Properties["updatedAt"]) { [string]$Decision.updatedAt } else { "" }
        $Decision | Add-Member -NotePropertyName translationUpdatedAt -NotePropertyValue $legacyTranslationTime
    }

    $sourceChangedNow = $false
    $storedSourceText = if ($Decision.PSObject.Properties["sourceText"]) { ConvertTo-FlatString $Decision.sourceText } else { "" }
    if ($Decision.PSObject.Properties["sourceHash"] -and -not [string]::IsNullOrWhiteSpace([string]$Decision.sourceHash)) {
        $sourceChangedNow = ([string]$Decision.sourceHash) -ne $sourceHash
    } elseif ($Decision.PSObject.Properties["sourceText"] -and -not [string]::IsNullOrWhiteSpace([string]$Decision.sourceText)) {
        $sourceChangedNow = $storedSourceText -ne $source
    }
    $rmkSourceChangedNow = $translationOrigin -eq "rmk" -and $Row.PSObject.Properties["rmkSourceChanged"] -and (ConvertTo-BoolValue $Row.rmkSourceChanged)
    $sourceChanged = $sourceChangedNow -or $rmkSourceChangedNow -or ($Decision.PSObject.Properties["sourceChanged"] -and (ConvertTo-BoolValue $Decision.sourceChanged))

    if ($sourceChanged) {
        $Decision.status = "pending"
        if ([string]::IsNullOrWhiteSpace([string]$Decision.text)) {
            $Decision.text = $defaultTranslation
        }
        if ($sourceChangedNow) {
            $rmkHistoricalSource = if ($rmkSourceChangedNow -and $Row.PSObject.Properties["rmkHistoricalSource"]) { ConvertTo-FlatString $Row.rmkHistoricalSource } else { "" }
            $previousSource = if ($rmkHistoricalSource) { $rmkHistoricalSource } elseif (-not [string]::IsNullOrWhiteSpace($storedSourceText)) { $storedSourceText } else { ConvertTo-FlatString $Decision.previousSourceText }
            if ($Decision.PSObject.Properties["previousSourceText"]) {
                $Decision.previousSourceText = $previousSource
            } else {
                $Decision | Add-Member -NotePropertyName previousSourceText -NotePropertyValue $previousSource
            }
            $Decision.updatedAt = (Get-Date).ToString("o")
            $script:dirty = $true
        } elseif ($rmkSourceChangedNow -and [string]::IsNullOrWhiteSpace((ConvertTo-FlatString $Decision.previousSourceText))) {
            $Decision.previousSourceText = ConvertTo-FlatString $Row.rmkHistoricalSource
        }
        if ($Decision.PSObject.Properties["sourceChanged"]) {
            $Decision.sourceChanged = $true
        } else {
            $Decision | Add-Member -NotePropertyName sourceChanged -NotePropertyValue $true
        }
    } else {
        if ($status -eq "pending" -and [string]::IsNullOrWhiteSpace([string]$Decision.text) -and -not [string]::IsNullOrWhiteSpace($defaultTranslation) -and [string]::IsNullOrWhiteSpace([string]$Decision.updatedAt)) {
            $status = "translated"
            $Decision.text = $defaultTranslation
        }
        $Decision.status = $status
        if (-not $Decision.PSObject.Properties["sourceChanged"]) {
            $Decision | Add-Member -NotePropertyName sourceChanged -NotePropertyValue $false
        }
        if (-not $Decision.PSObject.Properties["previousSourceText"]) {
            $Decision | Add-Member -NotePropertyName previousSourceText -NotePropertyValue ""
        }
    }

    $Decision.sourceHash = $sourceHash
    $Decision.sourceText = $source
    $Decision.target = Get-RelativeTarget $Row
}

function Set-DecisionStatus([object]$Row, [string]$Status) {
    $decision = Get-Decision $Row
    $decision.status = $Status
    if ($Status -ne "pending") {
        $decision.sourceChanged = $false
        $decision.previousSourceText = ""
    }
    $decision.updatedAt = (Get-Date).ToString("o")
    $script:dirty = $true
}

function Find-ComparisonFile([string]$Root) {
    $auditRoot = Join-Path $Root "_TranslationAudit"
    if (-not (Test-Path -LiteralPath $auditRoot)) {
        throw "검토 감사 폴더를 찾을 수 없습니다: $auditRoot"
    }
    $file = Get-ChildItem -LiteralPath $auditRoot -File -Filter "*-comparison.json" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $file) { throw "comparison.json 파일을 찾을 수 없습니다: $auditRoot" }
    return $file.FullName
}

function Find-LatestReviewRoot {
    $roots = @($script:appReviewRoot, (Join-Path $scriptRoot "reviews"))
    $candidates = New-Object "System.Collections.Generic.List[object]"
    foreach ($reviewsRoot in $roots) {
        if (-not (Test-Path -LiteralPath $reviewsRoot)) { continue }
        Get-ChildItem -LiteralPath $reviewsRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object {
                $audit = Join-Path $_.FullName "_TranslationAudit"
                (Test-Path -LiteralPath $audit) -and $null -ne (Get-ChildItem -LiteralPath $audit -File -Filter "*-comparison.json" -ErrorAction SilentlyContinue | Select-Object -First 1)
            } |
            ForEach-Object { [void]$candidates.Add($_) }
    }
    $dir = $candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($dir) { return $dir.FullName }
    return ""
}

function Load-Glossary {
    if ($script:glossaryLoaded) { return }
    $script:glossaryLoaded = $true
    $path = Join-Path $scriptRoot "glossary.generated.ko.json"
    if (-not (Test-Path -LiteralPath $path)) { return }
    try {
        $json = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        $script:glossary = @($json.terms | Where-Object { $_.source -and $_.ko })
        $indexedTerms = New-Object "System.Collections.Generic.List[object]"
        $prefixIndex = @{}
        $order = 0
        foreach ($term in $script:glossary) {
            $source = ([string]$term.source).Trim()
            if ($source.Length -lt 3) { continue }
            $indexed = [pscustomobject]@{
                Term = $term
                SearchSource = $source.ToLowerInvariant()
                Order = $order
            }
            $order++
            [void]$indexedTerms.Add($indexed)
            $prefix = $indexed.SearchSource.Substring(0, 3)
            if (-not $prefixIndex.ContainsKey($prefix)) {
                $prefixIndex[$prefix] = New-Object "System.Collections.Generic.List[object]"
            }
            [void]$prefixIndex[$prefix].Add($indexed)
        }
        $script:glossaryIndexedTerms = $indexedTerms.ToArray()
        $script:glossaryPrefixIndex = $prefixIndex
    } catch {
        $script:glossary = @()
        $script:glossaryIndexedTerms = @()
        $script:glossaryPrefixIndex = @{}
    }
}

function Load-Decisions {
    $script:decisions = @{}
    $script:validateLoadedDecisionSources = $false
    $path = Get-DecisionPath
    if (-not $path -or -not (Test-Path -LiteralPath $path)) { return }
    try {
        if ($script:comparisonFile -and (Test-Path -LiteralPath $script:comparisonFile -PathType Leaf)) {
            $decisionInfo = Get-Item -LiteralPath $path -ErrorAction Stop
            $comparisonInfo = Get-Item -LiteralPath $script:comparisonFile -ErrorAction Stop
            $script:validateLoadedDecisionSources = $comparisonInfo.LastWriteTimeUtc -gt $decisionInfo.LastWriteTimeUtc
        }
        $json = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        foreach ($item in @($json.items)) {
            $key = if ($item.id) { "id:$($item.id)" } else { "key:$($item.key)" }
            $loadedStatus = if ($item.status) { [string]$item.status } else { "pending" }
            if ($loadedStatus -eq "reviewed") { $loadedStatus = "approved" }
            $script:decisions[$key] = [pscustomobject]@{
                id = [string]$item.id
                key = [string]$item.key
                target = [string]$item.target
                status = $loadedStatus
                text = ConvertTo-FlatString $item.text
                note = [string]$item.note
                translationOrigin = if ($item.PSObject.Properties["translationOrigin"]) { [string]$item.translationOrigin } else { "" }
                translationUpdatedAt = if ($item.PSObject.Properties["translationUpdatedAt"]) { [string]$item.translationUpdatedAt } else { "" }
                sourceHash = [string]$item.sourceHash
                sourceText = ConvertTo-FlatString $item.sourceText
                sourceChanged = if ($item.PSObject.Properties["sourceChanged"]) { ConvertTo-BoolValue $item.sourceChanged } else { $false }
                previousSourceText = if ($item.PSObject.Properties["previousSourceText"]) { ConvertTo-FlatString $item.previousSourceText } else { "" }
                updatedAt = [string]$item.updatedAt
            }
        }
    } catch {
        [System.Windows.Forms.MessageBox]::Show("검수 상태 파일을 읽지 못했습니다.`r`n$($_.Exception.Message)", "RimWorld AI Translator") | Out-Null
    }
}

function Find-PreviousProjectDecisionFile([object]$Project, [string]$CurrentRoot) {
    if (-not $Project -or [string]::IsNullOrWhiteSpace($CurrentRoot)) { return "" }

    $currentFull = [System.IO.Path]::GetFullPath($CurrentRoot).TrimEnd("\", "/")
    $candidateRoots = New-Object "System.Collections.Generic.List[string]"
    if ($Project.latestReviewRoot) { [void]$candidateRoots.Add([string]$Project.latestReviewRoot) }
    foreach ($run in @($Project.runs | Sort-Object createdAt -Descending)) {
        if ($run.reviewRoot) { [void]$candidateRoots.Add([string]$run.reviewRoot) }
    }

    $seen = @{}
    foreach ($candidateRoot in $candidateRoots) {
        try {
            $candidateFull = [System.IO.Path]::GetFullPath($candidateRoot).TrimEnd("\", "/")
        } catch {
            continue
        }
        $key = $candidateFull.ToLowerInvariant()
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        if ($candidateFull -eq $currentFull) { continue }

        $decisionFile = Join-Path $candidateFull "review-decisions.json"
        if (Test-Path -LiteralPath $decisionFile -PathType Leaf) { return $decisionFile }
    }
    return ""
}

function Import-PreviousProjectDecisions {
    $path = Get-DecisionPath
    if ($path -and (Test-Path -LiteralPath $path)) { return }
    if (-not $script:selectedProjectId -or -not $script:reviewRoot) { return }
    $project = @($script:projects | Where-Object { $_.id -eq $script:selectedProjectId } | Select-Object -First 1)
    if ($project.Count -eq 0) { return }
    $currentRoot = [System.IO.Path]::GetFullPath($script:reviewRoot)
    $previousDecisionFile = Find-PreviousProjectDecisionFile -Project $project[0] -CurrentRoot $currentRoot
    if (-not $previousDecisionFile) { return }

    try {
        $json = [System.IO.File]::ReadAllText($previousDecisionFile, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        $targetKeyLookup = @{}
        $uniqueKeyLookup = @{}
        $ambiguousKeys = @{}
        $idOnlyLookup = @{}
        foreach ($item in @($json.items)) {
            if (-not $item) { continue }
            if ($item.key) {
                $plainKey = "key:$($item.key)"
                if ($item.target) { $targetKeyLookup["target:$($item.target)|$plainKey"] = $item }
                if ($ambiguousKeys.ContainsKey($plainKey)) { continue }
                if ($uniqueKeyLookup.ContainsKey($plainKey)) {
                    $uniqueKeyLookup.Remove($plainKey)
                    $ambiguousKeys[$plainKey] = $true
                } else {
                    $uniqueKeyLookup[$plainKey] = $item
                }
            } elseif ($item.id) {
                $idOnlyLookup["id:$($item.id)"] = $item
            }
        }

        $imported = 0
        $changedSources = 0
        foreach ($row in $script:rows) {
            $target = Get-RelativeTarget $row
            $item = $null
            if ($row.key) {
                $plainKey = "key:$($row.key)"
                $targetKey = "target:$target|$plainKey"
                if ($targetKeyLookup.ContainsKey($targetKey)) {
                    $item = $targetKeyLookup[$targetKey]
                } elseif ($uniqueKeyLookup.ContainsKey($plainKey)) {
                    $item = $uniqueKeyLookup[$plainKey]
                }
            } elseif ($row.id) {
                $idKey = "id:$($row.id)"
                if ($idOnlyLookup.ContainsKey($idKey)) { $item = $idOnlyLookup[$idKey] }
            }
            if (-not $item) { continue }
            $decision = [pscustomobject]@{
                id = [string]$row.id
                key = [string]$row.key
                target = $target
                status = if ($item.status) { [string]$item.status } else { "pending" }
                text = ConvertTo-FlatString $item.text
                note = [string]$item.note
                translationOrigin = if ($item.PSObject.Properties["translationOrigin"]) { [string]$item.translationOrigin } else { "" }
                translationUpdatedAt = if ($item.PSObject.Properties["translationUpdatedAt"]) { [string]$item.translationUpdatedAt } else { "" }
                sourceHash = [string]$item.sourceHash
                sourceText = ConvertTo-FlatString $item.sourceText
                sourceChanged = if ($item.PSObject.Properties["sourceChanged"]) { ConvertTo-BoolValue $item.sourceChanged } else { $false }
                previousSourceText = if ($item.PSObject.Properties["previousSourceText"]) { ConvertTo-FlatString $item.previousSourceText } else { "" }
                updatedAt = [string]$item.updatedAt
            }
            Normalize-DecisionForRow -Row $row -Decision $decision
            if (ConvertTo-BoolValue $decision.sourceChanged) { $changedSources++ }
            $script:decisions[(Get-RowIdentity $row)] = $decision
            $imported++
        }
        if ($imported -gt 0) {
            Add-Log "이전 검수 상태 ${imported}개를 이어받았습니다."
            if ($changedSources -gt 0) { Add-Log "원문이 바뀐 ${changedSources}개 항목을 변경됨·미번역 상태로 표시했습니다." }
            $script:dirty = $true
            Save-Decisions
        }
    } catch {
        Add-Log "이전 검수 상태를 이어받지 못했습니다: $($_.Exception.Message)"
    }
}

function Save-Decisions {
    if (-not $script:reviewRoot) { return }
    Save-CurrentEdit
    $items = New-Object "System.Collections.Generic.List[object]"
    foreach ($row in $script:rows) {
        $identity = if ($row.id) { "id:$($row.id)" } else { "key:$($row.key)" }
        $decision = if ($script:decisions.ContainsKey($identity)) { $script:decisions[$identity] } else { $null }
        $candidate = ConvertTo-FlatString $row.candidate
        $existing = ConvertTo-FlatString $row.existing
        $defaultText = if (-not [string]::IsNullOrWhiteSpace($candidate)) { $candidate } else { $existing }
        $defaultOrigin = if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            if ($row.PSObject.Properties["translationOrigin"] -and $row.translationOrigin) { [string]$row.translationOrigin } else { "ai" }
        } elseif (-not [string]::IsNullOrWhiteSpace($existing)) {
            if ($row.PSObject.Properties["existingOrigin"] -and $row.existingOrigin) { [string]$row.existingOrigin } else { "existing" }
        } else { "" }
        $defaultChanged = $defaultOrigin -eq "rmk" -and $row.PSObject.Properties["rmkSourceChanged"] -and (ConvertTo-BoolValue $row.rmkSourceChanged)
        $defaultStatus = if ([string]::IsNullOrWhiteSpace($defaultText) -or $defaultChanged) { "pending" } else { "translated" }
        $defaultTranslationUpdatedAt = if ($row.PSObject.Properties["translationUpdatedAt"]) { [string]$row.translationUpdatedAt } else { "" }

        if ($decision) {
            $status = [string]$decision.status
            $text = ConvertTo-FlatString $decision.text
            $note = [string]$decision.note
            $origin = [string]$decision.translationOrigin
            $translationUpdatedAt = [string]$decision.translationUpdatedAt
            $sourceHash = [string]$decision.sourceHash
            $sourceText = ConvertTo-FlatString $decision.sourceText
            $sourceChanged = ConvertTo-BoolValue $decision.sourceChanged
            $previousSourceText = ConvertTo-FlatString $decision.previousSourceText
            $updatedAt = [string]$decision.updatedAt
        } else {
            $status = $defaultStatus
            $text = $defaultText
            $note = ""
            $origin = $defaultOrigin
            $translationUpdatedAt = $defaultTranslationUpdatedAt
            $sourceHash = ""
            $sourceText = ConvertTo-FlatString $row.source
            $sourceChanged = $defaultChanged
            $previousSourceText = if ($defaultChanged -and $row.PSObject.Properties["rmkHistoricalSource"]) { ConvertTo-FlatString $row.rmkHistoricalSource } else { "" }
            $updatedAt = ""
        }

        $durableDefault = -not [string]::IsNullOrWhiteSpace($candidate) -or $defaultOrigin -in @("ai", "local")
        $changedFromDefault = $status -ne $defaultStatus -or
            -not [string]::Equals($text, $defaultText, [System.StringComparison]::Ordinal) -or
            -not [string]::IsNullOrWhiteSpace($note) -or
            $origin -ne $defaultOrigin -or
            $translationUpdatedAt -ne $defaultTranslationUpdatedAt -or
            $sourceChanged -ne $defaultChanged -or
            -not [string]::IsNullOrWhiteSpace($updatedAt) -or
            ($sourceChanged -and -not [string]::IsNullOrWhiteSpace($previousSourceText))
        if (-not $durableDefault -and -not $changedFromDefault) { continue }

        [void]$items.Add([pscustomobject]@{
            id = [string]$row.id
            key = [string]$row.key
            target = Get-RelativeTarget $row
            status = $status
            text = $text
            note = $note
            translationOrigin = $origin
            translationUpdatedAt = $translationUpdatedAt
            sourceHash = $sourceHash
            sourceText = $sourceText
            sourceChanged = $sourceChanged
            previousSourceText = $previousSourceText
            updatedAt = $updatedAt
        })
    }
    $payload = [ordered]@{
        version = 5
        sparse = $true
        reviewRoot = $script:reviewRoot
        comparison = $script:comparisonFile
        updatedAt = (Get-Date).ToString("o")
        items = $items.ToArray()
    }
    $path = Get-DecisionPath
    Write-Utf8JsonFile -Path $path -Value $payload -Depth 8
    $script:dirty = $false
    Invalidate-DashboardProjectData ([string]$script:selectedProjectId)
    $lblSave.Text = "저장됨 " + (Get-Date -Format "HH:mm:ss")
}

function Save-CurrentEdit {
    if ($script:loading -or $script:currentRowIndex -lt 0 -or $script:currentRowIndex -ge $script:rows.Count) { return }
    $row = $script:rows[$script:currentRowIndex]
    $decision = Get-Decision $row
    $textChanged = $decision.text -ne $txtTranslation.Text
    $noteChanged = $decision.note -ne $txtMemo.Text
    if ($textChanged -or $noteChanged) {
        $before = if ($script:reviewStats) { Get-DecisionStateSnapshot $row } else { $null }
        $decision.text = ConvertTo-FlatString $txtTranslation.Text
        $decision.note = [string]$txtMemo.Text
        if ($textChanged) {
            $decision.translationOrigin = if ($script:translationEditorOrigin) { [string]$script:translationEditorOrigin } else { "local" }
            $decision.translationUpdatedAt = (Get-Date).ToString("o")
        }
        if ([string]::IsNullOrWhiteSpace($decision.text)) {
            $decision.status = "pending"
        } elseif ($decision.status -eq "pending" -or $decision.status -eq "approved") {
            $decision.status = "translated"
            $decision.sourceChanged = $false
            $decision.previousSourceText = ""
        }
        $decision.updatedAt = (Get-Date).ToString("o")
        $script:dirty = $true
        $lblSave.Text = "저장 필요"
        if ($before) {
            $after = Get-DecisionStateSnapshot $row
            Update-ReviewStatsForDecisionChange -Before $before -After $after
            Update-FileListGroupDisplay ([string]$after.File)
            [void](Update-RenderedItemCard $script:currentRowIndex)
            Refresh-Summary
        }
        $script:translationEditorOrigin = [string]$decision.translationOrigin
    }
}

function Set-TranslationEditorValue([string]$Text, [string]$Origin) {
    $script:settingTranslationOrigin = $true
    try {
        $txtTranslation.Text = ConvertTo-FlatString $Text
        $script:translationEditorOrigin = if ($Origin) { $Origin.ToLowerInvariant() } else { "local" }
    } finally {
        $script:settingTranslationOrigin = $false
    }
}

function Build-SourceRowIndex {
    $index = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([System.StringComparer]::Ordinal)
    for ($i = 0; $i -lt $script:rows.Count; $i++) {
        $source = ConvertTo-FlatString $script:rows[$i].source
        if ([string]::IsNullOrWhiteSpace($source)) { continue }
        if (-not $index.ContainsKey($source)) {
            $index[$source] = New-Object 'System.Collections.Generic.List[int]'
        }
        [void]$index[$source].Add($i)
    }
    $script:sourceRowIndex = $index
}

function Get-DuplicateSourceRowIndexes([int]$RowIndex, [string]$Translation) {
    $duplicateIndexes = New-Object "System.Collections.Generic.List[int]"
    if ($RowIndex -lt 0 -or $RowIndex -ge $script:rows.Count) { return $duplicateIndexes.ToArray() }

    $source = ConvertTo-FlatString $script:rows[$RowIndex].source
    $translationText = ConvertTo-FlatString $Translation
    if ([string]::IsNullOrWhiteSpace($source) -or [string]::IsNullOrWhiteSpace($translationText)) {
        return $duplicateIndexes.ToArray()
    }

    if (-not $script:sourceRowIndex) { Build-SourceRowIndex }
    if (-not $script:sourceRowIndex.ContainsKey($source)) { return $duplicateIndexes.ToArray() }
    foreach ($i in $script:sourceRowIndex[$source]) {
        if ($i -eq $RowIndex) { continue }
        $decision = Get-Decision $script:rows[$i]
        $sameTranslation = [string]::Equals(
            $translationText,
            (ConvertTo-FlatString $decision.text),
            [System.StringComparison]::Ordinal
        )
        $settledStatus = [string]$decision.status -in @("translated", "approved")
        if (-not $sameTranslation -or -not $settledStatus -or (ConvertTo-BoolValue $decision.sourceChanged)) {
            [void]$duplicateIndexes.Add($i)
        }
    }
    return $duplicateIndexes.ToArray()
}

function Apply-TranslationToDuplicateRows([int[]]$RowIndexes, [string]$Translation) {
    $translationText = ConvertTo-FlatString $Translation
    if ([string]::IsNullOrWhiteSpace($translationText)) { return 0 }

    $changed = 0
    $updatedAt = (Get-Date).ToString("o")
    foreach ($index in @($RowIndexes | Select-Object -Unique)) {
        if ($index -lt 0 -or $index -ge $script:rows.Count) { continue }
        $decision = Get-Decision $script:rows[$index]
        $decision.text = $translationText
        $decision.status = "translated"
        $decision.translationOrigin = "local"
        $decision.translationUpdatedAt = $updatedAt
        $decision.sourceChanged = $false
        $decision.previousSourceText = ""
        $decision.updatedAt = $updatedAt
        $changed++
    }
    if ($changed -gt 0) {
        $script:dirty = $true
        $lblSave.Text = "저장 필요"
    }
    return $changed
}

function Confirm-DuplicateSourceTranslation {
    if (-not $script:translationEditedByUser -or $script:currentRowIndex -lt 0 -or $script:currentRowIndex -ge $script:rows.Count) {
        return 0
    }

    Save-CurrentEdit
    $translation = ConvertTo-FlatString $txtTranslation.Text
    $duplicates = @(Get-DuplicateSourceRowIndexes -RowIndex $script:currentRowIndex -Translation $translation)
    $script:translationEditedByUser = $false
    $script:translationEditBaseline = $translation
    if ($duplicates.Count -eq 0) { return 0 }

    $preview = $translation
    if ($preview.Length -gt 240) { $preview = $preview.Substring(0, 237) + "..." }
    $message = "같은 원문을 사용하는 항목 중 번역을 맞춰야 할 다른 항목이 $($duplicates.Count)개 있습니다.`r`n`r`n번역문:`r`n$preview`r`n`r`n다른 항목도 이 번역으로 통일할까요?`r`n`r`n예: 동일 원문 전체 통일`r`n아니요: 현재 항목만 적용"
    $answer = [System.Windows.Forms.MessageBox]::Show(
        $form,
        $message,
        "동일 원문 일괄 번역",
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Question,
        [System.Windows.Forms.MessageBoxDefaultButton]::Button2
    )
    if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return 0 }

    $changed = Apply-TranslationToDuplicateRows -RowIndexes $duplicates -Translation $translation
    if ($changed -gt 0) {
        Add-Log "동일 원문 번역을 현재 항목 외 $changed개에 일괄 적용했습니다."
        Save-Decisions
    }
    return $changed
}

function Save-ReviewWithDuplicatePrompt {
    $changed = Confirm-DuplicateSourceTranslation
    if ($script:dirty -or $changed -gt 0) { Save-Decisions }
    if ($changed -gt 0) {
        $current = $script:currentRowIndex
        Refresh-FileList
        Refresh-ItemList -SelectRowIndex $current
    }
}

function Get-RowSearchBlob([object]$Row, [object]$Decision, [string]$Mode) {
    switch ($Mode) {
        "키" {
            return @([string]$Row.id, [string]$Row.key, (Get-RelativeTarget $Row)) -join "`n"
        }
        "텍스트" {
            return @(
                (ConvertTo-FlatString $Row.source),
                (ConvertTo-FlatString $Row.existing),
                (ConvertTo-FlatString $Row.candidate),
                (ConvertTo-FlatString $Decision.text),
                [string]$Decision.note
            ) -join "`n"
        }
        "Def Class" {
            $context = Get-RowDefContext $Row
            return @(
                [string]$context.DefClass,
                [string]$context.DefName,
                (Get-OptionalRowText -Row $Row -Names @("defClass", "defType", "typeName", "TypeName"))
            ) -join "`n"
        }
        "Node" {
            $context = Get-RowDefContext $Row
            return @(
                [string]$context.Node,
                [string]$context.Field,
                (Get-OptionalRowText -Row $Row -Names @("node", "field", "Field"))
            ) -join "`n"
        }
        default {
            return @(
                [string]$Row.id,
                [string]$Row.key,
                (Get-RelativeTarget $Row),
                (ConvertTo-FlatString $Row.source),
                (ConvertTo-FlatString $Row.existing),
                (ConvertTo-FlatString $Row.candidate),
                (ConvertTo-FlatString $Decision.text),
                [string]$Decision.note
            ) -join "`n"
        }
    }
}

function Get-RowPassesFilter([object]$Row) {
    $decision = Get-Decision $Row
    $status = [string]$cmbStatus.SelectedItem
    if ($script:currentFile -ne "__ALL__" -and (Get-RelativeTarget $Row) -ne $script:currentFile) { return $false }
    switch ($status) {
        "미번역" { if ($decision.status -ne "pending") { return $false } }
        "번역됨" { if ($decision.status -ne "translated") { return $false } }
        "검토됨" { if ($decision.status -ne "approved") { return $false } }
        "업데이트로 변경됨" { if (-not (ConvertTo-BoolValue $decision.sourceChanged)) { return $false } }
        "RMK 가져옴" { if ([string]$decision.translationOrigin -ne "rmk") { return $false } }
        "내 번역" { if ([string]$decision.translationOrigin -ne "local") { return $false } }
        "반려" { if ($decision.status -ne "rejected") { return $false } }
        "보류" { if ($decision.status -ne "hold") { return $false } }
        "주의" {
            $warnings = @(Get-RowWarnings -Row $Row -Translation (ConvertTo-FlatString $decision.text))
            if ($warnings.Count -eq 0) { return $false }
        }
        "후보 있음" { if ([string]::IsNullOrWhiteSpace([string]$Row.candidate)) { return $false } }
        "기존 있음" { if ([string]::IsNullOrWhiteSpace([string]$Row.existing)) { return $false } }
    }

    $query = $txtSearch.Text.Trim().ToLowerInvariant()
    if ($query) {
        $mode = if ($cmbSearchField -and $cmbSearchField.SelectedItem) { [string]$cmbSearchField.SelectedItem } else { "텍스트/키" }
        $blob = Get-RowSearchBlob -Row $Row -Decision $decision -Mode $mode
        if (-not $blob.ToLowerInvariant().Contains($query)) { return $false }
    }
    return $true
}

function Get-ItemPreview([object]$Row) {
    $source = ((ConvertTo-FlatString $Row.source) -replace "\s+", " ").Trim()
    $candidate = ((ConvertTo-FlatString (Get-Decision $Row).text) -replace "\s+", " ").Trim()
    if ($source.Length -gt 64) { $source = $source.Substring(0, 61) + "..." }
    if ($candidate.Length -gt 64) { $candidate = $candidate.Substring(0, 61) + "..." }
    return @($source, $candidate)
}

function Refresh-Summary {
    if (-not $script:reviewStats) { Build-FileGroups }
    $total = [int]$script:reviewStats.Total
    $approved = [int]$script:reviewStats.Approved
    $translated = [int]$script:reviewStats.Translated
    $pending = [int]$script:reviewStats.Pending
    $updated = [int]$script:reviewStats.Updated
    $done = if ($total -gt 0) { [int](($approved / $total) * 100) } else { 0 }
    $lblProjectStats.Text = "전체 $total  ·  미번역 $pending  ·  번역 $translated`r`n검토 $approved  ·  업데이트 변경 $updated"
    if ($toolTip) { $toolTip.SetToolTip($lblProjectStats, "주의 상태는 상태 필터에서 확인할 수 있습니다.") }
    if ($statusFilterButtons -and $statusFilterButtons.Count -ge 5) {
        $filterCounts = @($total, $pending, $translated, $approved, $updated)
        for ($i = 0; $i -lt 5; $i++) {
            $statusFilterButtons[$i].AccessibleDescription = "$($statusFilterButtons[$i].Text) 상태 $($filterCounts[$i])개만 목록에 표시합니다."
            if ($toolTip) { $toolTip.SetToolTip($statusFilterButtons[$i], "$($statusFilterButtons[$i].Text) $($filterCounts[$i])개") }
        }
    }
    $progressReview.Maximum = [Math]::Max(1, $total)
    $progressReview.Value = [Math]::Min($approved, $progressReview.Maximum)
    $lblProgress.Text = "검토 진행률 $done%"
}

function Add-StatusToStats([object]$Stats, [string]$Status, [int]$Delta) {
    if (-not $Stats -or $Delta -eq 0) { return }
    switch ($Status) {
        "approved" { $Stats.Approved = [Math]::Max(0, [int]$Stats.Approved + $Delta) }
        "translated" { $Stats.Translated = [Math]::Max(0, [int]$Stats.Translated + $Delta) }
        "rejected" { $Stats.Rejected = [Math]::Max(0, [int]$Stats.Rejected + $Delta) }
        "hold" { $Stats.Hold = [Math]::Max(0, [int]$Stats.Hold + $Delta) }
        default { $Stats.Pending = [Math]::Max(0, [int]$Stats.Pending + $Delta) }
    }
}

function Add-StatusToFileGroup([object]$Group, [string]$Status, [int]$Delta) {
    if (-not $Group -or $Delta -eq 0) { return }
    switch ($Status) {
        "approved" { $Group.Approved = [Math]::Max(0, [int]$Group.Approved + $Delta) }
        "translated" { $Group.Translated = [Math]::Max(0, [int]$Group.Translated + $Delta) }
        default { $Group.Pending = [Math]::Max(0, [int]$Group.Pending + $Delta) }
    }
}

function Get-DecisionStateSnapshot([object]$Row) {
    $decision = Get-Decision $Row
    return [pscustomobject]@{
        File = Get-RelativeTarget $Row
        Status = [string]$decision.status
        Updated = ConvertTo-BoolValue $decision.sourceChanged
        Warning = @(Get-RowWarnings -Row $Row -Translation (ConvertTo-FlatString $decision.text)).Count -gt 0
    }
}

function Update-ReviewStatsForDecisionChange([object]$Before, [object]$After) {
    if (-not $Before -or -not $After -or -not $script:reviewStats) { return }

    Add-StatusToStats -Stats $script:reviewStats -Status ([string]$Before.Status) -Delta -1
    Add-StatusToStats -Stats $script:reviewStats -Status ([string]$After.Status) -Delta 1
    if ([bool]$Before.Updated -ne [bool]$After.Updated) {
        $script:reviewStats.Updated = [Math]::Max(0, [int]$script:reviewStats.Updated + $(if ($After.Updated) { 1 } else { -1 }))
    }
    if ([bool]$Before.Warning -ne [bool]$After.Warning) {
        $script:reviewStats.Warnings = [Math]::Max(0, [int]$script:reviewStats.Warnings + $(if ($After.Warning) { 1 } else { -1 }))
    }

    $group = if ($script:fileGroupMap -and $script:fileGroupMap.ContainsKey([string]$After.File)) { $script:fileGroupMap[[string]$After.File] } else { $null }
    if ($group) {
        Add-StatusToFileGroup -Group $group -Status ([string]$Before.Status) -Delta -1
        Add-StatusToFileGroup -Group $group -Status ([string]$After.Status) -Delta 1
        if ([bool]$Before.Warning -ne [bool]$After.Warning) {
            $group.Warnings = [Math]::Max(0, [int]$group.Warnings + $(if ($After.Warning) { 1 } else { -1 }))
        }
    }
}

function Build-FileGroups {
    $stats = [pscustomobject]@{ Total = 0; Approved = 0; Translated = 0; Rejected = 0; Hold = 0; Pending = 0; Updated = 0; Warnings = 0 }
    foreach ($row in $script:rows) {
        $stats.Total++
        $identity = if ($row.id) { "id:$($row.id)" } else { "key:$($row.key)" }
        $decision = if ($script:decisions.ContainsKey($identity)) { $script:decisions[$identity] } else { $null }
        if ($decision -and $script:validateLoadedDecisionSources) { $decision = Get-Decision $row }
        if ($decision) {
            $rowStatus = [string]$decision.status
            $rowUpdated = ConvertTo-BoolValue $decision.sourceChanged
        } else {
            $candidate = [string]$row.candidate
            $existing = [string]$row.existing
            $defaultText = if (-not [string]::IsNullOrWhiteSpace($candidate)) { $candidate } else { $existing }
            $defaultOrigin = if (-not [string]::IsNullOrWhiteSpace($candidate)) {
                if ($row.translationOrigin) { [string]$row.translationOrigin } else { "ai" }
            } elseif (-not [string]::IsNullOrWhiteSpace($existing)) {
                if ($row.existingOrigin) { [string]$row.existingOrigin } else { "existing" }
            } else { "" }
            $rowUpdated = $defaultOrigin -eq "rmk" -and (ConvertTo-BoolValue $row.rmkSourceChanged)
            $rowStatus = if ([string]::IsNullOrWhiteSpace($defaultText) -or $rowUpdated) { "pending" } else { "translated" }
        }
        switch ($rowStatus) {
            "approved" { $stats.Approved++ }
            "translated" { $stats.Translated++ }
            "rejected" { $stats.Rejected++ }
            "hold" { $stats.Hold++ }
            default { $stats.Pending++ }
        }
        if ($rowUpdated) { $stats.Updated++ }
    }
    $script:fileGroups = @()
    $script:fileGroupMap = @{}
    $script:reviewStats = $stats
}

function Refresh-FileList {
    Build-FileGroups
    $lvFiles.BeginUpdate()
    try {
        $lvFiles.Items.Clear()
        $all = [System.Windows.Forms.ListViewItem]::new("전체")
        $all.Tag = "__ALL__"
        [void]$all.SubItems.Add([string]$script:rows.Count)
        [void]$all.SubItems.Add("")
        [void]$lvFiles.Items.Add($all)
    } finally {
        $lvFiles.EndUpdate()
    }
}

function Get-TranslationSortTicks([object]$Decision) {
    if (-not $Decision -or [string]$Decision.translationOrigin -ne "local") { return [long]0 }
    $value = [string]$Decision.translationUpdatedAt
    $parsed = [datetime]::MinValue
    if ($value -and [datetime]::TryParse($value, [ref]$parsed)) { return $parsed.ToUniversalTime().Ticks }
    return [long]0
}

function Refresh-ItemList([int]$SelectRowIndex = -1) {
    Save-CurrentEdit
    $script:syncingItemSelection = $true
    $flowItems.BeginUpdate()
    try {
        $lvItems.Items.Clear()
        $flowItems.Items.Clear()
        $rowOrder = [System.Collections.Generic.List[int]]::new()
        for ($rowIndex = 0; $rowIndex -lt $script:rows.Count; $rowIndex++) { [void]$rowOrder.Add($rowIndex) }
        $orderedRowIndexes = $rowOrder.ToArray()
        $sortMode = if ($cmbSort -and $cmbSort.SelectedItem) { [string]$cmbSort.SelectedItem } else { "기본 순서" }
        if ($sortMode -eq "내 번역 최신순") {
            $orderedRowIndexes = @($orderedRowIndexes | Sort-Object `
                @{ Expression = { if ([string](Get-Decision $script:rows[[int]$_]).translationOrigin -eq "local") { 0 } else { 1 } }; Ascending = $true }, `
                @{ Expression = { Get-TranslationSortTicks (Get-Decision $script:rows[[int]$_]) }; Descending = $true }, `
                @{ Expression = { [int]$_ }; Ascending = $true })
        } elseif ($sortMode -eq "내 번역 오래된순") {
            $orderedRowIndexes = @($orderedRowIndexes | Sort-Object `
                @{ Expression = { if ([string](Get-Decision $script:rows[[int]$_]).translationOrigin -eq "local") { 0 } else { 1 } }; Ascending = $true }, `
                @{ Expression = { Get-TranslationSortTicks (Get-Decision $script:rows[[int]$_]) }; Ascending = $true }, `
                @{ Expression = { [int]$_ }; Ascending = $true })
        }
        $capacity = $orderedRowIndexes.Count
        $visibleBuffer = [int[]]::new($capacity)
        $itemBuffer = [object[]]::new($capacity)
        $positionMap = [int[]]::new($script:rows.Count)
        $matched = 0
        $fastAllRows = $script:currentFile -eq "__ALL__" -and
            [string]$cmbStatus.SelectedItem -eq "전체" -and
            [string]::IsNullOrWhiteSpace($txtSearch.Text)
        foreach ($rowIndex in @($orderedRowIndexes)) {
            $i = [int]$rowIndex
            $row = $script:rows[$i]
            if (-not $fastAllRows -and -not (Get-RowPassesFilter $row)) { continue }
            $visibleBuffer[$matched] = $i
            $positionMap[$i] = $matched + 1
            $itemBuffer[$matched] = [System.Collections.DictionaryEntry]::new($i, [string]$row.key)
            $matched++
        }
        if ($matched -eq $capacity) {
            $visibleIndexes = $visibleBuffer
            $listItems = $itemBuffer
        } else {
            $visibleIndexes = [int[]]::new($matched)
            $listItems = [object[]]::new($matched)
            if ($matched -gt 0) {
                [System.Array]::Copy($visibleBuffer, $visibleIndexes, $matched)
                [System.Array]::Copy($itemBuffer, $listItems, $matched)
            }
        }
        if ($listItems.Count -gt 0) {
            $flowItems.Items.AddRange($listItems)
        }
    } finally {
        $flowItems.EndUpdate()
        $script:syncingItemSelection = $false
    }
    $script:visibleRowIndexes = $visibleIndexes
    $script:visibleRowPositionMap = $positionMap

    if ($script:visibleRowIndexes.Count -eq 0) {
        Clear-CurrentView
        return
    }

    $targetRow = $script:visibleRowIndexes[0]
    if ($SelectRowIndex -ge 0) {
        foreach ($visible in $script:visibleRowIndexes) { if ($visible -eq $SelectRowIndex) { $targetRow = $visible; break } }
    }
    Select-RowIndex ([int]$targetRow)
    Refresh-Summary
}

function Refresh-ResultSelection {
    if (-not $flowItems) { return }
    $positionValue = if ($script:currentRowIndex -ge 0 -and $script:currentRowIndex -lt $script:visibleRowPositionMap.Length) { [int]$script:visibleRowPositionMap[$script:currentRowIndex] } else { 0 }
    if ($positionValue -le 0) {
        $script:syncingItemSelection = $true
        try { $flowItems.ClearSelected() } finally { $script:syncingItemSelection = $false }
        return
    }
    $position = $positionValue - 1
    if ($flowItems.SelectedIndex -ne $position) {
        $script:syncingItemSelection = $true
        try { $flowItems.SelectedIndex = $position } finally { $script:syncingItemSelection = $false }
    }
    $flowItems.Invalidate()
}

function Clear-CurrentView {
    $script:currentRowIndex = -1
    $txtSource.Text = ""
    $txtTranslation.Text = ""
    $txtExisting.Text = ""
    $txtCandidate.Text = ""
    $txtMeta.Text = ""
    $txtWarnings.Text = ""
    $txtHistory.Text = ""
    $txtTerms.Text = ""
    $txtMemo.Text = ""
    $lblCurrent.Text = "항목 없음"
    if ($lblUpdateBadge) { $lblUpdateBadge.Visible = $false }
}

function Set-HistoryView([string]$Source, [string]$Existing, [string]$Candidate, [string]$Translation, [string]$PreviousSource = "", [bool]$SourceChanged = $false, [string]$ReferenceCurrentSource = "") {
    $isDark = Get-IsWindowsDarkMode
    $titleColor = if ($isDark) { [System.Drawing.Color]::FromArgb(183, 174, 159) } else { [System.Drawing.Color]::FromArgb(112, 102, 86) }
    $bodyColor = if ($isDark) { [System.Drawing.Color]::FromArgb(239, 233, 221) } else { [System.Drawing.Color]::FromArgb(47, 44, 38) }
    $reviewColor = if ($isDark) { [System.Drawing.Color]::FromArgb(107, 188, 129) } else { [System.Drawing.Color]::FromArgb(47, 126, 75) }
    $sections = New-Object "System.Collections.Generic.List[object]"
    if ($SourceChanged) {
        $previousTitle = if ($ReferenceCurrentSource) { "RMK 번역 당시 원문" } else { "업데이트 전 원문" }
        $currentTitle = if ($ReferenceCurrentSource) { "현재 RMK 비교 원문" } else { "현재 원문" }
        [void]$sections.Add([pscustomobject]@{ Title = $previousTitle; Value = $(if ($PreviousSource) { $PreviousSource } else { "(기록 없음)" }); Color = Get-UpdateColor })
        [void]$sections.Add([pscustomobject]@{ Title = $currentTitle; Value = $(if ($ReferenceCurrentSource) { $ReferenceCurrentSource } else { $Source }); Color = $bodyColor })
        if ($ReferenceCurrentSource -and -not [string]::Equals($ReferenceCurrentSource, $Source, [System.StringComparison]::Ordinal)) {
            [void]$sections.Add([pscustomobject]@{ Title = "현재 번역 기준 원문"; Value = $Source; Color = $bodyColor })
        }
    } else {
        [void]$sections.Add([pscustomobject]@{ Title = "원문"; Value = $Source; Color = $bodyColor })
    }
    [void]$sections.Add([pscustomobject]@{ Title = "기존 번역"; Value = $(if ($Existing) { $Existing } else { "(없음)" }); Color = $bodyColor })
    [void]$sections.Add([pscustomobject]@{ Title = "AI 후보"; Value = $(if ($Candidate) { $Candidate } else { "(없음)" }); Color = $bodyColor })
    [void]$sections.Add([pscustomobject]@{ Title = "현재 검수"; Value = $(if ($Translation) { $Translation } else { "(비어 있음)" }); Color = $reviewColor })

    $txtHistory.Clear()
    foreach ($section in $sections) {
        $txtHistory.SelectionStart = $txtHistory.TextLength
        $txtHistory.SelectionFont = $script:historyTitleFont
        $txtHistory.SelectionColor = $titleColor
        $txtHistory.AppendText("$($section.Title)`r`n")
        $txtHistory.SelectionStart = $txtHistory.TextLength
        $txtHistory.SelectionFont = $script:historyBodyFont
        $txtHistory.SelectionColor = $section.Color
        $txtHistory.AppendText("$($section.Value)`r`n`r`n")
    }
    $txtHistory.SelectionStart = 0
    $txtHistory.ScrollToCaret()
}

function Select-RowIndex([int]$Index) {
    if ($Index -lt 0 -or $Index -ge $script:rows.Count) { return }
    $previousIndex = $script:currentRowIndex
    if ($previousIndex -ge 0 -and $previousIndex -ne $Index) {
        $changed = Confirm-DuplicateSourceTranslation
        if ($changed -gt 0) {
            Refresh-FileList
            Refresh-ItemList -SelectRowIndex $Index
            return
        }
    }
    Save-CurrentEdit
    $script:loading = $true
    try {
        $script:currentRowIndex = $Index
        $row = $script:rows[$Index]
        $decision = Get-Decision $row
        $translationValidation = Get-CachedTranslationValidation -Row $row -Translation (ConvertTo-FlatString $decision.text)
        $warnings = @(Get-RowWarnings -Row $row -Translation (ConvertTo-FlatString $decision.text))
        $relative = Get-RelativeTarget $row
        $source = ConvertTo-FlatString $row.source
        $candidate = ConvertTo-FlatString $row.candidate
        $existing = ConvertTo-FlatString $row.existing
        $translation = ConvertTo-FlatString $decision.text
        $originText = Get-TranslationOriginText ([string]$decision.translationOrigin)
        $translationTime = Format-TranslationTimestamp ([string]$decision.translationUpdatedAt)
        $sourceChanged = ConvertTo-BoolValue $decision.sourceChanged
        $previousSource = ConvertTo-FlatString $decision.previousSourceText
        $referenceCurrentSource = Get-OptionalRowText -Row $row -Names @("rmkCurrentSource")

        $statusLabel = Get-StatusText $decision.status
        $updateLabel = if ($sourceChanged) { "  ·  업데이트로 변경됨" } else { "" }
        $warningLabel = if ($warnings.Count -gt 0) { "  ·  주의 $($warnings.Count)" } else { "" }
        $lblCurrent.Text = "{0} / {1}   {2}  ·  {3}{4}" -f ($Index + 1), $script:rows.Count, $statusLabel, $originText, $warningLabel
        $lblCurrent.ForeColor = if ($sourceChanged) { Get-UpdateColor } else { Get-StatusColor $decision.status }
        $lblUpdateBadge.Visible = $sourceChanged
        $lblUpdateBadge.ForeColor = Get-UpdateColor
        $lblCurrent.AccessibleName = "현재 문자열 상태"
        $lblCurrent.AccessibleDescription = "$statusLabel$updateLabel, 전체 $($script:rows.Count)개 중 $($Index + 1)번째$warningLabel"
        $txtSource.Text = $source
        $txtTranslation.Text = $translation
        $txtExisting.Text = $existing
        $txtCandidate.Text = $candidate
        $existingOriginText = Get-TranslationOriginText (Get-OptionalRowText -Row $row -Names @("existingOrigin"))
        $lblExisting.Text = if ($existing) { "기존 번역  ·  $existingOriginText" } else { "기존 번역" }
        $lblCandidate.Text = "AI 번역 후보"
        $lblTranslationTitle.Text = "번역문  ·  $originText"
        $txtMemo.Text = [string]$decision.note
        $wordCount = @($source -split '\s+' | Where-Object { $_ }).Count
        $safeText = if ($translationValidation.SafeToApply) { "예" } else { "아니요" }
        $defContext = Get-RowDefContext $row
        $classNote = if ($defContext.DefClass -eq "Keyed") {
            "$($defContext.ClassDescription)."
        } else {
            "$($defContext.DefClass)는 $($defContext.ClassDescription)."
        }
        $nodeNote = if ($defContext.DefClass -eq "Keyed") {
            "$($defContext.NodeDescription)."
        } else {
            "'$($defContext.Field)' 노드는 $($defContext.NodeDescription)."
        }
        $translationTimeText = if ($translationTime) { "   ·   내 번역 시각 $translationTime" } else { "" }
        $txtMeta.Text = "Def Class :  $($defContext.DefClass) ($classNote)`r`nNode :  $($defContext.Node) ($nodeNote)`r`n파일  $relative`r`nID  $($row.id)   ·   출처 $originText$translationTimeText   ·   단어 $wordCount   ·   안전 적용 $safeText"
        $txtMeta.AccessibleDescription = "Def Class는 문자열이 속한 RimWorld 정의 유형이고, Node는 Def 이름과 번역 필드 경로입니다. $($txtMeta.Text)"
        $issueLines = New-Object "System.Collections.Generic.List[string]"
        if ($sourceChanged) {
            $changeMessage = if ($referenceCurrentSource) { "RMK 번역 당시 원문과 현재 같은 언어의 원문이 달라졌습니다." } else { "모드 업데이트로 원문이 변경되었습니다." }
            [void]$issueLines.Add("$changeMessage 다시 번역하거나 검토해야 적용할 수 있습니다.")
        }
        foreach ($warning in $warnings) { [void]$issueLines.Add([string]$warning) }
        $txtWarnings.Text = if ($issueLines.Count -gt 0) { [string]::Join("`r`n", $issueLines.ToArray()) } else { "문제 없음" }
        Set-HistoryView -Source $source -Existing $existing -Candidate $candidate -Translation $translation -PreviousSource $previousSource -SourceChanged $sourceChanged -ReferenceCurrentSource $referenceCurrentSource
        Update-TermsForRow $row
        Refresh-ResultSelection
    } finally {
        $script:loading = $false
    }
    if ($previousIndex -ne $Index) {
        $script:translationEditedByUser = $false
        $script:translationEditBaseline = $translation
    }
    $script:translationEditorOrigin = [string]$decision.translationOrigin
}

function Update-FileListGroupDisplay([string]$RelativeFile) {
    if (-not $lvFiles -or [string]::IsNullOrWhiteSpace($RelativeFile)) { return }
    $group = $script:fileGroups | Where-Object { $_.File -eq $RelativeFile } | Select-Object -First 1
    if (-not $group) { return }
    foreach ($item in @($lvFiles.Items)) {
        if ([string]$item.Tag -ne $RelativeFile) { continue }
        if ($item.SubItems.Count -ge 3) {
            $item.SubItems[1].Text = "$($group.Approved)/$($group.Total)"
            $item.SubItems[2].Text = [string]$group.Warnings
        }
        break
    }
}

function Update-RenderedItemCard([int]$RowIndex) {
    if ($RowIndex -lt 0 -or $RowIndex -ge $script:rows.Count -or -not $flowItems) { return $false }
    if ($RowIndex -ge $script:visibleRowPositionMap.Length) { return $false }
    $positionValue = [int]$script:visibleRowPositionMap[$RowIndex]
    if ($positionValue -le 0) { return $false }
    $position = $positionValue - 1
    if ($position -ge 0 -and $position -lt $flowItems.Items.Count) {
        $flowItems.Invalidate($flowItems.GetItemRectangle($position))
    }
    Refresh-ResultSelection
    return $true
}

function Update-TermsForRow([object]$Row) {
    if (-not $script:glossaryLoaded) {
        $txtTerms.Clear()
        return
    }
    $text = ((ConvertTo-FlatString $Row.source) + "`n" + (ConvertTo-FlatString $Row.candidate)).ToLowerInvariant()
    if ($text.Length -lt 3) {
        $txtTerms.Text = "관련 용어 없음"
        return
    }
    $prefixes = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::Ordinal)
    for ($i = 0; $i -le $text.Length - 3; $i++) { [void]$prefixes.Add($text.Substring($i, 3)) }
    $matchedOrders = New-Object "System.Collections.Generic.HashSet[int]"
    foreach ($prefix in $prefixes) {
        if (-not $script:glossaryPrefixIndex.ContainsKey($prefix)) { continue }
        foreach ($indexed in $script:glossaryPrefixIndex[$prefix]) {
            if ($text.Contains([string]$indexed.SearchSource)) { [void]$matchedOrders.Add([int]$indexed.Order) }
        }
    }
    $hits = New-Object "System.Collections.Generic.List[string]"
    foreach ($indexed in $script:glossaryIndexedTerms) {
        if ($matchedOrders.Contains([int]$indexed.Order)) {
            $term = $indexed.Term
            $source = [string]$term.source
            $line = "$source => $($term.ko)"
            if ($term.note) { $line += " ($($term.note))" }
            [void]$hits.Add($line)
        }
        if ($hits.Count -ge 60) { break }
    }
    if ($hits.Count -eq 0) {
        $txtTerms.Text = "관련 용어 없음"
    } else {
        $txtTerms.Text = [string]::Join("`r`n", $hits.ToArray())
    }
}

function Move-Selection([int]$Delta) {
    if (-not $script:visibleRowIndexes -or $script:visibleRowIndexes.Count -eq 0) { return }
    $currentVisible = 0
    for ($i = 0; $i -lt $script:visibleRowIndexes.Count; $i++) {
        if ([int]$script:visibleRowIndexes[$i] -eq $script:currentRowIndex) { $currentVisible = $i; break }
    }
    $next = [Math]::Max(0, [Math]::Min($script:visibleRowIndexes.Count - 1, $currentVisible + $Delta))
    Select-RowIndex ([int]$script:visibleRowIndexes[$next])
}

function Get-AdjacentVisibleRowIndex([int]$RowIndex) {
    if (-not $script:visibleRowIndexes -or $script:visibleRowIndexes.Count -eq 0) { return -1 }

    $position = -1
    for ($i = 0; $i -lt $script:visibleRowIndexes.Count; $i++) {
        if ([int]$script:visibleRowIndexes[$i] -eq $RowIndex) {
            $position = $i
            break
        }
    }
    if ($position -lt 0) { return [int]$script:visibleRowIndexes[0] }
    if ($position + 1 -lt $script:visibleRowIndexes.Count) { return [int]$script:visibleRowIndexes[$position + 1] }
    if ($position -gt 0) { return [int]$script:visibleRowIndexes[$position - 1] }
    return -1
}

function Mark-Current([string]$Status, [bool]$Advance) {
    if ($script:currentRowIndex -lt 0) { return }
    $old = $script:currentRowIndex
    $fallback = Get-AdjacentVisibleRowIndex $old
    $row = $script:rows[$old]
    Save-CurrentEdit
    $bulkChanged = Confirm-DuplicateSourceTranslation
    $before = Get-DecisionStateSnapshot $row
    Set-DecisionStatus -Row $row -Status $Status
    $lblSave.Text = if ($script:autoSave) { "자동 저장 대기" } else { "저장 필요" }
    Queue-AutoSave

    $currentStillVisible = Get-RowPassesFilter $row
    if ($bulkChanged -gt 0) {
        Refresh-FileList
        $target = if ($currentStillVisible) { $old } else { $fallback }
        Refresh-ItemList -SelectRowIndex $target
        if ($bulkChanged -gt 0 -and $currentStillVisible -and $Advance) { Move-Selection 1 }
        return
    }

    $after = Get-DecisionStateSnapshot $row
    Update-ReviewStatsForDecisionChange -Before $before -After $after
    Update-FileListGroupDisplay ([string]$after.File)
    if (-not $currentStillVisible) {
        Refresh-ItemList -SelectRowIndex $fallback
        return
    }

    [void](Update-RenderedItemCard $old)
    Refresh-Summary
    if ($Advance) {
        Move-Selection 1
    } else {
        Select-RowIndex $old
    }
}

function Approve-AllSafeTranslations {
    if (-not $script:rows -or $script:rows.Count -eq 0) { return }
    Save-CurrentEdit
    [void](Confirm-DuplicateSourceTranslation)

    $eligible = New-Object "System.Collections.Generic.List[int]"
    $blank = 0
    $changedSource = 0
    $unsafe = 0
    foreach ($index in 0..($script:rows.Count - 1)) {
        $row = $script:rows[$index]
        $decision = Get-Decision $row
        if ([string]::IsNullOrWhiteSpace((ConvertTo-FlatString $decision.text))) { $blank++; continue }
        if (ConvertTo-BoolValue $decision.sourceChanged) { $changedSource++; continue }
        if (@(Get-RowWarnings -Row $row -Translation (ConvertTo-FlatString $decision.text)).Count -gt 0) { $unsafe++; continue }
        if ([string]$decision.status -ne "approved") { [void]$eligible.Add($index) }
    }

    if ($eligible.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show(
            "새로 검토 완료로 바꿀 안전한 번역이 없습니다.`r`n`r`n빈 번역 $blank개 · 원문 변경 $changedSource개 · 주의 $unsafe개",
            "전체 검토 완료",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Information
        ) | Out-Null
        return
    }

    $answer = [System.Windows.Forms.MessageBox]::Show(
        "안전 검사를 통과한 번역 $($eligible.Count)개를 모두 검토 완료로 표시할까요?`r`n`r`n빈 번역 $blank개, 원문 변경 $changedSource개, 주의 $unsafe개는 건너뜁니다.",
        "전체 검토 완료",
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Question
    )
    if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return }

    $updatedAt = (Get-Date).ToString("o")
    foreach ($index in $eligible) {
        $decision = Get-Decision $script:rows[$index]
        $decision.status = "approved"
        $decision.updatedAt = $updatedAt
    }
    $script:dirty = $true
    Save-Decisions
    Refresh-FileList
    Refresh-ItemList -SelectRowIndex $script:currentRowIndex
    Add-Log "전체 검토 완료: $($eligible.Count)개 승인, 빈 번역 $blank개 · 원문 변경 $changedSource개 · 주의 $unsafe개 건너뜀"
}

function Load-ReviewRoot([string]$Root, [switch]$SkipPreviousDecisions) {
    $loadStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $stageStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $loadStages = New-Object 'System.Collections.Generic.List[string]'
    if (-not $Root -or -not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "검토 폴더를 찾을 수 없습니다: $Root"
    }
    if ($script:reviewRoot -and $script:dirty) {
        Save-Decisions
    }
    $script:reviewRoot = [System.IO.Path]::GetFullPath($Root)
    if (-not $script:selectedProjectId) {
        foreach ($project in $script:projects) {
            if (-not $project.latestReviewRoot) { continue }
            try {
                if ([System.IO.Path]::GetFullPath([string]$project.latestReviewRoot) -ieq $script:reviewRoot) {
                    $script:selectedProjectId = [string]$project.id
                    $script:selectedModRoot = [string]$project.modRoot
                    break
                }
            } catch {}
        }
    }
    $script:comparisonFile = Find-ComparisonFile $script:reviewRoot
    $parsed = [System.IO.File]::ReadAllText($script:comparisonFile, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $allRows = @($parsed)
    [void]$loadStages.Add(("JSON {0:n0}ms" -f $stageStopwatch.Elapsed.TotalMilliseconds)); $stageStopwatch.Restart()
    $excludedInternalCount = 0
    $screeningAudit = $script:comparisonFile -replace '-comparison\.json$', '-skipped-internal-identifiers.json'
    if (Test-Path -LiteralPath $screeningAudit -PathType Leaf) {
        $script:rows = [object[]]$allRows
        try {
            $auditInfo = Get-Item -LiteralPath $screeningAudit -ErrorAction Stop
            if ($auditInfo.Length -gt 2 -and $auditInfo.Length -le 1048576) {
                [object[]]$screenedRows = [System.IO.File]::ReadAllText($screeningAudit, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
                $excludedInternalCount = $screenedRows.Count
            }
        } catch {}
    } else {
        $includedRows = [System.Collections.Generic.List[object]]::new()
        foreach ($row in $allRows) {
            if (Get-InternalLocalizationIdentifierReason $row) { $excludedInternalCount++; continue }
            [void]$includedRows.Add($row)
        }
        $script:rows = $includedRows.ToArray()
    }
    $script:sourceRowIndex = $null
    if ($excludedInternalCount -gt 0) { Add-Log "내부 식별자 ${excludedInternalCount}개를 검수 목록에서 제외했습니다." }
    [void]$loadStages.Add(("필터 {0:n0}ms" -f $stageStopwatch.Elapsed.TotalMilliseconds)); $stageStopwatch.Restart()
    if ($script:validationCache.Count -gt 20000) { $script:validationCache = @{} }
    $script:reviewStats = $null
    $script:relativeTargetCache = @{}
    $script:currentRowIndex = -1
    Load-Decisions
    [void]$loadStages.Add(("상태 {0:n0}ms" -f $stageStopwatch.Elapsed.TotalMilliseconds)); $stageStopwatch.Restart()
    if (-not $SkipPreviousDecisions) { Import-PreviousProjectDecisions }
    [void]$loadStages.Add(("이전 작업 {0:n0}ms" -f $stageStopwatch.Elapsed.TotalMilliseconds)); $stageStopwatch.Restart()
    $script:currentFile = "__ALL__"
    $lblProject.Text = Get-CurrentDisplayName
    $lblPath.Text = if ($script:selectedModRoot) { $script:selectedModRoot } else { $script:reviewRoot }
    Update-SearchCrumb
    Refresh-FileList
    [void]$loadStages.Add(("파일 집계 {0:n0}ms" -f $stageStopwatch.Elapsed.TotalMilliseconds)); $stageStopwatch.Restart()
    if ($script:dirty) {
        Save-Decisions
        [void]$loadStages.Add(("상태 저장 {0:n0}ms" -f $stageStopwatch.Elapsed.TotalMilliseconds)); $stageStopwatch.Restart()
    }
    Refresh-ItemList
    [void]$loadStages.Add(("목록 화면 {0:n0}ms" -f $stageStopwatch.Elapsed.TotalMilliseconds)); $stageStopwatch.Restart()
    $lblSave.Text = "불러옴 " + (Get-Date -Format "HH:mm:ss")
    $script:lastReviewOutputPath = $script:reviewRoot
    $hasProjectMod = [bool](Get-ActiveProjectModRoot)
    if ($btnApply) { $btnApply.Enabled = $hasProjectMod }
    if ($btnApplyTranslated) { $btnApplyTranslated.Enabled = $hasProjectMod }
    if ($tabs -and $tabRmk -and $tabs.SelectedTab -eq $tabRmk) { Refresh-RmkPanel }
    $loadStopwatch.Stop()
    Add-Log ("검수 화면 로드: {0:n2}초 · {1}개 문자열" -f $loadStopwatch.Elapsed.TotalSeconds, $script:rows.Count)
    Add-Log ("로드 세부: " + [string]::Join(" · ", $loadStages))
}

function Choose-ReviewRoot {
    $dlg = [System.Windows.Forms.FolderBrowserDialog]::new()
    $dlg.Description = "검수할 리뷰 결과 폴더를 선택하세요."
    if ($script:reviewRoot -and (Test-Path -LiteralPath $script:reviewRoot)) {
        $dlg.SelectedPath = $script:reviewRoot
    } else {
        $reviewsRoot = Join-Path $scriptRoot "reviews"
        if (Test-Path -LiteralPath $reviewsRoot) { $dlg.SelectedPath = $reviewsRoot }
    }
    if ($dlg.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
        Load-ReviewRoot $dlg.SelectedPath
        if ($script:selectedModRoot) {
            Register-ProjectRun -ReviewRoot $dlg.SelectedPath -Provider "manual"
        }
    }
}

function Open-ReviewFolder {
    if ($script:reviewRoot -and (Test-Path -LiteralPath $script:reviewRoot)) {
        Start-Process -FilePath $script:explorerExe -ArgumentList "`"$script:reviewRoot`""
    }
}

function Open-ModFolder {
    $modRoot = Get-ActiveProjectModRoot
    if ($modRoot -and (Test-Path -LiteralPath $modRoot -PathType Container)) {
        Start-Process -FilePath $script:explorerExe -ArgumentList "`"$modRoot`""
    }
}

function Copy-ToClipboard([string]$Text) {
    if ($null -ne $Text) { [System.Windows.Forms.Clipboard]::SetText($Text) }
}

function Refresh-ProjectList {
    if (-not $cmbProject) { return }
    $current = $script:selectedProjectId
    $cmbProject.BeginUpdate()
    $script:loadingProjectList = $true
    try {
        $cmbProject.Items.Clear()
        foreach ($project in @($script:projects | Sort-Object @{ Expression = "updatedAt"; Descending = $true })) {
            $label = $project.name
            if ($project.workshopId) { $label += " [$($project.workshopId)]" }
            $item = [pscustomobject]@{ Display = $label; Project = $project }
            [void]$cmbProject.Items.Add($item)
            if ($current -and $project.id -eq $current) { $cmbProject.SelectedItem = $item }
        }
    } finally {
        $script:loadingProjectList = $false
        $cmbProject.EndUpdate()
    }
}

function Update-ModCatalogControls {
    if (-not $cmbModCatalog) { return }
    if ($cmbModCatalog.Visible) {
        $cmbModCatalog.BeginUpdate()
        try {
            $cmbModCatalog.Items.Clear()
            foreach ($mod in $script:modCatalog) { [void]$cmbModCatalog.Items.Add($mod) }
        } finally {
            $cmbModCatalog.EndUpdate()
        }
    }
    if ($cmbDashboardMods) {
        $cmbDashboardMods.BeginUpdate()
        try {
            $cmbDashboardMods.Items.Clear()
            foreach ($mod in $script:modCatalog) { [void]$cmbDashboardMods.Items.Add($mod) }
            if ($cmbDashboardMods.Items.Count -gt 0 -and $cmbDashboardMods.SelectedIndex -lt 0) { $cmbDashboardMods.SelectedIndex = 0 }
        } finally {
            $cmbDashboardMods.EndUpdate()
        }
    }
}

function Refresh-ModCatalog([switch]$PreferCache) {
    if (-not $cmbModCatalog) { return }
    if ($PreferCache -and (Try-LoadModCatalogCache -FastValidation)) {
        Update-ModCatalogControls
        $lblRunStatus.Text = "모드 $($script:modCatalog.Count)개 준비됨"
        return
    }

    $lblRunStatus.Text = "모드 검색 중..."
    [System.Windows.Forms.Application]::DoEvents()
    $script:modCatalog = @(Find-RimWorldMods)
    Update-ModCatalogControls
    Save-ModCatalogCache
    $lblRunStatus.Text = "모드 $($script:modCatalog.Count)개 검색됨"
}

function Set-SelectedMod([object]$ModInfo) {
    if (-not $ModInfo) { return $null }
    $modRoot = Get-NormalizedDirectoryPath ([string]$ModInfo.Path)
    $projectId = Get-ProjectIdForMod -ModRoot $modRoot -PackageId ([string]$ModInfo.PackageId) -WorkshopId ([string]$ModInfo.WorkshopId)
    $existing = @($script:projects | Where-Object { $_.id -eq $projectId } | Select-Object -First 1)
    $sourceLanguage = ""
    if ($existing.Count -eq 0) {
        $sourceLanguage = Select-ProjectSourceLanguage $ModInfo
        if (-not $sourceLanguage) {
            Add-Log "프로젝트 생성을 취소했습니다: $($ModInfo.Name)"
            return $null
        }
    }
    $project = Get-OrCreateProject -ModInfo $ModInfo -SourceLanguageFolder $sourceLanguage
    if (-not (Ensure-ProjectSourceLanguage $project)) { return $null }
    Save-ProjectStore
    Set-ActiveProject $project
    $selectedLanguage = if ($project.PSObject.Properties["sourceLanguageFolder"]) { [string]$project.sourceLanguageFolder } else { "Auto" }
    Add-Log "프로젝트 열림: $($project.name) · 원문 언어: $selectedLanguage"
    return $project
}

function Ensure-ProjectSourceLanguage([object]$Project) {
    if (-not $Project -or -not $Project.modRoot) { return $false }
    $current = if ($Project.PSObject.Properties["sourceLanguageFolder"]) { ([string]$Project.sourceLanguageFolder).Trim() } else { "Auto" }
    if ($current -and $current -ne "Auto") { return $true }
    if ($Project.latestReviewRoot -and (Test-Path -LiteralPath ([string]$Project.latestReviewRoot) -PathType Container)) { return $true }

    $options = @(Get-ModSourceLanguageOptions ([string]$Project.modRoot))
    if ($options.Count -eq 0) { return $true }
    $selected = if ($options.Count -eq 1) {
        [string]$options[0].Folder
    } else {
        Select-ProjectSourceLanguage ([pscustomobject]@{ Path = [string]$Project.modRoot; Name = [string]$Project.name })
    }
    if (-not $selected) { return $false }
    $Project.sourceLanguageFolder = $selected
    $Project.updatedAt = (Get-Date).ToString("o")
    Save-ProjectStore
    Invalidate-DashboardProjectData ([string]$Project.id)
    return $true
}

function Choose-ModFolder {
    $dlg = [System.Windows.Forms.FolderBrowserDialog]::new()
    $dlg.Description = "프로젝트로 만들 RimWorld 모드 폴더를 선택하세요."
    if ($script:selectedModRoot -and (Test-Path -LiteralPath $script:selectedModRoot)) {
        $dlg.SelectedPath = $script:selectedModRoot
    }
    if ($dlg.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
        $info = Get-RimWorldModInfo -ModPath $dlg.SelectedPath -Source "Manual"
        if (-not $info) {
            $info = [pscustomobject]@{
                Display = Split-Path -Leaf $dlg.SelectedPath
                Name = Split-Path -Leaf $dlg.SelectedPath
                Path = [System.IO.Path]::GetFullPath($dlg.SelectedPath)
                Source = "Manual"
                Folder = Split-Path -Leaf $dlg.SelectedPath
                PackageId = ""
                WorkshopId = Get-WorkshopIdFromPath $dlg.SelectedPath
                Search = $dlg.SelectedPath.ToLowerInvariant()
            }
        }
        $project = Set-SelectedMod $info
        if ($project -and $dashboardPanel -and $dashboardPanel.Visible) {
            Show-Workspace
            Load-SourceOnlyForSelectedMod
        }
    }
}

function Set-TranslationRunning([bool]$Running) {
    $hasProjectMod = [bool](Get-ActiveProjectModRoot)
    $btnTranslate.Enabled = (-not $Running) -and $hasProjectMod
    $btnStop.Enabled = $Running
    $btnApply.Enabled = (-not $Running) -and [bool]$script:reviewRoot -and $hasProjectMod
    $btnApplyTranslated.Enabled = (-not $Running) -and [bool]$script:reviewRoot -and $hasProjectMod
    $btnLoad.Enabled = -not $Running
    $btnChooseMod.Enabled = -not $Running
    $btnRefreshMods.Enabled = -not $Running
    $cmbModCatalog.Enabled = -not $Running
    $cmbProject.Enabled = -not $Running
    $txtApiKeys.Enabled = -not $Running
    if ($pnlApiSettings) { $pnlApiSettings.Enabled = -not $Running }
    $chkIncludePatches.Enabled = -not $Running
    $chkDryRun.Enabled = -not $Running
    $chkApplyToRmk.Enabled = -not $Running
    $hasRmkWorkspace = $script:rmkWorkspaceRoot -and (Test-RmkRoot -Path $script:rmkWorkspaceRoot -RequireGit)
    if ($btnRmkBuild) { $btnRmkBuild.Enabled = (-not $Running) -and [bool]$hasRmkWorkspace }
    Update-StopButtonAppearance
}

function Get-ExistingProjectTranslationInfo([string]$ModRoot, [string]$RmkReferenceRoot = "") {
    $reviewCount = 0
    $project = Get-SelectedProject
    $currentReviewBelongsToProject = $project -and (Test-ReviewRootBelongsToProject -Project $project -ReviewRoot $script:reviewRoot)
    if ($currentReviewBelongsToProject -and $script:rows.Count -gt 0 -and $script:decisions.Count -gt 0) {
        $reviewCount = @($script:decisions.Values | Where-Object { -not [string]::IsNullOrWhiteSpace((ConvertTo-FlatString $_.text)) }).Count
    } else {
        if ($project -and $project.latestReviewRoot) {
            $decisionPath = Join-Path ([string]$project.latestReviewRoot) "review-decisions.json"
            if (Test-Path -LiteralPath $decisionPath -PathType Leaf) {
                try {
                    $saved = [System.IO.File]::ReadAllText($decisionPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
                    $reviewCount = @($saved.items | Where-Object { -not [string]::IsNullOrWhiteSpace((ConvertTo-FlatString $_.text)) }).Count
                } catch {
                }
            }
        }
    }

    $koreanFileCount = 0
    $koreanRoot = Join-Path $ModRoot "Languages\Korean"
    if (Test-Path -LiteralPath $koreanRoot -PathType Container) {
        try { $koreanFileCount = @(Get-ChildItem -LiteralPath $koreanRoot -Recurse -File -Filter "*.xml" -ErrorAction Stop).Count } catch {}
    }
    $rmkFileCount = 0
    if ($RmkReferenceRoot -and (Test-Path -LiteralPath $RmkReferenceRoot -PathType Container)) {
        try { $rmkFileCount = @(Get-ChildItem -LiteralPath $RmkReferenceRoot -Recurse -File -Filter "*.xml" -ErrorAction Stop).Count } catch {}
    }
    return [pscustomobject]@{
        ReviewTranslationCount = $reviewCount
        KoreanFileCount = $koreanFileCount
        RmkFileCount = $rmkFileCount
        HasExistingTranslation = ($reviewCount -gt 0 -or $koreanFileCount -gt 0 -or $rmkFileCount -gt 0)
    }
}

function Select-AiTranslationMode([object]$ExistingInfo) {
    if (-not $ExistingInfo -or -not $ExistingInfo.HasExistingTranslation) { return "Overwrite" }

    $dialog = [System.Windows.Forms.Form]::new()
    $dialog.Text = "AI 번역 방식 선택"
    $dialog.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterParent
    $dialog.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $dialog.ClientSize = [System.Drawing.Size]::new(680, 278)
    $dialog.MinimizeBox = $false
    $dialog.MaximizeBox = $false
    $dialog.ShowInTaskbar = $false
    $dialog.ShowIcon = $false
    $dialog.BackColor = $script:surfaceColor
    $dialog.Font = New-Font 9
    $dialog.Tag = "Cancel"

    $accent = [System.Windows.Forms.Panel]::new()
    $accent.SetBounds(0, 0, 680, 4)
    $accent.BackColor = $script:accentColor

    $title = New-Label "기존 번역을 어떻게 처리할까요?" 28 24 620 30 $script:textColor 13 ([System.Drawing.FontStyle]::Bold)
    $bodyText = "검수 프로젝트 번역 $($ExistingInfo.ReviewTranslationCount)개 · 모드 Korean XML $($ExistingInfo.KoreanFileCount)개 · RMK XML $($ExistingInfo.RmkFileCount)개`r`n`r`n덮어씌우기는 모든 문자열의 후보를 다시 만들고, 미번역 부분만 번역하기는 기존 번역을 보존합니다."
    $body = New-Label $bodyText 28 66 624 86 $script:mutedColor 9.5
    $body.AutoEllipsis = $false

    $divider = [System.Windows.Forms.Panel]::new()
    $divider.SetBounds(28, 164, 624, 1)
    $divider.BackColor = [System.Drawing.Color]::FromArgb(120, $script:mutedColor)

    $btnOverwrite = New-Button "덮어씌우기" $script:accentColor
    $btnOverwrite.ForeColor = [System.Drawing.Color]::White
    $btnOverwrite.SetBounds(68, 192, 150, 46)
    $btnMissingOnly = New-Button "미번역 부분만 번역하기" ([System.Drawing.Color]::FromArgb(42, 139, 86))
    $btnMissingOnly.ForeColor = [System.Drawing.Color]::White
    $btnMissingOnly.SetBounds(230, 192, 260, 46)
    $btnCancel = New-Button "취소" $script:surfaceColor
    $btnCancel.ForeColor = $script:textColor
    $btnCancel.FlatAppearance.BorderColor = $script:mutedColor
    $btnCancel.FlatAppearance.BorderSize = 1
    $btnCancel.SetBounds(502, 192, 110, 46)

    Set-AccessibleControl $btnOverwrite "기존 번역 덮어씌우기" "모든 문자열을 다시 번역하고 새 후보로 교체합니다." 0
    Set-AccessibleControl $btnMissingOnly "미번역 부분만 번역하기" "기존 번역은 보존하고 번역이 없는 문자열만 번역합니다." 1
    Set-AccessibleControl $btnCancel "AI 번역 취소" "번역을 시작하지 않고 창을 닫습니다." 2

    $btnOverwrite.Add_Click({ $dialog.Tag = "Overwrite"; $dialog.Close() })
    $btnMissingOnly.Add_Click({ $dialog.Tag = "MissingOnly"; $dialog.Close() })
    $btnCancel.Add_Click({ $dialog.Tag = "Cancel"; $dialog.Close() })
    $dialog.AcceptButton = $btnMissingOnly
    $dialog.CancelButton = $btnCancel
    $dialog.Controls.AddRange(@($accent, $title, $body, $divider, $btnOverwrite, $btnMissingOnly, $btnCancel))

    try {
        if ($form -and -not $form.IsDisposed -and $form.Visible) {
            [void]$dialog.ShowDialog($form)
        } else {
            [void]$dialog.ShowDialog()
        }
        return [string]$dialog.Tag
    } finally {
        $dialog.Dispose()
    }
}

function New-PreserveTranslationFile {
    if (-not $script:reviewRoot -or $script:rows.Count -eq 0 -or $script:decisions.Count -eq 0) { return $null }
    $items = New-Object "System.Collections.Generic.List[object]"
    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($row in $script:rows) {
        $decision = Get-Decision $row
        $key = ([string]$row.key).Trim()
        $text = ConvertTo-FlatString $decision.text
        if (-not $key -or -not $seen.Add($key) -or [string]::IsNullOrWhiteSpace($text) -or (ConvertTo-BoolValue $decision.sourceChanged)) { continue }
        [void]$items.Add([pscustomobject]@{
            key = $key
            text = $text
            origin = [string]$decision.translationOrigin
            translationUpdatedAt = [string]$decision.translationUpdatedAt
        })
    }
    if ($items.Count -eq 0) { return $null }
    $path = New-TempFilePath "preserve-translations" ".json"
    $payload = [ordered]@{ version = 1; items = $items.ToArray() }
    [System.IO.File]::WriteAllText($path, ($payload | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
    [void]$script:tempFiles.Add($path)
    return [pscustomobject]@{ Path = $path; Count = $items.Count }
}

function Start-Translation {
    if ($script:process -and -not $script:process.HasExited) {
        [System.Windows.Forms.MessageBox]::Show("이미 번역이 실행 중입니다.", "RimWorld AI Translator") | Out-Null
        return
    }
    $selectedProject = Get-SelectedProject
    if ($selectedProject -and -not (Ensure-ProjectSourceLanguage $selectedProject)) { return }
    try {
        $modRoot = Get-ActiveProjectModRoot -Require
    } catch {
        [System.Windows.Forms.MessageBox]::Show("먼저 프로젝트를 만들거나 여세요.", "RimWorld AI Translator") | Out-Null
        return
    }
    if (-not (Test-Path -LiteralPath $script:translatorScript -PathType Leaf) -or -not (Test-Path -LiteralPath $script:translationRunnerScript -PathType Leaf)) {
        [System.Windows.Forms.MessageBox]::Show("번역 실행 파일을 찾을 수 없습니다. 프로그램 패키지를 다시 확인하세요.", "RimWorld AI Translator") | Out-Null
        return
    }
    Save-ReviewWithDuplicatePrompt
    Save-CurrentApiProviderControls -Persist
    $rmkTarget = Get-RmkReferenceTarget (Get-SelectedProject)
    $rmkReference = if ($rmkTarget) { [string]$rmkTarget.LanguageRoot } else { "" }
    $rmkWorkbook = if ($rmkTarget -and $rmkTarget.PSObject.Properties["WorkbookPath"]) { [string]$rmkTarget.WorkbookPath } else { "" }
    $existingInfo = Get-ExistingProjectTranslationInfo -ModRoot $modRoot -RmkReferenceRoot $rmkReference
    $translationMode = Select-AiTranslationMode $existingInfo
    if ($translationMode -eq "Cancel") { return }

    Ensure-AppDataStore
    Remove-TempFiles
    $script:lastReviewOutputPath = ""
    $script:lastProvider = ""
    $script:translationLogFile = New-TempFilePath "translation-output" ".log"
    [System.IO.File]::WriteAllText($script:translationLogFile, "", [System.Text.UTF8Encoding]::new($false))
    [void]$script:tempFiles.Add($script:translationLogFile)
    $script:translationLogOffset = 0L
    $script:translationLogPartial = ""

    $selectedProvider = Get-ApiProviderProfile
    $selectedProviderConfig = Get-SelectedApiProviderConfig
    if (-not $selectedProvider -or -not $selectedProviderConfig) {
        [System.Windows.Forms.MessageBox]::Show("번역 API 설정을 읽을 수 없습니다.", "RimWorld AI Translator") | Out-Null
        return
    }
    $keys = @(Get-ApiKeyLines ([string]$script:apiProviderKeys[$script:selectedApiProviderId]))
    $effectiveProvider = $selectedProvider
    $effectiveProviderConfig = $selectedProviderConfig
    if ([string]$selectedProvider.Provider -ne "Google" -and $keys.Count -eq 0) {
        $effectiveProvider = Get-ApiProviderProfile "Google"
        $effectiveProviderConfig = $script:apiProviderConfigs["Google"]
    }
    if ([string]$effectiveProvider.Provider -ne "Google") {
        $providerUri = $null
        if (-not [System.Uri]::TryCreate(([string]$effectiveProviderConfig.url).Trim(), [System.UriKind]::Absolute, [ref]$providerUri) -or $providerUri.Scheme -ne [System.Uri]::UriSchemeHttps) {
            [System.Windows.Forms.MessageBox]::Show("선택한 번역 API의 HTTPS URL을 확인하세요.", "RimWorld AI Translator") | Out-Null
            return
        }
        $providerModel = ([string]$effectiveProviderConfig.model).Trim()
        if (-not $providerModel -or $providerModel.Length -gt 200 -or $providerModel -match "[\x00-\x1F\x7F]") {
            [System.Windows.Forms.MessageBox]::Show("선택한 번역 API의 모델 ID를 확인하세요.", "RimWorld AI Translator") | Out-Null
            return
        }
    }
    $sourceLanguage = Get-SelectedProjectSourceLanguage
    $translationParameters = [ordered]@{
        ModRoot = $modRoot
        LanguageFolderName = "Korean"
        SourceLanguageFolder = $sourceLanguage
        ReviewOnly = $true
        ReviewRoot = $script:appReviewRoot
        BatchSize = 40
        MaxGeneratedGlossaryTermsPerBatch = 40
        TranslationProvider = [string]$effectiveProvider.Provider
        ProviderName = [string]$effectiveProviderConfig.name
        BaseUrl = [string]$effectiveProviderConfig.url
        Model = [string]$effectiveProviderConfig.model
        Temperature = [double]$effectiveProviderConfig.temperature
        ResponseFormatMode = [string]$effectiveProvider.ResponseFormat
        CompletionTokenParameter = [string]$effectiveProvider.TokenParameter
        RequestsPerMinutePerKey = [int]$effectiveProvider.Rpm
        InputTokensPerMinutePerKey = [int]$effectiveProvider.InputTpm
        DailyTokenBudgetPerKey = [int]$effectiveProvider.DailyTokens
        MaxCompletionTokens = [int]$effectiveProvider.MaxOutput
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$effectiveProvider.ReasoningEffort)) {
        $translationParameters.ReasoningEffort = [string]$effectiveProvider.ReasoningEffort
    }
    if ($rmkReference) {
        $translationParameters.ReferenceLanguageRoot = @($rmkReference)
    }
    if ($rmkWorkbook -and (Test-Path -LiteralPath $rmkWorkbook -PathType Leaf)) {
        $translationParameters.ReferenceSourceWorkbook = $rmkWorkbook
    }
    $preservedReview = $null
    if ($translationMode -eq "MissingOnly") {
        $preservedReview = New-PreserveTranslationFile
        $translationParameters.TranslateMissingOnly = $true
        if ($preservedReview) {
            $translationParameters.PreserveTranslationFile = [string]$preservedReview.Path
        }
    }
    if ($chkIncludePatches.Checked) { $translationParameters.IncludePatches = $true }
    if ($chkDryRun.Checked) { $translationParameters.DryRun = $true }

    $txtLog.Clear()
    Add-Log "번역 시작: $modRoot"
    Add-Log "원문 기준 언어: $sourceLanguage"
    if ([string]$effectiveProvider.Provider -eq "Google") {
        if ([string]$selectedProvider.Provider -eq "Google") {
            Add-Log "번역 API: Google 번역"
        } else {
            Add-Log "$($selectedProvider.Name) API 키 없음: Google 번역으로 자동 전환"
        }
    } else {
        Add-Log "번역 API: $($effectiveProviderConfig.name) · 모델 $($effectiveProviderConfig.model) · API 키 $($keys.Count)개"
    }
    if ($translationMode -eq "MissingOnly") {
        $preservedCount = if ($preservedReview) { $preservedReview.Count } else { 0 }
        Add-Log "번역 방식: 기존 번역 보존, 미번역 항목만 번역 (검수 번역 ${preservedCount}개 보존)"
    } else {
        Add-Log "번역 방식: 전체 항목의 번역 후보를 새로 생성"
    }
    if ($rmkReference) { Add-Log "RMK 기존 번역 참조: $rmkReference" }
    if ($rmkWorkbook) { Add-Log "RMK 번역 당시 원문 비교: $rmkWorkbook" }

    $argumentFile = New-TempFilePath "translation-arguments" ".json"
    $argumentPayload = [ordered]@{ version = 1; parameters = $translationParameters }
    [System.IO.File]::WriteAllText($argumentFile, ($argumentPayload | ConvertTo-Json -Depth 4), [System.Text.UTF8Encoding]::new($false))
    [void]$script:tempFiles.Add($argumentFile)

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $runnerArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $script:translationRunnerScript, "-TranslatorScript", $script:translatorScript, "-ArgumentFile", $argumentFile, "-LogFile", $script:translationLogFile)
    $psi.FileName = $script:powershellExe
    $psi.Arguments = [string]::Join(" ", @($runnerArgs | ForEach-Object { Quote-WindowsProcessArgument $_ }))
    $psi.WorkingDirectory = $scriptRoot
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $false
    $psi.RedirectStandardError = $false
    $psi.CreateNoWindow = $true
    if ([string]$effectiveProvider.Provider -ne "Google" -and $keys.Count -gt 0) {
        $psi.EnvironmentVariables["RIMWORLD_TRANSLATOR_API_KEYS"] = [string]::Join("`n", $keys)
        $psi.EnvironmentVariables["CEREBRAS_API_KEY"] = ""
    } else {
        $psi.EnvironmentVariables["RIMWORLD_TRANSLATOR_API_KEYS"] = ""
        $psi.EnvironmentVariables["CEREBRAS_API_KEY"] = ""
    }

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    $proc.EnableRaisingEvents = $true
    $script:process = $proc
    $script:activeAiTranslationMode = $translationMode
    $script:startedAt = Get-Date
    $script:processExitHandled = $false
    $script:stopRequested = $false
    $progressRun.Value = 0
    $progressRun.Maximum = 100
    $lblRunStatus.Text = "실행 준비 중"
    Set-TranslationRunning $true
    try {
        [void]$proc.Start()
        Add-Log "번역 프로세스 PID=$($proc.Id)"
    } catch {
        Add-Log "실행 실패: $($_.Exception.Message)"
        $script:activeAiTranslationMode = ""
        Set-TranslationRunning $false
    }
}

function Stop-Translation {
    if ($script:process -and -not $script:process.HasExited) {
        $script:stopRequested = $true
        $btnStop.Enabled = $false
        $lblRunStatus.Text = "중지 요청 중"
        Add-Log "사용자 요청으로 중지합니다."
        Stop-ProcessTree $script:process.Id
    }
}

function Load-SourceOnlyForSelectedMod {
    if ($script:process -and -not $script:process.HasExited) {
        [System.Windows.Forms.MessageBox]::Show("이미 작업이 실행 중입니다.", "RimWorld AI Translator") | Out-Null
        return
    }
    $selectedProject = Get-SelectedProject
    if ($selectedProject -and -not (Ensure-ProjectSourceLanguage $selectedProject)) { return }
    try {
        $modRoot = Get-ActiveProjectModRoot -Require
    } catch {
        [System.Windows.Forms.MessageBox]::Show("먼저 프로젝트를 만들거나 여세요.", "RimWorld AI Translator") | Out-Null
        return
    }
    if (-not (Test-Path -LiteralPath $script:translatorScript -PathType Leaf) -or -not (Test-Path -LiteralPath $script:translationRunnerScript -PathType Leaf)) {
        [System.Windows.Forms.MessageBox]::Show("원문 로드 실행 파일을 찾을 수 없습니다. 프로그램 패키지를 다시 확인하세요.", "RimWorld AI Translator") | Out-Null
        return
    }

    Save-ReviewWithDuplicatePrompt
    Ensure-AppDataStore
    Remove-TempFiles
    $script:lastReviewOutputPath = ""
    $script:lastProvider = "sourceonly"
    $script:translationLogFile = New-TempFilePath "source-refresh-output" ".log"
    [System.IO.File]::WriteAllText($script:translationLogFile, "", [System.Text.UTF8Encoding]::new($false))
    [void]$script:tempFiles.Add($script:translationLogFile)
    $script:translationLogOffset = 0L
    $script:translationLogPartial = ""
    $sourceLanguage = Get-SelectedProjectSourceLanguage
    $rmkTarget = Get-RmkReferenceTarget (Get-SelectedProject)
    $rmkReference = if ($rmkTarget) { [string]$rmkTarget.LanguageRoot } else { "" }
    $rmkWorkbook = if ($rmkTarget -and $rmkTarget.PSObject.Properties["WorkbookPath"]) { [string]$rmkTarget.WorkbookPath } else { "" }
    $translationParameters = [ordered]@{
        ModRoot = $modRoot
        LanguageFolderName = "Korean"
        SourceLanguageFolder = $sourceLanguage
        ReviewOnly = $true
        ReviewRoot = $script:appReviewRoot
        SourceOnly = $true
    }
    if ($chkIncludePatches.Checked) { $translationParameters.IncludePatches = $true }
    if ($rmkReference) { $translationParameters.ReferenceLanguageRoot = @($rmkReference) }
    if ($rmkWorkbook -and (Test-Path -LiteralPath $rmkWorkbook -PathType Leaf)) { $translationParameters.ReferenceSourceWorkbook = $rmkWorkbook }

    $txtLog.Clear()
    Add-Log "원문 로드 시작: $modRoot"
    Add-Log "원문 기준 언어: $sourceLanguage"
    if ($rmkReference) { Add-Log "RMK 기존 번역을 기본 번역으로 불러옵니다: $rmkReference" }
    if ($rmkWorkbook) { Add-Log "RMK 번역 당시 원문과 현재 원문을 비교합니다: $rmkWorkbook" }

    $argumentFile = New-TempFilePath "source-refresh-arguments" ".json"
    $argumentPayload = [ordered]@{ version = 1; parameters = $translationParameters }
    [System.IO.File]::WriteAllText($argumentFile, ($argumentPayload | ConvertTo-Json -Depth 4), [System.Text.UTF8Encoding]::new($false))
    [void]$script:tempFiles.Add($argumentFile)

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $runnerArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $script:translationRunnerScript, "-TranslatorScript", $script:translatorScript, "-ArgumentFile", $argumentFile, "-LogFile", $script:translationLogFile)
    $psi.FileName = $script:powershellExe
    $psi.Arguments = [string]::Join(" ", @($runnerArgs | ForEach-Object { Quote-WindowsProcessArgument $_ }))
    $psi.WorkingDirectory = $scriptRoot
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $false
    $psi.RedirectStandardError = $false
    $psi.CreateNoWindow = $true
    $psi.EnvironmentVariables["RIMWORLD_TRANSLATOR_API_KEYS"] = ""
    $psi.EnvironmentVariables["CEREBRAS_API_KEY"] = ""

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    $proc.EnableRaisingEvents = $true
    $script:process = $proc
    $script:activeAiTranslationMode = "SourceOnly"
    $script:startedAt = Get-Date
    $script:processExitHandled = $false
    $script:stopRequested = $false
    $progressRun.Value = 0
    $progressRun.Maximum = 100
    $lblRunStatus.Text = "원문 로드 중"
    Set-TranslationRunning $true
    try {
        [void]$proc.Start()
        Add-Log "원문 로드 프로세스 PID=$($proc.Id)"
    } catch {
        Add-Log "원문 로드 실행 실패: $($_.Exception.Message)"
        $script:activeAiTranslationMode = ""
        $lblRunStatus.Text = "원문 로드 실패"
        Set-TranslationRunning $false
    }
}

function Apply-ReviewedTranslations([string]$ApplyStatus = "ApprovedOnly") {
    if ($chkApplyToRmk.Checked) {
        Export-ReviewedTranslationsToRmk $ApplyStatus
        return
    }
    try {
        $modRoot = Get-ActiveProjectModRoot -Require
    } catch {
        [System.Windows.Forms.MessageBox]::Show("적용할 프로젝트를 먼저 열어주세요.", "RimWorld AI Translator") | Out-Null
        return
    }
    if (-not $script:reviewRoot -or -not (Test-Path -LiteralPath $script:reviewRoot)) {
        [System.Windows.Forms.MessageBox]::Show("적용할 검수 결과가 없습니다.", "RimWorld AI Translator") | Out-Null
        return
    }
    if (-not (Test-Path -LiteralPath $script:reviewApplyScript)) {
        [System.Windows.Forms.MessageBox]::Show("검토 적용 스크립트를 찾을 수 없습니다.`r`n$script:reviewApplyScript", "RimWorld AI Translator") | Out-Null
        return
    }
    Save-ReviewWithDuplicatePrompt
    $modeLabel = if ($ApplyStatus -eq "TranslatedAndApproved") { "번역됨 적용" } else { "검토됨 적용" }
    $eligibleCount = 0
    foreach ($row in $script:rows) {
        $status = [string](Get-Decision $row).status
        if ($status -eq "approved" -or ($ApplyStatus -eq "TranslatedAndApproved" -and $status -eq "translated")) {
            $eligibleCount++
        }
    }
    if ($eligibleCount -eq 0) {
        [System.Windows.Forms.MessageBox]::Show("현재 적용할 문자열이 없습니다.", "RimWorld AI Translator", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
        return
    }
    $applyTarget = Join-Path $modRoot "Languages\Korean"
    $confirmText = "$modeLabel 대상 $eligibleCount개를 Korean 폴더에 반영합니다.`r`n`r`n대상: $applyTarget`r`n`r`n계속할까요?"
    $confirm = [System.Windows.Forms.MessageBox]::Show(
        $confirmText,
        "번역 적용 확인",
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Warning,
        [System.Windows.Forms.MessageBoxDefaultButton]::Button2
    )
    if ($confirm -ne [System.Windows.Forms.DialogResult]::Yes) { return }
    $applyArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $script:reviewApplyScript, "-ModRoot", $modRoot, "-ReviewRoot", $script:reviewRoot, "-LanguageFolderName", "Korean", "-Overwrite", "-ApplyStatus", $ApplyStatus)
    Add-Log "$modeLabel 시작: $script:reviewRoot"
    $lblRunStatus.Text = "$modeLabel 중"
    Set-TranslationRunning $true
    try {
        $output = & $script:powershellExe @applyArgs 2>&1
        $exitCode = $LASTEXITCODE
        foreach ($line in @($output)) { Add-Log ([string]$line) }
        if ($exitCode -eq 0) {
            $lblRunStatus.Text = "$modeLabel 완료"
            Mark-ProjectApplied
        } else {
            $lblRunStatus.Text = "$modeLabel 실패"
        }
    } catch {
        Add-Log "$modeLabel 실패: $($_.Exception.Message)"
        $lblRunStatus.Text = "$modeLabel 실패"
    } finally {
        Set-TranslationRunning $false
    }
}

function Format-LocalTimeText([string]$IsoText) {
    if ([string]::IsNullOrWhiteSpace($IsoText)) { return "-" }
    try {
        $dt = [datetime]::Parse($IsoText).ToLocalTime()
        return $dt.ToString("yyyy-MM-dd HH:mm")
    } catch {
        return $IsoText
    }
}

function Get-ProjectReviewStats([object]$Project) {
    $stats = [pscustomobject]@{
        Total = 0
        Pending = 0
        Translated = 0
        Approved = 0
        Updated = 0
        Warnings = 0
        Label = "검수 없음"
    }
    if (-not $Project -or -not $Project.latestReviewRoot -or -not (Test-Path -LiteralPath $Project.latestReviewRoot -PathType Container)) {
        return $stats
    }

    try {
        $comparison = Find-ComparisonFile ([string]$Project.latestReviewRoot)
        $comparisonInfo = Get-Item -LiteralPath $comparison -ErrorAction Stop
        $decisionPath = Join-Path ([string]$Project.latestReviewRoot) "review-decisions.json"
        $decisionStamp = "missing"
        if (Test-Path -LiteralPath $decisionPath -PathType Leaf) {
            $decisionInfo = Get-Item -LiteralPath $decisionPath -ErrorAction Stop
            $decisionStamp = "$($decisionInfo.Length):$($decisionInfo.LastWriteTimeUtc.Ticks)"
        }
        $cacheKey = if ($Project.id) { [string]$Project.id } else { [System.IO.Path]::GetFullPath([string]$Project.latestReviewRoot).ToLowerInvariant() }
        $stamp = "$($comparisonInfo.FullName)|$($comparisonInfo.Length):$($comparisonInfo.LastWriteTimeUtc.Ticks)|$decisionStamp"
        if ($script:projectStatsCache.ContainsKey($cacheKey)) {
            $cached = $script:projectStatsCache[$cacheKey]
            if ([string]$cached.Stamp -eq $stamp) { return $cached.Stats }
        }

        $parsedRows = [System.IO.File]::ReadAllText($comparison, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        $rows = @($parsedRows | Where-Object { -not (Get-InternalLocalizationIdentifierReason $_) })
        $decisions = @{}
        if (Test-Path -LiteralPath $decisionPath -PathType Leaf) {
            $json = [System.IO.File]::ReadAllText($decisionPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
            foreach ($item in @($json.items)) {
                if (-not $item) { continue }
                if ($item.id) { $decisions["id:$($item.id)"] = $item }
                if ($item.key) { $decisions["key:$($item.key)"] = $item }
                if ($item.target -and $item.key) { $decisions["target:$($item.target)|key:$($item.key)"] = $item }
            }
        }

        foreach ($row in $rows) {
            $stats.Total++

            $decision = $null
            $idIdentity = if ($row.id) { "id:$($row.id)" } else { "" }
            $keyIdentity = if ($row.key) { "key:$($row.key)" } else { "" }
            if ($idIdentity -and $decisions.ContainsKey($idIdentity)) { $decision = $decisions[$idIdentity] }
            elseif ($keyIdentity -and $decisions.ContainsKey($keyIdentity)) { $decision = $decisions[$keyIdentity] }
            $status = if ($decision -and $decision.status) { [string]$decision.status } else {
                if ([string]::IsNullOrWhiteSpace((Get-DefaultTranslationForRow $row))) { "pending" } else { "translated" }
            }
            if ($status -eq "reviewed") { $status = "approved" }

            $sourceChanged = $false
            if ($decision) {
                $source = ConvertTo-FlatString $row.source
                $sourceChanged = $decision.PSObject.Properties["sourceChanged"] -and (ConvertTo-BoolValue $decision.sourceChanged)
                if (-not $sourceChanged -and $decision.PSObject.Properties["sourceText"] -and -not [string]::IsNullOrWhiteSpace([string]$decision.sourceText)) {
                    $sourceChanged = $sourceChanged -or ((ConvertTo-FlatString $decision.sourceText) -ne $source)
                } elseif (-not $sourceChanged -and $decision.PSObject.Properties["sourceHash"] -and -not [string]::IsNullOrWhiteSpace([string]$decision.sourceHash)) {
                    $sourceChanged = ([string]$decision.sourceHash) -ne (Get-TextFingerprint $source)
                }
                if ($sourceChanged) { $status = "pending" }
            }
            if ($sourceChanged) { $stats.Updated++ }

            switch ($status) {
                "approved" { $stats.Approved++ }
                "translated" { $stats.Translated++ }
                default { $stats.Pending++ }
            }
        }

        $stats.Label = "전체 $($stats.Total) / 미번역 $($stats.Pending) / 번역됨 $($stats.Translated) / 검토됨 $($stats.Approved) / 변경 $($stats.Updated)"
        $script:projectStatsCache[$cacheKey] = [pscustomobject]@{ Stamp = $stamp; Stats = $stats }
        $script:projectStatsCacheDirty = $true
    } catch {
        $stats.Label = "검수 통계 읽기 실패"
    }
    return $stats
}

function Get-ProjectActivityRows {
    $rows = New-Object "System.Collections.Generic.List[object]"
    foreach ($project in @($script:projects)) {
        foreach ($run in @($project.runs)) {
            if (-not $run) { continue }
            [void]$rows.Add([pscustomobject]@{
                Time = [string]$run.createdAt
                Project = [string]$project.name
                Kind = "번역"
                Text = "검수 작업 생성"
            })
        }
        if ($project.lastAppliedAt) {
            [void]$rows.Add([pscustomobject]@{
                Time = [string]$project.lastAppliedAt
                Project = [string]$project.name
                Kind = "적용"
                Text = "Korean 폴더 반영"
            })
        }
        if ($project.latestReviewRoot -and (Test-Path -LiteralPath $project.latestReviewRoot -PathType Container)) {
            $decisionPath = Join-Path ([string]$project.latestReviewRoot) "review-decisions.json"
            if (Test-Path -LiteralPath $decisionPath -PathType Leaf) {
                try {
                    $json = [System.IO.File]::ReadAllText($decisionPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
                    foreach ($item in @($json.items | Where-Object { $_.updatedAt } | Sort-Object updatedAt -Descending | Select-Object -First 12)) {
                        $keyText = if ($item.key) { [string]$item.key } else { [string]$item.id }
                        $activityStatus = Get-StatusText ([string]$item.status)
                        if ($item.PSObject.Properties["sourceChanged"] -and (ConvertTo-BoolValue $item.sourceChanged)) {
                            $activityStatus += " (업데이트 변경)"
                        }
                        [void]$rows.Add([pscustomObject]@{
                            Time = [string]$item.updatedAt
                            Project = [string]$project.name
                            Kind = "검수"
                            Text = "$keyText -> $activityStatus"
                        })
                    }
                } catch {
                }
            }
        }
    }
    return @($rows | Sort-Object @{ Expression = "Time"; Descending = $true } | Select-Object -First 120)
}

function Open-ProjectWorkspace([object]$Project) {
    if (-not $Project) { return }
    if (-not (Ensure-ProjectSourceLanguage $Project)) { return }
    Set-ActiveProject $Project
    if ($Project.modRoot -and (Test-Path -LiteralPath $Project.modRoot -PathType Container)) {
        Show-Workspace
        [System.Windows.Forms.Application]::DoEvents()
        if ($Project.latestReviewRoot -and (Test-Path -LiteralPath $Project.latestReviewRoot -PathType Container)) {
            try {
                $sameReview = $script:reviewRoot -and
                    ([System.IO.Path]::GetFullPath([string]$script:reviewRoot).TrimEnd("\", "/") -ieq [System.IO.Path]::GetFullPath([string]$Project.latestReviewRoot).TrimEnd("\", "/")) -and
                    $script:rows.Count -gt 0
                if ($sameReview) { return }
            } catch {
            }
            Load-ReviewRoot ([string]$Project.latestReviewRoot)
        } else {
            Load-SourceOnlyForSelectedMod
        }
        return
    }
    if ($Project.latestReviewRoot -and (Test-Path -LiteralPath $Project.latestReviewRoot -PathType Container)) {
        try {
            $sameReview = $script:reviewRoot -and
                ([System.IO.Path]::GetFullPath([string]$script:reviewRoot).TrimEnd("\", "/") -ieq [System.IO.Path]::GetFullPath([string]$Project.latestReviewRoot).TrimEnd("\", "/")) -and
                $script:rows.Count -gt 0
            if ($sameReview) { Show-Workspace; return }
        } catch {
        }
        Load-ReviewRoot ([string]$Project.latestReviewRoot)
        Show-Workspace
        return
    }
    [System.Windows.Forms.MessageBox]::Show("저장된 모드 폴더를 찾을 수 없습니다.", "RimWorld AI Translator") | Out-Null
}

function Refresh-DashboardProjects {
    if (-not $flowDashboardProjects) { return }
    $filter = if ($txtDashboardSearch) { $txtDashboardSearch.Text.Trim().ToLowerInvariant() } else { "" }
    $projectRevision = [string]::Join(";", @($script:projects | ForEach-Object { "$($_.id):$($_.updatedAt):$($_.latestReviewRoot)" }))
    $renderKey = "$filter|$($script:themeMode)|$(Get-IsWindowsDarkMode)|$($script:highContrast)|$($script:textSize)|$projectRevision"
    if (-not $script:dashboardProjectsDirty -and $script:lastDashboardRenderKey -eq $renderKey) { return }
    $projectAccent = if ($script:highContrast) {
        if (Get-IsWindowsDarkMode) { [System.Drawing.Color]::FromArgb(224, 177, 92) } else { [System.Drawing.Color]::FromArgb(119, 77, 22) }
    } else {
        if (Get-IsWindowsDarkMode) { [System.Drawing.Color]::FromArgb(190, 150, 92) } else { [System.Drawing.Color]::FromArgb(166, 124, 70) }
    }
    $deleteColor = if (Get-IsWindowsDarkMode) { [System.Drawing.Color]::FromArgb(139, 67, 62) } else { [System.Drawing.Color]::FromArgb(151, 71, 65) }
    $renderSucceeded = $false
    $flowDashboardProjects.SuspendLayout()
    try {
        $flowDashboardProjects.Controls.Clear()
        $matchingProjects = @($script:projects | Sort-Object @{ Expression = "updatedAt"; Descending = $true } | Where-Object {
            if (-not $filter) { return $true }
            $blob = @([string]$_.name, [string]$_.modRoot, [string]$_.packageId, [string]$_.workshopId, [string]$_.sourceLanguageFolder) -join "`n"
            return $blob.ToLowerInvariant().Contains($filter)
        })

        if ($matchingProjects.Count -eq 0) {
            $empty = New-Label "아직 프로젝트가 없습니다. 감지된 모드를 선택해 프로젝트를 만들거나 폴더를 직접 추가하세요." 12 12 820 34 $script:itemMuted 10
            $flowDashboardProjects.Controls.Add($empty)
            $renderSucceeded = $true
            return
        }

        foreach ($project in $matchingProjects) {
            $stats = Get-ProjectReviewStats $project
            $hasReview = $project.latestReviewRoot -and (Test-Path -LiteralPath ([string]$project.latestReviewRoot) -PathType Container)
            $card = [System.Windows.Forms.Panel]::new()
            $card.Size = [System.Drawing.Size]::new(410, 204)
            $card.Margin = [System.Windows.Forms.Padding]::new(10)
            $card.BackColor = $script:itemCardBack
            $card.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
            $card.Tag = $project
            $card.Cursor = [System.Windows.Forms.Cursors]::Hand

            $name = [string]$project.name
            $accentLine = [System.Windows.Forms.Panel]::new()
            $accentLine.SetBounds(0, 0, 4, 204)
            $accentLine.BackColor = $projectAccent
            $lblName = New-Label $name 22 18 366 26 $script:itemText 11.5 ([System.Drawing.FontStyle]::Bold)
            $lblName.AutoEllipsis = $true
            if ($toolTip) { $toolTip.SetToolTip($lblName, $name) }
            $idText = if ($project.workshopId) { "Workshop $($project.workshopId)" } elseif ($project.packageId) { [string]$project.packageId } else { Split-Path -Leaf ([string]$project.modRoot) }
            $sourceFolder = if ($project.PSObject.Properties["sourceLanguageFolder"]) { [string]$project.sourceLanguageFolder } else { "Auto" }
            $sourceText = if ($sourceFolder -eq "Auto") { "자동" } else { Get-ProjectSourceLanguageName $sourceFolder }
            $lblId = New-Label "$idText  ·  원문 $sourceText" 22 48 366 20 $script:itemMuted 8.3
            $totalText = if ($hasReview) { "전체 $($stats.Total)" } else { "원문 미로드" }
            $coverageText = if ($hasReview) { "번역 $($stats.Translated)  ·  검토 $($stats.Approved)" } else { "열어서 원문을 불러오세요" }
            $lblTotal = New-Label $totalText 22 78 132 30 $script:itemText $(if ($hasReview) { 13 } else { 11 }) ([System.Drawing.FontStyle]::Bold)
            $lblCoverage = New-Label $coverageText 160 83 228 24 $script:itemMuted 8.8
            $lblPending = New-Label ("미번역 " + $stats.Pending) 22 116 110 22 (Get-StatusColor "pending") 8.7 ([System.Drawing.FontStyle]::Bold)
            $lblUpdated = New-Label ("업데이트 변경 " + $stats.Updated) 160 116 200 22 $(if ($stats.Updated -gt 0) { Get-UpdateColor } else { $script:itemSubtle }) 8.7 ([System.Drawing.FontStyle]::Bold)
            $lblPending.Visible = [bool]$hasReview
            $lblUpdated.Visible = [bool]$hasReview

            $progressTrack = [System.Windows.Forms.Panel]::new()
            $progressTrack.SetBounds(22, 146, 366, 5)
            $progressTrack.BackColor = if (Get-IsWindowsDarkMode) { [System.Drawing.Color]::FromArgb(69, 70, 66) } else { [System.Drawing.Color]::FromArgb(220, 222, 216) }
            $progressFill = [System.Windows.Forms.Panel]::new()
            $completed = $stats.Translated + $stats.Approved
            $fillWidth = if ($stats.Total -gt 0) { [int][Math]::Round(366 * ($completed / [double]$stats.Total)) } else { 0 }
            $progressFill.SetBounds(0, 0, [Math]::Max(0, [Math]::Min(366, $fillWidth)), 5)
            $progressFill.BackColor = $projectAccent
            $progressTrack.Controls.Add($progressFill)

            $lblTime = New-Label ("최근 작업 " + (Format-LocalTimeText ([string]$project.latestReviewAt))) 22 170 196 20 $script:itemSubtle 8.1
            $btnOpen = New-Button "열기" $projectAccent
            $btnOpen.ForeColor = [System.Drawing.Color]::White
            $btnOpen.SetBounds(304, 158, 86, 36)
            $btnOpen.Tag = $project
            Set-AccessibleControl $btnOpen "$name 프로젝트 열기" "$name 모드의 번역 및 검수 작업 화면을 엽니다." 0
            $btnOpen.Add_Click({ Open-ProjectWorkspace $this.Tag })
            $btnDelete = New-Button "삭제" $deleteColor
            $btnDelete.ForeColor = [System.Drawing.Color]::White
            $btnDelete.SetBounds(226, 158, 70, 36)
            $btnDelete.Tag = $project
            Set-AccessibleControl $btnDelete "$name 프로젝트 삭제" "$name 프로젝트의 로컬 검수 기록을 삭제합니다. 원본 모드와 Korean 폴더는 보존합니다." 0
            $btnDelete.Add_Click({ Remove-TranslationProject $this.Tag })

            $card.AccessibleName = "$name 프로젝트"
            $card.AccessibleDescription = "$idText, 원문 $sourceText, $($stats.Label), 최근 검수 $(Format-LocalTimeText ([string]$project.latestReviewAt))"

            foreach ($clickTarget in @($card, $accentLine, $lblName, $lblId, $lblTotal, $lblCoverage, $lblPending, $lblUpdated, $progressTrack, $lblTime)) {
                $clickTarget.Tag = $project
                $clickTarget.Add_Click({ Open-ProjectWorkspace $this.Tag })
            }
            $card.Controls.AddRange(@($accentLine, $lblName, $lblId, $lblTotal, $lblCoverage, $lblPending, $lblUpdated, $progressTrack, $lblTime, $btnDelete, $btnOpen))
            $flowDashboardProjects.Controls.Add($card)
        }
        $renderSucceeded = $true
    } finally {
        $flowDashboardProjects.ResumeLayout()
        if ($renderSucceeded) {
            $script:dashboardProjectsDirty = $false
            $script:lastDashboardRenderKey = $renderKey
            Save-ProjectStatsCache
        }
    }
}

function Refresh-DashboardActivity {
    if (-not $lvDashboardActivity) { return }
    $lvDashboardActivity.BeginUpdate()
    try {
        $lvDashboardActivity.Items.Clear()
        foreach ($row in Get-ProjectActivityRows) {
            $item = [System.Windows.Forms.ListViewItem]::new((Format-LocalTimeText ([string]$row.Time)))
            [void]$item.SubItems.Add([string]$row.Project)
            [void]$item.SubItems.Add([string]$row.Kind)
            [void]$item.SubItems.Add([string]$row.Text)
            [void]$lvDashboardActivity.Items.Add($item)
        }
    } finally {
        $lvDashboardActivity.EndUpdate()
    }
}

function Save-CurrentApiProviderControls([switch]$Persist) {
    if ($script:syncingApiProvider -or -not $txtDashboardApiKeys) { return }
    $providerProfile = Get-ApiProviderProfile
    $config = Get-SelectedApiProviderConfig
    if (-not $providerProfile -or -not $config) { return }

    $script:apiProviderKeys[$script:selectedApiProviderId] = [string]$txtDashboardApiKeys.Text
    if ($script:selectedApiProviderId -eq "Custom" -and -not [string]::IsNullOrWhiteSpace($txtApiProviderCustomName.Text)) {
        $config.name = $txtApiProviderCustomName.Text.Trim()
    }
    $config.url = $txtApiProviderUrl.Text.Trim()
    $config.model = $cmbApiProviderModel.Text.Trim()
    $temperatureText = $cmbApiProviderTemperature.Text.Trim()
    if (-not $temperatureText -or $temperatureText -eq "모델 기본값") {
        $config.temperature = -1
    } else {
        $parsedTemperature = 0.0
        if ([double]::TryParse($temperatureText.Replace(",", "."), [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsedTemperature)) {
            $config.temperature = [Math]::Max(-1, [Math]::Min(2, $parsedTemperature))
        }
    }
    if ($txtApiKeys) { $txtApiKeys.Text = [string]$script:apiProviderKeys[$script:selectedApiProviderId] }
    if ($Persist) { Save-AppSettings }
}

function Refresh-ApiProviderButtons {
    if (-not $script:apiProviderButtons) { return }
    foreach ($providerProfile in $script:apiProviders) {
        $button = $script:apiProviderButtons[[string]$providerProfile.Id]
        if (-not $button) { continue }
        $selected = [string]$providerProfile.Id -eq $script:selectedApiProviderId
        $button.Text = if ($selected) { "●  $($providerProfile.Name)" } else { "    $($providerProfile.Name)" }
        $button.BackColor = if ($selected) { $script:accentColor } else { $script:surfaceColor }
        $button.ForeColor = if ($selected) { [System.Drawing.Color]::White } else { $script:textColor }
        $button.FlatAppearance.BorderColor = if ($selected) { $script:accentColor } else { [System.Drawing.Color]::FromArgb(96, $script:mutedColor) }
        $button.FlatAppearance.BorderSize = 1
    }
}

function Show-ApiProviderControls([string]$ProviderId = "", [switch]$SkipCurrentSave) {
    if (-not $txtDashboardApiKeys) { return }
    if (-not $SkipCurrentSave) { Save-CurrentApiProviderControls }
    if (-not $ProviderId) { $ProviderId = $script:selectedApiProviderId }
    $providerProfile = Get-ApiProviderProfile $ProviderId
    if (-not $providerProfile) { return }
    $script:selectedApiProviderId = [string]$providerProfile.Id
    $config = $script:apiProviderConfigs[$script:selectedApiProviderId]

    $script:syncingApiProvider = $true
    try {
        $lblApiProviderTitle.Text = [string]$config.name
        $lblApiProviderDescription.Text = [string]$providerProfile.Description
        $txtApiProviderCustomName.Text = [string]$config.name
        $txtDashboardApiKeys.Text = [string]$script:apiProviderKeys[$script:selectedApiProviderId]
        $txtApiProviderUrl.Text = [string]$config.url
        $cmbApiProviderModel.Items.Clear()
        foreach ($model in @($providerProfile.Models)) { [void]$cmbApiProviderModel.Items.Add([string]$model) }
        $cmbApiProviderModel.Text = [string]$config.model
        $cmbApiProviderTemperature.Text = if ([double]$config.temperature -lt 0) { "모델 기본값" } else { ([double]$config.temperature).ToString("0.##", [System.Globalization.CultureInfo]::InvariantCulture) }

        $isGoogle = [string]$providerProfile.Provider -eq "Google"
        $lblApiProviderCustomName.Visible = $false
        $txtApiProviderCustomName.Visible = $false
        foreach ($control in @($lblApiProviderKeys, $txtDashboardApiKeys, $lblApiProviderUrl, $txtApiProviderUrl, $lblApiProviderModel, $cmbApiProviderModel, $lblApiProviderTemperature, $cmbApiProviderTemperature)) {
            $control.Visible = -not $isGoogle
        }
        $lblApiProviderNotice.Text = if ($isGoogle) {
            "API 키 없이 Google 기계 번역으로 초벌 후보를 만듭니다. 용어집과 추가 프롬프트는 적용되지 않습니다."
        } else {
            "API 키가 비어 있으면 Google 번역으로 자동 전환합니다. 키는 메모리에만 유지됩니다."
        }
        if ($txtApiKeys) { $txtApiKeys.Text = [string]$script:apiProviderKeys[$script:selectedApiProviderId] }
    } finally {
        $script:syncingApiProvider = $false
    }
    Refresh-ApiProviderButtons
}

function Select-ApiProvider([string]$ProviderId) {
    if (-not (Get-ApiProviderProfile $ProviderId)) { return }
    Show-ApiProviderControls -ProviderId $ProviderId
    Save-AppSettings
}

function Resize-ApiProviderSettingsLayout {
    if (-not $pnlApiSettings -or $pnlApiSettings.ClientSize.Width -le 0) { return }
    $panelWidth = $pnlApiSettings.ClientSize.Width
    $listWidth = if ($panelWidth -lt 700) { 164 } else { 190 }
    $detailX = $listWidth + 28
    $detailWidth = [Math]::Max(360, $panelWidth - $detailX)
    $fieldWidth = [Math]::Max(300, $detailWidth - 10)

    $flowApiProviders.SetBounds(0, 58, $listWidth, 370)
    foreach ($button in $script:apiProviderButtons.Values) { $button.Width = [Math]::Max(146, $listWidth - 18) }
    $apiProviderDivider.SetBounds(($listWidth + 12), 58, 1, 370)
    $pnlApiDetail.SetBounds($detailX, 58, $detailWidth, 370)
    $lblApiProviderTitle.Width = $fieldWidth
    $lblApiProviderDescription.Width = $fieldWidth
    $txtApiProviderCustomName.Width = $fieldWidth
    $txtDashboardApiKeys.Width = $fieldWidth
    $txtApiProviderUrl.Width = $fieldWidth
    $lblApiProviderNotice.Width = $fieldWidth
    $modelWidth = [Math]::Max(190, [int]($fieldWidth * 0.65))
    $tempX = $modelWidth + 14
    $tempWidth = [Math]::Max(94, $fieldWidth - $tempX)
    $cmbApiProviderModel.Width = $modelWidth
    $lblApiProviderTemperature.SetBounds($tempX, 234, $tempWidth, 20)
    $cmbApiProviderTemperature.SetBounds($tempX, 256, $tempWidth, 30)
    $lblDashSettingsNote.SetBounds(286, 332, [Math]::Max(100, $fieldWidth - 286), 24)
}

function Resize-RmkSettingsLayout {
    if (-not $pnlRmkSettings -or $pnlRmkSettings.ClientSize.Width -le 0) { return }
    $width = $pnlRmkSettings.ClientSize.Width
    $settingsRmkDivider.Width = $width
    $buttonsWidth = (94 * 3) + (8 * 2)
    $pathWidth = [Math]::Max(260, $width - $buttonsWidth - 14)
    $txtDashboardRmkWorkspace.Width = $pathWidth
    $buttonX = $pathWidth + 14
    $btnDashboardRmkAuto.Left = $buttonX
    $btnDashboardRmkChoose.Left = $buttonX + 102
    $btnDashboardRmkOpen.Left = $buttonX + 204
    $lblDashboardRmkReference.Width = $width
    $noteX = if ($width -gt 760) { 438 } else { 0 }
    $noteY = if ($width -gt 760) { 144 } else { 174 }
    $lblDashboardRmkNote.SetBounds($noteX, $noteY, [Math]::Max(280, $width - $noteX), 34)
}

function Resize-DashboardSettingsLayout {
    if (-not $dashSettingsPage -or $dashSettingsPage.ClientSize.Width -le 0) { return }
    $width = $dashSettingsPage.ClientSize.Width
    $inner = [Math]::Max(620, $width - 56)
    $wide = $width -ge 1080
    if ($wide) {
        $apiWidth = [Math]::Min(760, [Math]::Max(660, [int]($inner * 0.68)))
        $appearanceX = 28 + $apiWidth + 28
        $appearanceWidth = [Math]::Max(260, $inner - $apiWidth - 28)
        $pnlApiSettings.SetBounds(28, 66, $apiWidth, 440)
        $pnlAppearanceSettings.SetBounds($appearanceX, 66, $appearanceWidth, 300)
        $pnlRmkSettings.SetBounds(28, 536, $inner, 190)
        $dashSettingsPage.AutoScrollMinSize = [System.Drawing.Size]::new(0, 750)
    } else {
        $pnlApiSettings.SetBounds(28, 66, $inner, 440)
        $pnlAppearanceSettings.SetBounds(28, 532, $inner, 270)
        $pnlRmkSettings.SetBounds(28, 830, $inner, 220)
        $dashSettingsPage.AutoScrollMinSize = [System.Drawing.Size]::new(0, 1080)
    }
    Resize-ApiProviderSettingsLayout
    Resize-RmkSettingsLayout
}

function Sync-DashboardSettingsFromMain {
    if (-not $txtDashboardApiKeys) { return }
    $script:syncingSettings = $true
    try {
        if ($txtApiKeys -and $txtApiKeys.Text -and -not $script:apiProviderKeys[$script:selectedApiProviderId]) {
            $script:apiProviderKeys[$script:selectedApiProviderId] = $txtApiKeys.Text
        }
        Show-ApiProviderControls -ProviderId $script:selectedApiProviderId -SkipCurrentSave
        $chkDashboardIncludePatches.Checked = $chkIncludePatches.Checked
        $chkDashboardDryRun.Checked = $chkDryRun.Checked
        $cmbDashboardTheme.SelectedIndex = switch ($script:themeMode) { "Light" { 1 } "Dark" { 2 } default { 0 } }
        $sizeIndex = $cmbDashboardTextSize.Items.IndexOf([string]$script:textSize)
        $cmbDashboardTextSize.SelectedIndex = if ($sizeIndex -ge 0) { $sizeIndex } else { 1 }
        $chkDashboardHighContrast.Checked = $script:highContrast
        $chkDashboardAutoSave.Checked = $script:autoSave
        $chkDashboardRmkUseExisting.Checked = $script:rmkUseExisting
        Update-RmkControls
    } finally {
        $script:syncingSettings = $false
    }
}

function Sync-MainSettingsFromDashboard {
    if (-not $txtApiKeys -or $script:syncingSettings) { return }
    $script:syncingSettings = $true
    try {
        Save-CurrentApiProviderControls
        $txtApiKeys.Text = [string]$script:apiProviderKeys[$script:selectedApiProviderId]
        $chkIncludePatches.Checked = $chkDashboardIncludePatches.Checked
        $chkDryRun.Checked = $chkDashboardDryRun.Checked
    } finally {
        $script:syncingSettings = $false
    }
}

function Apply-TextSize {
    $bodySize = [float]$script:textSize
    $txtSource.Font = New-Font $bodySize
    $txtTranslation.Font = New-Font ($bodySize + 0.5)
    $txtExisting.Font = New-Font ([Math]::Max(9, $bodySize - 0.5))
    $txtCandidate.Font = New-Font ([Math]::Max(9, $bodySize - 0.5))
    $txtTerms.Font = New-Font $bodySize
    $txtMemo.Font = New-Font $bodySize
    $txtWarnings.Font = New-Font $bodySize
    $txtMeta.Font = New-Font ([Math]::Max(9, $bodySize - 1))
    $script:historyTitleFont = New-Font ([Math]::Max(8.5, $bodySize - 1.5)) ([System.Drawing.FontStyle]::Bold)
    $script:historyBodyFont = New-Font ([Math]::Max(9, $bodySize - 0.5))
    $txtHistory.Font = $script:historyBodyFont
}

function Apply-DashboardPreferences {
    if ($script:syncingSettings) { return }
    $script:themeMode = switch ($cmbDashboardTheme.SelectedIndex) { 1 { "Light" } 2 { "Dark" } default { "System" } }
    if ($cmbDashboardTextSize.SelectedItem) {
        $script:textSize = [Math]::Max(9, [Math]::Min(12, [int][string]$cmbDashboardTextSize.SelectedItem))
    }
    $script:highContrast = $chkDashboardHighContrast.Checked
    $script:autoSave = $chkDashboardAutoSave.Checked
    Save-AppSettings
    Apply-TextSize
    Apply-AppTheme
    if ($script:rows.Count -gt 0) {
        Refresh-ItemList -SelectRowIndex $script:currentRowIndex
    }
    Invalidate-DashboardProjectData
    if ($dashboardPanel.Visible) { Show-Dashboard "settings" }
}

function Queue-AutoSave {
    if (-not $script:autoSave -or -not $autoSaveTimer) { return }
    $autoSaveTimer.Stop()
    $autoSaveTimer.Start()
}

function Focus-NextWorkRegion([int]$Direction = 1) {
    if ($dashboardPanel.Visible) {
        if ($dashSettingsPage.Visible) {
            $targets = @($txtDashboardApiKeys, $cmbDashboardTheme, $cmbDashboardTextSize, $chkDashboardAutoSave, $btnDashboardRmkChoose, $chkDashboardRmkUseExisting)
        } elseif ($dashActivityPage.Visible) {
            $targets = @($btnDashProjects, $lvDashboardActivity)
        } else {
            $targets = @($txtDashboardSearch, $cmbDashboardMods, $btnDashboardAddMod)
        }
    } else {
        $targets = @($txtSearch, $txtTranslation, $tabs)
    }
    $targets = @($targets | Where-Object { $_ -and $_.Visible -and $_.Enabled })
    if ($targets.Count -eq 0) { return }
    $current = -1
    for ($i = 0; $i -lt $targets.Count; $i++) {
        if ($targets[$i].ContainsFocus -or $targets[$i].Focused) { $current = $i; break }
    }
    $next = ($current + $Direction) % $targets.Count
    if ($next -lt 0) { $next += $targets.Count }
    [void]$targets[$next].Focus()
}

function Refresh-DashboardTabButtons {
    if (-not $btnDashProjects) { return }
    $inactiveBack = [System.Drawing.Color]::FromArgb(48, 53, 49)
    $inactiveFore = [System.Drawing.Color]::FromArgb(224, 229, 222)
    foreach ($button in @($btnDashProjects, $btnDashActivity, $btnDashSettings)) {
        $button.BackColor = $inactiveBack
        $button.ForeColor = $inactiveFore
        $button.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(77, 83, 77)
        $button.FlatAppearance.BorderSize = 1
    }
    $active = if ($dashSettingsPage.Visible) { $btnDashSettings } elseif ($dashActivityPage.Visible) { $btnDashActivity } else { $btnDashProjects }
    $active.BackColor = $script:accentColor
    $active.ForeColor = [System.Drawing.Color]::White
    $active.FlatAppearance.BorderColor = $script:accentColor
}

function Show-Dashboard([string]$Tab = "projects") {
    if (-not $dashboardPanel) { return }
    Save-ReviewWithDuplicatePrompt
    $top.Visible = $false
    $main.Visible = $false
    $dashboardPanel.Visible = $true
    $dashboardPanel.BringToFront()
    if (Get-Command Apply-AppTheme -ErrorAction SilentlyContinue) { Apply-AppTheme }

    $isDark = Get-IsWindowsDarkMode
    $inactiveBack = [System.Drawing.Color]::FromArgb(53, 59, 54)
    $inactiveFore = [System.Drawing.Color]::FromArgb(224, 229, 222)
    $activeBack = if ($script:highContrast) {
        if ($isDark) { [System.Drawing.Color]::FromArgb(224, 177, 92) } else { [System.Drawing.Color]::FromArgb(119, 77, 22) }
    } else {
        if ($isDark) { [System.Drawing.Color]::FromArgb(190, 150, 92) } else { [System.Drawing.Color]::FromArgb(166, 124, 70) }
    }
    foreach ($button in @($btnDashProjects, $btnDashActivity, $btnDashSettings)) {
        if ($button) {
            $button.BackColor = $inactiveBack
            $button.ForeColor = $inactiveFore
        }
    }
    $dashProjectsPage.Visible = $false
    $dashActivityPage.Visible = $false
    $dashSettingsPage.Visible = $false

    switch ($Tab) {
        "activity" {
            $dashActivityPage.Visible = $true
            $btnDashActivity.BackColor = $activeBack
            $btnDashActivity.ForeColor = [System.Drawing.Color]::White
            Refresh-DashboardActivity
        }
        "settings" {
            $dashSettingsPage.Visible = $true
            $btnDashSettings.BackColor = $activeBack
            $btnDashSettings.ForeColor = [System.Drawing.Color]::White
            Refresh-RmkRoots
            Sync-DashboardSettingsFromMain
        }
        default {
            $dashProjectsPage.Visible = $true
            $btnDashProjects.BackColor = $activeBack
            $btnDashProjects.ForeColor = [System.Drawing.Color]::White
            Refresh-DashboardProjects
        }
    }
    Refresh-DashboardTabButtons
}

function Show-Workspace {
    if ($dashboardPanel) { $dashboardPanel.Visible = $false }
    $top.Visible = $true
    $main.Visible = $true
    $main.BringToFront()
    $top.BringToFront()
    Sync-MainSettingsFromDashboard
    if (Get-Command Apply-AppTheme -ErrorAction SilentlyContinue) { Apply-AppTheme }
}

Ensure-AppDataStore
Load-AppSettings
if ($PreviewTheme -in @("System", "Light", "Dark")) { $script:themeMode = $PreviewTheme }
if ($PreviewTextSize -ge 9 -and $PreviewTextSize -le 12) { $script:textSize = $PreviewTextSize }
if ($PreviewHighContrast) { $script:highContrast = $true }

$form = [System.Windows.Forms.Form]::new()
$form.Text = "RimWorld AI Translator"
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
$form.AutoScaleMode = [System.Windows.Forms.AutoScaleMode]::None
$form.ClientSize = [System.Drawing.Size]::new(1180, 780)
$form.MinimumSize = [System.Drawing.Size]::new(900, 600)
$form.BackColor = [System.Drawing.Color]::FromArgb(18, 24, 30)
$form.Font = New-Font 9
$form.KeyPreview = $true
$form.WindowState = [System.Windows.Forms.FormWindowState]::Maximized

$toolTip = [System.Windows.Forms.ToolTip]::new()
$toolTip.AutoPopDelay = 6000
$toolTip.InitialDelay = 450
$toolTip.ReshowDelay = 100

$top = [System.Windows.Forms.Panel]::new()
$top.Dock = [System.Windows.Forms.DockStyle]::Top
$top.Height = 78
$top.BackColor = [System.Drawing.Color]::FromArgb(34, 42, 50)
$form.Controls.Add($top)

$topAccent = [System.Windows.Forms.Panel]::new()
$topAccent.SetBounds(0, 75, 1180, 3)
$topAccent.Anchor = [System.Windows.Forms.AnchorStyles]::Left -bor [System.Windows.Forms.AnchorStyles]::Right -bor [System.Windows.Forms.AnchorStyles]::Bottom

$lblProject = New-Label "RimWorld AI Translator" 18 8 420 24 ([System.Drawing.Color]::FromArgb(230, 238, 246)) 12 ([System.Drawing.FontStyle]::Bold)
$lblPath = New-Label "모드를 선택하면 이 화면에서 번역과 검수를 바로 시작합니다." 18 34 650 20 ([System.Drawing.Color]::FromArgb(148, 161, 174)) 8.5
$lblSave = New-Label "" 940 134 96 20 ([System.Drawing.Color]::FromArgb(160, 174, 188)) 8.5

$lblProjectPick = New-Label "" 18 60 90 18 ([System.Drawing.Color]::FromArgb(188, 199, 210)) 8.5 ([System.Drawing.FontStyle]::Bold)
$lblProjectPick.Visible = $false
$cmbProject = [System.Windows.Forms.ComboBox]::new()
$cmbProject.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbProject.DisplayMember = "Display"
$cmbProject.Font = New-Font 9
$cmbProject.SetBounds(18, 80, 250, 28)
$cmbProject.Visible = $false

$lblModPick = New-Label "모드" 18 60 80 18 ([System.Drawing.Color]::FromArgb(188, 199, 210)) 8.5 ([System.Drawing.FontStyle]::Bold)
$cmbModCatalog = [System.Windows.Forms.ComboBox]::new()
$cmbModCatalog.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbModCatalog.DisplayMember = "Display"
$cmbModCatalog.Font = New-Font 9
$cmbModCatalog.SetBounds(18, 80, 620, 28)

$btnRefreshMods = New-Button "새로고침" ([System.Drawing.Color]::FromArgb(72, 86, 100))
$btnRefreshMods.ForeColor = [System.Drawing.Color]::White
$btnRefreshMods.SetBounds(646, 78, 86, 30)
$btnChooseMod = New-Button "찾기" ([System.Drawing.Color]::FromArgb(72, 86, 100))
$btnChooseMod.ForeColor = [System.Drawing.Color]::White
$btnChooseMod.SetBounds(740, 78, 62, 30)

$lblApi = New-Label "API 키 (비우면 Google)" 820 12 180 18 ([System.Drawing.Color]::FromArgb(188, 199, 210)) 8.5 ([System.Drawing.FontStyle]::Bold)
$txtApiKeys = New-TextBox -Multiline
$txtApiKeys.SetBounds(820, 32, 260, 76)
$txtApiKeys.BackColor = [System.Drawing.Color]::FromArgb(26, 34, 42)
$txtApiKeys.ForeColor = [System.Drawing.Color]::FromArgb(230, 238, 246)

$chkIncludePatches = [System.Windows.Forms.CheckBox]::new()
$chkIncludePatches.Text = "Patches 포함"
$chkIncludePatches.SetBounds(1096, 52, 120, 24)
$chkIncludePatches.ForeColor = [System.Drawing.Color]::FromArgb(218, 226, 234)
$chkIncludePatches.BackColor = [System.Drawing.Color]::Transparent
$chkDryRun = [System.Windows.Forms.CheckBox]::new()
$chkDryRun.Text = "Dry run"
$chkDryRun.SetBounds(1096, 78, 90, 24)
$chkDryRun.ForeColor = [System.Drawing.Color]::FromArgb(218, 226, 234)
$chkDryRun.BackColor = [System.Drawing.Color]::Transparent

$btnTranslate = New-Button "번역 시작" ([System.Drawing.Color]::FromArgb(27, 126, 220))
$btnTranslate.ForeColor = [System.Drawing.Color]::White
$btnTranslate.SetBounds(1040, 108, 100, 34)
$btnTranslate.Enabled = $false
$btnStop = New-Button "중지" ([System.Drawing.Color]::FromArgb(124, 58, 68))
$btnStop.ForeColor = [System.Drawing.Color]::White
$btnStop.SetBounds(1148, 108, 64, 34)
$btnStop.Enabled = $false
$btnApply = New-Button "검토됨 적용" ([System.Drawing.Color]::FromArgb(35, 154, 94))
$btnApply.ForeColor = [System.Drawing.Color]::White
$btnApply.SetBounds(1220, 108, 118, 34)
$btnApply.Enabled = $false
$btnApplyTranslated = New-Button "번역됨 적용" ([System.Drawing.Color]::FromArgb(66, 149, 228))
$btnApplyTranslated.ForeColor = [System.Drawing.Color]::White
$btnApplyTranslated.SetBounds(1346, 108, 126, 34)
$btnApplyTranslated.Enabled = $false

$chkApplyToRmk = [System.Windows.Forms.CheckBox]::new()
$chkApplyToRmk.Text = "RMK에 적용"
$chkApplyToRmk.Checked = $false
$chkApplyToRmk.SetBounds(1096, 108, 104, 26)
$chkApplyToRmk.ForeColor = [System.Drawing.Color]::FromArgb(218, 226, 234)
$chkApplyToRmk.BackColor = [System.Drawing.Color]::Transparent

$btnHome = New-Button "홈" ([System.Drawing.Color]::FromArgb(72, 86, 100))
$btnHome.ForeColor = [System.Drawing.Color]::White
$btnHome.SetBounds(1096, 14, 60, 30)
$btnLoad = New-Button "원문 로드" ([System.Drawing.Color]::FromArgb(72, 86, 100))
$btnLoad.ForeColor = [System.Drawing.Color]::White
$btnLoad.SetBounds(1164, 14, 96, 30)
$btnOpenFolder = New-Button "모드 폴더" ([System.Drawing.Color]::FromArgb(72, 86, 100))
$btnOpenFolder.ForeColor = [System.Drawing.Color]::White
$btnOpenFolder.SetBounds(1268, 14, 88, 30)
$btnSave = New-Button "저장" ([System.Drawing.Color]::FromArgb(72, 86, 100))
$btnSave.ForeColor = [System.Drawing.Color]::White
$btnSave.SetBounds(1364, 14, 70, 30)

$progressRun = [System.Windows.Forms.ProgressBar]::new()
$progressRun.SetBounds(18, 140, 520, 10)
$progressRun.Minimum = 0
$progressRun.Maximum = 100
$progressRun.Value = 0
$progressRun.TabStop = $false
$progressRun.AccessibleName = "AI 번역 진행률"
$lblRunStatus = New-Label "대기 중" 552 134 380 20 ([System.Drawing.Color]::FromArgb(160, 174, 188)) 8.5

$top.Controls.AddRange(@($lblProject, $lblPath, $lblSave, $lblProjectPick, $cmbProject, $lblModPick, $cmbModCatalog, $btnRefreshMods, $btnChooseMod, $lblApi, $txtApiKeys, $chkIncludePatches, $chkDryRun, $btnTranslate, $btnStop, $chkApplyToRmk, $btnApply, $btnApplyTranslated, $btnHome, $btnLoad, $btnOpenFolder, $btnSave, $progressRun, $lblRunStatus, $topAccent))

$main = [System.Windows.Forms.SplitContainer]::new()
$main.Dock = [System.Windows.Forms.DockStyle]::Fill
$main.SplitterWidth = 2
$main.SplitterDistance = 390
$main.BackColor = [System.Drawing.Color]::FromArgb(232, 236, 240)
$main.TabStop = $false
$form.Controls.Add($main)

$rightSplit = [System.Windows.Forms.SplitContainer]::new()
$rightSplit.Dock = [System.Windows.Forms.DockStyle]::Fill
$rightSplit.SplitterWidth = 2
$rightSplit.SplitterDistance = 690
$rightSplit.BackColor = [System.Drawing.Color]::FromArgb(232, 236, 240)
$rightSplit.TabStop = $false
$main.Panel2.Controls.Add($rightSplit)

$left = $main.Panel1
$left.BackColor = [System.Drawing.Color]::FromArgb(248, 249, 250)

$lblSearchCrumb = New-Label "모드`r`n전체 문자열  ·  모든 상태" 16 12 352 62 ([System.Drawing.Color]::FromArgb(36, 45, 54)) 10.5 ([System.Drawing.FontStyle]::Bold)

$cmbSearchField = [System.Windows.Forms.ComboBox]::new()
$cmbSearchField.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbSearchField.Font = New-Font 9
$cmbSearchField.SetBounds(16, 66, 92, 30)
[void]$cmbSearchField.Items.AddRange(@("텍스트/키", "텍스트", "키", "Def Class", "Node"))
$cmbSearchField.DropDownWidth = 128
$cmbSearchField.SelectedIndex = 0

$txtSearch = New-TextBox
$txtSearch.SetBounds(108, 66, 178, 30)
$txtSearch.Text = ""
$cmbStatus = [System.Windows.Forms.ComboBox]::new()
$cmbStatus.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbStatus.Font = New-Font 9
$cmbStatus.SetBounds(292, 66, 76, 30)
$cmbStatus.DropDownWidth = 168
[void]$cmbStatus.Items.AddRange(@("전체", "미번역", "번역됨", "검토됨", "업데이트로 변경됨", "RMK 가져옴", "내 번역", "주의", "후보 있음", "기존 있음"))
$cmbStatus.SelectedIndex = 0

$cmbSort = [System.Windows.Forms.ComboBox]::new()
$cmbSort.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbSort.Font = New-Font 8.5
$cmbSort.SetBounds(16, 174, 352, 30)
$cmbSort.DropDownWidth = 190
[void]$cmbSort.Items.AddRange(@("기본 순서", "내 번역 최신순", "내 번역 오래된순"))
$cmbSort.SelectedIndex = 0

$statusFilterBar = [System.Windows.Forms.FlowLayoutPanel]::new()
$statusFilterBar.FlowDirection = [System.Windows.Forms.FlowDirection]::LeftToRight
$statusFilterBar.WrapContents = $false
$statusFilterBar.AutoScroll = $false
$statusFilterBar.Margin = [System.Windows.Forms.Padding]::new(0)
$statusFilterButtons = New-Object "System.Collections.Generic.List[System.Windows.Forms.Button]"
foreach ($spec in @(
    [pscustomobject]@{ Text = "전체"; Status = "전체"; Width = 48 },
    [pscustomobject]@{ Text = "미번역"; Status = "미번역"; Width = 60 },
    [pscustomobject]@{ Text = "번역됨"; Status = "번역됨"; Width = 60 },
    [pscustomobject]@{ Text = "검토됨"; Status = "검토됨"; Width = 60 },
    [pscustomobject]@{ Text = "변경됨"; Status = "업데이트로 변경됨"; Width = 60 }
)) {
    $filterButton = New-Button $spec.Text ([System.Drawing.Color]::White)
    $filterButton.Tag = $spec.Status
    $filterButton.Width = $spec.Width
    $filterButton.Height = 30
    $filterButton.Margin = [System.Windows.Forms.Padding]::new(0, 0, 4, 0)
    $filterButton.Font = New-Font 8 ([System.Drawing.FontStyle]::Bold)
    [void]$statusFilterButtons.Add($filterButton)
    [void]$statusFilterBar.Controls.Add($filterButton)
}

$lblProjectStats = New-Label "전체 0" 16 102 350 42 ([System.Drawing.Color]::FromArgb(53, 63, 72)) 8.5 ([System.Drawing.FontStyle]::Bold)
$progressReview = [System.Windows.Forms.ProgressBar]::new()
$progressReview.SetBounds(16, 128, 260, 14)
$progressReview.Minimum = 0
$progressReview.Maximum = 1
$progressReview.Value = 0
$progressReview.TabStop = $false
$progressReview.AccessibleName = "검토 진행률"
$lblProgress = New-Label "검토 진행률 0%" 282 124 100 20 ([System.Drawing.Color]::FromArgb(80, 88, 96)) 8

$lvFiles = [System.Windows.Forms.ListView]::new()
$lvFiles.SetBounds(16, 152, 352, 120)
$lvFiles.View = [System.Windows.Forms.View]::Details
$lvFiles.FullRowSelect = $true
$lvFiles.HideSelection = $false
$lvFiles.MultiSelect = $false
$lvFiles.Font = New-Font 8.5
[void]$lvFiles.Columns.Add("파일", 230)
[void]$lvFiles.Columns.Add("검토됨", 70)
[void]$lvFiles.Columns.Add("주의", 46)

$lvItems = [System.Windows.Forms.ListView]::new()
$lvItems.SetBounds(16, 284, 352, 436)
$lvItems.Anchor = [System.Windows.Forms.AnchorStyles]::Top -bor [System.Windows.Forms.AnchorStyles]::Bottom -bor [System.Windows.Forms.AnchorStyles]::Left -bor [System.Windows.Forms.AnchorStyles]::Right
$lvItems.View = [System.Windows.Forms.View]::Details
$lvItems.FullRowSelect = $true
$lvItems.HideSelection = $false
$lvItems.MultiSelect = $false
$lvItems.Font = New-Font 8.5
[void]$lvItems.Columns.Add("상태", 58)
[void]$lvItems.Columns.Add("키", 128)
[void]$lvItems.Columns.Add("원문", 126)
[void]$lvItems.Columns.Add("번역", 126)
$lvItems.Visible = $false

$flowItems = [System.Windows.Forms.ListBox]::new()
$flowItems.SetBounds(16, 284, 352, 436)
$flowItems.Anchor = [System.Windows.Forms.AnchorStyles]::Top -bor [System.Windows.Forms.AnchorStyles]::Bottom -bor [System.Windows.Forms.AnchorStyles]::Left -bor [System.Windows.Forms.AnchorStyles]::Right
$flowItems.DrawMode = [System.Windows.Forms.DrawMode]::OwnerDrawFixed
$flowItems.ItemHeight = 94
$flowItems.IntegralHeight = $false
$flowItems.BorderStyle = [System.Windows.Forms.BorderStyle]::None
$flowItems.SelectionMode = [System.Windows.Forms.SelectionMode]::One
$flowItems.DisplayMember = "Value"
$flowItems.HorizontalScrollbar = $false
$flowItems.Font = New-Font 9
$flowItems.AccessibleName = "검색된 문자열 목록"
$flowItems.AccessibleDescription = "필터와 검색 조건에 맞는 번역 문자열 전체 목록입니다. 위아래 화살표로 이동할 수 있습니다."
$flowItems.Add_DrawItem({
    param($listControl, $e)
    if ($e.Index -lt 0 -or $e.Index -ge $listControl.Items.Count) { return }
    $item = $listControl.Items[$e.Index]
    $rowIndex = [int]$item.Key
    if ($rowIndex -lt 0 -or $rowIndex -ge $script:rows.Count) { return }

    $row = $script:rows[$rowIndex]
    $decision = Get-Decision $row
    $preview = Get-ItemPreview $row
    $originText = Get-TranslationOriginShortText ([string]$decision.translationOrigin)
    $isUpdated = ConvertTo-BoolValue $decision.sourceChanged
    $statusText = if ($isUpdated) { "변경됨" } else { Get-StatusText $decision.status }
    $statusColor = if ($isUpdated) { Get-UpdateColor } else { Get-StatusColor $decision.status }
    $warnings = @(Get-RowWarnings -Row $row -Translation (ConvertTo-FlatString $decision.text))
    $translationText = if ([string]::IsNullOrWhiteSpace($preview[1])) { "번역 대기" } else { $preview[1] }
    if (-not [string]::IsNullOrWhiteSpace($preview[1]) -and $originText) { $translationText = "$originText  ·  $translationText" }
    $translationColor = $script:itemMuted
    if ($warnings.Count -gt 0) {
        $translationText = "주의 · $translationText"
        $translationColor = if (Get-IsWindowsDarkMode) { [System.Drawing.Color]::FromArgb(238, 183, 92) } else { [System.Drawing.Color]::FromArgb(145, 91, 16) }
    }
    $defContext = Get-RowDefContext $row
    $keyText = "$($defContext.DefClass)  ·  $($defContext.Node)"
    $selected = $e.Index -eq $listControl.SelectedIndex
    $cardBack = if ($selected) { $script:itemCardSelected } else { $script:itemCardBack }
    $bounds = $e.Bounds
    $cardHeight = [Math]::Max(1, $bounds.Height - 6)
    $cardRect = [System.Drawing.Rectangle]::new($bounds.X, $bounds.Y, [Math]::Max(1, $bounds.Width - 1), $cardHeight)
    $backgroundBrush = [System.Drawing.SolidBrush]::new($listControl.BackColor)
    $cardBrush = [System.Drawing.SolidBrush]::new($cardBack)
    $stripeBrush = [System.Drawing.SolidBrush]::new($statusColor)
    try {
        $e.Graphics.FillRectangle($backgroundBrush, $bounds)
        $e.Graphics.FillRectangle($cardBrush, $cardRect)
        $e.Graphics.FillRectangle($stripeBrush, [System.Drawing.Rectangle]::new($cardRect.X, $cardRect.Y, 3, $cardRect.Height))
    } finally {
        $stripeBrush.Dispose()
        $cardBrush.Dispose()
        $backgroundBrush.Dispose()
    }

    $textDelta = [Math]::Max(-1, [Math]::Min(2, $script:textSize - 10))
    $statusWidth = if ($isUpdated) { 70 } else { 60 }
    $sourceRect = [System.Drawing.Rectangle]::new($cardRect.X + 18, $cardRect.Y + 6, [Math]::Max(30, $cardRect.Width - $statusWidth - 42), 24 + $textDelta)
    $statusRect = [System.Drawing.Rectangle]::new($cardRect.Right - $statusWidth - 12, $cardRect.Y + 6, $statusWidth, 24)
    $translationRect = [System.Drawing.Rectangle]::new($cardRect.X + 18, $cardRect.Y + 31 + $textDelta, [Math]::Max(30, $cardRect.Width - 34), 24 + $textDelta)
    $keyRect = [System.Drawing.Rectangle]::new($cardRect.X + 18, $cardRect.Y + 58 + ($textDelta * 2), [Math]::Max(30, $cardRect.Width - 34), 20)
    $singleLine = [System.Windows.Forms.TextFormatFlags]::NoPrefix -bor [System.Windows.Forms.TextFormatFlags]::EndEllipsis -bor [System.Windows.Forms.TextFormatFlags]::SingleLine -bor [System.Windows.Forms.TextFormatFlags]::VerticalCenter
    $rightAligned = $singleLine -bor [System.Windows.Forms.TextFormatFlags]::Right
    [System.Windows.Forms.TextRenderer]::DrawText($e.Graphics, $preview[0], (New-Font ([Math]::Max(8.5, $script:textSize - 0.5))), $sourceRect, $script:itemText, $singleLine)
    [System.Windows.Forms.TextRenderer]::DrawText($e.Graphics, $statusText, (New-Font 8 ([System.Drawing.FontStyle]::Bold)), $statusRect, $statusColor, $rightAligned)
    [System.Windows.Forms.TextRenderer]::DrawText($e.Graphics, $translationText, (New-Font ([Math]::Max(8, $script:textSize - 1.5))), $translationRect, $translationColor, $singleLine)
    [System.Windows.Forms.TextRenderer]::DrawText($e.Graphics, $keyText, (New-Font 7.8), $keyRect, $script:itemSubtle, $singleLine)
})
$flowItems.Add_SelectedIndexChanged({
    if ($script:syncingItemSelection -or $script:loading -or $flowItems.SelectedIndex -lt 0) { return }
    $selectedItem = $flowItems.SelectedItem
    if ($selectedItem) { Select-RowIndex ([int]$selectedItem.Key) }
})

$left.Controls.AddRange(@($lblSearchCrumb, $cmbSearchField, $txtSearch, $cmbStatus, $statusFilterBar, $cmbSort, $lblProjectStats, $progressReview, $lblProgress, $lvFiles, $lvItems, $flowItems))

$center = $rightSplit.Panel1
$center.BackColor = [System.Drawing.Color]::White
$center.AutoScroll = $true

$lblCurrent = New-Label "항목 없음" 18 14 520 24 ([System.Drawing.Color]::FromArgb(36, 45, 54)) 11 ([System.Drawing.FontStyle]::Bold)
$lblUpdateBadge = New-Label "업데이트로 변경됨" 520 14 150 24 ([System.Drawing.Color]::FromArgb(174, 105, 24)) 8.5 ([System.Drawing.FontStyle]::Bold)
$lblUpdateBadge.TextAlign = [System.Drawing.ContentAlignment]::MiddleRight
$lblUpdateBadge.Visible = $false
$lblSourceTitle = New-Label "원문" 18 42 120 20 ([System.Drawing.Color]::FromArgb(52, 61, 70)) 9 ([System.Drawing.FontStyle]::Bold)
$pnlSourceFrame = [System.Windows.Forms.Panel]::new()
$pnlSourceFrame.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
$txtSource = New-TextBox -Multiline
$txtSource.ReadOnly = $true
$txtSource.Font = New-Font 10
$txtSource.BackColor = [System.Drawing.Color]::FromArgb(245, 247, 249)
$txtSource.BorderStyle = [System.Windows.Forms.BorderStyle]::None
$pnlSourceFrame.Controls.Add($txtSource)

$lblTranslationTitle = New-Label "번역" 18 150 120 20 ([System.Drawing.Color]::FromArgb(52, 61, 70)) 9 ([System.Drawing.FontStyle]::Bold)
$pnlTranslationFrame = [System.Windows.Forms.Panel]::new()
$pnlTranslationFrame.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
$txtTranslation = New-TextBox -Multiline
$txtTranslation.Font = New-Font 10.5
$txtTranslation.BorderStyle = [System.Windows.Forms.BorderStyle]::None
$translationAccent = [System.Windows.Forms.Panel]::new()
$translationAccent.Height = 3
$pnlTranslationFrame.Controls.AddRange(@($txtTranslation, $translationAccent))

$txtMeta = New-TextBox -Multiline
$txtMeta.SetBounds(18, 296, 640, 92)
$txtMeta.ReadOnly = $true
$txtMeta.BackColor = [System.Drawing.Color]::FromArgb(248, 249, 250)

$btnPrev = New-Button "‹" ([System.Drawing.Color]::FromArgb(238, 240, 242))
$btnNext = New-Button "›" ([System.Drawing.Color]::FromArgb(238, 240, 242))
$btnUseCandidate = New-Button "AI 후보" ([System.Drawing.Color]::FromArgb(230, 238, 248))
$btnUseExisting = New-Button "기존" ([System.Drawing.Color]::FromArgb(230, 238, 248))
$btnUseSource = New-Button "복사" ([System.Drawing.Color]::FromArgb(238, 238, 238))
$btnResetEdit = New-Button "되돌리기" ([System.Drawing.Color]::FromArgb(238, 238, 238))
$btnPending = New-Button "미번역" ([System.Drawing.Color]::FromArgb(236, 240, 245))
$btnTranslated = New-Button "번역됨" ([System.Drawing.Color]::FromArgb(220, 235, 252))
$btnApprove = New-Button "검토 완료" ([System.Drawing.Color]::FromArgb(218, 242, 226))
$btnApproveNext = New-Button "완료 후 다음" ([System.Drawing.Color]::FromArgb(51, 174, 111))
$btnApproveNext.ForeColor = [System.Drawing.Color]::White
$btnApproveAll = New-Button "전체 검토" ([System.Drawing.Color]::FromArgb(236, 229, 216))

$editorDivider = [System.Windows.Forms.Panel]::new()
$lblReferenceTitle = New-Label "참고 번역" 18 430 120 20 ([System.Drawing.Color]::FromArgb(52, 61, 70)) 8.5 ([System.Drawing.FontStyle]::Bold)

$buttons = @($btnPrev, $btnNext, $btnUseCandidate, $btnUseExisting, $btnUseSource, $btnResetEdit, $btnPending, $btnTranslated, $btnApprove, $btnApproveNext, $btnApproveAll)
$x = 18
foreach ($button in $buttons) {
    $w = switch ($button.Text) {
        "‹" { 40 }
        "›" { 40 }
        "되돌리기" { 82 }
        "완료 후 다음" { 112 }
        "AI 후보" { 70 }
        "기존" { 58 }
        "복사" { 58 }
        default { 74 }
    }
    $button.SetBounds($x, 388, $w, 34)
    $x += $w + 8
}

$lblExisting = New-Label "기존 번역" 18 440 120 20 ([System.Drawing.Color]::FromArgb(52, 61, 70)) 9 ([System.Drawing.FontStyle]::Bold)
$txtExisting = New-TextBox -Multiline
$txtExisting.SetBounds(18, 464, 310, 94)
$txtExisting.ReadOnly = $true
$txtExisting.Font = New-Font 9.5
$txtExisting.BackColor = [System.Drawing.Color]::FromArgb(248, 249, 250)
$lblCandidate = New-Label "번역 후보" 348 440 120 20 ([System.Drawing.Color]::FromArgb(52, 61, 70)) 9 ([System.Drawing.FontStyle]::Bold)
$txtCandidate = New-TextBox -Multiline
$txtCandidate.SetBounds(348, 464, 310, 94)
$txtCandidate.ReadOnly = $true
$txtCandidate.Font = New-Font 9.5
$txtCandidate.BackColor = [System.Drawing.Color]::FromArgb(248, 249, 250)

$center.Controls.AddRange(@($lblCurrent, $lblUpdateBadge, $lblSourceTitle, $pnlSourceFrame, $lblTranslationTitle, $pnlTranslationFrame, $txtMeta, $editorDivider, $btnPrev, $btnNext, $btnUseCandidate, $btnUseExisting, $btnUseSource, $btnResetEdit, $btnPending, $btnTranslated, $btnApprove, $btnApproveNext, $btnApproveAll, $lblReferenceTitle, $lblExisting, $txtExisting, $lblCandidate, $txtCandidate))

function Resize-ReviewEditorLayout {
    if (-not $center -or $center.ClientSize.Width -le 0) { return }
    $pad = if ($center.ClientSize.Width -lt 520) { 18 } else { 24 }
    $contentWidth = [Math]::Max(300, $center.ClientSize.Width - ($pad * 2))
    $contentHeight = [Math]::Max(420, $center.ClientSize.Height)
    $ultraCompact = $center.ClientSize.Height -lt 500
    $veryCompact = $contentHeight -lt 660
    $compact = $contentHeight -lt 760
    $sourceHeight = if ($ultraCompact) { 52 } elseif ($veryCompact) { 72 } elseif ($compact) { 92 } else { 118 }
    $translationHeight = if ($ultraCompact) { 72 } elseif ($veryCompact) { 112 } elseif ($compact) { 148 } else { 190 }
    $metaHeight = if ($ultraCompact) { 46 } elseif ($veryCompact) { 82 } else { 92 }

    $lblCurrent.SetBounds($pad, 18, [Math]::Max(180, $contentWidth - 178), 28)
    $lblUpdateBadge.SetBounds(($pad + $contentWidth - 168), 18, 168, 26)
    $lblSourceTitle.SetBounds($pad, 56, $contentWidth, 20)
    $pnlSourceFrame.SetBounds($pad, 80, $contentWidth, $sourceHeight)
    $txtSource.SetBounds(11, 9, [Math]::Max(120, $contentWidth - 24), [Math]::Max(38, $sourceHeight - 18))

    $translationLabelY = 80 + $sourceHeight + $(if ($ultraCompact) { 8 } else { 18 })
    $translationBoxY = $translationLabelY + $(if ($ultraCompact) { 20 } else { 24 })
    $lblTranslationTitle.SetBounds($pad, $translationLabelY, $contentWidth, 20)
    $pnlTranslationFrame.SetBounds($pad, $translationBoxY, $contentWidth, $translationHeight)
    $txtTranslation.SetBounds(11, 9, [Math]::Max(120, $contentWidth - 24), [Math]::Max(42, $translationHeight - 20))
    $translationAccent.SetBounds(0, [Math]::Max(0, $translationHeight - 3), $contentWidth, 3)

    $metaY = $translationBoxY + $translationHeight + $(if ($ultraCompact) { 8 } else { 14 })
    $txtMeta.SetBounds($pad, $metaY, $contentWidth, $metaHeight)
    $dividerY = $metaY + $metaHeight + $(if ($ultraCompact) { 4 } else { 8 })
    $editorDivider.SetBounds($pad, $dividerY, $contentWidth, 1)
    $toolbarY = $dividerY + $(if ($ultraCompact) { 8 } else { 12 })

    $utilityButtons = @($btnPrev, $btnNext, $btnUseCandidate, $btnUseExisting, $btnUseSource, $btnResetEdit)
    $utilityWidths = @(38, 38, 72, 62, 58, 72)
    $statusButtons = @($btnPending, $btnTranslated, $btnApprove, $btnApproveNext, $btnApproveAll)
    $statusWidths = @(72, 76, 88, 108, 88)
    $gap = 7
    $utilityTotal = ($utilityWidths | Measure-Object -Sum).Sum + ($gap * ($utilityWidths.Count - 1))
    $statusTotal = ($statusWidths | Measure-Object -Sum).Sum + ($gap * ($statusWidths.Count - 1))
    $singleRow = $contentWidth -ge ($utilityTotal + $statusTotal + 22)

    $x = $pad
    for ($i = 0; $i -lt $utilityButtons.Count; $i++) {
        $utilityButtons[$i].SetBounds($x, $toolbarY, $utilityWidths[$i], 36)
        $x += $utilityWidths[$i] + $gap
    }

    if ($singleRow) {
        $x = $pad + $contentWidth - $statusTotal
        $statusY = $toolbarY
        $toolbarBottom = $toolbarY + 36
    } else {
        $x = $pad
        $statusY = $toolbarY + $(if ($ultraCompact) { 40 } else { 44 })
        $toolbarBottom = $statusY + 36
    }
    for ($i = 0; $i -lt $statusButtons.Count; $i++) {
        $statusButtons[$i].SetBounds($x, $statusY, $statusWidths[$i], 36)
        $x += $statusWidths[$i] + $gap
    }

    $referenceTitleY = $toolbarBottom + 17
    $suggestionLabelY = $referenceTitleY + 25
    $lblReferenceTitle.SetBounds($pad, $referenceTitleY, $contentWidth, 20)
    $halfWidth = [Math]::Max(140, [int](($contentWidth - 14) / 2))
    $bottomBoxY = $suggestionLabelY + 22
    $bottomHeight = [Math]::Max(76, $center.ClientSize.Height - $bottomBoxY - 18)
    $lblExisting.SetBounds($pad, $suggestionLabelY, $halfWidth, 18)
    $txtExisting.SetBounds($pad, $bottomBoxY, $halfWidth, $bottomHeight)
    $candidateX = $pad + $halfWidth + 14
    $lblCandidate.SetBounds($candidateX, $suggestionLabelY, $halfWidth, 18)
    $txtCandidate.SetBounds($candidateX, $bottomBoxY, $halfWidth, $bottomHeight)
    $requiredHeight = $bottomBoxY + [Math]::Max(76, $bottomHeight) + 18
    $center.AutoScrollMinSize = [System.Drawing.Size]::new(0, $requiredHeight)
}

$center.Add_Resize({ Resize-ReviewEditorLayout })
Resize-ReviewEditorLayout

$side = $rightSplit.Panel2
$side.BackColor = [System.Drawing.Color]::White

$tabs = [System.Windows.Forms.TabControl]::new()
$tabs.Dock = [System.Windows.Forms.DockStyle]::Fill
$tabs.Font = New-Font 8.5 ([System.Drawing.FontStyle]::Bold)
$tabs.DrawMode = [System.Windows.Forms.TabDrawMode]::OwnerDrawFixed
$tabs.SizeMode = [System.Windows.Forms.TabSizeMode]::Fixed
$tabs.ItemSize = [System.Drawing.Size]::new(44, 38)
$tabs.Padding = [System.Drawing.Point]::new(2, 3)
$tabs.Multiline = $false
$side.Controls.Add($tabs)

$tabHistory = [System.Windows.Forms.TabPage]::new()
$tabHistory.Text = "역사"
$tabTerms = [System.Windows.Forms.TabPage]::new()
$tabTerms.Text = "용어"
$tabMemo = [System.Windows.Forms.TabPage]::new()
$tabMemo.Text = "메모"
$tabRmk = [System.Windows.Forms.TabPage]::new()
$tabRmk.Text = "RMK"
$tabIssues = [System.Windows.Forms.TabPage]::new()
$tabIssues.Text = "문제"
$tabLog = [System.Windows.Forms.TabPage]::new()
$tabLog.Text = "로그"
[void]$tabs.TabPages.AddRange(@($tabHistory, $tabTerms, $tabMemo, $tabRmk, $tabIssues, $tabLog))

function Resize-SideTabs {
    if (-not $tabs -or $tabs.TabPages.Count -le 0 -or $tabs.ClientSize.Width -le 0) { return }
    $availableWidth = [Math]::Max(220, $tabs.ClientSize.Width - 100)
    $itemWidth = [Math]::Max(44, [int][Math]::Floor($availableWidth / $tabs.TabPages.Count))
    $tabs.ItemSize = [System.Drawing.Size]::new($itemWidth, 38)
}

$script:sideTabResizePending = $false
function Queue-SideTabResize {
    if (-not $side -or -not $side.IsHandleCreated) {
        Resize-SideTabs
        return
    }
    if ($script:sideTabResizePending) { return }
    $script:sideTabResizePending = $true
    try {
        [void]$side.BeginInvoke([System.Windows.Forms.MethodInvoker]{
            $script:sideTabResizePending = $false
            Resize-SideTabs
        })
    } catch {
        $script:sideTabResizePending = $false
    }
}

$side.Add_Resize({ Queue-SideTabResize })
Resize-SideTabs
$tabs.Add_DrawItem({
    $bounds = $_.Bounds
    $selected = $_.Index -eq $this.SelectedIndex
    $fore = if ($selected) { $script:tabActive } else { $script:tabText }
    $brush = [System.Drawing.SolidBrush]::new($script:tabBack)
    try {
        $_.Graphics.FillRectangle($brush, $bounds)
        if ($selected) {
            $accentBrush = [System.Drawing.SolidBrush]::new($script:tabActive)
            try {
                $_.Graphics.FillRectangle($accentBrush, $bounds.X + 8, $bounds.Bottom - 3, [Math]::Max(8, $bounds.Width - 16), 3)
            } finally {
                $accentBrush.Dispose()
            }
        }
        [System.Windows.Forms.TextRenderer]::DrawText(
            $_.Graphics,
            $this.TabPages[$_.Index].Text,
            $this.Font,
            $bounds,
            $fore,
            ([System.Windows.Forms.TextFormatFlags]::HorizontalCenter -bor [System.Windows.Forms.TextFormatFlags]::VerticalCenter -bor [System.Windows.Forms.TextFormatFlags]::SingleLine)
        )
    } finally {
        $brush.Dispose()
    }
})

$txtHistory = [System.Windows.Forms.RichTextBox]::new()
$txtHistory.Dock = [System.Windows.Forms.DockStyle]::Fill
$txtHistory.ReadOnly = $true
$txtHistory.DetectUrls = $false
$txtHistory.Font = New-Font 9.5
$txtHistory.BackColor = [System.Drawing.Color]::FromArgb(248, 249, 250)
$script:historyTitleFont = New-Font 8.5 ([System.Drawing.FontStyle]::Bold)
$script:historyBodyFont = New-Font 9.5
$tabHistory.Controls.Add($txtHistory)

$txtTerms = New-TextBox -Multiline
$txtTerms.Dock = [System.Windows.Forms.DockStyle]::Fill
$txtTerms.ReadOnly = $true
$txtTerms.BackColor = [System.Drawing.Color]::FromArgb(248, 249, 250)
$tabTerms.Controls.Add($txtTerms)

$txtMemo = New-TextBox -Multiline
$txtMemo.Dock = [System.Windows.Forms.DockStyle]::Fill
$txtMemo.BackColor = [System.Drawing.Color]::White
$tabMemo.Controls.Add($txtMemo)

$lblRmkStatus = New-Label "RMK 상태 확인 중" 12 12 300 82 ([System.Drawing.Color]::FromArgb(52, 61, 70)) 9 ([System.Drawing.FontStyle]::Bold)
$lblRmkStatus.AutoEllipsis = $true
$btnRmkRefresh = New-Button "상태 갱신" ([System.Drawing.Color]::FromArgb(238, 241, 244))
$btnRmkOpen = New-Button "폴더 열기" ([System.Drawing.Color]::FromArgb(238, 241, 244))
$btnRmkBuild = New-Button "LoadFolders 빌드" ([System.Drawing.Color]::FromArgb(166, 124, 70))
$btnRmkBuild.ForeColor = [System.Drawing.Color]::White
$txtRmkDetails = New-TextBox -Multiline
$txtRmkDetails.ReadOnly = $true
$txtRmkDetails.BackColor = [System.Drawing.Color]::FromArgb(248, 249, 250)
$txtRmkDetails.WordWrap = $true
$tabRmk.Controls.AddRange(@($lblRmkStatus, $btnRmkRefresh, $btnRmkOpen, $btnRmkBuild, $txtRmkDetails))

function Resize-RmkTab {
    if (-not $tabRmk -or $tabRmk.ClientSize.Width -le 0) { return }
    $padding = 12
    $gap = 8
    $width = [Math]::Max(220, $tabRmk.ClientSize.Width - ($padding * 2))
    $half = [Math]::Max(96, [int](($width - $gap) / 2))
    $lblRmkStatus.SetBounds($padding, 12, $width, 84)
    $btnRmkRefresh.SetBounds($padding, 104, $half, 34)
    $btnRmkOpen.SetBounds(($padding + $half + $gap), 104, $half, 34)
    $btnRmkBuild.SetBounds($padding, 146, $width, 36)
    $txtRmkDetails.SetBounds($padding, 194, $width, [Math]::Max(120, $tabRmk.ClientSize.Height - 206))
}

$tabRmk.Add_Resize({ Resize-RmkTab })
Resize-RmkTab

$txtWarnings = New-TextBox -Multiline
$txtWarnings.Dock = [System.Windows.Forms.DockStyle]::Fill
$txtWarnings.ReadOnly = $true
$txtWarnings.BackColor = [System.Drawing.Color]::FromArgb(248, 249, 250)
$tabIssues.Controls.Add($txtWarnings)

$txtLog = New-TextBox -Multiline
$txtLog.Dock = [System.Windows.Forms.DockStyle]::Fill
$txtLog.ReadOnly = $true
$txtLog.Font = [System.Drawing.Font]::new("Consolas", 8.5)
$txtLog.BackColor = [System.Drawing.Color]::FromArgb(17, 23, 29)
$txtLog.ForeColor = [System.Drawing.Color]::FromArgb(214, 224, 234)
$tabLog.Controls.Add($txtLog)

foreach ($panel in @($left, $center, $side, $main.Panel1, $main.Panel2, $rightSplit.Panel1, $rightSplit.Panel2)) {
    $panel.BackColor = [System.Drawing.Color]::FromArgb(24, 31, 38)
}
$main.BackColor = [System.Drawing.Color]::FromArgb(42, 51, 60)
$rightSplit.BackColor = [System.Drawing.Color]::FromArgb(42, 51, 60)

foreach ($list in @($lvFiles, $lvItems)) {
    $list.BackColor = [System.Drawing.Color]::FromArgb(30, 38, 46)
    $list.ForeColor = [System.Drawing.Color]::FromArgb(222, 231, 240)
    $list.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
}

foreach ($box in @($txtSearch, $txtSource, $txtTranslation, $txtMeta, $txtExisting, $txtCandidate, $txtHistory, $txtTerms, $txtMemo, $txtRmkDetails, $txtWarnings)) {
    $box.BackColor = [System.Drawing.Color]::FromArgb(30, 38, 46)
    $box.ForeColor = [System.Drawing.Color]::FromArgb(226, 235, 244)
}
$txtSource.BackColor = [System.Drawing.Color]::FromArgb(37, 46, 55)
$txtTranslation.BackColor = [System.Drawing.Color]::FromArgb(246, 248, 250)
$txtTranslation.ForeColor = [System.Drawing.Color]::FromArgb(18, 24, 30)

foreach ($label in @($lblProjectStats, $lblProgress, $lblCurrent, $lblExisting, $lblCandidate)) {
    $label.ForeColor = [System.Drawing.Color]::FromArgb(214, 224, 234)
}

foreach ($page in @($tabHistory, $tabTerms, $tabMemo, $tabRmk, $tabIssues, $tabLog)) {
    $page.BackColor = [System.Drawing.Color]::FromArgb(24, 31, 38)
}

$dashboardPanel = [System.Windows.Forms.Panel]::new()
$dashboardPanel.Dock = [System.Windows.Forms.DockStyle]::Fill
$dashboardPanel.BackColor = [System.Drawing.Color]::FromArgb(18, 24, 30)
$dashboardPanel.Visible = $false
$dashboardPanel.AutoScroll = $false
$form.Controls.Add($dashboardPanel)

$dashHeader = [System.Windows.Forms.Panel]::new()
$dashHeader.SetBounds(0, 0, $form.ClientSize.Width, 76)
$dashHeader.Anchor = [System.Windows.Forms.AnchorStyles]::Top -bor [System.Windows.Forms.AnchorStyles]::Left -bor [System.Windows.Forms.AnchorStyles]::Right
$dashHeader.BackColor = [System.Drawing.Color]::FromArgb(31, 39, 48)
$dashboardPanel.Controls.Add($dashHeader)

$dashAccent = [System.Windows.Forms.Panel]::new()
$dashAccent.SetBounds(0, 67, $form.ClientSize.Width, 3)
$dashAccent.Anchor = [System.Windows.Forms.AnchorStyles]::Left -bor [System.Windows.Forms.AnchorStyles]::Right -bor [System.Windows.Forms.AnchorStyles]::Bottom

$lblDashTitle = New-Label "RimWorld AI Translator" 22 14 340 28 ([System.Drawing.Color]::White) 13 ([System.Drawing.FontStyle]::Bold)
$lblDashSub = New-Label "모드별로 원문 로드, 번역, 검수, 적용 상태를 관리합니다." 22 44 560 20 ([System.Drawing.Color]::FromArgb(150, 164, 178)) 8.5
$btnDashProjects = New-Button "프로젝트" ([System.Drawing.Color]::FromArgb(28, 126, 220))
$btnDashProjects.ForeColor = [System.Drawing.Color]::White
$btnDashProjects.SetBounds(620, 22, 96, 34)
$btnDashActivity = New-Button "활동" ([System.Drawing.Color]::FromArgb(37, 46, 55))
$btnDashActivity.ForeColor = [System.Drawing.Color]::FromArgb(215, 226, 237)
$btnDashActivity.SetBounds(724, 22, 74, 34)
$btnDashSettings = New-Button "설정" ([System.Drawing.Color]::FromArgb(37, 46, 55))
$btnDashSettings.ForeColor = [System.Drawing.Color]::FromArgb(215, 226, 237)
$btnDashSettings.SetBounds(806, 22, 74, 34)
$dashHeader.Controls.AddRange(@($lblDashTitle, $lblDashSub, $btnDashProjects, $btnDashActivity, $btnDashSettings, $dashAccent))

$dashContent = [System.Windows.Forms.Panel]::new()
$dashContent.SetBounds(0, 76, $form.ClientSize.Width, [Math]::Max(1, $form.ClientSize.Height - 76))
$dashContent.Anchor = [System.Windows.Forms.AnchorStyles]::Top -bor [System.Windows.Forms.AnchorStyles]::Bottom -bor [System.Windows.Forms.AnchorStyles]::Left -bor [System.Windows.Forms.AnchorStyles]::Right
$dashContent.BackColor = [System.Drawing.Color]::FromArgb(18, 24, 30)
$dashboardPanel.Controls.Add($dashContent)
$dashHeader.BringToFront()

$dashProjectsPage = [System.Windows.Forms.Panel]::new()
$dashProjectsPage.Dock = [System.Windows.Forms.DockStyle]::Fill
$dashProjectsPage.BackColor = [System.Drawing.Color]::FromArgb(18, 24, 30)
$dashContent.Controls.Add($dashProjectsPage)

$lblDashProjects = New-Label "프로젝트" 24 20 180 30 ([System.Drawing.Color]::White) 14 ([System.Drawing.FontStyle]::Bold)
$lblDashboardSearch = New-Label "프로젝트 검색" 24 36 170 20 ([System.Drawing.Color]::FromArgb(160, 174, 188)) 8.5 ([System.Drawing.FontStyle]::Bold)
$txtDashboardSearch = New-TextBox
$txtDashboardSearch.SetBounds(24, 60, 330, 30)
$txtDashboardSearch.BackColor = [System.Drawing.Color]::FromArgb(30, 38, 46)
$txtDashboardSearch.ForeColor = [System.Drawing.Color]::FromArgb(226, 235, 244)
$lblDashboardMod = New-Label "프로젝트 대상 모드" 378 36 170 20 ([System.Drawing.Color]::FromArgb(160, 174, 188)) 8.5 ([System.Drawing.FontStyle]::Bold)
$cmbDashboardMods = [System.Windows.Forms.ComboBox]::new()
$cmbDashboardMods.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbDashboardMods.DisplayMember = "Display"
$cmbDashboardMods.Font = New-Font 9
$cmbDashboardMods.SetBounds(378, 60, 440, 30)
$btnDashboardAddMod = New-Button "프로젝트 만들기" ([System.Drawing.Color]::FromArgb(28, 126, 220))
$btnDashboardAddMod.ForeColor = [System.Drawing.Color]::White
$btnDashboardAddMod.SetBounds(826, 58, 112, 32)
$btnDashboardChooseMod = New-Button "폴더 선택" ([System.Drawing.Color]::FromArgb(72, 86, 100))
$btnDashboardChooseMod.ForeColor = [System.Drawing.Color]::White
$btnDashboardChooseMod.SetBounds(946, 58, 96, 32)
$btnDashboardRefreshMods = New-Button "새로고침" ([System.Drawing.Color]::FromArgb(72, 86, 100))
$btnDashboardRefreshMods.ForeColor = [System.Drawing.Color]::White
$btnDashboardRefreshMods.SetBounds(1050, 58, 92, 32)

$flowDashboardProjects = [System.Windows.Forms.FlowLayoutPanel]::new()
$flowDashboardProjects.SetBounds(16, 112, 1418, 560)
$flowDashboardProjects.Anchor = [System.Windows.Forms.AnchorStyles]::Top -bor [System.Windows.Forms.AnchorStyles]::Bottom -bor [System.Windows.Forms.AnchorStyles]::Left -bor [System.Windows.Forms.AnchorStyles]::Right
$flowDashboardProjects.AutoScroll = $true
$flowDashboardProjects.BackColor = [System.Drawing.Color]::FromArgb(18, 24, 30)
$dashProjectsPage.Controls.AddRange(@($lblDashProjects, $lblDashboardSearch, $txtDashboardSearch, $lblDashboardMod, $cmbDashboardMods, $btnDashboardAddMod, $btnDashboardChooseMod, $btnDashboardRefreshMods, $flowDashboardProjects))

$dashActivityPage = [System.Windows.Forms.Panel]::new()
$dashActivityPage.Dock = [System.Windows.Forms.DockStyle]::Fill
$dashActivityPage.BackColor = [System.Drawing.Color]::FromArgb(18, 24, 30)
$dashActivityPage.Visible = $false
$dashContent.Controls.Add($dashActivityPage)

$lblDashActivity = New-Label "활동" 24 20 180 30 ([System.Drawing.Color]::White) 14 ([System.Drawing.FontStyle]::Bold)
$lvDashboardActivity = [System.Windows.Forms.ListView]::new()
$lvDashboardActivity.SetBounds(24, 66, 1378, 606)
$lvDashboardActivity.Anchor = [System.Windows.Forms.AnchorStyles]::Top -bor [System.Windows.Forms.AnchorStyles]::Bottom -bor [System.Windows.Forms.AnchorStyles]::Left -bor [System.Windows.Forms.AnchorStyles]::Right
$lvDashboardActivity.View = [System.Windows.Forms.View]::Details
$lvDashboardActivity.FullRowSelect = $true
$lvDashboardActivity.HideSelection = $false
$lvDashboardActivity.Font = New-Font 9
$lvDashboardActivity.BackColor = [System.Drawing.Color]::FromArgb(30, 38, 46)
$lvDashboardActivity.ForeColor = [System.Drawing.Color]::FromArgb(226, 235, 244)
[void]$lvDashboardActivity.Columns.Add("시간", 150)
[void]$lvDashboardActivity.Columns.Add("모드", 320)
[void]$lvDashboardActivity.Columns.Add("종류", 90)
[void]$lvDashboardActivity.Columns.Add("내용", 720)
$dashActivityPage.Controls.AddRange(@($lblDashActivity, $lvDashboardActivity))

$dashSettingsPage = [System.Windows.Forms.Panel]::new()
$dashSettingsPage.Dock = [System.Windows.Forms.DockStyle]::Fill
$dashSettingsPage.BackColor = [System.Drawing.Color]::FromArgb(18, 24, 30)
$dashSettingsPage.AutoScroll = $true
$dashSettingsPage.AutoScrollMinSize = [System.Drawing.Size]::new(0, 760)
$dashSettingsPage.Visible = $false
$dashContent.Controls.Add($dashSettingsPage)

$lblDashSettings = New-Label "설정" 24 20 180 30 ([System.Drawing.Color]::White) 14 ([System.Drawing.FontStyle]::Bold)
$pnlApiSettings = [System.Windows.Forms.Panel]::new()
$pnlApiSettings.SetBounds(28, 66, 720, 440)
$lblDashApi = New-Label "번역 API" 0 0 220 28 ([System.Drawing.Color]::FromArgb(218, 228, 238)) 11 ([System.Drawing.FontStyle]::Bold)
$lblDashApiHint = New-Label "사용할 서비스를 선택하세요. 키는 저장되지 않습니다." 0 28 360 20 ([System.Drawing.Color]::FromArgb(150, 164, 178)) 8.3
$flowApiProviders = [System.Windows.Forms.FlowLayoutPanel]::new()
$flowApiProviders.SetBounds(0, 58, 190, 370)
$flowApiProviders.FlowDirection = [System.Windows.Forms.FlowDirection]::TopDown
$flowApiProviders.WrapContents = $false
$flowApiProviders.AutoScroll = $true
$flowApiProviders.Padding = [System.Windows.Forms.Padding]::new(0)
$flowApiProviders.Margin = [System.Windows.Forms.Padding]::new(0)
foreach ($providerProfile in $script:apiProviders) {
    $providerButton = New-Button ([string]$providerProfile.Name) ([System.Drawing.Color]::FromArgb(72, 86, 100))
    $providerButton.Tag = [string]$providerProfile.Id
    $providerButton.Size = [System.Drawing.Size]::new(172, 34)
    $providerButton.Margin = [System.Windows.Forms.Padding]::new(0, 0, 0, 5)
    $providerButton.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
    $providerButton.Padding = [System.Windows.Forms.Padding]::new(10, 0, 4, 0)
    $providerButton.Font = New-Font 8.7 ([System.Drawing.FontStyle]::Bold)
    $script:apiProviderButtons[[string]$providerProfile.Id] = $providerButton
    [void]$flowApiProviders.Controls.Add($providerButton)
}
$apiProviderDivider = [System.Windows.Forms.Panel]::new()
$apiProviderDivider.SetBounds(202, 58, 1, 370)

$pnlApiDetail = [System.Windows.Forms.Panel]::new()
$pnlApiDetail.SetBounds(220, 58, 500, 370)
$lblApiProviderTitle = New-Label "Cerebras" 0 0 300 28 ([System.Drawing.Color]::FromArgb(218, 228, 238)) 11 ([System.Drawing.FontStyle]::Bold)
$lblApiProviderDescription = New-Label "Gemma 4와 초고속 추론 모델" 0 30 430 20 ([System.Drawing.Color]::FromArgb(150, 164, 178)) 8.3
$lblApiProviderCustomName = New-Label "서비스 명칭" 0 58 150 20 ([System.Drawing.Color]::FromArgb(180, 190, 200)) 8.3 ([System.Drawing.FontStyle]::Bold)
$txtApiProviderCustomName = New-TextBox
$txtApiProviderCustomName.SetBounds(0, 80, 460, 30)
$lblApiProviderKeys = New-Label "API 키 · 한 줄에 하나씩" 0 58 220 20 ([System.Drawing.Color]::FromArgb(180, 190, 200)) 8.3 ([System.Drawing.FontStyle]::Bold)
$txtDashboardApiKeys = New-TextBox -Multiline
$txtDashboardApiKeys.SetBounds(0, 80, 460, 82)
$txtDashboardApiKeys.BackColor = [System.Drawing.Color]::FromArgb(30, 38, 46)
$txtDashboardApiKeys.ForeColor = [System.Drawing.Color]::FromArgb(226, 235, 244)
$lblApiProviderUrl = New-Label "API URL" 0 172 120 20 ([System.Drawing.Color]::FromArgb(180, 190, 200)) 8.3 ([System.Drawing.FontStyle]::Bold)
$txtApiProviderUrl = New-TextBox
$txtApiProviderUrl.SetBounds(0, 194, 460, 30)
$lblApiProviderModel = New-Label "모델" 0 234 100 20 ([System.Drawing.Color]::FromArgb(180, 190, 200)) 8.3 ([System.Drawing.FontStyle]::Bold)
$cmbApiProviderModel = [System.Windows.Forms.ComboBox]::new()
$cmbApiProviderModel.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDown
$cmbApiProviderModel.Font = New-Font 9.5
$cmbApiProviderModel.SetBounds(0, 256, 300, 30)
$lblApiProviderTemperature = New-Label "Temperature" 316 234 140 20 ([System.Drawing.Color]::FromArgb(180, 190, 200)) 8.3 ([System.Drawing.FontStyle]::Bold)
$cmbApiProviderTemperature = [System.Windows.Forms.ComboBox]::new()
$cmbApiProviderTemperature.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDown
$cmbApiProviderTemperature.Font = New-Font 9.5
$cmbApiProviderTemperature.SetBounds(316, 256, 144, 30)
[void]$cmbApiProviderTemperature.Items.AddRange(@("모델 기본값", "0", "0.1", "0.2"))
$lblApiProviderNotice = New-Label "키가 비어 있으면 Google 번역을 사용합니다." 0 298 460 22 ([System.Drawing.Color]::FromArgb(150, 164, 178)) 8.3
$chkDashboardIncludePatches = [System.Windows.Forms.CheckBox]::new()
$chkDashboardIncludePatches.Text = "Patches 포함"
$chkDashboardIncludePatches.SetBounds(0, 332, 150, 26)
$chkDashboardIncludePatches.ForeColor = [System.Drawing.Color]::FromArgb(218, 226, 234)
$chkDashboardIncludePatches.BackColor = [System.Drawing.Color]::Transparent
$chkDashboardDryRun = [System.Windows.Forms.CheckBox]::new()
$chkDashboardDryRun.Text = "Dry run"
$chkDashboardDryRun.SetBounds(158, 332, 120, 26)
$chkDashboardDryRun.ForeColor = [System.Drawing.Color]::FromArgb(218, 226, 234)
$chkDashboardDryRun.BackColor = [System.Drawing.Color]::Transparent
$lblDashSettingsNote = New-Label "배치 크기 40 · 여러 키는 입력 순서대로 순환" 286 332 300 24 ([System.Drawing.Color]::FromArgb(150, 164, 178)) 8.3
$pnlApiDetail.Controls.AddRange(@($lblApiProviderTitle, $lblApiProviderDescription, $lblApiProviderCustomName, $txtApiProviderCustomName, $lblApiProviderKeys, $txtDashboardApiKeys, $lblApiProviderUrl, $txtApiProviderUrl, $lblApiProviderModel, $cmbApiProviderModel, $lblApiProviderTemperature, $cmbApiProviderTemperature, $lblApiProviderNotice, $chkDashboardIncludePatches, $chkDashboardDryRun, $lblDashSettingsNote))
$pnlApiSettings.Controls.AddRange(@($lblDashApi, $lblDashApiHint, $flowApiProviders, $apiProviderDivider, $pnlApiDetail))

$pnlAppearanceSettings = [System.Windows.Forms.Panel]::new()
$pnlAppearanceSettings.SetBounds(776, 66, 350, 300)
$settingsDivider = [System.Windows.Forms.Panel]::new()
$settingsDivider.SetBounds(0, 0, 350, 1)
$lblDashAppearance = New-Label "화면 및 편집" 0 0 240 28 ([System.Drawing.Color]::FromArgb(218, 228, 238)) 11 ([System.Drawing.FontStyle]::Bold)
$lblDashTheme = New-Label "테마" 0 46 120 20 ([System.Drawing.Color]::FromArgb(180, 190, 200)) 8.5 ([System.Drawing.FontStyle]::Bold)
$cmbDashboardTheme = [System.Windows.Forms.ComboBox]::new()
$cmbDashboardTheme.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbDashboardTheme.Font = New-Font 9.5
$cmbDashboardTheme.SetBounds(0, 70, 220, 30)
[void]$cmbDashboardTheme.Items.AddRange(@("시스템 설정 따름", "밝게", "어둡게"))

$lblDashTextSize = New-Label "본문 글자 크기" 0 118 160 20 ([System.Drawing.Color]::FromArgb(180, 190, 200)) 8.5 ([System.Drawing.FontStyle]::Bold)
$cmbDashboardTextSize = [System.Windows.Forms.ComboBox]::new()
$cmbDashboardTextSize.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbDashboardTextSize.Font = New-Font 9.5
$cmbDashboardTextSize.SetBounds(0, 142, 220, 30)
[void]$cmbDashboardTextSize.Items.AddRange(@("9", "10", "11", "12"))

$chkDashboardHighContrast = [System.Windows.Forms.CheckBox]::new()
$chkDashboardHighContrast.Text = "고대비"
$chkDashboardHighContrast.SetBounds(0, 190, 150, 26)
$chkDashboardHighContrast.BackColor = [System.Drawing.Color]::Transparent
$chkDashboardAutoSave = [System.Windows.Forms.CheckBox]::new()
$chkDashboardAutoSave.Text = "편집 내용 자동 저장"
$chkDashboardAutoSave.SetBounds(0, 224, 210, 26)
$chkDashboardAutoSave.BackColor = [System.Drawing.Color]::Transparent
$pnlAppearanceSettings.Controls.AddRange(@($lblDashAppearance, $lblDashTheme, $cmbDashboardTheme, $lblDashTextSize, $cmbDashboardTextSize, $chkDashboardHighContrast, $chkDashboardAutoSave))

$pnlRmkSettings = [System.Windows.Forms.Panel]::new()
$pnlRmkSettings.SetBounds(28, 536, 1098, 190)
$settingsRmkDivider = [System.Windows.Forms.Panel]::new()
$settingsRmkDivider.SetBounds(0, 0, 1098, 1)
$lblDashRmk = New-Label "RMK 로컬 연동" 0 18 240 26 ([System.Drawing.Color]::FromArgb(218, 228, 238)) 10 ([System.Drawing.FontStyle]::Bold)
$lblDashboardRmkWorkspace = New-Label "작업 클론 (bus 브랜치)" 0 54 220 20 ([System.Drawing.Color]::FromArgb(180, 190, 200)) 8.5 ([System.Drawing.FontStyle]::Bold)
$txtDashboardRmkWorkspace = New-TextBox
$txtDashboardRmkWorkspace.SetBounds(0, 78, 650, 32)
$txtDashboardRmkWorkspace.ReadOnly = $true
$btnDashboardRmkAuto = New-Button "자동 찾기" ([System.Drawing.Color]::FromArgb(72, 86, 100))
$btnDashboardRmkAuto.ForeColor = [System.Drawing.Color]::White
$btnDashboardRmkAuto.SetBounds(662, 76, 94, 34)
$btnDashboardRmkChoose = New-Button "폴더 선택" ([System.Drawing.Color]::FromArgb(72, 86, 100))
$btnDashboardRmkChoose.ForeColor = [System.Drawing.Color]::White
$btnDashboardRmkChoose.SetBounds(764, 76, 94, 34)
$btnDashboardRmkOpen = New-Button "폴더 열기" ([System.Drawing.Color]::FromArgb(72, 86, 100))
$btnDashboardRmkOpen.ForeColor = [System.Drawing.Color]::White
$btnDashboardRmkOpen.SetBounds(866, 76, 94, 34)
$lblDashboardRmkReference = New-Label "RMK 구독본을 찾는 중입니다." 0 118 1040 24 ([System.Drawing.Color]::FromArgb(150, 164, 178)) 8.5
$chkDashboardRmkUseExisting = [System.Windows.Forms.CheckBox]::new()
$chkDashboardRmkUseExisting.Text = "원문 갱신과 AI 번역에서 RMK 기존 번역 자동 사용"
$chkDashboardRmkUseExisting.SetBounds(0, 144, 420, 26)
$chkDashboardRmkUseExisting.BackColor = [System.Drawing.Color]::Transparent
$lblDashboardRmkNote = New-Label "Steam 구독본은 읽기 전용 참조입니다. 내보내기는 bus 브랜치 작업 클론에만 기록하며 커밋·푸시는 하지 않습니다." 438 144 650 30 ([System.Drawing.Color]::FromArgb(150, 164, 178)) 8.3
$pnlRmkSettings.Controls.AddRange(@($settingsRmkDivider, $lblDashRmk, $lblDashboardRmkWorkspace, $txtDashboardRmkWorkspace, $btnDashboardRmkAuto, $btnDashboardRmkChoose, $btnDashboardRmkOpen, $lblDashboardRmkReference, $chkDashboardRmkUseExisting, $lblDashboardRmkNote))

$dashSettingsPage.Controls.AddRange(@($lblDashSettings, $pnlApiSettings, $pnlAppearanceSettings, $pnlRmkSettings))

$dashboardPanel.Add_Resize({
    $dashHeader.SetBounds(0, 0, $dashboardPanel.ClientSize.Width, 70)
    $dashContent.SetBounds(0, 70, $dashboardPanel.ClientSize.Width, [Math]::Max(1, $dashboardPanel.ClientSize.Height - 70))
})

function Update-SplitMinimumSizes {
    if (-not $main -or -not $rightSplit) { return }

    try {
        $main.Panel1MinSize = 0
        $main.Panel2MinSize = 0
        $mainWidth = $main.ClientSize.Width
        $mainRequired = 340 + 740 + $main.SplitterWidth
        if ($mainWidth -ge $mainRequired) {
            $maxMainDistance = $mainWidth - 740 - $main.SplitterWidth
            $main.SplitterDistance = [Math]::Max(340, [Math]::Min($main.SplitterDistance, $maxMainDistance))
            $main.Panel1MinSize = 340
            $main.Panel2MinSize = 740
        }
    } catch {}

    try {
        $rightSplit.Panel1MinSize = 0
        $rightSplit.Panel2MinSize = 0
        if ($rightSplit.Orientation -eq [System.Windows.Forms.Orientation]::Horizontal) {
            $rightHeight = $rightSplit.ClientSize.Height
            if ($rightHeight -ge 470) {
                $rightSplit.Panel1MinSize = 340
                $rightSplit.Panel2MinSize = 100
                $rightSplit.SplitterDistance = [Math]::Max(340, [Math]::Min($rightSplit.SplitterDistance, $rightHeight - 100 - $rightSplit.SplitterWidth))
            }
        } else {
            $rightWidth = $rightSplit.ClientSize.Width
            $rightRequired = 420 + 320 + $rightSplit.SplitterWidth
            if ($rightWidth -ge $rightRequired) {
                $maxRightDistance = $rightWidth - 320 - $rightSplit.SplitterWidth
                $rightSplit.SplitterDistance = [Math]::Max(420, [Math]::Min($rightSplit.SplitterDistance, $maxRightDistance))
                $rightSplit.Panel1MinSize = 420
                $rightSplit.Panel2MinSize = 320
            }
        }
    } catch {}
}

function Apply-AppTheme {
    $isDark = Get-IsWindowsDarkMode
    $themeSignature = "$isDark|$($script:highContrast)|$($script:textSize)|$($form.ClientSize.Width)x$($form.ClientSize.Height)"
    if ($script:appliedThemeSignature -eq $themeSignature) { return }
    if ($isDark) {
        $bg = [System.Drawing.Color]::FromArgb(25, 28, 27)
        $surface = [System.Drawing.Color]::FromArgb(34, 38, 36)
        $subtle = [System.Drawing.Color]::FromArgb(43, 48, 45)
        $line = [System.Drawing.Color]::FromArgb(68, 74, 69)
        $text = [System.Drawing.Color]::FromArgb(239, 236, 227)
        $muted = [System.Drawing.Color]::FromArgb(181, 180, 169)
        $faint = [System.Drawing.Color]::FromArgb(137, 143, 136)
        $header = [System.Drawing.Color]::FromArgb(30, 33, 31)
        $headerButton = [System.Drawing.Color]::FromArgb(48, 53, 49)
        $headerLine = [System.Drawing.Color]::FromArgb(77, 83, 77)
        $searchCard = [System.Drawing.Color]::FromArgb(43, 49, 44)
    } else {
        $bg = [System.Drawing.Color]::FromArgb(241, 242, 239)
        $surface = [System.Drawing.Color]::FromArgb(253, 253, 251)
        $subtle = [System.Drawing.Color]::FromArgb(235, 238, 235)
        $line = [System.Drawing.Color]::FromArgb(204, 209, 203)
        $text = [System.Drawing.Color]::FromArgb(42, 46, 43)
        $muted = [System.Drawing.Color]::FromArgb(91, 99, 92)
        $faint = [System.Drawing.Color]::FromArgb(132, 140, 133)
        $header = [System.Drawing.Color]::FromArgb(39, 43, 40)
        $headerButton = [System.Drawing.Color]::FromArgb(53, 59, 54)
        $headerLine = [System.Drawing.Color]::FromArgb(80, 87, 81)
        $searchCard = [System.Drawing.Color]::FromArgb(229, 234, 228)
    }
    if ($script:highContrast) {
        if ($isDark) {
            $bg = [System.Drawing.Color]::FromArgb(12, 12, 12)
            $surface = [System.Drawing.Color]::FromArgb(22, 22, 22)
            $subtle = [System.Drawing.Color]::FromArgb(34, 34, 34)
            $line = [System.Drawing.Color]::FromArgb(174, 165, 147)
            $text = [System.Drawing.Color]::White
            $muted = [System.Drawing.Color]::FromArgb(225, 218, 204)
            $faint = [System.Drawing.Color]::FromArgb(194, 188, 176)
            $searchCard = [System.Drawing.Color]::FromArgb(32, 32, 30)
        } else {
            $bg = [System.Drawing.Color]::White
            $surface = [System.Drawing.Color]::White
            $subtle = [System.Drawing.Color]::FromArgb(242, 242, 238)
            $line = [System.Drawing.Color]::FromArgb(74, 70, 63)
            $text = [System.Drawing.Color]::FromArgb(15, 15, 15)
            $muted = [System.Drawing.Color]::FromArgb(55, 52, 47)
            $faint = [System.Drawing.Color]::FromArgb(78, 74, 67)
            $searchCard = [System.Drawing.Color]::FromArgb(238, 235, 226)
        }
    }
    $headerText = [System.Drawing.Color]::FromArgb(245, 242, 233)
    $headerMuted = [System.Drawing.Color]::FromArgb(180, 187, 178)
    $primary = if ($isDark) { [System.Drawing.Color]::FromArgb(196, 154, 91) } else { [System.Drawing.Color]::FromArgb(174, 126, 66) }
    $primarySoft = if ($isDark) { [System.Drawing.Color]::FromArgb(68, 59, 44) } else { [System.Drawing.Color]::FromArgb(240, 229, 210) }
    $steel = if ($isDark) { [System.Drawing.Color]::FromArgb(82, 124, 139) } else { [System.Drawing.Color]::FromArgb(74, 111, 124) }
    $green = if ($isDark) { [System.Drawing.Color]::FromArgb(71, 139, 92) } else { [System.Drawing.Color]::FromArgb(62, 124, 82) }
    if ($script:highContrast) {
        $primary = if ($isDark) { [System.Drawing.Color]::FromArgb(224, 177, 92) } else { [System.Drawing.Color]::FromArgb(119, 77, 22) }
        $primarySoft = if ($isDark) { [System.Drawing.Color]::FromArgb(75, 61, 38) } else { [System.Drawing.Color]::FromArgb(244, 232, 210) }
        $steel = if ($isDark) { [System.Drawing.Color]::FromArgb(94, 163, 190) } else { [System.Drawing.Color]::FromArgb(38, 88, 108) }
        $green = if ($isDark) { [System.Drawing.Color]::FromArgb(68, 158, 94) } else { [System.Drawing.Color]::FromArgb(31, 103, 56) }
    }

    $script:itemCardBack = $surface
    $script:itemCardSelected = if ($isDark) { [System.Drawing.Color]::FromArgb(53, 58, 53) } else { [System.Drawing.Color]::FromArgb(225, 232, 225) }
    $script:itemText = $text
    $script:itemMuted = $muted
    $script:itemSubtle = $faint
    $script:tabBack = $surface
    $script:tabActive = $primary
    $script:tabText = $muted
    $script:tabActiveText = [System.Drawing.Color]::White
    $script:accentColor = $primary
    $script:surfaceColor = $surface
    $script:textColor = $text
    $script:mutedColor = $muted

    $form.BackColor = $bg
    $formWidth = [Math]::Max(1, $form.ClientSize.Width)
    $formHeight = [Math]::Max(1, $form.ClientSize.Height)
    try {
        $main.Panel1MinSize = 0
        $main.Panel2MinSize = 0
        $rightSplit.Panel1MinSize = 0
        $rightSplit.Panel2MinSize = 0
    } catch {}
    $top.Dock = [System.Windows.Forms.DockStyle]::None
    $top.SetBounds(0, 0, $formWidth, 78)
    $top.Anchor = [System.Windows.Forms.AnchorStyles]::Top -bor [System.Windows.Forms.AnchorStyles]::Left -bor [System.Windows.Forms.AnchorStyles]::Right
    $top.BackColor = $header
    $topAccent.SetBounds(0, 75, $formWidth, 3)
    $topAccent.BackColor = $primary
    $main.Dock = [System.Windows.Forms.DockStyle]::None
    $main.SetBounds(0, 78, $formWidth, [Math]::Max(1, $formHeight - 78))
    $main.Anchor = [System.Windows.Forms.AnchorStyles]::Top -bor [System.Windows.Forms.AnchorStyles]::Bottom -bor [System.Windows.Forms.AnchorStyles]::Left -bor [System.Windows.Forms.AnchorStyles]::Right
    $dashHeader.BackColor = $header
    $dashHeader.Height = 70
    $dashAccent.SetBounds(0, 67, $formWidth, 3)
    $dashAccent.BackColor = $primary
    $dashContent.Top = 70
    foreach ($control in @($main, $main.Panel1, $main.Panel2, $left, $center, $side, $rightSplit, $rightSplit.Panel1, $rightSplit.Panel2, $dashboardPanel, $dashContent, $dashProjectsPage, $dashActivityPage, $dashSettingsPage, $flowDashboardProjects, $flowItems, $statusFilterBar, $pnlApiSettings, $pnlAppearanceSettings, $pnlRmkSettings)) {
        if ($control) { $control.BackColor = $bg }
    }
    foreach ($panel in @($center, $side, $pnlApiDetail, $flowApiProviders)) {
        if ($panel) { $panel.BackColor = $surface }
    }

    $topWidth = $formWidth
    $lblProject.SetBounds(24, 12, 286, 25)
    $lblProject.Font = New-Font 11.5 ([System.Drawing.FontStyle]::Bold)
    $lblProject.ForeColor = $headerText
    $lblProject.AutoEllipsis = $true
    $lblPath.SetBounds(24, 42, 286, 18)
    $lblPath.Font = New-Font 8
    $lblPath.ForeColor = $headerMuted
    $lblPath.AutoEllipsis = $true
    $lblPath.Visible = $true
    $lblProjectPick.Visible = $false
    $cmbProject.Visible = $false
    $lblModPick.Visible = $false
    $cmbModCatalog.Visible = $false
    $btnRefreshMods.Visible = $false
    $btnChooseMod.Visible = $false
    $lblApi.Visible = $false
    $txtApiKeys.Visible = $false
    $chkIncludePatches.Visible = $false
    $chkDryRun.Visible = $false
    $progressRun.Visible = $false
    $actionWidths = @(96, 88, 54, 92, 100)
    $actionGap = 8
    $actionTotal = ($actionWidths | Measure-Object -Sum).Sum + ($actionGap * ($actionWidths.Count - 1))
    $actionX = [Math]::Max(434, $topWidth - 24 - $actionTotal)
    $rmkCheckWidth = 96
    $rmkCheckX = [Math]::Max(330, $actionX - $rmkCheckWidth - 8)
    $showFullUtilities = $actionX -ge 646
    $veryCompactHeader = $actionX -lt 516
    $utilityEnd = 550
    $showSaveStatus = $showFullUtilities -and ($rmkCheckX - $utilityEnd) -ge 116
    $showRunStatus = $showSaveStatus
    $lblSave.Visible = $showSaveStatus
    $lblRunStatus.Visible = $showRunStatus
    $statusWidth = [Math]::Max(96, $rmkCheckX - $utilityEnd - 12)
    $lblRunStatus.SetBounds($utilityEnd, 17, $statusWidth, 18)
    $lblRunStatus.ForeColor = $headerMuted
    $lblSave.SetBounds($utilityEnd, 40, $statusWidth, 18)
    $lblSave.ForeColor = $headerMuted

    $btnHome.Text = "프로젝트"
    if ($veryCompactHeader) {
        $lblProject.Width = 200
        $lblPath.Width = 200
        $btnHome.SetBounds(236, 21, 74, 36)
    } else {
        $btnHome.SetBounds(330, 21, 74, 36)
    }
    $btnHome.Visible = $true
    $btnOpenFolder.Text = "폴더"
    $btnOpenFolder.SetBounds(412, 21, 60, 36)
    $btnOpenFolder.Visible = $showFullUtilities
    $btnSave.SetBounds(480, 21, 58, 36)
    $btnSave.Visible = $showFullUtilities
    $chkApplyToRmk.SetBounds($rmkCheckX, 27, $rmkCheckWidth, 24)
    $chkApplyToRmk.BackColor = $header
    $chkApplyToRmk.ForeColor = $headerText
    $btnLoad.Text = "원문 갱신"
    $btnLoad.SetBounds($actionX, 21, $actionWidths[0], 36)
    $btnTranslate.Text = "AI 번역"
    $btnTranslate.SetBounds(($actionX + $actionWidths[0] + $actionGap), 21, $actionWidths[1], 36)
    $btnStop.SetBounds(($actionX + $actionWidths[0] + $actionGap + $actionWidths[1] + $actionGap), 21, $actionWidths[2], 36)
    $btnApply.Text = "검토 적용"
    $btnApply.SetBounds(($actionX + $actionWidths[0] + $actionGap + $actionWidths[1] + $actionGap + $actionWidths[2] + $actionGap), 21, $actionWidths[3], 36)
    $btnApplyTranslated.Text = "전체 적용"
    $btnApplyTranslated.SetBounds(($actionX + $actionWidths[0] + $actionGap + $actionWidths[1] + $actionGap + $actionWidths[2] + $actionGap + $actionWidths[3] + $actionGap), 21, $actionWidths[4], 36)

    foreach ($button in @($btnHome, $btnSave, $btnOpenFolder, $btnLoad, $btnDashboardChooseMod, $btnDashboardRefreshMods, $btnDashboardRmkAuto, $btnDashboardRmkChoose, $btnDashboardRmkOpen, $btnRmkRefresh, $btnRmkOpen, $btnDashActivity, $btnDashSettings)) {
        if ($button) {
            $button.BackColor = $headerButton
            $button.ForeColor = $headerText
            $button.FlatAppearance.BorderColor = $headerLine
            $button.FlatAppearance.BorderSize = 1
        }
    }
    foreach ($button in @($btnTranslate, $btnDashProjects, $btnDashboardAddMod)) {
        if ($button) {
            $button.BackColor = $primary
            $button.ForeColor = [System.Drawing.Color]::White
            $button.FlatAppearance.BorderColor = $primary
            $button.FlatAppearance.BorderSize = 0
        }
    }
    Update-StopButtonAppearance
    $btnApply.BackColor = $green
    $btnApply.ForeColor = [System.Drawing.Color]::White
    $btnApply.FlatAppearance.BorderColor = $green
    $btnApply.FlatAppearance.BorderSize = 0
    $btnApplyTranslated.BackColor = $steel
    $btnApplyTranslated.ForeColor = [System.Drawing.Color]::White
    $btnApplyTranslated.FlatAppearance.BorderColor = $steel
    $btnApplyTranslated.FlatAppearance.BorderSize = 0

    $main.BackColor = $line
    $rightSplit.BackColor = $line
    $mainWidth = [Math]::Max(1, $main.ClientSize.Width)
    $leftWidth = [Math]::Min(410, [Math]::Max(360, [int]($mainWidth * 0.24)))
    $leftWidth = [Math]::Min($leftWidth, [Math]::Max(340, $mainWidth - 740 - $main.SplitterWidth))
    try { $main.SplitterDistance = $leftWidth } catch {}

    $rightWidth = [Math]::Max(1, $rightSplit.ClientSize.Width)
    $compactWorkspace = $mainWidth -lt 1100
    try {
        $rightSplit.Panel1MinSize = 0
        $rightSplit.Panel2MinSize = 0
        if ($compactWorkspace) {
            $rightSplit.Orientation = [System.Windows.Forms.Orientation]::Horizontal
            $rightHeight = [Math]::Max(1, $rightSplit.ClientSize.Height)
            $centerHeight = [Math]::Max(340, [Math]::Min([int]($rightHeight * 0.8), $rightHeight - 104 - $rightSplit.SplitterWidth))
            $rightSplit.SplitterDistance = $centerHeight
        } else {
            $rightSplit.Orientation = [System.Windows.Forms.Orientation]::Vertical
            $sideWidth = [Math]::Min(370, [Math]::Max(330, [int]($rightWidth * 0.25)))
            $centerWidth = [Math]::Max(420, $rightWidth - $sideWidth - $rightSplit.SplitterWidth)
            $centerWidth = [Math]::Min($centerWidth, [Math]::Max(420, $rightWidth - 330 - $rightSplit.SplitterWidth))
            $rightSplit.SplitterDistance = $centerWidth
        }
    } catch {}
    Queue-SideTabResize

    $leftInner = [Math]::Max(268, $leftWidth - 32)
    $lblSearchCrumb.SetBounds(16, 16, $leftInner, 64)
    $lblSearchCrumb.Padding = [System.Windows.Forms.Padding]::new(14, 9, 12, 7)
    $lblSearchCrumb.BackColor = $searchCard
    $lblSearchCrumb.ForeColor = $primary
    $lblSearchCrumb.BorderStyle = [System.Windows.Forms.BorderStyle]::None
    $cmbSearchField.SetBounds(16, 92, 88, 32)
    $statusWidth = 122
    $statusX = $leftWidth - $statusWidth - 16
    $txtSearch.SetBounds(104, 92, [Math]::Max(72, $statusX - 112), 32)
    $cmbStatus.SetBounds($statusX, 92, $statusWidth, 32)
    $statusFilterBar.SetBounds(16, 136, $leftInner, 30)
    $cmbSort.SetBounds(16, 174, $leftInner, 32)
    $lblProjectStats.SetBounds(16, 214, $leftInner, 42)
    $progressReview.Visible = $false
    $lblProgress.Visible = $false
    $lvFiles.Visible = $false
    $flowItems.SetBounds(16, 266, $leftInner, [Math]::Max(240, $main.ClientSize.Height - 282))
    $flowItems.ItemHeight = 94 + ([Math]::Max(-1, [Math]::Min(2, $script:textSize - 10)) * 4)
    $flowItems.BorderStyle = [System.Windows.Forms.BorderStyle]::None

    foreach ($box in @($txtSearch, $txtSource, $txtTranslation, $txtMeta, $txtExisting, $txtCandidate, $txtHistory, $txtTerms, $txtMemo, $txtRmkDetails, $txtWarnings, $txtDashboardSearch, $txtDashboardApiKeys, $txtDashboardRmkWorkspace, $txtApiKeys, $txtApiProviderUrl, $txtApiProviderCustomName)) {
        if ($box) {
            $box.BackColor = $surface
            $box.ForeColor = $text
        }
    }
    $txtSource.BackColor = $subtle
    $pnlSourceFrame.BackColor = $subtle
    $pnlTranslationFrame.BackColor = $surface
    $pnlSourceFrame.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
    $pnlTranslationFrame.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
    $translationAccent.BackColor = $primary
    $editorDivider.BackColor = $line
    $txtMeta.BackColor = $surface
    $txtMeta.ForeColor = $muted
    $txtMeta.BorderStyle = [System.Windows.Forms.BorderStyle]::None
    $txtExisting.BackColor = $subtle
    $txtCandidate.BackColor = $subtle
    $txtSource.BorderStyle = [System.Windows.Forms.BorderStyle]::None
    $txtTranslation.BorderStyle = [System.Windows.Forms.BorderStyle]::None
    $txtExisting.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
    $txtCandidate.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
    $txtSearch.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
    $txtLog.BackColor = if ($isDark) { [System.Drawing.Color]::FromArgb(16, 22, 28) } else { [System.Drawing.Color]::FromArgb(248, 247, 242) }
    $txtLog.ForeColor = $text

    foreach ($combo in @($cmbSearchField, $cmbStatus, $cmbSort, $cmbModCatalog, $cmbProject, $cmbDashboardMods, $cmbDashboardTheme, $cmbDashboardTextSize, $cmbApiProviderModel, $cmbApiProviderTemperature)) {
        if ($combo) {
            $combo.BackColor = $surface
            $combo.ForeColor = $text
            $combo.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
        }
    }
    foreach ($list in @($lvFiles, $lvDashboardActivity)) {
        if ($list) {
            $list.BackColor = $surface
            $list.ForeColor = $text
            $list.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
        }
    }
    foreach ($page in @($tabHistory, $tabTerms, $tabMemo, $tabRmk, $tabIssues, $tabLog)) {
        if ($page) {
            $page.BackColor = $surface
            $page.Padding = [System.Windows.Forms.Padding]::new(12)
        }
    }
    foreach ($sideBox in @($txtHistory, $txtTerms, $txtMemo, $txtRmkDetails, $txtWarnings)) {
        if ($sideBox) {
            $sideBox.BorderStyle = [System.Windows.Forms.BorderStyle]::None
            $sideBox.BackColor = $surface
        }
    }
    foreach ($label in @($lblSearchCrumb, $lblProjectStats, $lblProgress, $lblExisting, $lblCandidate, $lblReferenceTitle, $lblRmkStatus, $lblDashProjects, $lblDashActivity, $lblDashSettings, $lblDashApi, $lblDashApiHint, $lblApiProviderTitle, $lblApiProviderDescription, $lblApiProviderCustomName, $lblApiProviderKeys, $lblApiProviderUrl, $lblApiProviderModel, $lblApiProviderTemperature, $lblApiProviderNotice, $lblDashboardSearch, $lblDashboardMod, $lblDashSettingsNote, $lblDashAppearance, $lblDashTheme, $lblDashTextSize, $lblDashRmk, $lblDashboardRmkWorkspace, $lblDashboardRmkReference, $lblDashboardRmkNote)) {
        if ($label -and $label -ne $lblSearchCrumb) { $label.ForeColor = $text }
    }
    $lblSourceTitle.ForeColor = $muted
    $lblTranslationTitle.ForeColor = $muted
    $lblReferenceTitle.ForeColor = $muted
    $lblUpdateBadge.ForeColor = Get-UpdateColor
    if ($script:currentRowIndex -ge 0) {
        $currentDecision = Get-Decision $script:rows[$script:currentRowIndex]
        $lblCurrent.ForeColor = Get-StatusColor ([string]$currentDecision.status)
    } else {
        $lblCurrent.ForeColor = $text
    }
    foreach ($label in @($lblDashSub)) {
        if ($label) { $label.ForeColor = $headerMuted }
    }
    foreach ($check in @($chkDashboardIncludePatches, $chkDashboardDryRun, $chkDashboardHighContrast, $chkDashboardAutoSave, $chkDashboardRmkUseExisting)) {
        if ($check) {
            $check.ForeColor = $text
            $check.BackColor = $bg
        }
    }
    $settingsDivider.BackColor = $line
    $settingsRmkDivider.BackColor = $line
    $apiProviderDivider.BackColor = $line

    $lblSourceTitle.Text = "원문"
    $lblTranslationTitle.Text = "번역문"
    $lblSourceTitle.Visible = $true
    $lblTranslationTitle.Visible = $true
    foreach ($button in @($btnPrev, $btnNext, $btnUseCandidate, $btnUseExisting, $btnUseSource, $btnResetEdit, $btnPending, $btnTranslated)) {
        if ($button) {
            $button.BackColor = $surface
            $button.ForeColor = $text
            $button.FlatAppearance.BorderColor = $line
            $button.FlatAppearance.BorderSize = 1
        }
    }
    $btnUseCandidate.BackColor = $primarySoft
    $btnUseCandidate.FlatAppearance.BorderColor = $primary
    $btnTranslated.BackColor = $steel
    $btnTranslated.ForeColor = [System.Drawing.Color]::White
    $btnTranslated.FlatAppearance.BorderColor = $steel
    $btnTranslated.FlatAppearance.BorderSize = 0
    $btnApprove.BackColor = $green
    $btnApprove.ForeColor = [System.Drawing.Color]::White
    $btnApprove.FlatAppearance.BorderColor = $green
    $btnApprove.FlatAppearance.BorderSize = 0
    $btnApproveNext.BackColor = $green
    $btnApproveNext.ForeColor = [System.Drawing.Color]::White
    $btnApproveNext.FlatAppearance.BorderColor = $green
    $btnApproveNext.FlatAppearance.BorderSize = 0
    $btnApproveAll.BackColor = $surface
    $btnApproveAll.ForeColor = $primary
    $btnApproveAll.FlatAppearance.BorderColor = $primary
    $btnApproveAll.FlatAppearance.BorderSize = 1
    $btnRmkBuild.BackColor = $primary
    $btnRmkBuild.ForeColor = [System.Drawing.Color]::White
    $btnRmkBuild.FlatAppearance.BorderColor = $primary
    Refresh-StatusFilterButtons
    Resize-ReviewEditorLayout

    $tabs.Appearance = [System.Windows.Forms.TabAppearance]::Normal
    $tabs.SizeMode = [System.Windows.Forms.TabSizeMode]::Fixed
    $tabWidth = [Math]::Max(48, [Math]::Min(68, [int](($side.ClientSize.Width - 8) / [Math]::Max(1, $tabs.TabPages.Count))))
    $tabs.ItemSize = [System.Drawing.Size]::new($tabWidth, 38)
    $tabs.BackColor = $surface
    $tabs.Invalidate()

    $dashWidth = [Math]::Max(860, $dashboardPanel.ClientSize.Width)
    $dashHeight = [Math]::Max(560, $dashboardPanel.ClientSize.Height)
    $lblDashTitle.SetBounds(28, 18, 300, 26)
    $lblDashTitle.ForeColor = $headerText
    $lblDashSub.Visible = $false
    $btnDashProjects.SetBounds(350, 16, 92, 36)
    $btnDashActivity.SetBounds(450, 16, 82, 36)
    $btnDashSettings.SetBounds(540, 16, 82, 36)
    $dashHeader.SetBounds(0, 0, $dashWidth, 70)
    $dashContent.SetBounds(0, 70, $dashWidth, [Math]::Max(1, $dashHeight - 70))
    $lblDashProjects.SetBounds(32, 24, 220, 34)
    $searchWidth = [Math]::Min(360, [Math]::Max(250, [int]($dashWidth * 0.25)))
    $buttonTotal = 126 + 90 + 90 + 16
    $modX = 32 + $searchWidth + 28
    $buttonX = $dashWidth - 32 - $buttonTotal
    $comboWidth = [Math]::Max(180, $buttonX - $modX - 14)
    $lblDashboardSearch.SetBounds(32, 72, 170, 20)
    $txtDashboardSearch.SetBounds(32, 96, $searchWidth, 34)
    $lblDashboardMod.SetBounds($modX, 72, 170, 20)
    $cmbDashboardMods.SetBounds($modX, 96, $comboWidth, 34)
    $btnDashboardAddMod.SetBounds($buttonX, 96, 126, 34)
    $btnDashboardChooseMod.SetBounds(($buttonX + 134), 96, 90, 34)
    $btnDashboardRefreshMods.SetBounds(($buttonX + 232), 96, 90, 34)
    foreach ($button in @($btnDashboardChooseMod, $btnDashboardRefreshMods)) {
        $button.BackColor = $surface
        $button.ForeColor = $text
        $button.FlatAppearance.BorderColor = $line
    }
    $flowDashboardProjects.SetBounds(22, 152, [Math]::Max(320, $dashWidth - 44), [Math]::Max(260, $dashHeight - 176))
    $lvDashboardActivity.SetBounds(24, 66, [Math]::Max(320, $dashWidth - 48), [Math]::Max(260, $dashHeight - 154))
    Resize-DashboardSettingsLayout
    Refresh-ApiProviderButtons
    if ($dashboardPanel.Visible) { Refresh-DashboardTabButtons }
    Update-SplitMinimumSizes
    $script:appliedThemeSignature = $themeSignature
}

$form.AccessibleName = "RimWorld AI Translator"
$form.AccessibleDescription = "RimWorld 모드의 원문, 번역문, 검토 상태를 관리하는 로컬 번역 도구"
$lblCurrent.AccessibleRole = [System.Windows.Forms.AccessibleRole]::StaticText
$lblUpdateBadge.AccessibleRole = [System.Windows.Forms.AccessibleRole]::StaticText
$lblUpdateBadge.AccessibleName = "업데이트로 변경된 문자열"
$lblUpdateBadge.AccessibleDescription = "모드 업데이트로 이 키의 원문이 바뀌어 다시 번역하거나 검토해야 합니다."
$flowItems.AccessibleRole = [System.Windows.Forms.AccessibleRole]::List
$flowItems.AccessibleName = "검색된 문자열 목록"
$tabs.AccessibleRole = [System.Windows.Forms.AccessibleRole]::PageTabList

Set-AccessibleControl $btnHome "프로젝트 목록" "프로젝트 화면으로 돌아갑니다." 0
Set-AccessibleControl $btnSave "검수 내용 저장" "현재 편집 내용을 로컬 프로젝트에 저장합니다. 단축키 Ctrl+S." 1
Set-AccessibleControl $btnOpenFolder "모드 폴더 열기" "현재 프로젝트의 RimWorld 모드 폴더를 엽니다." 2
Set-AccessibleControl $btnLoad "원문 불러오기" "현재 모드에서 번역 가능한 문자열을 다시 불러옵니다. 단축키 F5." 3
Set-AccessibleControl $btnTranslate "AI 초벌 번역" "API 키가 있으면 AI, 없으면 Google 번역으로 초벌 번역을 만듭니다. 단축키 F9." 4
Set-AccessibleControl $btnStop "번역 중지" "실행 중인 번역 작업을 중지합니다. 단축키 Shift+F9." 5
Set-AccessibleControl $chkApplyToRmk "RMK 적용 대상" "해제하면 원본 모드의 Korean 폴더에, 선택하면 RMK 작업 클론에 적용합니다." 6
Set-AccessibleControl $btnApply "검토 완료 번역 적용" "검토 완료 상태만 선택한 적용 대상에 반영합니다." 7
Set-AccessibleControl $btnApplyTranslated "번역된 항목 모두 적용" "번역됨과 검토 완료 상태를 선택한 적용 대상에 반영합니다." 8

Set-AccessibleControl $cmbSearchField "검색 범위" "텍스트, 키, Def Class 또는 Node 중 검색할 범위를 선택합니다." 0
Set-AccessibleControl $txtSearch "문자열 검색" "원문, 번역문 또는 키를 검색합니다. 단축키 Ctrl+F." 1
Set-AccessibleControl $cmbStatus "번역 상태 필터" "미번역, 번역됨, 검토됨, 업데이트로 변경됨 또는 주의 항목을 고릅니다." 2
Set-AccessibleControl $cmbSort "번역 정렬" "기본 순서 또는 내가 편집한 번역의 최신순과 오래된순을 선택합니다." 3
for ($i = 0; $i -lt $statusFilterButtons.Count; $i++) {
    $filterButton = $statusFilterButtons[$i]
    Set-AccessibleControl $filterButton "$($filterButton.Text) 상태 필터" "$($filterButton.Text) 상태의 문자열만 목록에 표시합니다." $i
}
Set-AccessibleControl $txtSource "원문" "선택된 문자열의 읽기 전용 원문입니다." 0
Set-AccessibleControl $txtTranslation "번역문 편집" "선택된 문자열의 한국어 번역문을 편집합니다. 단축키 F2." 1
Set-AccessibleControl $txtMeta "문자열 정보" "Def Class, Node, 문맥 설명, 파일, ID, 단어 수와 안전 적용 여부입니다." 2
Set-AccessibleControl $btnPrev "이전 문자열" "이전 검색 결과로 이동합니다. 단축키 Shift+F3 또는 Alt+위쪽 화살표." 3
Set-AccessibleControl $btnNext "다음 문자열" "다음 검색 결과로 이동합니다. 단축키 F3 또는 Alt+아래쪽 화살표." 4
Set-AccessibleControl $btnUseCandidate "AI 후보 사용" "AI 초벌 번역을 편집기에 넣습니다. 단축키 Alt+1." 5
Set-AccessibleControl $btnUseExisting "기존 번역 사용" "기존 Korean 번역을 편집기에 넣습니다. 단축키 Alt+2." 6
Set-AccessibleControl $btnUseSource "번역문 복사" "현재 번역문을 클립보드에 복사합니다." 7
Set-AccessibleControl $btnResetEdit "편집 되돌리기" "저장된 번역문으로 되돌립니다. 단축키 Alt+0." 8
Set-AccessibleControl $btnPending "미번역으로 표시" "현재 항목을 미번역 상태로 바꿉니다. 단축키 Ctrl+1." 9
Set-AccessibleControl $btnTranslated "번역됨으로 표시" "현재 항목을 번역됨 상태로 바꿉니다. 단축키 Ctrl+2." 10
Set-AccessibleControl $btnApprove "검토 완료로 표시" "현재 항목을 검토 완료 상태로 바꿉니다. 단축키 Ctrl+3 또는 Ctrl+Shift+Enter." 11
Set-AccessibleControl $btnApproveNext "검토 완료 후 다음" "현재 항목을 검토 완료로 저장하고 다음 항목으로 이동합니다. 단축키 Ctrl+Enter." 12
Set-AccessibleControl $btnApproveAll "전체 검토 완료" "빈 번역, 원문 변경과 주의 항목을 제외한 모든 안전한 번역을 검토 완료로 표시합니다. 단축키 Ctrl+Shift+3." 13
Set-AccessibleControl $txtExisting "기존 번역" "모드 또는 RMK에서 가져온 기존 Korean 번역입니다." 14
Set-AccessibleControl $txtCandidate "AI 번역 후보" "AI 또는 Google이 만든 초벌 번역입니다." 15
Set-AccessibleControl $tabs "참고 정보 탭" "역사, 용어, 메모, RMK, 문제와 로그를 전환합니다." 0
Set-AccessibleControl $txtHistory "번역 역사" "원문, 기존 번역, AI 후보와 현재 검수 번역을 보여줍니다." 0
Set-AccessibleControl $txtTerms "관련 용어" "현재 문자열과 관련된 RimWorld 용어를 보여줍니다." 0
Set-AccessibleControl $txtMemo "검수 메모" "현재 문자열에 대한 로컬 메모를 편집합니다." 0
Set-AccessibleControl $txtRmkDetails "RMK 연결 정보" "현재 프로젝트의 RMK 번역 경로, 버전과 Git 작업 상태를 보여줍니다." 0
Set-AccessibleControl $btnRmkRefresh "RMK 상태 갱신" "RMK 구독본과 작업 클론에서 현재 프로젝트를 다시 찾습니다." 1
Set-AccessibleControl $btnRmkOpen "RMK 폴더 열기" "현재 RMK 번역 항목 또는 작업 클론 폴더를 엽니다." 2
Set-AccessibleControl $btnRmkBuild "RMK LoadFolders 빌드" "RMK 작업 클론의 LoadFoldersBuilder를 실행합니다." 3
Set-AccessibleControl $txtWarnings "주의 사항" "토큰 누락, 림월드 조사 표기 오류와 안전 적용 여부 등 현재 문자열의 문제를 보여줍니다." 0
Set-AccessibleControl $txtLog "작업 로그" "원문 로드, 번역과 적용 과정의 로그입니다." 0

Set-AccessibleControl $btnDashProjects "프로젝트 탭" "로컬 번역 프로젝트를 표시합니다." 0
Set-AccessibleControl $btnDashActivity "활동 탭" "최근 번역과 검토 활동을 표시합니다." 1
Set-AccessibleControl $btnDashSettings "설정 탭" "API와 화면 설정을 표시합니다." 2
Set-AccessibleControl $txtDashboardSearch "프로젝트 검색" "모드 이름, Workshop ID 또는 패키지 ID를 검색합니다." 0
Set-AccessibleControl $cmbDashboardMods "프로젝트 대상 모드" "자동으로 찾은 RimWorld 모드 중 프로젝트로 만들 모드를 선택합니다." 1
Set-AccessibleControl $btnDashboardAddMod "프로젝트 만들기" "선택한 모드로 로컬 번역 프로젝트를 만듭니다." 2
Set-AccessibleControl $btnDashboardChooseMod "모드 폴더 선택" "자동 검색되지 않은 모드 폴더를 직접 선택합니다." 3
Set-AccessibleControl $btnDashboardRefreshMods "모드 목록 새로고침" "Steam과 RimWorld 모드 폴더를 다시 검색합니다." 4
Set-AccessibleControl $lvDashboardActivity "최근 활동 목록" "번역, 검토와 적용 내역입니다." 0
foreach ($providerProfile in $script:apiProviders) {
    $providerButton = $script:apiProviderButtons[[string]$providerProfile.Id]
    Set-AccessibleControl $providerButton "$($providerProfile.Name) 번역 API 선택" "$($providerProfile.Description) 설정을 표시하고 번역 제공자로 선택합니다." 0
}
Set-AccessibleControl $txtDashboardApiKeys "선택한 제공자의 API 키" "한 줄에 하나씩 여러 API 키를 입력합니다. 이 값은 설정 파일에 저장되지 않습니다." 0
Set-AccessibleControl $txtApiProviderUrl "Chat Completions API URL" "선택한 제공자의 HTTPS Chat Completions 주소입니다." 1
Set-AccessibleControl $cmbApiProviderModel "번역 모델" "기본 모델을 선택하거나 모델 ID를 직접 입력합니다." 2
Set-AccessibleControl $cmbApiProviderTemperature "번역 Temperature" "모델 기본값 또는 0에서 2 사이의 값을 입력합니다." 3
Set-AccessibleControl $chkDashboardIncludePatches "Patches 폴더 포함" "모드의 Patches 폴더도 번역 대상으로 포함합니다." 1
Set-AccessibleControl $chkDashboardDryRun "시험 실행" "파일을 쓰지 않고 번역 대상을 점검합니다." 2
Set-AccessibleControl $cmbDashboardTheme "테마" "시스템 설정, 밝은 테마 또는 어두운 테마를 선택합니다." 3
Set-AccessibleControl $cmbDashboardTextSize "본문 글자 크기" "번역문과 참고 정보의 글자 크기를 9에서 12 사이로 선택합니다." 4
Set-AccessibleControl $chkDashboardHighContrast "고대비" "텍스트와 경계선 대비를 높입니다." 5
Set-AccessibleControl $chkDashboardAutoSave "자동 저장" "입력을 멈춘 뒤 편집 내용을 자동으로 저장합니다." 6
Set-AccessibleControl $txtDashboardRmkWorkspace "RMK 작업 클론" "번역을 내보낼 RMK Git 클론의 경로입니다." 7
Set-AccessibleControl $btnDashboardRmkAuto "RMK 작업 클론 자동 찾기" "RimWorld 로컬 Mods 폴더에서 RMK Git 클론을 찾습니다." 8
Set-AccessibleControl $btnDashboardRmkChoose "RMK 작업 클론 선택" "RMK Git 클론 폴더를 직접 선택합니다." 9
Set-AccessibleControl $btnDashboardRmkOpen "RMK 작업 클론 열기" "설정된 RMK 폴더를 탐색기로 엽니다." 10
Set-AccessibleControl $chkDashboardRmkUseExisting "RMK 기존 번역 자동 사용" "원문 갱신과 AI 번역에서 RMK 번역을 기본값으로 사용하고 없는 키만 번역합니다." 11

foreach ($hiddenControl in @($cmbProject, $cmbModCatalog, $btnRefreshMods, $btnChooseMod, $txtApiKeys, $chkIncludePatches, $chkDryRun, $lvFiles, $lvItems)) {
    if ($hiddenControl) { $hiddenControl.TabStop = $false }
}

Set-CueBanner $txtSearch "검색어 입력"
Set-CueBanner $txtDashboardSearch "프로젝트 검색"

$toolTip.SetToolTip($txtSearch, "검색으로 이동: Ctrl+F · 검색 지우기: Esc")
$toolTip.SetToolTip($lblUpdateBadge, "모드 업데이트로 원문이 바뀐 항목입니다. 다시 번역하거나 검토해야 적용됩니다.")
foreach ($filterButton in $statusFilterButtons) {
    $toolTip.SetToolTip($filterButton, "$($filterButton.Text) 상태만 표시")
}
$toolTip.SetToolTip($btnSave, "저장 (Ctrl+S)")
$toolTip.SetToolTip($btnLoad, "원문 갱신 (F5)")
$toolTip.SetToolTip($btnTranslate, "AI 번역 (F9)")
$toolTip.SetToolTip($btnStop, "AI 번역 중지 (Shift+F9)")
$toolTip.SetToolTip($txtTranslation, "번역문 입력으로 이동 (F2)")
$toolTip.SetToolTip($btnPrev, "이전 문자열 (Shift+F3 또는 Alt+↑)")
$toolTip.SetToolTip($btnNext, "다음 문자열 (F3 또는 Alt+↓)")
$toolTip.SetToolTip($btnUseCandidate, "AI 후보 사용 (Alt+1)")
$toolTip.SetToolTip($btnUseExisting, "기존 번역 사용 (Alt+2)")
$toolTip.SetToolTip($btnUseSource, "현재 번역문을 클립보드에 복사합니다.")
$toolTip.SetToolTip($btnResetEdit, "저장된 검수 번역으로 되돌리기 (Alt+0)")
$toolTip.SetToolTip($btnPending, "미번역으로 표시 (Ctrl+1)")
$toolTip.SetToolTip($btnTranslated, "번역됨으로 표시 (Ctrl+2)")
$toolTip.SetToolTip($btnApprove, "검토 완료로 표시 (Ctrl+3 또는 Ctrl+Shift+Enter)")
$toolTip.SetToolTip($btnApproveNext, "검토 완료 후 다음 (Ctrl+Enter)")
$toolTip.SetToolTip($btnApproveAll, "안전한 번역 전체를 검토 완료로 표시 (Ctrl+Shift+3)")
$toolTip.SetToolTip($cmbSort, "내가 직접 편집한 번역을 번역 시각순으로 정렬")
$toolTip.SetToolTip($chkApplyToRmk, "해제: 원본 모드 Languages\Korean · 선택: RMK 작업 클론")
$toolTip.SetToolTip($btnApply, "검토 완료 상태만 선택한 대상에 반영합니다.")
$toolTip.SetToolTip($btnApplyTranslated, "번역됨과 검토 완료 상태를 선택한 대상에 반영합니다.")
$toolTip.SetToolTip($btnRmkBuild, "LoadFolders.xml과 ModList.tsv를 다시 빌드합니다.")
$toolTip.SetToolTip($cmbDashboardTheme, "기본값은 Windows 앱 테마를 따릅니다.")
$toolTip.SetToolTip($cmbDashboardTextSize, "번역문, 기존 번역, AI 후보와 참고 탭의 글자 크기")

Sync-DashboardSettingsFromMain
Apply-TextSize
Resize-DashboardSettingsLayout

$form.Add_Resize({
    if ($script:layouting -or $form.WindowState -eq [System.Windows.Forms.FormWindowState]::Minimized) { return }
    $script:layouting = $true
    try {
        Apply-AppTheme
    } finally {
        $script:layouting = $false
    }
})

$autoSaveTimer = [System.Windows.Forms.Timer]::new()
$autoSaveTimer.Interval = 1200
$autoSaveTimer.Add_Tick({
    $autoSaveTimer.Stop()
    if (-not $script:autoSave -or -not $script:dirty -or -not $script:reviewRoot -or $script:currentRowIndex -lt 0) { return }
    try {
        Save-CurrentEdit
        Save-Decisions
    } catch {
        $lblSave.Text = "자동 저장 실패"
        Add-Log "자동 저장 실패: $($_.Exception.Message)"
    }
})

$dashboardSearchTimer = [System.Windows.Forms.Timer]::new()
$dashboardSearchTimer.Interval = 180
$dashboardSearchTimer.Add_Tick({
    $dashboardSearchTimer.Stop()
    Refresh-DashboardProjects
})

$searchTimer = [System.Windows.Forms.Timer]::new()
$searchTimer.Interval = 160
$searchTimer.Add_Tick({
    $searchTimer.Stop()
    if (-not $script:loading) { Refresh-ItemList -SelectRowIndex $script:currentRowIndex }
})

$cmbProject.Add_SelectedIndexChanged({
    if ($script:loadingProjectList -or -not $cmbProject.SelectedItem) { return }
    $project = $cmbProject.SelectedItem.Project
    if (-not $project) { return }
    Save-ReviewWithDuplicatePrompt
    Set-ActiveProject $project
    if ($project.modRoot -and (Test-Path -LiteralPath $project.modRoot -PathType Container)) {
        return
    } elseif ($project.latestReviewRoot -and (Test-Path -LiteralPath $project.latestReviewRoot -PathType Container)) {
        Load-ReviewRoot $project.latestReviewRoot
    }
})
$cmbModCatalog.Add_SelectedIndexChanged({
    if ($script:loadingProjectList -or -not $cmbModCatalog.SelectedItem -or -not $cmbModCatalog.Visible) { return }
    [void](Set-SelectedMod $cmbModCatalog.SelectedItem)
})
$btnRefreshMods.Add_Click({ Refresh-ModCatalog })
$btnChooseMod.Add_Click({ Choose-ModFolder })
$btnTranslate.Add_Click({ Start-Translation })
$btnStop.Add_Click({ Stop-Translation })
$btnApply.Add_Click({ Apply-ReviewedTranslations "ApprovedOnly" })
$btnApplyTranslated.Add_Click({ Apply-ReviewedTranslations "TranslatedAndApproved" })
$btnHome.Add_Click({ Show-Dashboard "projects" })
$btnLoad.Add_Click({ Load-SourceOnlyForSelectedMod })
$btnOpenFolder.Add_Click({ Open-ModFolder })
$btnSave.Add_Click({ Save-ReviewWithDuplicatePrompt })
$btnDashProjects.Add_Click({ Show-Dashboard "projects" })
$btnDashActivity.Add_Click({ Show-Dashboard "activity" })
$btnDashSettings.Add_Click({ Show-Dashboard "settings" })
$txtDashboardSearch.Add_TextChanged({
    $dashboardSearchTimer.Stop()
    $dashboardSearchTimer.Start()
})
$btnDashboardRefreshMods.Add_Click({ Refresh-ModCatalog; Refresh-DashboardProjects })
$btnDashboardChooseMod.Add_Click({ Choose-ModFolder })
$btnDashboardAddMod.Add_Click({
    if (-not $cmbDashboardMods.SelectedItem) {
        [System.Windows.Forms.MessageBox]::Show("프로젝트로 만들 모드를 선택하세요.", "RimWorld AI Translator") | Out-Null
        return
    }
    $project = Set-SelectedMod $cmbDashboardMods.SelectedItem
    if (-not $project) { return }
    Show-Workspace
    Load-SourceOnlyForSelectedMod
})
$dashSettingsPage.Add_Resize({ Resize-DashboardSettingsLayout })
$pnlApiSettings.Add_Resize({ Resize-ApiProviderSettingsLayout })
$pnlRmkSettings.Add_Resize({ Resize-RmkSettingsLayout })
foreach ($providerButton in $script:apiProviderButtons.Values) {
    $providerButton.Add_Click({ Select-ApiProvider ([string]$this.Tag) })
}
$txtDashboardApiKeys.Add_TextChanged({
    if ($script:syncingApiProvider) { return }
    $script:apiProviderKeys[$script:selectedApiProviderId] = [string]$txtDashboardApiKeys.Text
    Sync-MainSettingsFromDashboard
})
$txtApiProviderUrl.Add_Leave({ Save-CurrentApiProviderControls -Persist })
$txtApiProviderCustomName.Add_Leave({ Save-CurrentApiProviderControls -Persist; Show-ApiProviderControls -ProviderId $script:selectedApiProviderId -SkipCurrentSave })
$cmbApiProviderModel.Add_Leave({ Save-CurrentApiProviderControls -Persist })
$cmbApiProviderModel.Add_SelectedIndexChanged({ if (-not $script:syncingApiProvider) { Save-CurrentApiProviderControls -Persist } })
$cmbApiProviderTemperature.Add_Leave({ Save-CurrentApiProviderControls -Persist })
$cmbApiProviderTemperature.Add_SelectedIndexChanged({ if (-not $script:syncingApiProvider) { Save-CurrentApiProviderControls -Persist } })
$chkDashboardIncludePatches.Add_CheckedChanged({ Sync-MainSettingsFromDashboard })
$chkDashboardDryRun.Add_CheckedChanged({ Sync-MainSettingsFromDashboard })
$cmbDashboardTheme.Add_SelectedIndexChanged({ Apply-DashboardPreferences })
$cmbDashboardTextSize.Add_SelectedIndexChanged({ Apply-DashboardPreferences })
$chkDashboardHighContrast.Add_CheckedChanged({ Apply-DashboardPreferences })
$chkDashboardAutoSave.Add_CheckedChanged({ Apply-DashboardPreferences })
$btnDashboardRmkAuto.Add_Click({ AutoFind-RmkWorkspace })
$btnDashboardRmkChoose.Add_Click({ Choose-RmkWorkspace })
$btnDashboardRmkOpen.Add_Click({ Open-RmkFolder })
$chkDashboardRmkUseExisting.Add_CheckedChanged({
    if ($script:syncingSettings) { return }
    $script:rmkUseExisting = $chkDashboardRmkUseExisting.Checked
    Save-AppSettings
    Refresh-RmkPanel -Force
})
$btnRmkRefresh.Add_Click({ Refresh-RmkPanel -Force })
$btnRmkOpen.Add_Click({ Open-RmkFolder })
$btnRmkBuild.Add_Click({ Build-RmkWorkspace })
$cmbSearchField.Add_SelectedIndexChanged({ if (-not $script:loading) { Refresh-ItemList -SelectRowIndex $script:currentRowIndex } })
$txtSearch.Add_TextChanged({
    if ($script:loading) { return }
    $searchTimer.Stop()
    $searchTimer.Start()
})
$txtSearch.Add_KeyDown({
    if ($_.KeyCode -eq [System.Windows.Forms.Keys]::Down) {
        Move-Selection 1
        $_.SuppressKeyPress = $true
    } elseif ($_.KeyCode -eq [System.Windows.Forms.Keys]::Up) {
        Move-Selection -1
        $_.SuppressKeyPress = $true
    } elseif ($_.KeyCode -eq [System.Windows.Forms.Keys]::Enter) {
        [void]$txtTranslation.Focus()
        $_.SuppressKeyPress = $true
    }
})
foreach ($filterButton in $statusFilterButtons) {
    $filterButton.Add_Click({
        $index = $cmbStatus.Items.IndexOf([string]$this.Tag)
        if ($index -ge 0) { $cmbStatus.SelectedIndex = $index }
    })
}
$cmbStatus.Add_SelectedIndexChanged({
    Refresh-StatusFilterButtons
    if (-not $script:loading) { Update-SearchCrumb; Refresh-ItemList -SelectRowIndex $script:currentRowIndex }
})
$tabs.Add_SelectedIndexChanged({
    if ($tabs.SelectedTab -eq $tabRmk) {
        Refresh-RmkPanel
        return
    }
    if ($tabs.SelectedTab -ne $tabTerms -or $script:glossaryLoaded) { return }
    $form.UseWaitCursor = $true
    try {
        Load-Glossary
        if ($script:currentRowIndex -ge 0 -and $script:currentRowIndex -lt $script:rows.Count) {
            Update-TermsForRow $script:rows[$script:currentRowIndex]
        }
    } finally {
        $form.UseWaitCursor = $false
    }
})
$txtTranslation.Add_Enter({
    $lblTranslationTitle.Text = "번역문  ·  편집 중"
    $lblTranslationTitle.ForeColor = $script:accentColor
})
$txtTranslation.Add_Leave({
    if ($script:currentRowIndex -ge 0 -and $script:currentRowIndex -lt $script:rows.Count) {
        $originText = Get-TranslationOriginText ([string](Get-Decision $script:rows[$script:currentRowIndex]).translationOrigin)
        $lblTranslationTitle.Text = "번역문  ·  $originText"
    } else {
        $lblTranslationTitle.Text = "번역문"
    }
    $lblTranslationTitle.ForeColor = $script:mutedColor
})
$cmbSort.Add_SelectedIndexChanged({
    if (-not $script:loading) { Refresh-ItemList -SelectRowIndex $script:currentRowIndex }
})
$txtTranslation.Add_TextChanged({
    if (-not $script:loading) {
        if (-not $script:settingTranslationOrigin) { $script:translationEditorOrigin = "local" }
        $script:translationEditedByUser = -not [string]::Equals(
            (ConvertTo-FlatString $txtTranslation.Text),
            (ConvertTo-FlatString $script:translationEditBaseline),
            [System.StringComparison]::Ordinal
        )
        $script:dirty = $true
        $lblSave.Text = if ($script:autoSave) { "자동 저장 대기" } else { "저장 필요" }
        Queue-AutoSave
    }
})
$txtMemo.Add_TextChanged({
    if (-not $script:loading) {
        $script:dirty = $true
        $lblSave.Text = if ($script:autoSave) { "자동 저장 대기" } else { "저장 필요" }
        Queue-AutoSave
    }
})
$lvFiles.Add_SelectedIndexChanged({
    if ($lvFiles.SelectedItems.Count -eq 0) { return }
    $script:currentFile = [string]$lvFiles.SelectedItems[0].Tag
    Update-SearchCrumb
    Refresh-ItemList
})
$lvItems.Add_SelectedIndexChanged({
    if ($lvItems.SelectedItems.Count -eq 0) { return }
    Select-RowIndex ([int]$lvItems.SelectedItems[0].Tag)
})

$btnPrev.Add_Click({ Move-Selection -1 })
$btnNext.Add_Click({ Move-Selection 1 })
$btnUseCandidate.Add_Click({
    if ($script:currentRowIndex -ge 0) {
        Set-TranslationEditorValue -Text (ConvertTo-FlatString $script:rows[$script:currentRowIndex].candidate) -Origin "ai"
    }
})
$btnUseExisting.Add_Click({
    if ($script:currentRowIndex -ge 0) {
        $row = $script:rows[$script:currentRowIndex]
        $origin = Get-OptionalRowText -Row $row -Names @("existingOrigin")
        Set-TranslationEditorValue -Text (ConvertTo-FlatString $row.existing) -Origin $(if ($origin) { $origin } else { "existing" })
    }
})
$btnUseSource.Add_Click({ Copy-ToClipboard $txtTranslation.Text })
$btnResetEdit.Add_Click({
    if ($script:currentRowIndex -ge 0) {
        $decision = Get-Decision $script:rows[$script:currentRowIndex]
        $script:loading = $true
        try {
            $txtTranslation.Text = ConvertTo-FlatString $decision.text
            $script:translationEditorOrigin = [string]$decision.translationOrigin
        } finally { $script:loading = $false }
        $script:translationEditedByUser = $false
        $script:translationEditBaseline = ConvertTo-FlatString $decision.text
        $lblSave.Text = ""
    }
})
$btnPending.Add_Click({ Mark-Current "pending" $false })
$btnTranslated.Add_Click({ Mark-Current "translated" $false })
$btnApprove.Add_Click({ Mark-Current "approved" $false })
$btnApproveNext.Add_Click({ Mark-Current "approved" $true })
$btnApproveAll.Add_Click({ Approve-AllSafeTranslations })

$txtSource.Add_DoubleClick({ Copy-ToClipboard $txtSource.Text })
$txtCandidate.Add_DoubleClick({ Copy-ToClipboard $txtCandidate.Text })
$txtExisting.Add_DoubleClick({ Copy-ToClipboard $txtExisting.Text })
$txtMeta.Add_DoubleClick({ Copy-ToClipboard $txtMeta.Text })

$form.Add_KeyDown({
    if ($_.Control -and $_.KeyCode -eq [System.Windows.Forms.Keys]::F) {
        if ($dashboardPanel.Visible) {
            [void]$txtDashboardSearch.Focus()
            $txtDashboardSearch.SelectAll()
        } else {
            [void]$txtSearch.Focus()
            $txtSearch.SelectAll()
        }
        $_.SuppressKeyPress = $true
    } elseif ($_.Control -and $_.KeyCode -eq [System.Windows.Forms.Keys]::S) {
        Save-ReviewWithDuplicatePrompt
        $_.SuppressKeyPress = $true
    } elseif ($_.Control -and $_.Shift -and $_.KeyCode -eq [System.Windows.Forms.Keys]::Enter) {
        if (-not $dashboardPanel.Visible) { Mark-Current "approved" $false }
        $_.SuppressKeyPress = $true
    } elseif ($_.Control -and $_.KeyCode -eq [System.Windows.Forms.Keys]::Enter) {
        if (-not $dashboardPanel.Visible) { Mark-Current "approved" $true }
        $_.SuppressKeyPress = $true
    } elseif ($_.Control -and -not $_.Alt -and $_.KeyCode -in @([System.Windows.Forms.Keys]::D1, [System.Windows.Forms.Keys]::NumPad1)) {
        if (-not $dashboardPanel.Visible) { Mark-Current "pending" $false }
        $_.SuppressKeyPress = $true
    } elseif ($_.Control -and -not $_.Alt -and $_.KeyCode -in @([System.Windows.Forms.Keys]::D2, [System.Windows.Forms.Keys]::NumPad2)) {
        if (-not $dashboardPanel.Visible) { Mark-Current "translated" $false }
        $_.SuppressKeyPress = $true
    } elseif ($_.Control -and -not $_.Alt -and $_.KeyCode -in @([System.Windows.Forms.Keys]::D3, [System.Windows.Forms.Keys]::NumPad3)) {
        if (-not $dashboardPanel.Visible) {
            if ($_.Shift) { Approve-AllSafeTranslations } else { Mark-Current "approved" $false }
        }
        $_.SuppressKeyPress = $true
    } elseif ($_.Alt -and -not $_.Control -and $_.KeyCode -in @([System.Windows.Forms.Keys]::D1, [System.Windows.Forms.Keys]::NumPad1)) {
        if (-not $dashboardPanel.Visible -and $script:currentRowIndex -ge 0 -and -not [string]::IsNullOrWhiteSpace([string]$script:rows[$script:currentRowIndex].candidate)) { $btnUseCandidate.PerformClick() }
        $_.SuppressKeyPress = $true
    } elseif ($_.Alt -and -not $_.Control -and $_.KeyCode -in @([System.Windows.Forms.Keys]::D2, [System.Windows.Forms.Keys]::NumPad2)) {
        if (-not $dashboardPanel.Visible -and $script:currentRowIndex -ge 0 -and -not [string]::IsNullOrWhiteSpace([string]$script:rows[$script:currentRowIndex].existing)) { $btnUseExisting.PerformClick() }
        $_.SuppressKeyPress = $true
    } elseif ($_.Alt -and -not $_.Control -and $_.KeyCode -in @([System.Windows.Forms.Keys]::D0, [System.Windows.Forms.Keys]::NumPad0)) {
        if (-not $dashboardPanel.Visible -and $script:currentRowIndex -ge 0) { $btnResetEdit.PerformClick() }
        $_.SuppressKeyPress = $true
    } elseif ($_.KeyCode -eq [System.Windows.Forms.Keys]::F2) {
        if (-not $dashboardPanel.Visible -and $txtTranslation.Enabled) {
            [void]$txtTranslation.Focus()
            $txtTranslation.SelectionStart = $txtTranslation.TextLength
        }
        $_.SuppressKeyPress = $true
    } elseif ($_.Shift -and $_.KeyCode -eq [System.Windows.Forms.Keys]::F3) {
        if (-not $dashboardPanel.Visible) { Move-Selection -1 }
        $_.SuppressKeyPress = $true
    } elseif ($_.KeyCode -eq [System.Windows.Forms.Keys]::F3) {
        if (-not $dashboardPanel.Visible) { Move-Selection 1 }
        $_.SuppressKeyPress = $true
    } elseif ($_.KeyCode -eq [System.Windows.Forms.Keys]::F5) {
        if ($dashboardPanel.Visible) {
            Refresh-ModCatalog
            Refresh-DashboardProjects
        } elseif ($btnLoad.Enabled) {
            Load-SourceOnlyForSelectedMod
        }
        $_.SuppressKeyPress = $true
    } elseif ($_.Shift -and $_.KeyCode -eq [System.Windows.Forms.Keys]::F9) {
        if ($btnStop.Enabled) { Stop-Translation }
        $_.SuppressKeyPress = $true
    } elseif ($_.KeyCode -eq [System.Windows.Forms.Keys]::F9) {
        if (-not $dashboardPanel.Visible -and $btnTranslate.Enabled) { Start-Translation }
        $_.SuppressKeyPress = $true
    } elseif ($_.KeyCode -eq [System.Windows.Forms.Keys]::F6) {
        Focus-NextWorkRegion $(if ($_.Shift) { -1 } else { 1 })
        $_.SuppressKeyPress = $true
    } elseif ($_.KeyCode -eq [System.Windows.Forms.Keys]::Escape) {
        if ($dashboardPanel.Visible -and $txtDashboardSearch.Text) {
            $txtDashboardSearch.Clear()
            [void]$txtDashboardSearch.Focus()
        } elseif (-not $dashboardPanel.Visible -and ($txtSearch.Text -or $cmbStatus.SelectedIndex -gt 0)) {
            $txtSearch.Clear()
            $cmbStatus.SelectedIndex = 0
            [void]$txtSearch.Focus()
        }
        $_.SuppressKeyPress = $true
    } elseif ($_.Alt -and $_.KeyCode -in @([System.Windows.Forms.Keys]::Down, [System.Windows.Forms.Keys]::Right)) {
        if (-not $dashboardPanel.Visible) { Move-Selection 1 }
        $_.SuppressKeyPress = $true
    } elseif ($_.Alt -and $_.KeyCode -in @([System.Windows.Forms.Keys]::Up, [System.Windows.Forms.Keys]::Left)) {
        if (-not $dashboardPanel.Visible) { Move-Selection -1 }
        $_.SuppressKeyPress = $true
    }
})

$timer = [System.Windows.Forms.Timer]::new()
$timer.Interval = 250
$timer.Add_Tick({
    foreach ($line in (Read-NewProcessLogLines)) {
        Add-Log $line
        Update-ProgressFromLine $line
    }

    if ($script:process -and $script:process.HasExited -and -not $script:processExitHandled) {
        foreach ($line in (Read-NewProcessLogLines)) {
            Add-Log $line
            Update-ProgressFromLine $line
        }
        if (-not [string]::IsNullOrEmpty($script:translationLogPartial)) {
            Add-Log $script:translationLogPartial
            Update-ProgressFromLine $script:translationLogPartial
            $script:translationLogPartial = ""
        }

        $script:processExitHandled = $true
        $exitCode = $script:process.ExitCode
        $isSourceRefresh = $script:activeAiTranslationMode -eq "SourceOnly"
        $elapsed = if ($script:startedAt) { [Math]::Round(((Get-Date) - $script:startedAt).TotalSeconds, 1) } else { 0 }
        Add-Log "프로세스 종료. ExitCode=$exitCode, 경과 ${elapsed}s"

        if ($script:stopRequested) {
            $lblRunStatus.Text = "중지됨"
            Add-Log "사용자 요청으로 중지 완료."
        } elseif ($exitCode -eq 0) {
            if ($progressRun.Maximum -gt 0) { $progressRun.Value = $progressRun.Maximum }
            if ($script:lastReviewOutputPath -and (Test-Path -LiteralPath $script:lastReviewOutputPath -PathType Container)) {
                try {
                    if ($isSourceRefresh) {
                        $lblRunStatus.Text = "원문 목록 구성 중"
                        [System.Windows.Forms.Application]::DoEvents()
                    }
                    $replaceExisting = $script:activeAiTranslationMode -eq "Overwrite"
                    Load-ReviewRoot $script:lastReviewOutputPath -SkipPreviousDecisions:$replaceExisting
                    Register-ProjectRun -ReviewRoot $script:lastReviewOutputPath -Provider $script:lastProvider
                    if ($isSourceRefresh) {
                        $lblRunStatus.Text = "원문 로드 완료"
                        Add-Log "원문 목록을 불러왔습니다. 기존 한국어 번역이 있으면 번역칸의 기본값으로 사용합니다."
                    } elseif ($replaceExisting) {
                        $lblRunStatus.Text = "검수 결과 불러옴"
                        Add-Log "새 번역 후보로 기존 검수 번역을 교체해 불러왔습니다. 이전 작업 기록은 보존됩니다."
                    } else {
                        $lblRunStatus.Text = "검수 결과 불러옴"
                        Add-Log "기존 번역을 보존하고 새 번역 후보를 현재 화면에 불러왔습니다."
                    }
                } catch {
                    $lblRunStatus.Text = "검수 결과 열기 실패"
                    Add-Log "검수 결과를 열지 못했습니다: $($_.Exception.Message)"
                }
            } else {
                $lblRunStatus.Text = if ($isSourceRefresh) { "원문 로드 완료" } else { "완료" }
            }
        } else {
            $lblRunStatus.Text = if ($isSourceRefresh) { "원문 로드 실패" } else { "종료 코드 $exitCode" }
        }

        try { $script:process.Dispose() } catch {}
        $script:process = $null
        $script:stopRequested = $false
        $script:activeAiTranslationMode = ""
        Set-TranslationRunning $false
        Remove-TempFiles
    }
})
$timer.Start()

$form.Add_FormClosing({
    if ($autoSaveTimer) { $autoSaveTimer.Stop() }
    if ($dashboardSearchTimer) { $dashboardSearchTimer.Stop() }
    if ($searchTimer) { $searchTimer.Stop() }
    if ($script:startupCatalogTimer) {
        $script:startupCatalogTimer.Stop()
        $script:startupCatalogTimer.Dispose()
        $script:startupCatalogTimer = $null
    }
    Save-ProjectStatsCache
    if ($script:process -and -not $script:process.HasExited) {
        try { Stop-ProcessTree $script:process.Id } catch {}
    }
    Remove-TempFiles
    [void](Confirm-DuplicateSourceTranslation)
    if ($script:dirty) {
        $result = [System.Windows.Forms.MessageBox]::Show("저장하지 않은 검수 내용이 있습니다. 저장할까요?", "RimWorld AI Translator", [System.Windows.Forms.MessageBoxButtons]::YesNoCancel, [System.Windows.Forms.MessageBoxIcon]::Question)
        if ($result -eq [System.Windows.Forms.DialogResult]::Cancel) {
            $_.Cancel = $true
            return
        }
        if ($result -eq [System.Windows.Forms.DialogResult]::Yes) { Save-Decisions }
    }
    if ($script:textFingerprintSha256) {
        try { $script:textFingerprintSha256.Dispose() } catch {}
        $script:textFingerprintSha256 = $null
    }
})

$form.Add_FormClosed({
    foreach ($font in @($script:fontCache.Values)) {
        try { $font.Dispose() } catch {}
    }
    $script:fontCache.Clear()
})

Ensure-AppDataStore
Load-ProjectStore
Refresh-ProjectList

Add-Log "프로그램 시작 안내"
Add-Log "1. 프로젝트 화면에서 모드를 골라 프로젝트를 만드세요. 프로젝트 하나가 모드 하나입니다."
Add-Log "2. 프로젝트가 열리면 그 프로젝트의 모드만 원문 로드, AI 번역, 적용 대상으로 사용됩니다."
Add-Log "3. API 키를 비우면 Google 번역 후보를 생성합니다."
Add-Log "4. AI 후보는 번역됨 상태이며, 직접 확인하면 검토됨으로 바꿀 수 있습니다."
Add-Log "5. 원문이 바뀐 키는 업데이트 변경으로 표시되고 미번역으로 내려가며 적용 대상에서 제외됩니다."

$form.Add_Shown({
    try {
        if ($LayoutSnapshotWidth -gt 0 -and $LayoutSnapshotHeight -gt 0) {
            $form.WindowState = [System.Windows.Forms.FormWindowState]::Normal
            $form.ClientSize = [System.Drawing.Size]::new($LayoutSnapshotWidth, $LayoutSnapshotHeight)
        }
        Apply-AppTheme
        if ($script:initialReviewRoot) {
            Load-ReviewRoot $script:initialReviewRoot
            Show-Workspace
        } else {
            $initialTab = if ($InitialDashboardTab -in @("projects", "activity", "settings")) { $InitialDashboardTab } else { "projects" }
            Show-Dashboard $initialTab
        }
    } catch {
        [System.Windows.Forms.MessageBox]::Show("검토 결과를 열지 못했습니다.`r`n$($_.Exception.Message)", "RimWorld AI Translator") | Out-Null
    }

    # RMK 폴더 탐색은 설정 탭, 번역 시작, 내보내기처럼 실제로 필요할 때 수행한다.
    Update-RmkControls

    # 첫 화면을 먼저 그린 다음 모드 캐시를 검증한다. 느린 Steam 드라이브가 초기 창 표시를 막지 않는다.
    $script:startupCatalogTimer = [System.Windows.Forms.Timer]::new()
    $script:startupCatalogTimer.Interval = 50
    $script:startupCatalogTimer.Add_Tick({
        $script:startupCatalogTimer.Stop()
        try {
            Refresh-ModCatalog -PreferCache
        } catch {
            Add-Log "모드 자동 검색 실패: $($_.Exception.Message)"
        } finally {
            $script:startupCatalogTimer.Dispose()
            $script:startupCatalogTimer = $null
        }
    })
    $script:startupCatalogTimer.Start()

    if (-not [string]::IsNullOrWhiteSpace($script:layoutSnapshotPath)) {
        $script:layoutSnapshotTimer = [System.Windows.Forms.Timer]::new()
        $script:layoutSnapshotTimer.Interval = 1200
        $script:layoutSnapshotTimer.Add_Tick({
            $script:layoutSnapshotTimer.Stop()
            try {
                $snapshotDir = Split-Path -Parent $script:layoutSnapshotPath
                if ($snapshotDir -and -not (Test-Path -LiteralPath $snapshotDir)) {
                    New-Item -ItemType Directory -Force -Path $snapshotDir | Out-Null
                }
                $form.Refresh()
                [System.Windows.Forms.Application]::DoEvents()
                $bitmap = [System.Drawing.Bitmap]::new($form.ClientSize.Width, $form.ClientSize.Height)
                try {
                    $rect = [System.Drawing.Rectangle]::new(0, 0, $form.ClientSize.Width, $form.ClientSize.Height)
                    $form.DrawToBitmap($bitmap, $rect)
                    $bitmap.Save($script:layoutSnapshotPath, [System.Drawing.Imaging.ImageFormat]::Png)
                } finally {
                    $bitmap.Dispose()
                }
                $auditPath = [System.IO.Path]::ChangeExtension($script:layoutSnapshotPath, ".accessibility.json")
                $auditRows = @(Get-AccessibilityAuditRows -Parent $form)
                [System.IO.File]::WriteAllText(
                    $auditPath,
                    ($auditRows | ConvertTo-Json -Depth 5),
                    (New-Object System.Text.UTF8Encoding($false))
                )
                $runtimeLogPath = [System.IO.Path]::ChangeExtension($script:layoutSnapshotPath, ".runtime.log")
                [System.IO.File]::WriteAllText($runtimeLogPath, [string]$txtLog.Text, [System.Text.UTF8Encoding]::new($false))
            } finally {
                $form.Close()
            }
        })
        $script:layoutSnapshotTimer.Start()
    }
})

[void][System.Windows.Forms.Application]::Run($form)
