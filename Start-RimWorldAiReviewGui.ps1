param(
    [string]$ReviewRoot = "",
    [string]$LayoutSnapshotPath = "",
    [int]$LayoutSnapshotWidth = 0,
    [int]$LayoutSnapshotHeight = 0,
    [string]$InitialDashboardTab = "",
    [string]$InitialWorkspaceSideTab = "",
    [ValidateSet("", "loading", "error", "cancelled", "completed")]
    [string]$PreviewOperationState = "",
    [switch]$PreviewTranslationPreflight,
    [switch]$PreviewCommandPalette,
    [string]$PreviewTheme = "",
    [ValidateSet("", "Professional", "SciFi", "Vivid", "Studio", "Frontier")]
    [string]$PreviewDesignPreset = "",
    [int]$PreviewTextSize = 0,
    [switch]$PreviewHighContrast,
    [string]$AppDataRoot = "",
    [switch]$DisableBackgroundDiscovery,
    [string]$PerformanceReportPath = "",
    [ValidateRange(1, 20)]
    [int]$PerformanceIterations = 5
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
    if ($InitialWorkspaceSideTab) { $relaunchArguments += @("-InitialWorkspaceSideTab", "`"$InitialWorkspaceSideTab`"") }
    if ($PreviewOperationState) { $relaunchArguments += @("-PreviewOperationState", "`"$PreviewOperationState`"") }
    if ($PreviewTranslationPreflight) { $relaunchArguments += "-PreviewTranslationPreflight" }
    if ($PreviewCommandPalette) { $relaunchArguments += "-PreviewCommandPalette" }
    if ($PreviewTheme) { $relaunchArguments += @("-PreviewTheme", "`"$PreviewTheme`"") }
    if ($PreviewDesignPreset) { $relaunchArguments += @("-PreviewDesignPreset", "`"$PreviewDesignPreset`"") }
    if ($PreviewTextSize -gt 0) { $relaunchArguments += @("-PreviewTextSize", [string]$PreviewTextSize) }
    if ($PreviewHighContrast) { $relaunchArguments += "-PreviewHighContrast" }
    if ($AppDataRoot) { $relaunchArguments += @("-AppDataRoot", "`"$AppDataRoot`"") }
    if ($DisableBackgroundDiscovery) { $relaunchArguments += "-DisableBackgroundDiscovery" }
    if ($PerformanceReportPath) { $relaunchArguments += @("-PerformanceReportPath", "`"$PerformanceReportPath`"") }
    if ($PerformanceIterations -ne 5) { $relaunchArguments += @("-PerformanceIterations", [string]$PerformanceIterations) }
    Start-Process -FilePath $systemPowerShell -ArgumentList $relaunchArguments -WindowStyle Hidden
    return
}

$ErrorActionPreference = "Stop"
$script:startupWatch = [System.Diagnostics.Stopwatch]::StartNew()

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$storageScriptPath = Join-Path $scriptRoot "RimWorldAiTranslator.Storage.ps1"
if (-not (Test-Path -LiteralPath $storageScriptPath -PathType Leaf)) {
    throw "State storage component was not found: $storageScriptPath"
}
. $storageScriptPath
$projectCleanupScriptPath = Join-Path $scriptRoot "RimWorldAiTranslator.ProjectCleanup.ps1"
if (-not (Test-Path -LiteralPath $projectCleanupScriptPath -PathType Leaf)) {
    throw "Project cleanup component was not found: $projectCleanupScriptPath"
}
. $projectCleanupScriptPath
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
if (-not ("RimWorldTranslatorRowRuntimeCacheStore" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.CompilerServices;

public sealed class RimWorldTranslatorRowRuntimeCache
{
    public string Identity { get; set; }
    public string RelativeTarget { get; set; }
    public string SourceFingerprint { get; set; }
    public object Decision { get; set; }
    public object DefContext { get; set; }
    public string SearchKey { get; set; }
    public string SearchText { get; set; }
    public string SearchDefClass { get; set; }
    public string SearchNode { get; set; }
    public string SearchAll { get; set; }
    public string SourcePreview { get; set; }
    public string DefaultPreview { get; set; }

    public RimWorldTranslatorRowRuntimeCache()
    {
        Identity = String.Empty;
        RelativeTarget = String.Empty;
        SourceFingerprint = String.Empty;
    }
}

public sealed class RimWorldTranslatorRowRuntimeCacheStore
{
    private ConditionalWeakTable<object, RimWorldTranslatorRowRuntimeCache> entries =
        new ConditionalWeakTable<object, RimWorldTranslatorRowRuntimeCache>();

    public RimWorldTranslatorRowRuntimeCache Get(object row)
    {
        if (row == null) throw new ArgumentNullException("row");
        return entries.GetValue(row, delegate(object key) { return new RimWorldTranslatorRowRuntimeCache(); });
    }

    public void Reset()
    {
        entries = new ConditionalWeakTable<object, RimWorldTranslatorRowRuntimeCache>();
    }
}
"@
}
$validationScriptPath = Join-Path $scriptRoot "RimWorldAiTranslator.Validation.ps1"
if (-not (Test-Path -LiteralPath $validationScriptPath -PathType Leaf)) {
    throw "Translation validation component was not found: $validationScriptPath"
}
. $validationScriptPath
$providerValidationScriptPath = Join-Path $scriptRoot "RimWorldAiTranslator.ProviderValidation.ps1"
if (-not (Test-Path -LiteralPath $providerValidationScriptPath -PathType Leaf)) {
    throw "API provider validation component was not found: $providerValidationScriptPath"
}
. $providerValidationScriptPath
$translationMemoryScriptPath = Join-Path $scriptRoot "RimWorldAiTranslator.TranslationMemory.ps1"
if (-not (Test-Path -LiteralPath $translationMemoryScriptPath -PathType Leaf)) {
    throw "Translation memory component was not found: $translationMemoryScriptPath"
}
. $translationMemoryScriptPath
$diagnosticsScriptPath = Join-Path $scriptRoot "RimWorldAiTranslator.Diagnostics.ps1"
if (-not (Test-Path -LiteralPath $diagnosticsScriptPath -PathType Leaf)) {
    throw "Diagnostic component was not found: $diagnosticsScriptPath"
}
. $diagnosticsScriptPath
$uiSystemScriptPath = Join-Path $scriptRoot "RimWorldAiTranslator.UiSystem.ps1"
if (-not (Test-Path -LiteralPath $uiSystemScriptPath -PathType Leaf)) {
    throw "UI design component was not found: $uiSystemScriptPath"
}
. $uiSystemScriptPath
$qualityScriptPath = Join-Path $scriptRoot "RimWorldAiTranslator.Quality.ps1"
if (-not (Test-Path -LiteralPath $qualityScriptPath -PathType Leaf)) {
    throw "Translation quality component was not found: $qualityScriptPath"
}
. $qualityScriptPath
if (-not [string]::IsNullOrWhiteSpace($LayoutSnapshotPath) -or -not [string]::IsNullOrWhiteSpace($PerformanceReportPath)) {
    $uiAuditScriptPath = Join-Path $scriptRoot "RimWorldAiTranslator.UiAudit.ps1"
    if (-not (Test-Path -LiteralPath $uiAuditScriptPath -PathType Leaf)) {
        throw "UI audit component was not found: $uiAuditScriptPath"
    }
    . $uiAuditScriptPath
}
[System.Windows.Forms.Application]::EnableVisualStyles()
[System.Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)

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
$script:performanceReportPath = $PerformanceReportPath
$script:performanceIterations = $PerformanceIterations
$script:lastReviewLoadMetrics = $null
$script:startupCatalogTimer = $null
$script:startupCatalogCacheLoaded = $false
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
$script:rowRuntimeCacheStore = [RimWorldTranslatorRowRuntimeCacheStore]::new()
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
$script:startupSettingsError = ""
$script:visibleRowIndexes = @()
$script:visibleRowPositionMap = [int[]]@()
$script:syncingItemSelection = $false
$script:lastRenderedSelectionPosition = -1
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
$script:translationMemoryCache = @{}
$script:DisplayLocalizationFieldPattern = '^(label|labelshort|description|jobstring|reportstring|deathmessage|deathmessagefemale|deathmessagemale|letterlabel|lettertext|header|headertip|summary|formatstring|formatstringunfinalized|fixedname|reason|text|slateref)$'
$script:TechnicalLocalizationFieldPattern = '^(defname|parentname|classname|class|thingclass|workerclass|compclass|hediffclass|thoughtclass|abilityclass|worldobjectclass|nodeclass|debuglabel|tagdef|texpath|texname|graphicpath|shader|sound|sounddef|iconpath|packageid|xpath|operation|colorchannel|rendernode|rendertree|rendertreedef|bodypart|bodypartdef|bodytype|headtype|racedef|thingdef|pawnkinddef|jobdef|statdef|skilldef|hediffdef|genedef)$'
$script:DeniedLocalizationFields = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
$defFieldRulePath = Join-Path $scriptRoot "rimworld-def-field-rules.txt"
if (Test-Path -LiteralPath $defFieldRulePath -PathType Leaf) {
    foreach ($line in [System.IO.File]::ReadAllLines($defFieldRulePath, [System.Text.Encoding]::UTF8)) {
        if ($line -match '^\s*deny\t([A-Za-z_][A-Za-z0-9_]*)\s*$') { [void]$script:DeniedLocalizationFields.Add($matches[1]) }
    }
}
$script:appDataRoot = if ($AppDataRoot) { [System.IO.Path]::GetFullPath($AppDataRoot) } else { Join-Path $env:LOCALAPPDATA "RimWorldAiTranslator" }
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
$script:designPreset = "Professional"
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
$script:qualityEntries = @()
$script:qualityIssues = $null
$script:visibleQualityIssues = @()
$script:qualityDirty = $true
$script:lastQualityElapsedMs = 0
$script:lastOperationState = $null
$script:lastOperationType = ""
$script:operationPulse = 0
$script:tempFiles = New-Object "System.Collections.Generic.List[string]"
$script:startedAt = $null
$script:stopRequested = $false
$script:stopRequestedAt = $null
$script:cancellationFile = ""
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
    $script:designPreset = "Professional"
    $script:textSize = 10
    $script:highContrast = $false
    $script:autoSave = $true
    $script:rmkWorkspaceRoot = ""
    $script:rmkUseExisting = $true
    if (-not (Test-Utf8JsonStoreExists $script:settingsPath)) { return }
    try {
        $settings = Read-Utf8JsonFile $script:settingsPath
        if ([string]$settings.themeMode -in @("System", "Light", "Dark")) {
            $script:themeMode = [string]$settings.themeMode
        }
        if ($settings.PSObject.Properties["designPreset"] -and [string]$settings.designPreset -in @("Professional", "SciFi", "Vivid", "Studio", "Frontier")) {
            $script:designPreset = [string]$settings.designPreset
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
        throw "Settings and backup could not be loaded. $($_.Exception.Message)"
    }
}

function Save-AppSettings {
    Ensure-AppDataStore
    $settings = [ordered]@{
        version = 3
        themeMode = $script:themeMode
        designPreset = $script:designPreset
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

function Resize-WorkspaceLoadCover {
    if (-not $workspaceLoadCover -or -not $main) { return }
    $workspaceLoadCover.SetBounds($main.Left, $main.Top, [Math]::Max(1, $main.Width), [Math]::Max(1, $main.Height))
    $contentWidth = [Math]::Min(480, [Math]::Max(280, $workspaceLoadCover.ClientSize.Width - 48))
    $contentX = [Math]::Max(24, [int](($workspaceLoadCover.ClientSize.Width - $contentWidth) / 2))
    $contentY = [Math]::Max(48, [int](($workspaceLoadCover.ClientSize.Height - 86) / 2))
    $lblWorkspaceLoadTitle.SetBounds($contentX, $contentY, $contentWidth, 28)
    $lblWorkspaceLoadDetail.SetBounds($contentX, ($contentY + 32), $contentWidth, 22)
    $progressWorkspaceLoad.SetBounds($contentX, ($contentY + 64), $contentWidth, 5)
}

function Show-WorkspaceLoadCover([string]$Title = "프로젝트 구성 중", [string]$Detail = "문자열과 검수 상태를 한 번에 준비하고 있습니다.") {
    if (-not $workspaceLoadCover -or -not $form.Visible -or -not $main.Visible -or $form.Opacity -lt 0.99) { return $false }
    $lblWorkspaceLoadTitle.Text = $Title
    $lblWorkspaceLoadDetail.Text = $Detail
    Resize-WorkspaceLoadCover
    $main.SuspendLayout()
    $form.UseWaitCursor = $true
    $progressWorkspaceLoad.Style = [System.Windows.Forms.ProgressBarStyle]::Marquee
    $progressWorkspaceLoad.MarqueeAnimationSpeed = 24
    $workspaceLoadCover.Visible = $true
    $workspaceLoadCover.BringToFront()
    if ($operationOverlay -and $operationOverlay.Visible) { $operationOverlay.BringToFront() }
    $workspaceLoadCover.Update()
    [System.Windows.Forms.Application]::DoEvents()
    return $true
}

function Hide-WorkspaceLoadCover([bool]$WasShown) {
    if (-not $WasShown) { return }
    try {
        $main.ResumeLayout($true)
    } finally {
        $progressWorkspaceLoad.MarqueeAnimationSpeed = 0
        $workspaceLoadCover.Visible = $false
        $form.UseWaitCursor = $false
        $main.Invalidate($true)
    }
}

function Remove-StaleTempFiles {
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) "RimWorldAiTranslatorGui"
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) { return }
    try {
        $item = Get-Item -LiteralPath $dir -Force -ErrorAction Stop
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { return }
        $cutoff = [DateTime]::UtcNow.AddDays(-2)
        foreach ($file in Get-ChildItem -LiteralPath $dir -File -Force -ErrorAction SilentlyContinue) {
            if ($file.LastWriteTimeUtc -lt $cutoff) {
                Remove-Item -LiteralPath $file.FullName -Force -ErrorAction SilentlyContinue
            }
        }
    } catch {
    }
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
    $operation = Get-RimWorldOperationStateFromLine $Line
    if ($operation.IsDeterminate -and $operation.Total -gt 0) {
        $progressRun.Maximum = [int]$operation.Total
        $progressRun.Value = [Math]::Min([int]$operation.Current, [int]$operation.Total)
        $lblRunStatus.Text = "$($operation.Label) $($operation.Current) / $($operation.Total)"
    } elseif ($operation.Stage -ne "activity") {
        $lblRunStatus.Text = [string]$operation.Label
    }
    Update-OperationOverlay -Operation $operation -Line $Line

    if ($Line -match "^Review output:\s+(.+)$") {
        $script:lastReviewOutputPath = $matches[1].Trim()
    } elseif ($Line -match "^Translation provider:\s+(.+)$") {
        $script:lastProvider = $matches[1].Trim()
    }
    if ($Line -match "^Done\.$") {
        if ($progressRun.Maximum -gt 0) { $progressRun.Value = $progressRun.Maximum }
    }
}

function Add-OperationOverlayLog([string]$Line) {
    if (-not $txtOperationLog -or [string]::IsNullOrWhiteSpace($Line)) { return }
    $safeLine = ConvertTo-GuiLogLine $Line
    if ([string]::IsNullOrWhiteSpace($safeLine)) { return }
    $lines = New-Object "System.Collections.Generic.List[string]"
    foreach ($existing in @($txtOperationLog.Lines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) { [void]$lines.Add([string]$existing) }
    [void]$lines.Add($safeLine)
    while ($lines.Count -gt 7) { $lines.RemoveAt(0) }
    $txtOperationLog.Lines = $lines.ToArray()
    $txtOperationLog.SelectionStart = $txtOperationLog.TextLength
    $txtOperationLog.ScrollToCaret()
}

function Update-OperationOverlay {
    param([object]$Operation, [string]$Line = "")
    if (-not $operationOverlay -or -not $Operation) { return }
    if ($Line) { Add-OperationOverlayLog $Line }
    $script:lastOperationState = $Operation
    $lblOperationStage.Text = [string]$Operation.Label
    if ($Operation.Detail) { $lblOperationDetail.Text = [string]$Operation.Detail }
    if ($Operation.IsDeterminate -and [int]$Operation.Total -gt 0) {
        $progressOperation.Style = [System.Windows.Forms.ProgressBarStyle]::Continuous
        $progressOperation.MarqueeAnimationSpeed = 0
        $progressOperation.Maximum = [int]$Operation.Total
        $progressOperation.Value = [Math]::Min([int]$Operation.Current, [int]$Operation.Total)
        $percent = [int][Math]::Round(100 * ([int]$Operation.Current / [double][int]$Operation.Total))
        $lblOperationCount.Text = "$($Operation.Current) / $($Operation.Total) 배치  ·  ${percent}%"
    } else {
        if ([string]$Operation.Kind -notin @("completed", "cancelled", "error")) {
            $progressOperation.Style = [System.Windows.Forms.ProgressBarStyle]::Marquee
            $progressOperation.MarqueeAnimationSpeed = 28
        }
        $lblOperationCount.Text = if ([int]$Operation.Total -gt 0) { "$($Operation.Total.ToString('N0'))개 확인" } else { "작업 응답 대기 중" }
    }
    if ($pnlOperationScan) { $pnlOperationScan.Invalidate() }
}

function Show-OperationOverlay([string]$Title, [string]$Detail, [string]$OperationType = "Translation") {
    if (-not $operationOverlay) { return }
    if ($operationDismissTimer) { $operationDismissTimer.Stop() }
    $script:lastOperationType = $OperationType
    $script:operationReturnToDashboard = [bool]($dashboardPanel -and $dashboardPanel.Visible)
    $script:operationPulse = 0
    $txtOperationLog.Clear()
    $lblOperationTitle.Text = $Title
    $lblOperationStage.Text = "작업공간 확인"
    $lblOperationDetail.Text = $Detail
    $lblOperationCount.Text = "작업 응답 대기 중"
    $progressOperation.Style = [System.Windows.Forms.ProgressBarStyle]::Marquee
    $progressOperation.MarqueeAnimationSpeed = 28
    $btnOperationCancel.Visible = $true
    $btnOperationCancel.Enabled = $true
    $btnOperationRetry.Visible = $false
    $btnOperationReview.Visible = $false
    $btnOperationClose.Visible = $false
    $operationOverlay.Visible = $true
    Update-OperationContentLayout
    $operationOverlay.BringToFront()
    Resize-OperationOverlay
}

function Complete-OperationOverlay([string]$Kind, [string]$Title, [string]$Detail) {
    if (-not $operationOverlay) { return }
    $progressOperation.Style = [System.Windows.Forms.ProgressBarStyle]::Continuous
    $progressOperation.MarqueeAnimationSpeed = 0
    $progressOperation.Maximum = 100
    $progressOperation.Value = if ($Kind -eq "completed") { 100 } else { 0 }
    $lblOperationStage.Text = $Title
    $lblOperationDetail.Text = $Detail
    $lblOperationCount.Text = switch ($Kind) {
        "completed" { "결과 저장됨" }
        "cancelled" { "완료분 보존" }
        default { "실패 · 재시도 가능" }
    }
    $btnOperationCancel.Visible = $false
    $btnOperationRetry.Visible = $Kind -eq "error"
    $btnOperationReview.Visible = $false
    $btnOperationClose.Visible = $Kind -eq "error"
    Resize-OperationOverlay
    if ($pnlOperationScan) { $pnlOperationScan.Invalidate() }
    if ($Kind -in @("completed", "cancelled") -and -not $PreviewOperationState -and $operationDismissTimer) {
        $operationDismissTimer.Stop()
        $operationDismissTimer.Start()
    }
}

function Hide-OperationOverlay {
    if (-not $operationOverlay) { return }
    if ($operationDismissTimer) { $operationDismissTimer.Stop() }
    $operationOverlay.Visible = $false
    $progressOperation.MarqueeAnimationSpeed = 0
    Update-OperationContentLayout
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
    return $script:rowRuntimeCacheStore.Get($Row)
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
    if (-not (Test-Utf8JsonStoreExists $script:projectStatsCachePath)) { return }
    try {
        $json = Read-Utf8JsonFile $script:projectStatsCachePath
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
    if (Test-Utf8JsonStoreExists $script:projectStorePath) {
        $script:projects = @(Read-RimWorldProjectStore $script:projectStorePath)
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
    return Test-RimWorldPathStrictlyInsideRoot -Path $Path -Root $Root
}

function Test-PathContainsReparsePoint([string]$Path, [string]$StopRoot) {
    return Test-RimWorldPathContainsReparsePoint -Path $Path -StopRoot $StopRoot
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
    $modRoot = if ($Project -and $Project.modRoot) { [string]$Project.modRoot } else { "" }
    return Get-RimWorldAppOwnedReviewDirectory -Path $Path -ReviewRoots @(Get-AppOwnedReviewRoots) -ModRoot $modRoot
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
    return Get-RimWorldProjectCleanupPlan -Project $Project -ReviewRoots @(Get-AppOwnedReviewRoots)
}

function Remove-AppOwnedProjectReviewDirectories([object]$Project, [string[]]$Paths) {
    return Remove-RimWorldAppOwnedReviewDirectories -Project $Project -ReviewRoots @(Get-AppOwnedReviewRoots) -Paths $Paths
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
    if (@($plan.MarkerErrors).Count -gt 0) {
        $message = $message.Replace("`r`n`r`n계속할까요?", "`r`n- 소유권 표식을 읽지 못한 검수 폴더 $(@($plan.MarkerErrors).Count)개`r`n`r`n계속할까요?")
    }
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
        $xmlCount = 0
        try {
            foreach ($xmlPath in [System.IO.Directory]::EnumerateFiles($directory.FullName, "*.xml", [System.IO.SearchOption]::AllDirectories)) {
                $xmlCount++
            }
        } catch {
        }
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
    if (-not (Test-Utf8JsonStoreExists $script:modCatalogCachePath)) { return $false }
    try {
        $cache = Read-Utf8JsonFile $script:modCatalogCachePath
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
    $hasUsableIndex = $rows.Count -gt 0 -and (Test-Path -LiteralPath (Join-Path $rootFull "ModList.tsv") -PathType Leaf)
    $targetMatches = if ($workshopId) { @($rows | Where-Object { $_.WorkshopId -eq $workshopId }) } else { @() }
    if ($targetMatches.Count -eq 0 -and $packageId) {
        $targetMatches = @($rows | Where-Object { $_.PackageId -ieq $packageId })
    }
    if ($hasUsableIndex -and $targetMatches.Count -eq 0) {
        $script:rmkTargetCache[$cacheKey] = @()
        return @()
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

    if ($yamlPaths.Count -eq 0 -and -not $hasUsableIndex -and (Test-Path -LiteralPath $dataRoot -PathType Container)) {
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
    $message = "$modeLabel $eligibleCount개를 RMK 작업 클론으로 내보냅니다.`r`n`r`n$creationText`r`n원문 이력 XLSX도 생성하거나 데이터 손실 없이 갱신합니다.`r`n대상: $targetPath`r`n`r`n완료 후 LoadFoldersBuilder를 실행하지만 Git 커밋이나 푸시는 하지 않습니다."
    $answer = [System.Windows.Forms.MessageBox]::Show($message, "RMK 내보내기", [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Information, [System.Windows.Forms.MessageBoxDefaultButton]::Button2)
    if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return }

    Set-TranslationRunning $true
    $lblRunStatus.Text = "RMK 내보내기 중"
    try {
        if (-not $target) { $target = New-RmkTarget -Project $project -WorkspaceRoot $script:rmkWorkspaceRoot }
        $sourceLanguage = Get-SelectedProjectSourceLanguage
        $exportArguments = @(
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $script:rmkExportScript,
            "-RmkEntryRoot", [string]$target.Root,
            "-ReviewRoot", $script:reviewRoot,
            "-ReviewLanguageFolderName", "Korean",
            "-RmkLanguageFolderName", "Korean (한국어)",
            "-SourceLanguage", $sourceLanguage,
            "-ApplyStatus", $ApplyStatus,
            "-Overwrite"
        )
        if ($target.PSObject.Properties["WorkbookPath"] -and $target.WorkbookPath) {
            $exportArguments += @("-WorkbookPath", [string]$target.WorkbookPath)
        }
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
        [System.Windows.Forms.MessageBox]::Show("RMK 번역 XML, 원문 이력 XLSX와 LoadFolders 빌드가 완료됐습니다.`r`nGit 커밋·푸시는 하지 않았습니다.", "RMK 내보내기") | Out-Null
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

function Get-ProtectedTokenCountsForValidation([string]$Text) {
    return Get-RimWorldProtectedTokenCounts $Text
}

function Get-TranslationTokenIssues([string]$Source, [string]$Translation) {
    return Get-RimWorldTokenPreservationIssues -Source $Source -Target $Translation
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
    if ($script:DeniedLocalizationFields.Contains($fieldLower)) { return "RimWorld NoTranslate 필드 '$fieldLower'" }
    if ($fieldLower -match $script:TechnicalLocalizationFieldPattern) { return "내부 참조 필드 '$fieldLower'" }
    if ($keyLower -match "\.alienrace\.generalsettings\.alienpartgenerator\.colorchannels\.") { return "AlienRace 색상 채널 식별자" }
    if ($fieldLower -eq "name" -and $keyLower -match "\.alienrace\.") { return "AlienRace 내부 이름" }
    if ($fieldLower -eq "name" -and $keyLower -match "\.(colorchannels|bodyaddons|powermodes)\.") { return "실행 중 사용하는 목록 식별자" }
    if ($keyLower -match "\.(graphicpaths?|rendernodes?|rendertree)\." -and -not $isDisplayField) { return "렌더링 또는 그래픽 경로 식별자" }
    if ($typeLower -match "pawnrendertreedef" -and -not $isDisplayField) { return "PawnRenderTreeDef 내부 식별자" }
    return ""
}

function Test-PathologicalTranslationText([string]$Text) {
    return Test-RimWorldPathologicalTranslation $Text
}

function Test-ContainsKoreanText([string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return $false }
    return $Text -match "[\uAC00-\uD7AF]"
}

function Get-InvalidKoreanParticleNotations([string]$Text) {
    return @(Get-RimWorldInvalidKoreanParticleNotations $Text)
}

function Get-TranslationValidation([object]$Row, [string]$Translation) {
    $source = ConvertTo-FlatString $Row.source
    $translationText = ConvertTo-FlatString $Translation
    $isBlank = [string]::IsNullOrWhiteSpace($translationText)
    $isPathological = Test-PathologicalTranslationText $translationText
    $tokenIssues = Get-TranslationTokenIssues -Source $source -Translation $translationText
    $missingTokens = @($tokenIssues.MissingTokens)
    $unexpectedTokens = @($tokenIssues.UnexpectedTokens)
    $tokenCountMismatches = @($tokenIssues.TokenCountMismatches)
    $sameAsSource = [string]::Equals($source, $translationText, [System.StringComparison]::Ordinal)
    $hasKorean = Test-ContainsKoreanText $translationText
    $invalidKoreanParticles = @(Get-InvalidKoreanParticleNotations $translationText)
    $safeToApply = -not $isBlank -and -not $isPathological -and $missingTokens.Count -eq 0 -and $unexpectedTokens.Count -eq 0 -and $tokenCountMismatches.Count -eq 0 -and -not $tokenIssues.GrammarPrefixMoved -and $invalidKoreanParticles.Count -eq 0 -and -not $sameAsSource -and $hasKorean
    return [pscustomobject]@{
        SafeToApply = $safeToApply
        IsBlank = $isBlank
        IsPathological = $isPathological
        MissingTokens = $missingTokens
        UnexpectedTokens = $unexpectedTokens
        TokenCountMismatches = $tokenCountMismatches
        GrammarPrefixMoved = [bool]$tokenIssues.GrammarPrefixMoved
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
    if ($validation.UnexpectedTokens.Count -gt 0) { [void]$warnings.Add("추가된 토큰: $([string]::Join('|', $validation.UnexpectedTokens))") }
    if ($validation.TokenCountMismatches.Count -gt 0) { [void]$warnings.Add("토큰 개수 변경: $([string]::Join('|', $validation.TokenCountMismatches))") }
    if ($validation.GrammarPrefixMoved) { [void]$warnings.Add("문법 접두사 위치 변경") }
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
    $rmkSourceChanged = $translationOrigin -ne "ai" -and $Row.PSObject.Properties["rmkSourceChanged"] -and (ConvertTo-BoolValue $Row.rmkSourceChanged)
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

function Get-ExistingDecision([object]$Row) {
    $identity = Get-RowIdentity $Row
    if (-not $script:decisions.ContainsKey($identity)) { return $null }
    $cache = Get-RowRuntimeCache $Row
    if ($cache.Decision) { return $cache.Decision }
    if ($script:validateLoadedDecisionSources) { return Get-Decision $Row }
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
    $rmkSourceChangedNow = $translationOrigin -ne "ai" -and $Row.PSObject.Properties["rmkSourceChanged"] -and (ConvertTo-BoolValue $Row.rmkSourceChanged)
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
    Invalidate-TranslationMemoryCache
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
    if (-not $path -or -not (Test-Utf8JsonStoreExists $path)) { return }
    try {
        if ($script:comparisonFile -and (Test-Path -LiteralPath $script:comparisonFile -PathType Leaf)) {
            $decisionReadPath = if (Test-Path -LiteralPath $path -PathType Leaf) { $path } else { "$path.bak" }
            $decisionInfo = Get-Item -LiteralPath $decisionReadPath -ErrorAction Stop
            $comparisonInfo = Get-Item -LiteralPath $script:comparisonFile -ErrorAction Stop
            $script:validateLoadedDecisionSources = $comparisonInfo.LastWriteTimeUtc -gt $decisionInfo.LastWriteTimeUtc
        }
        $json = Read-Utf8JsonFile $path
        if (-not $json -or -not $json.PSObject.Properties["items"]) {
            Block-Utf8JsonStoreWrites $path
            throw "Review decision store is missing the items collection."
        }
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
        $script:rows = @()
        $script:decisions = @{}
        throw "검수 상태 파일을 읽지 못했습니다. 원본과 백업은 보존되며 이 작업은 저장할 수 없습니다. $($_.Exception.Message)"
    }
}

function Get-PreviousProjectReviewRoots([object]$Project, [string]$CurrentRoot) {
    if (-not $Project -or [string]::IsNullOrWhiteSpace($CurrentRoot)) { return "" }

    $currentFull = [System.IO.Path]::GetFullPath($CurrentRoot).TrimEnd("\", "/")
    $candidateRoots = New-Object "System.Collections.Generic.List[string]"
    if ($Project.latestReviewRoot) { [void]$candidateRoots.Add([string]$Project.latestReviewRoot) }
    foreach ($run in @($Project.runs | Sort-Object createdAt -Descending)) {
        if ($run.reviewRoot) { [void]$candidateRoots.Add([string]$run.reviewRoot) }
    }

    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    $result = New-Object "System.Collections.Generic.List[string]"
    foreach ($candidateRoot in $candidateRoots) {
        try {
            $candidateFull = [System.IO.Path]::GetFullPath($candidateRoot).TrimEnd("\", "/")
        } catch {
            continue
        }
        if (-not $seen.Add($candidateFull) -or $candidateFull -eq $currentFull) { continue }
        if (Test-Path -LiteralPath $candidateFull -PathType Container) { [void]$result.Add($candidateFull) }
    }
    return $result.ToArray()
}

function Find-PreviousProjectDecisionFile([object]$Project, [string]$CurrentRoot) {
    foreach ($candidateFull in @(Get-PreviousProjectReviewRoots -Project $Project -CurrentRoot $CurrentRoot)) {
        $decisionFile = Join-Path $candidateFull "review-decisions.json"
        if (Test-Path -LiteralPath $decisionFile -PathType Leaf) { return $decisionFile }
    }
    return ""
}

function Find-PreviousProjectComparisonFile([object]$Project, [string]$CurrentRoot) {
    foreach ($candidateFull in @(Get-PreviousProjectReviewRoots -Project $Project -CurrentRoot $CurrentRoot)) {
        $auditRoot = Join-Path $candidateFull "_TranslationAudit"
        if (-not (Test-Path -LiteralPath $auditRoot -PathType Container)) { continue }
        $comparisonFile = Get-ChildItem -LiteralPath $auditRoot -File -Filter "*-comparison.json" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($comparisonFile) { return $comparisonFile.FullName }
    }
    return ""
}

function Get-ReviewSourceIdentity([object]$Row) {
    if (-not $Row) { return "" }
    $kind = Get-OptionalRowText -Row $Row -Names @("kind")
    $defClass = Get-OptionalRowText -Row $Row -Names @("defClass", "typeName")
    $node = Get-OptionalRowText -Row $Row -Names @("node", "key")
    if (-not $node) { return "" }
    $className = if ($kind -eq "Keyed") { "Keyed" } elseif ($defClass) { $defClass } else { $kind }
    if (-not $className) { return "" }
    return "$className+$node"
}

function Test-ReviewSourceTextEqual([string]$Left, [string]$Right) {
    $leftText = ((ConvertTo-FlatString $Left) -replace "`r`n", "`n" -replace "`r", "`n").Trim()
    $rightText = ((ConvertTo-FlatString $Right) -replace "`r`n", "`n" -replace "`r", "`n").Trim()
    $leftText = [System.Text.RegularExpressions.Regex]::Replace($leftText, '[ \t\u00A0]+(?=\n|$)', '')
    $rightText = [System.Text.RegularExpressions.Regex]::Replace($rightText, '[ \t\u00A0]+(?=\n|$)', '')
    return [string]::Equals($leftText, $rightText, [System.StringComparison]::Ordinal)
}

function Import-PreviousProjectDecisions {
    $path = Get-DecisionPath
    if ($path -and (Test-Path -LiteralPath $path)) { return }
    if (-not $script:selectedProjectId -or -not $script:reviewRoot) { return }
    $project = @($script:projects | Where-Object { $_.id -eq $script:selectedProjectId } | Select-Object -First 1)
    if ($project.Count -eq 0) { return }
    $currentRoot = [System.IO.Path]::GetFullPath($script:reviewRoot)
    $previousDecisionFile = Find-PreviousProjectDecisionFile -Project $project[0] -CurrentRoot $currentRoot
    $previousComparisonFile = Find-PreviousProjectComparisonFile -Project $project[0] -CurrentRoot $currentRoot
    if (-not $previousDecisionFile -and -not $previousComparisonFile) { return }

    try {
        $json = if ($previousDecisionFile) { [System.IO.File]::ReadAllText($previousDecisionFile, [System.Text.Encoding]::UTF8) | ConvertFrom-Json } else { $null }
        $targetKeyLookup = @{}
        $uniqueKeyLookup = @{}
        $ambiguousKeys = @{}
        $idOnlyLookup = @{}
        $previousDecisionItems = if ($json) { @($json.items) } else { @() }
        foreach ($item in $previousDecisionItems) {
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

        $previousSourceLookup = New-Object "System.Collections.Generic.Dictionary[string,string]" ([System.StringComparer]::OrdinalIgnoreCase)
        $ambiguousSourceIdentities = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
        if ($previousComparisonFile) {
            [object[]]$previousRows = [System.IO.File]::ReadAllText($previousComparisonFile, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
            foreach ($previousRow in $previousRows) {
                $sourceIdentity = Get-ReviewSourceIdentity $previousRow
                if (-not $sourceIdentity -or $ambiguousSourceIdentities.Contains($sourceIdentity)) { continue }
                $previousSource = ConvertTo-FlatString $previousRow.source
                if ($previousSourceLookup.ContainsKey($sourceIdentity)) {
                    if (-not (Test-ReviewSourceTextEqual -Left $previousSourceLookup[$sourceIdentity] -Right $previousSource)) {
                        [void]$previousSourceLookup.Remove($sourceIdentity)
                        [void]$ambiguousSourceIdentities.Add($sourceIdentity)
                    }
                    continue
                }
                $previousSourceLookup[$sourceIdentity] = $previousSource
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
            $decision = if ($item) {
                [pscustomobject]@{
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
            } else {
                New-Decision $row
            }
            Normalize-DecisionForRow -Row $row -Decision $decision
            $sourceIdentity = Get-ReviewSourceIdentity $row
            $changedFromPreviousSnapshot = $false
            if ($sourceIdentity -and $previousSourceLookup.ContainsKey($sourceIdentity)) {
                $previousSource = ConvertTo-FlatString $previousSourceLookup[$sourceIdentity]
                $currentSource = ConvertTo-FlatString $row.source
                if (-not [string]::IsNullOrWhiteSpace($previousSource) -and -not (Test-ReviewSourceTextEqual -Left $previousSource -Right $currentSource)) {
                    $changedFromPreviousSnapshot = $true
                    $decision.status = "pending"
                    if (-not (ConvertTo-BoolValue $decision.sourceChanged) -or [string]::IsNullOrWhiteSpace((ConvertTo-FlatString $decision.previousSourceText))) {
                        $decision.previousSourceText = $previousSource
                    }
                    $decision.sourceChanged = $true
                    $decision.sourceHash = Get-RowSourceFingerprint $row
                    $decision.sourceText = $currentSource
                    if ([string]::IsNullOrWhiteSpace([string]$decision.updatedAt)) { $decision.updatedAt = (Get-Date).ToString("o") }
                }
            }
            if ($item -or $changedFromPreviousSnapshot -or (ConvertTo-BoolValue $decision.sourceChanged)) {
                $script:decisions[(Get-RowIdentity $row)] = $decision
            }
            if ($item) { $imported++ }
            if (ConvertTo-BoolValue $decision.sourceChanged) { $changedSources++ }
        }
        if ($imported -gt 0 -or $changedSources -gt 0) {
            Add-Log "이전 검수 상태 ${imported}개를 이어받았습니다."
            if ($changedSources -gt 0) { Add-Log "직전 프로젝트 원문과 달라진 ${changedSources}개 항목을 변경됨·미번역 상태로 표시했습니다." }
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
    if (-not $script:dirty) { return }
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
        $defaultChanged = $defaultOrigin -ne "ai" -and $row.PSObject.Properties["rmkSourceChanged"] -and (ConvertTo-BoolValue $row.rmkSourceChanged)
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
    $editorText = ConvertTo-FlatString $txtTranslation.Text
    $textChanged = (ConvertTo-FlatString $decision.text) -ne $editorText
    $textEditedByUser = $textChanged -and $script:translationEditedByUser
    if ($textChanged -and -not $textEditedByUser) { $textChanged = $false }
    $noteChanged = $decision.note -ne $txtMemo.Text
    if ($textChanged -or $noteChanged) {
        $before = if ($script:reviewStats) { Get-DecisionStateSnapshot $row } else { $null }
        if ($textChanged) { $decision.text = $editorText }
        $decision.note = [string]$txtMemo.Text
        if ($textEditedByUser) {
            $decision.translationOrigin = if ($script:translationEditorOrigin) { [string]$script:translationEditorOrigin } else { "local" }
            $decision.translationUpdatedAt = (Get-Date).ToString("o")
        }
        if ([string]::IsNullOrWhiteSpace($decision.text)) {
            $decision.status = "pending"
        } elseif ($textEditedByUser -and ($decision.status -eq "pending" -or $decision.status -eq "approved")) {
            $decision.status = "translated"
            $decision.sourceChanged = $false
            $decision.previousSourceText = ""
        }
        $decision.updatedAt = (Get-Date).ToString("o")
        $script:dirty = $true
        Invalidate-TranslationMemoryCache
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
        Invalidate-TranslationMemoryCache
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
    $cache = Get-RowRuntimeCache $Row
    $cacheProperty = switch ($Mode) {
        "키" { "SearchKey" }
        "텍스트" { "SearchText" }
        "Def Class" { "SearchDefClass" }
        "Node" { "SearchNode" }
        default { "SearchAll" }
    }
    $staticBlob = $cache.$cacheProperty
    if ($null -eq $staticBlob) {
        $staticBlob = switch ($Mode) {
            "키" {
                @([string]$Row.id, [string]$Row.key, [string]$Row.target) -join "`n"
            }
            "텍스트" {
                @(
                    (ConvertTo-FlatString $Row.source),
                    (ConvertTo-FlatString $Row.existing),
                    (ConvertTo-FlatString $Row.candidate)
                ) -join "`n"
            }
            "Def Class" {
                $context = Get-RowDefContext $Row
                @(
                    [string]$context.DefClass,
                    [string]$context.DefName,
                    (Get-OptionalRowText -Row $Row -Names @("defClass", "defType", "typeName", "TypeName"))
                ) -join "`n"
            }
            "Node" {
                $context = Get-RowDefContext $Row
                @(
                    [string]$context.Node,
                    [string]$context.Field,
                    (Get-OptionalRowText -Row $Row -Names @("node", "field", "Field"))
                ) -join "`n"
            }
            default {
                @(
                    [string]$Row.id,
                    [string]$Row.key,
                    [string]$Row.target,
                    (ConvertTo-FlatString $Row.source),
                    (ConvertTo-FlatString $Row.existing),
                    (ConvertTo-FlatString $Row.candidate)
                ) -join "`n"
            }
        }
        $staticBlob = ([string]$staticBlob).ToLowerInvariant()
        $cache.$cacheProperty = $staticBlob
    }
    if ($Decision -and $Mode -in @("텍스트", "텍스트/키")) {
        $dynamicBlob = @((ConvertTo-FlatString $Decision.text), [string]$Decision.note) -join "`n"
        if (-not [string]::IsNullOrWhiteSpace($dynamicBlob)) {
            return "$staticBlob`n$($dynamicBlob.ToLowerInvariant())"
        }
    }
    return [string]$staticBlob
}

function Get-RowFilterContext {
    $query = if ($txtSearch) { $txtSearch.Text.Trim().ToLowerInvariant() } else { "" }
    return [pscustomobject]@{
        CurrentFile = [string]$script:currentFile
        Status = if ($cmbStatus -and $cmbStatus.SelectedItem) { [string]$cmbStatus.SelectedItem } else { "" }
        Query = $query
        Mode = if ($cmbSearchField -and $cmbSearchField.SelectedItem) { [string]$cmbSearchField.SelectedItem } else { "텍스트/키" }
    }
}

function Get-RowPassesFilter([object]$Row, [object]$Context = $null) {
    if (-not $Context) { $Context = Get-RowFilterContext }
    if ($Context.CurrentFile -ne "__ALL__" -and (Get-RelativeTarget $Row) -ne $Context.CurrentFile) { return $false }
    $status = [string]$Context.Status
    $decision = $null
    if ($status -notin @("", "전체", "후보 있음", "기존 있음")) {
        $decision = Get-ExistingDecision $Row
        if ($decision) {
            $decisionStatus = [string]$decision.status
            $decisionOrigin = [string]$decision.translationOrigin
            $decisionText = ConvertTo-FlatString $decision.text
            $sourceChanged = ConvertTo-BoolValue $decision.sourceChanged
        } else {
            $decisionText = Get-DefaultTranslationForRow $Row
            $decisionOrigin = Get-DefaultTranslationOriginForRow $Row
            $sourceChanged = $decisionOrigin -ne "ai" -and $Row.PSObject.Properties["rmkSourceChanged"] -and (ConvertTo-BoolValue $Row.rmkSourceChanged)
            $decisionStatus = if ([string]::IsNullOrWhiteSpace($decisionText) -or $sourceChanged) { "pending" } else { "translated" }
        }
    }
    switch ($status) {
        "미번역" { if ($decisionStatus -ne "pending") { return $false } }
        "번역됨" { if ($decisionStatus -ne "translated") { return $false } }
        "검토됨" { if ($decisionStatus -ne "approved") { return $false } }
        "업데이트로 변경됨" { if (-not $sourceChanged) { return $false } }
        "RMK 가져옴" { if ($decisionOrigin -ne "rmk") { return $false } }
        "내 번역" { if ($decisionOrigin -ne "local") { return $false } }
        "반려" { if ($decisionStatus -ne "rejected") { return $false } }
        "보류" { if ($decisionStatus -ne "hold") { return $false } }
        "주의" {
            $warnings = @(Get-RowWarnings -Row $Row -Translation $decisionText)
            if ($warnings.Count -eq 0) { return $false }
        }
        "후보 있음" { if ([string]::IsNullOrWhiteSpace([string]$Row.candidate)) { return $false } }
        "기존 있음" { if ([string]::IsNullOrWhiteSpace([string]$Row.existing)) { return $false } }
    }

    $query = [string]$Context.Query
    if ($query) {
        $mode = [string]$Context.Mode
        if (-not $decision -and $mode -in @("텍스트", "텍스트/키")) { $decision = Get-ExistingDecision $Row }
        $blob = Get-RowSearchBlob -Row $Row -Decision $decision -Mode $mode
        if (-not $blob.Contains($query)) { return $false }
    }
    return $true
}

function Get-ItemPreview([object]$Row) {
    $cache = Get-RowRuntimeCache $Row
    if ($null -eq $cache.SourcePreview) {
        $source = ((ConvertTo-FlatString $Row.source) -replace "\s+", " ").Trim()
        if ($source.Length -gt 64) { $source = $source.Substring(0, 61) + "..." }
        $cache.SourcePreview = $source
    }
    $decision = Get-ExistingDecision $Row
    if ($decision) {
        $candidate = ((ConvertTo-FlatString $decision.text) -replace "\s+", " ").Trim()
        if ($candidate.Length -gt 64) { $candidate = $candidate.Substring(0, 61) + "..." }
    } else {
        if ($null -eq $cache.DefaultPreview) {
            $candidate = ((Get-DefaultTranslationForRow $Row) -replace "\s+", " ").Trim()
            if ($candidate.Length -gt 64) { $candidate = $candidate.Substring(0, 61) + "..." }
            $cache.DefaultPreview = $candidate
        }
        $candidate = [string]$cache.DefaultPreview
    }
    $source = [string]$cache.SourcePreview
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
            $rowUpdated = $defaultOrigin -ne "ai" -and (ConvertTo-BoolValue $row.rmkSourceChanged)
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
        $filterContext = if ($fastAllRows) { $null } else { Get-RowFilterContext }
        foreach ($rowIndex in @($orderedRowIndexes)) {
            $i = [int]$rowIndex
            $row = $script:rows[$i]
            if (-not $fastAllRows -and -not (Get-RowPassesFilter -Row $row -Context $filterContext)) { continue }
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
        $script:lastRenderedSelectionPosition = -1
        return
    }
    $position = $positionValue - 1
    if ($flowItems.SelectedIndex -ne $position) {
        $script:syncingItemSelection = $true
        try { $flowItems.SelectedIndex = $position } finally { $script:syncingItemSelection = $false }
    }
    $positionsToRefresh = @($script:lastRenderedSelectionPosition, $position) |
        Where-Object { $_ -ge 0 -and $_ -lt $flowItems.Items.Count } |
        Select-Object -Unique
    foreach ($refreshPosition in $positionsToRefresh) {
        $flowItems.Invalidate($flowItems.GetItemRectangle([int]$refreshPosition))
    }
    $script:lastRenderedSelectionPosition = $position
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
    if ($txtDiffSource) { $txtDiffSource.Text = "" }
    if ($txtDiffBefore) { $txtDiffBefore.Text = "" }
    if ($txtDiffAfter) { $txtDiffAfter.Text = "" }
    if ($lblDiffSummary) { $lblDiffSummary.Text = "비교할 문자열을 선택하세요" }
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
    Update-TranslationDiffView -Source $Source -Existing $Existing -Candidate $Candidate -Translation $Translation -SourceChanged $SourceChanged
}

function Set-RichTextDiffHighlight {
    param([System.Windows.Forms.RichTextBox]$Box, [string]$Text, [int]$Start, [int]$Length)
    if (-not $Box) { return }
    $Box.Text = [string]$Text
    $Box.SelectAll()
    $Box.SelectionBackColor = $Box.BackColor
    $Box.SelectionColor = $script:textColor
    if ($Length -gt 0 -and $Start -ge 0 -and ($Start + $Length) -le $Box.TextLength) {
        $Box.Select($Start, $Length)
        $Box.SelectionBackColor = if (Get-IsWindowsDarkMode) { [System.Drawing.Color]::FromArgb(91, 69, 37) } else { [System.Drawing.Color]::FromArgb(255, 229, 168) }
        $Box.SelectionColor = $script:textColor
    }
    $Box.Select(0, 0)
}

function Update-TranslationDiffView {
    param([string]$Source, [string]$Existing, [string]$Candidate, [string]$Translation, [bool]$SourceChanged = $false)
    if (-not $txtDiffSource) { return }
    $before = [string]$Existing
    $beforeLabel = "기존 번역"
    if ([string]::IsNullOrWhiteSpace($before) -and -not [string]::IsNullOrWhiteSpace($Candidate)) {
        $before = [string]$Candidate
        $beforeLabel = "AI 후보"
    }
    $diff = Get-RimWorldSimpleDiff -Before $before -After ([string]$Translation)
    $txtDiffSource.Text = [string]$Source
    $lblDiffBefore.Text = $beforeLabel
    Set-RichTextDiffHighlight -Box $txtDiffBefore -Text $before -Start ([int]$diff.PrefixLength) -Length ([string]$diff.BeforeChanged).Length
    Set-RichTextDiffHighlight -Box $txtDiffAfter -Text ([string]$Translation) -Start ([int]$diff.PrefixLength) -Length ([string]$diff.AfterChanged).Length
    if ([string]::IsNullOrWhiteSpace($before) -and [string]::IsNullOrWhiteSpace($Translation)) {
        $lblDiffSummary.Text = "비교할 번역이 없습니다"
    } elseif (-not $diff.Changed) {
        $lblDiffSummary.Text = $(if ($SourceChanged) { "번역은 같지만 원문이 변경되었습니다" } else { "기존 번역과 현재 번역이 같습니다" })
    } else {
        $changedSize = ([string]$diff.BeforeChanged).Length + ([string]$diff.AfterChanged).Length
        $lblDiffSummary.Text = $(if ($SourceChanged) { "원문 변경됨  ·  번역 차이 ${changedSize}자" } else { "번역 차이 ${changedSize}자" })
    }
    $lblDiffSummary.ForeColor = if ($SourceChanged) { Get-UpdateColor } elseif ($diff.Changed) { $script:accentColor } else { $script:mutedColor }
}

function Get-CurrentQualityEntries {
    $entries = New-Object "System.Collections.Generic.List[object]"
    $hasDecisions = $script:decisions.Count -gt 0
    for ($i = 0; $i -lt $script:rows.Count; $i++) {
        $row = $script:rows[$i]
        $decision = if ($hasDecisions) { Get-ExistingDecision $row } else { $null }
        if ($decision) {
            $translation = ConvertTo-FlatString $decision.text
            $status = [string]$decision.status
            $sourceChanged = ConvertTo-BoolValue $decision.sourceChanged
        } else {
            $candidate = ConvertTo-FlatString $row.candidate
            $existing = ConvertTo-FlatString $row.existing
            if (-not [string]::IsNullOrWhiteSpace($candidate)) {
                $translation = $candidate
                $origin = if ($row.PSObject.Properties["translationOrigin"] -and $row.translationOrigin) { [string]$row.translationOrigin } else { "ai" }
            } elseif (-not [string]::IsNullOrWhiteSpace($existing)) {
                $translation = $existing
                $origin = if ($row.PSObject.Properties["existingOrigin"] -and $row.existingOrigin) { [string]$row.existingOrigin } else { "existing" }
            } else {
                $translation = ""
                $origin = ""
            }
            $sourceChanged = $origin -ne "ai" -and $row.PSObject.Properties["rmkSourceChanged"] -and (ConvertTo-BoolValue $row.rmkSourceChanged)
            $status = if ([string]::IsNullOrWhiteSpace($translation) -or $sourceChanged) { "pending" } else { "translated" }
        }
        $safeToApply = $false
        $tokenOrTagIssue = $false
        if (-not [string]::IsNullOrWhiteSpace($translation)) {
            $rowValidationStillApplies = -not $decision -and
                $row.PSObject.Properties["safeToApply"] -and
                [bool]$row.safeToApply
            if ($rowValidationStillApplies) {
                $safeToApply = $true
            } else {
                $validation = Get-CachedTranslationValidation -Row $row -Translation $translation
                $safeToApply = [bool]$validation.SafeToApply
                $tokenOrTagIssue = $validation.MissingTokens.Count -gt 0 -or
                    $validation.UnexpectedTokens.Count -gt 0 -or
                    $validation.TokenCountMismatches.Count -gt 0 -or
                    [bool]$validation.GrammarPrefixMoved
            }
        }
        $defClass = if ($row.PSObject.Properties["defClass"]) { [string]$row.defClass } else { "" }
        if ([string]::IsNullOrWhiteSpace($defClass)) { $defClass = [string](Get-RowDefContext $row).DefClass }
        [void]$entries.Add([pscustomobject]@{
            index = $i
            key = [string]$row.key
            target = Get-RelativeTarget $row
            defClass = $defClass
            source = ConvertTo-FlatString $row.source
            translation = $translation
            existing = ConvertTo-FlatString $row.existing
            status = $status
            sourceChanged = $sourceChanged
            safeToApply = $safeToApply
            tokenOrTagIssue = $tokenOrTagIssue
        })
    }
    return $entries.ToArray()
}

function Get-QualityCategoryText([string]$Category) {
    switch ($Category) {
        "Missing" { "미번역" }
        "SourceChanged" { "원문 변경" }
        "Unsafe" { "안전 실패" }
        "TokenOrTag" { "토큰/태그" }
        "SameAsSource" { "원문 동일" }
        "TooShort" { "너무 짧음" }
        "TooLong" { "너무 김" }
        "ExistingChanged" { "기존과 다름" }
        "DuplicateIdentity" { "중복 식별자" }
        default { $Category }
    }
}

function Test-QualityIssueVisible([object]$Issue, [string]$Filter) {
    switch ($Filter) {
        "오류" { return [string]$Issue.Severity -eq "error" }
        "경고" { return [string]$Issue.Severity -eq "warning" }
        "미번역" { return [string]$Issue.Category -eq "Missing" }
        "원문 변경" { return [string]$Issue.Category -eq "SourceChanged" }
        "토큰/태그" { return [string]$Issue.Category -in @("TokenOrTag", "Unsafe") }
        "길이 이상" { return [string]$Issue.Category -in @("TooShort", "TooLong") }
        "원문과 동일" { return [string]$Issue.Category -eq "SameAsSource" }
        "중복 식별자" { return [string]$Issue.Category -eq "DuplicateIdentity" }
        default { return $true }
    }
}

function New-QualityListViewItem([object]$Issue) {
    $severityText = switch ([string]$Issue.Severity) { "error" { "오류" } "warning" { "경고" } default { "참고" } }
    $item = [System.Windows.Forms.ListViewItem]::new($severityText)
    [void]$item.SubItems.Add((Get-QualityCategoryText ([string]$Issue.Category)))
    $key = if ($Issue.Key) { [string]$Issue.Key } elseif ([int]$Issue.Index -ge 0 -and [int]$Issue.Index -lt $script:rows.Count) { [string]$script:rows[[int]$Issue.Index].key } else { "-" }
    [void]$item.SubItems.Add($key)
    $item.Tag = $Issue
    $item.ForeColor = switch ([string]$Issue.Severity) {
        "error" { if (Get-IsWindowsDarkMode) { [System.Drawing.Color]::FromArgb(240, 125, 118) } else { [System.Drawing.Color]::FromArgb(157, 55, 51) } }
        "warning" { Get-UpdateColor }
        default { $script:textColor }
    }
    return $item
}

function Get-SelectedQualityIssue {
    if (-not $lvQualityIssues -or $lvQualityIssues.SelectedIndices.Count -eq 0) { return $null }
    $index = [int]$lvQualityIssues.SelectedIndices[0]
    if ($index -lt 0 -or $index -ge $script:visibleQualityIssues.Count) { return $null }
    return $script:visibleQualityIssues[$index]
}

function Refresh-QualityCenter([switch]$Force) {
    if (-not $lvQualityIssues) { return }
    if ($Force -or $script:qualityDirty -or $null -eq $script:qualityIssues) {
        $qualityTimer = [System.Diagnostics.Stopwatch]::StartNew()
        $script:qualityEntries = @(Get-CurrentQualityEntries)
        $script:qualityIssues = @(Get-RimWorldQualityIssues -Entries $script:qualityEntries)
        $qualityTimer.Stop()
        $script:lastQualityElapsedMs = [Math]::Round($qualityTimer.Elapsed.TotalMilliseconds, 1)
        $script:qualityDirty = $false
    }
    $filter = if ($cmbQualityCategory.SelectedItem) { [string]$cmbQualityCategory.SelectedItem } else { "전체 문제" }
    $visibleIssues = New-Object "System.Collections.Generic.List[object]"
    $errorCount = 0
    $warningCount = 0
    foreach ($issue in $script:qualityIssues) {
        if ([string]$issue.Severity -eq "error") { $errorCount++ }
        elseif ([string]$issue.Severity -eq "warning") { $warningCount++ }
        if (Test-QualityIssueVisible $issue $filter) { [void]$visibleIssues.Add($issue) }
    }
    $script:visibleQualityIssues = $visibleIssues.ToArray()
    $elapsedText = if ($script:lastQualityElapsedMs -gt 0) { "  ·  검사 $([Math]::Round($script:lastQualityElapsedMs / 1000, 1))초" } else { "" }
    $lblQualitySummary.Text = "전체 $($script:rows.Count.ToString('N0'))개  ·  오류 $errorCount  ·  경고 $warningCount  ·  표시 $($script:visibleQualityIssues.Count)$elapsedText"
    $lvQualityIssues.BeginUpdate()
    try {
        $lvQualityIssues.SelectedIndices.Clear()
        $lvQualityIssues.VirtualListSize = $script:visibleQualityIssues.Count
        $lvQualityIssues.Invalidate()
    } finally {
        $lvQualityIssues.EndUpdate()
    }
    $btnQualityJump.Enabled = $false
    $txtWarnings.Clear()
}

function Invoke-QualityCenterRefresh([switch]$Force) {
    $needsAnalysis = $Force -or $script:qualityDirty -or $null -eq $script:qualityIssues
    if (-not $needsAnalysis) {
        Refresh-QualityCenter
        return
    }
    $lblQualitySummary.Text = "$($script:rows.Count.ToString('N0'))개 문자열의 토큰·상태·길이를 검사하는 중"
    $lvQualityIssues.VirtualListSize = 0
    $btnQualityJump.Enabled = $false
    $form.UseWaitCursor = $true
    [System.Windows.Forms.Application]::DoEvents()
    try {
        Refresh-QualityCenter -Force:$Force
    } finally {
        $form.UseWaitCursor = $false
    }
}

function Show-SelectedQualityIssue {
    $issue = Get-SelectedQualityIssue
    if (-not $issue) { return }
    $txtWarnings.Text = "$(Get-QualityCategoryText ([string]$issue.Category))`r`n$([string]$issue.Detail)"
    $btnQualityJump.Enabled = [int]$issue.Index -ge 0 -and [int]$issue.Index -lt $script:rows.Count
}

function Jump-ToSelectedQualityIssue {
    $issue = Get-SelectedQualityIssue
    if (-not $issue) { return }
    $rowIndex = [int]$issue.Index
    if ($rowIndex -lt 0 -or $rowIndex -ge $script:rows.Count) { return }
    $cmbStatus.SelectedIndex = 0
    $txtSearch.Text = ""
    if ($searchTimer) { $searchTimer.Stop() }
    Refresh-ItemList -SelectRowIndex $rowIndex
    Select-RowIndex $rowIndex
    [void]$txtTranslation.Focus()
}

function Export-QualityReport {
    if (-not $script:reviewRoot -or $script:rows.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show("보고서를 만들 검수 프로젝트가 없습니다.", "품질 보고서") | Out-Null
        return
    }
    Refresh-QualityCenter -Force
    $dialog = [System.Windows.Forms.SaveFileDialog]::new()
    $dialog.Title = "개인정보 보호 품질 보고서 저장"
    $dialog.Filter = "HTML 보고서 (*.html)|*.html"
    $dialog.DefaultExt = "html"
    $dialog.AddExtension = $true
    $dialog.OverwritePrompt = $true
    $dialog.FileName = "translation-quality-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".html"
    try {
        if ($dialog.ShowDialog($form) -ne [System.Windows.Forms.DialogResult]::OK) { return }
        $result = Export-RimWorldQualityReport -Path $dialog.FileName -Entries $script:qualityEntries -Issues $script:qualityIssues
        Add-Log "품질 보고서를 저장했습니다. 집계 수치만 포함하며 원문·번역문·API 키·절대 경로는 제외했습니다."
        [System.Windows.Forms.MessageBox]::Show("품질 보고서를 저장했습니다.`r`n`r`n$($result.Path)`r`n`r`n원문, 번역문, API 키와 절대 경로는 포함하지 않습니다.", "품질 보고서", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
    } finally {
        $dialog.Dispose()
    }
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

function Invalidate-TranslationMemoryCache {
    $script:translationMemoryCache = @{}
    $script:qualityDirty = $true
}

function Get-TranslationMemorySuggestionsForRow([object]$Row) {
    $source = ConvertTo-FlatString $Row.source
    if ([string]::IsNullOrWhiteSpace($source)) { return @() }
    if (-not $script:sourceRowIndex) { Build-SourceRowIndex }
    if (-not $script:sourceRowIndex.ContainsKey($source)) { return @() }
    if (-not $script:translationMemoryCache.ContainsKey($source)) {
        $entries = New-Object "System.Collections.Generic.List[object]"
        foreach ($rowIndex in $script:sourceRowIndex[$source]) {
            $memoryRow = $script:rows[[int]$rowIndex]
            $decision = Get-ExistingDecision $memoryRow
            if ($decision) {
                $translation = ConvertTo-FlatString $decision.text
                $status = [string]$decision.status
                $origin = [string]$decision.translationOrigin
                $sourceChanged = ConvertTo-BoolValue $decision.sourceChanged
                $updatedAt = if ($decision.translationUpdatedAt) { [string]$decision.translationUpdatedAt } else { [string]$decision.updatedAt }
            } else {
                $translation = Get-DefaultTranslationForRow $memoryRow
                $origin = Get-DefaultTranslationOriginForRow $memoryRow
                $sourceChanged = $origin -ne "ai" -and $memoryRow.PSObject.Properties["rmkSourceChanged"] -and (ConvertTo-BoolValue $memoryRow.rmkSourceChanged)
                $status = if ([string]::IsNullOrWhiteSpace($translation) -or $sourceChanged) { "pending" } else { "translated" }
                $updatedAt = Get-OptionalRowText -Row $memoryRow -Names @("translationUpdatedAt")
            }
            if ($status -notin @("approved", "translated") -or [string]::IsNullOrWhiteSpace($translation) -or $sourceChanged) { continue }
            $validation = Get-CachedTranslationValidation -Row $memoryRow -Translation $translation
            [void]$entries.Add([pscustomobject]@{
                Source = $source
                Translation = $translation
                Identity = Get-RowIdentity $memoryRow
                Status = $status
                Origin = $origin
                SourceChanged = $sourceChanged
                SafeToApply = [bool]$validation.SafeToApply
                UpdatedAt = $updatedAt
                Target = Get-RelativeTarget $memoryRow
            })
        }
        $script:translationMemoryCache[$source] = $entries.ToArray()
    }
    return @(Select-RimWorldTranslationMemorySuggestions -Entries @($script:translationMemoryCache[$source]) -Source $source -ExcludeIdentity (Get-RowIdentity $Row) -Maximum 5)
}

function Update-TermsForRow([object]$Row) {
    if ($tabs -and $tabTerms -and $tabs.SelectedTab -ne $tabTerms) { return }
    $output = New-Object "System.Collections.Generic.List[string]"
    $memorySuggestions = @(Get-TranslationMemorySuggestionsForRow $Row)
    if ($memorySuggestions.Count -gt 0) {
        [void]$output.Add("로컬 번역 메모리 · 동일 원문")
        foreach ($suggestion in $memorySuggestions) {
            $origin = Get-TranslationOriginText ([string]$suggestion.Origin)
            $status = Get-StatusText ([string]$suggestion.Status)
            $file = Split-Path -Leaf ([string]$suggestion.Target)
            [void]$output.Add("[$status · $origin · $file]")
            [void]$output.Add([string]$suggestion.Text)
            [void]$output.Add("")
        }
    }
    if (-not $script:glossaryLoaded) {
        $txtTerms.Text = if ($output.Count -gt 0) { [string]::Join("`r`n", $output.ToArray()) } else { "관련 용어 또는 번역 메모리 없음" }
        return
    }
    $text = ((ConvertTo-FlatString $Row.source) + "`n" + (ConvertTo-FlatString $Row.candidate)).ToLowerInvariant()
    if ($text.Length -lt 3) {
        $txtTerms.Text = if ($output.Count -gt 0) { [string]::Join("`r`n", $output.ToArray()) } else { "관련 용어 또는 번역 메모리 없음" }
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
    if ($hits.Count -gt 0) {
        if ($output.Count -gt 0) { [void]$output.Add("용어집") }
        foreach ($hit in $hits) { [void]$output.Add([string]$hit) }
    }
    $txtTerms.Text = if ($output.Count -gt 0) { [string]::Join("`r`n", $output.ToArray()) } else { "관련 용어 또는 번역 메모리 없음" }
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
    Invalidate-TranslationMemoryCache
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
    $coverShown = Show-WorkspaceLoadCover
    try {
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
    $script:rowRuntimeCacheStore.Reset()
    Invalidate-TranslationMemoryCache
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
    $script:lastReviewLoadMetrics = [pscustomobject]@{
        totalMilliseconds = [Math]::Round($loadStopwatch.Elapsed.TotalMilliseconds, 3)
        rows = $script:rows.Count
        stages = $loadStages.ToArray()
        atomicCoverUsed = [bool]$coverShown
    }
    Add-Log ("검수 화면 로드: {0:n2}초 · {1}개 문자열" -f $loadStopwatch.Elapsed.TotalSeconds, $script:rows.Count)
    Add-Log ("로드 세부: " + [string]::Join(" · ", $loadStages))
    } finally {
        Hide-WorkspaceLoadCover $coverShown
    }
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

function Select-AiTranslationMode {
    param([object]$ExistingInfo, [switch]$PreviewOnly)
    $dialog = [System.Windows.Forms.Form]::new()
    $dialog.Text = "번역 작업 준비"
    $dialog.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterParent
    $dialog.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $dialog.ClientSize = [System.Drawing.Size]::new(760, 512)
    $dialog.MinimizeBox = $false
    $dialog.MaximizeBox = $false
    $dialog.ShowInTaskbar = $false
    $dialog.ShowIcon = $false
    $dialog.BackColor = $script:surfaceColor
    $dialog.Font = New-Font 9
    $hasExisting = $ExistingInfo -and [bool]$ExistingInfo.HasExistingTranslation
    $dialog.Tag = if ($hasExisting) { "MissingOnly" } else { "Overwrite" }

    $accent = [System.Windows.Forms.Panel]::new()
    $accent.SetBounds(0, 0, 760, 4)
    $accent.BackColor = $script:accentColor

    $title = New-Label "번역 작업 준비" 30 24 520 32 $script:textColor 14 ([System.Drawing.FontStyle]::Bold)
    $subtitle = New-Label "외부 전송 범위와 저장 방식을 확인한 뒤 시작합니다." 30 58 620 22 $script:mutedColor 9

    $summary = [System.Windows.Forms.Panel]::new()
    $summary.SetBounds(30, 96, 700, 178)
    $summary.BackColor = if ($script:uiTokens) { ConvertTo-RimWorldUiColor $script:uiTokens.Colors.SurfaceMuted } else { $script:surfaceColor }
    $summary.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle

    $project = Get-SelectedProject
    $projectName = if ($project) { [string]$project.name } else { "선택한 프로젝트" }
    $sourceLanguage = Get-SelectedProjectSourceLanguage
    $profile = Get-ApiProviderProfile
    $config = Get-SelectedApiProviderConfig
    $keyCount = if ($profile -and [string]$profile.Provider -ne "Google") { @(Get-ApiKeyLines ([string]$script:apiProviderKeys[$script:selectedApiProviderId])).Count } else { 0 }
    $usesFallback = $profile -and [string]$profile.Provider -ne "Google" -and $keyCount -eq 0
    $providerText = if ($usesFallback) { "Google 번역 (키 없음 대체)" } elseif ($profile) { [string]$profile.Name } else { "설정 확인 필요" }
    $modelText = if ($usesFallback -or ($profile -and [string]$profile.Provider -eq "Google")) { "Google Translate" } elseif ($config) { [string]$config.model } else { "-" }
    $estimate = Get-RimWorldTranslationEstimate -Entries @($script:rows) -BatchSize 40
    $targetText = if ($estimate.Entries -gt 0) { "$($estimate.Entries.ToString('N0'))개 · 최대 $($estimate.Batches.ToString('N0'))배치" } else { "원문 분석 후 확정" }
    $tokenText = if ($estimate.Entries -eq 0) {
        "원문 분석 후 계산"
    } elseif ($usesFallback -or ($profile -and [string]$profile.Provider -eq "Google")) {
        "Google 번역 · API 토큰 추정 해당 없음"
    } else {
        "약 $($estimate.EstimatedInputTokensLow.ToString('N0')) ~ $($estimate.EstimatedInputTokensHigh.ToString('N0')) 입력 토큰"
    }
    $summaryRows = @(
        [pscustomobject]@{ Label = "프로젝트"; Value = $projectName },
        [pscustomobject]@{ Label = "원문 기준"; Value = $sourceLanguage },
        [pscustomobject]@{ Label = "번역 엔진"; Value = "$providerText · $modelText" },
        [pscustomobject]@{ Label = "예상 범위"; Value = $targetText },
        [pscustomobject]@{ Label = "사용량 추정"; Value = $tokenText }
    )
    $rowY = 14
    foreach ($summaryRow in $summaryRows) {
        $rowLabel = New-Label ([string]$summaryRow.Label) 16 $rowY 94 24 $script:mutedColor 8.5 ([System.Drawing.FontStyle]::Bold)
        $rowValue = New-Label ([string]$summaryRow.Value) 112 $rowY 564 24 $script:textColor 9
        $rowValue.AutoEllipsis = $true
        $summary.Controls.AddRange(@($rowLabel, $rowValue))
        $rowY += 31
    }

    $privacyBand = [System.Windows.Forms.Panel]::new()
    $privacyBand.SetBounds(30, 286, 700, 52)
    $privacyBand.BackColor = if ($script:uiTokens) { ConvertTo-RimWorldUiColor $script:uiTokens.Colors.Selection } else { $script:surfaceColor }
    $privacyTitle = New-Label "검수 프로젝트에 먼저 저장" 16 7 220 20 $script:textColor 8.8 ([System.Drawing.FontStyle]::Bold)
    $privacyBody = New-Label "번역 실행만으로 원본 모드나 Korean 폴더를 수정하지 않습니다. 적용은 검토 화면에서 별도로 실행합니다." 16 27 660 18 $script:mutedColor 8.2
    $privacyBand.Controls.AddRange(@($privacyTitle, $privacyBody))

    $modeTitle = New-Label "번역 범위" 30 354 180 22 $script:textColor 9 ([System.Drawing.FontStyle]::Bold)
    $btnMissingOnly = New-Button "미번역 부분만" $script:surfaceColor
    $btnMissingOnly.SetBounds(30, 382, 180, 42)
    $btnOverwrite = New-Button "전체 다시 번역" $script:surfaceColor
    $btnOverwrite.SetBounds(218, 382, 180, 42)
    $modeHint = New-Label "" 414 382 316 44 $script:mutedColor 8.3

    $refreshMode = {
        $missingSelected = [string]$dialog.Tag -eq "MissingOnly"
        foreach ($modeButton in @($btnMissingOnly, $btnOverwrite)) {
            $selected = ($modeButton -eq $btnMissingOnly -and $missingSelected) -or ($modeButton -eq $btnOverwrite -and -not $missingSelected)
            $modeButton.BackColor = if ($selected) { $script:accentColor } else { $script:surfaceColor }
            $modeButton.ForeColor = if ($selected) { $script:accentTextColor } else { $script:textColor }
            $modeButton.FlatAppearance.BorderColor = if ($selected) { $script:accentColor } else { $script:borderColor }
            $modeButton.FlatAppearance.BorderSize = 1
        }
        $modeHint.Text = if ($missingSelected) {
            if ($hasExisting) { "기존 번역과 직접 편집한 내용은 보존하고 빈 항목만 번역합니다." } else { "현재 보존할 번역이 없어 모든 미번역 항목이 대상입니다." }
        } else {
            if ($hasExisting) { "기존 후보를 새 결과로 교체합니다. 이전 작업 이력은 보존됩니다." } else { "현재 원문 전체의 초벌 번역 후보를 생성합니다." }
        }
    }
    $btnMissingOnly.Add_Click({ $dialog.Tag = "MissingOnly"; & $refreshMode })
    $btnOverwrite.Add_Click({ $dialog.Tag = "Overwrite"; & $refreshMode })
    & $refreshMode

    $divider = [System.Windows.Forms.Panel]::new()
    $divider.SetBounds(30, 442, 700, 1)
    $divider.BackColor = $script:borderColor

    $btnStart = New-Button "번역 시작" $script:accentColor
    $btnStart.ForeColor = $script:accentTextColor
    $btnStart.SetBounds(486, 456, 132, 40)
    $btnCancel = New-Button "취소" $script:surfaceColor
    $btnCancel.ForeColor = $script:textColor
    $btnCancel.FlatAppearance.BorderColor = $script:borderColor
    $btnCancel.FlatAppearance.BorderSize = 1
    $btnCancel.SetBounds(628, 456, 102, 40)

    Set-AccessibleControl $btnMissingOnly "미번역 부분만 번역" "기존 번역은 보존하고 번역이 없는 문자열만 번역 대상으로 선택합니다." 0
    Set-AccessibleControl $btnOverwrite "전체 다시 번역" "모든 문자열을 다시 번역하고 새 후보로 교체합니다." 1
    Set-AccessibleControl $btnStart "확인한 범위로 번역 시작" "표시된 제공자와 번역 범위로 초벌 번역을 시작합니다." 2
    Set-AccessibleControl $btnCancel "번역 준비 취소" "외부 전송 없이 창을 닫습니다." 3

    $btnStart.Add_Click({ $dialog.Close() })
    $btnCancel.Add_Click({ $dialog.Tag = "Cancel"; $dialog.Close() })
    $dialog.AcceptButton = $btnStart
    $dialog.CancelButton = $btnCancel
    $dialog.Controls.AddRange(@($accent, $title, $subtitle, $summary, $privacyBand, $modeTitle, $btnMissingOnly, $btnOverwrite, $modeHint, $divider, $btnStart, $btnCancel))

    if ($PreviewOnly) {
        $dialog.Show($form)
        return $dialog
    }
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
        $identity = Get-ReviewSourceIdentity $row
        $text = ConvertTo-FlatString $decision.text
        if (-not $key -or -not $identity -or -not $seen.Add($identity) -or [string]::IsNullOrWhiteSpace($text) -or (ConvertTo-BoolValue $decision.sourceChanged)) { continue }
        [void]$items.Add([pscustomobject]@{
            key = $key
            kind = if ($row.PSObject.Properties["kind"]) { [string]$row.kind } else { "" }
            defClass = if ($row.PSObject.Properties["defClass"]) { [string]$row.defClass } else { "" }
            target = Get-RelativeTarget $row
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
    $script:cancellationFile = New-TempFilePath "translation-cancel" ".signal"
    [void]$script:tempFiles.Add($script:cancellationFile)

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
        CancellationFile = $script:cancellationFile
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
    if ($rmkWorkbook) {
        Add-Log "RMK 번역 당시 원문 비교: $rmkWorkbook"
    } elseif ($rmkReference) {
        Add-Log "RMK 원문 기록 XLSX 없음: 직전 로컬 프로젝트 원문 이력으로 업데이트 여부를 비교합니다."
    }

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
    $script:stopRequestedAt = $null
    $progressRun.Value = 0
    $progressRun.Maximum = 100
    $lblRunStatus.Text = "실행 준비 중"
    Set-TranslationRunning $true
    Show-OperationOverlay -Title "초벌 번역 실행" -Detail "$($effectiveProviderConfig.name) · $sourceLanguage 원문 · 검수 프로젝트에만 저장" -OperationType "Translation"
    try {
        [void]$proc.Start()
        Add-Log "번역 프로세스 PID=$($proc.Id)"
    } catch {
        Add-Log "실행 실패: $($_.Exception.Message)"
        try { $proc.Dispose() } catch {}
        $script:process = $null
        $script:processExitHandled = $true
        $script:activeAiTranslationMode = ""
        Set-TranslationRunning $false
        Complete-OperationOverlay -Kind "error" -Title "번역 프로세스를 시작하지 못했습니다" -Detail $_.Exception.Message
    }
}

function Stop-Translation {
    if ($script:process -and -not $script:process.HasExited) {
        $script:stopRequested = $true
        $script:stopRequestedAt = Get-Date
        $btnStop.Enabled = $false
        $lblRunStatus.Text = "중지 요청 중"
        if ($operationOverlay -and $operationOverlay.Visible) {
            $lblOperationStage.Text = "안전하게 중지하는 중"
            $lblOperationDetail.Text = "완료된 배치를 검수 프로젝트에 남긴 뒤 프로세스를 종료합니다."
            $btnOperationCancel.Enabled = $false
        }
        Add-Log "사용자 요청으로 중지합니다. 완료된 배치는 보존합니다."
        try {
            if ([string]::IsNullOrWhiteSpace($script:cancellationFile)) { throw "Cancellation signal path is missing." }
            [System.IO.File]::WriteAllText($script:cancellationFile, "cancel", [System.Text.UTF8Encoding]::new($false))
        } catch {
            Add-Log "취소 신호를 기록하지 못해 실행 프로세스를 즉시 종료합니다."
            Stop-ProcessTree $script:process.Id
        }
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
    $sourceLanguage = Get-SelectedProjectSourceLanguage
    $txtLog.Clear()
    Show-OperationOverlay -Title "모드 원문 분석" -Detail "$sourceLanguage 원문을 준비하고 있습니다." -OperationType "SourceOnly"
    Set-TranslationRunning $true
    $btnStop.Enabled = $false
    $btnOperationCancel.Enabled = $false
    $lblRunStatus.Text = "원문 분석 준비 중"
    [System.Windows.Forms.Application]::DoEvents()

    $prepareWatch = [System.Diagnostics.Stopwatch]::StartNew()
    $proc = $null
    try {
        Ensure-AppDataStore
        Remove-TempFiles
        $script:lastReviewOutputPath = ""
        $script:lastProvider = "sourceonly"
        $script:translationLogFile = New-TempFilePath "source-refresh-output" ".log"
        [System.IO.File]::WriteAllText($script:translationLogFile, "", [System.Text.UTF8Encoding]::new($false))
        [void]$script:tempFiles.Add($script:translationLogFile)
        $script:translationLogOffset = 0L
        $script:translationLogPartial = ""
        $script:cancellationFile = New-TempFilePath "source-refresh-cancel" ".signal"
        [void]$script:tempFiles.Add($script:cancellationFile)

        $lblOperationStage.Text = "RMK 번역 이력 확인"
        $lblOperationDetail.Text = "로컬 인덱스에서 기존 번역과 원문 기록을 찾고 있습니다."
        $lblOperationCount.Text = "로컬 자료 확인 중"
        [System.Windows.Forms.Application]::DoEvents()
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
            CancellationFile = $script:cancellationFile
        }
        if ($chkIncludePatches.Checked) { $translationParameters.IncludePatches = $true }
        if ($rmkReference) { $translationParameters.ReferenceLanguageRoot = @($rmkReference) }
        if ($rmkWorkbook -and (Test-Path -LiteralPath $rmkWorkbook -PathType Leaf)) { $translationParameters.ReferenceSourceWorkbook = $rmkWorkbook }

        Add-Log "원문 로드 시작: $modRoot"
        Add-Log "원문 기준 언어: $sourceLanguage"
        if ($rmkReference) { Add-Log "RMK 기존 번역을 기본 번역으로 불러옵니다: $rmkReference" }
        if ($rmkWorkbook) {
            Add-Log "RMK 번역 당시 원문과 현재 원문을 비교합니다: $rmkWorkbook"
        } elseif ($rmkReference) {
            Add-Log "RMK 원문 기록 XLSX 없음: 직전 로컬 프로젝트 원문 이력으로 업데이트 여부를 비교합니다."
        }

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
        $script:stopRequestedAt = $null
        $progressRun.Value = 0
        $progressRun.Maximum = 100
        $lblRunStatus.Text = "원문 로드 중"
        $lblOperationStage.Text = "원문 분석 프로세스 시작"
        $lblOperationDetail.Text = "$sourceLanguage 원문과 기존 번역 이력을 비교합니다."
        [void]$proc.Start()
        $btnStop.Enabled = $true
        $btnOperationCancel.Enabled = $true
        $prepareWatch.Stop()
        Add-Log ("원문 분석 준비 완료: {0:N0}ms" -f $prepareWatch.Elapsed.TotalMilliseconds)
        Add-Log "원문 로드 프로세스 PID=$($proc.Id)"
    } catch {
        $prepareWatch.Stop()
        Add-Log "원문 로드 실행 실패: $($_.Exception.Message)"
        if ($proc) { try { $proc.Dispose() } catch {} }
        $script:process = $null
        $script:processExitHandled = $true
        $script:activeAiTranslationMode = ""
        $lblRunStatus.Text = "원문 로드 실패"
        Set-TranslationRunning $false
        Complete-OperationOverlay -Kind "error" -Title "원문 분석을 시작하지 못했습니다" -Detail $_.Exception.Message
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
        $decision = Get-Decision $row
        $status = [string]$decision.status
        if (-not (ConvertTo-BoolValue $decision.sourceChanged) -and
            ($status -eq "approved" -or ($ApplyStatus -eq "TranslatedAndApproved" -and $status -eq "translated"))) {
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
        if (Test-Utf8JsonStoreExists $decisionPath) {
            $decisionReadPath = if (Test-Path -LiteralPath $decisionPath -PathType Leaf) { $decisionPath } else { "$decisionPath.bak" }
            $decisionInfo = Get-Item -LiteralPath $decisionReadPath -ErrorAction Stop
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
        if (Test-Utf8JsonStoreExists $decisionPath) {
            $json = Read-Utf8JsonFile $decisionPath
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

            $defaultOrigin = Get-DefaultTranslationOriginForRow $row
            $sourceChanged = $defaultOrigin -ne "ai" -and $row.PSObject.Properties["rmkSourceChanged"] -and (ConvertTo-BoolValue $row.rmkSourceChanged)
            if ($decision) {
                $source = ConvertTo-FlatString $row.source
                $sourceChanged = $sourceChanged -or ($decision.PSObject.Properties["sourceChanged"] -and (ConvertTo-BoolValue $decision.sourceChanged))
                if (-not $sourceChanged -and $decision.PSObject.Properties["sourceText"] -and -not [string]::IsNullOrWhiteSpace([string]$decision.sourceText)) {
                    $sourceChanged = $sourceChanged -or ((ConvertTo-FlatString $decision.sourceText) -ne $source)
                } elseif (-not $sourceChanged -and $decision.PSObject.Properties["sourceHash"] -and -not [string]::IsNullOrWhiteSpace([string]$decision.sourceHash)) {
                    $sourceChanged = ([string]$decision.sourceHash) -ne (Get-TextFingerprint $source)
                }
            }
            if ($sourceChanged) { $status = "pending" }
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
        if ($Project.latestReviewRoot -and (Test-Path -LiteralPath $Project.latestReviewRoot -PathType Container)) {
            $sameReview = $false
            try {
                $sameReview = $script:reviewRoot -and
                    ([System.IO.Path]::GetFullPath([string]$script:reviewRoot).TrimEnd("\", "/") -ieq [System.IO.Path]::GetFullPath([string]$Project.latestReviewRoot).TrimEnd("\", "/")) -and
                    $script:rows.Count -gt 0
            } catch {
                $sameReview = $false
            }
            if (-not $sameReview) { Load-ReviewRoot ([string]$Project.latestReviewRoot) }
            Show-Workspace
        } else {
            if ($dashboardPanel -and $dashboardPanel.Visible) {
                Load-SourceOnlyForSelectedMod
            } else {
                Show-Workspace
                Load-SourceOnlyForSelectedMod
            }
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
    $providerProfile = Get-ApiProviderProfile
    $providerKeys = if ($script:apiProviderKeys.ContainsKey($script:selectedApiProviderId)) { Get-ApiProviderKeyCount ([string]$script:apiProviderKeys[$script:selectedApiProviderId]) } else { 0 }
    if ($lblDashProviderStatus -and $providerProfile) {
        if ([string]$providerProfile.Provider -eq "Google") {
            $lblDashProviderStatus.Text = "번역 엔진  ·  Google 번역"
            $lblDashProviderHint.Text = "API 키 없이 실행 · 용어집과 추가 프롬프트는 미지원"
        } elseif ($providerKeys -gt 0) {
            $config = Get-SelectedApiProviderConfig
            $lblDashProviderStatus.Text = "번역 엔진  ·  $($providerProfile.Name)"
            $lblDashProviderHint.Text = "키 $providerKeys개 · $([string]$config.model) · 키는 저장하지 않음"
        } else {
            $lblDashProviderStatus.Text = "번역 엔진  ·  Google 대체 사용"
            $lblDashProviderHint.Text = "$($providerProfile.Name) 키 없음 · 설정에서 키 입력 가능"
        }
    }
    $projectRevision = [string]::Join(";", @($script:projects | ForEach-Object { "$($_.id):$($_.updatedAt):$($_.latestReviewRoot)" }))
    $availableWidth = [Math]::Max(320, $flowDashboardProjects.ClientSize.Width - 20)
    $columnCount = [Math]::Max(1, [Math]::Min(4, [int][Math]::Floor($availableWidth / 360)))
    $cardWidth = [Math]::Max(320, [Math]::Min(440, [int][Math]::Floor($availableWidth / $columnCount) - 20))
    $cardInnerWidth = $cardWidth - 44
    $renderKey = "$filter|$($script:designPreset)|$($script:themeMode)|$(Get-IsWindowsDarkMode)|$($script:highContrast)|$($script:textSize)|$availableWidth|$projectRevision"
    if (-not $script:dashboardProjectsDirty -and $script:lastDashboardRenderKey -eq $renderKey) { return }
    $projectAccent = if ($script:accentColor) { $script:accentColor } else { [System.Drawing.Color]::FromArgb(166, 124, 70) }
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
            $emptyWidth = [Math]::Max(560, $availableWidth - 24)
            $emptyPanel = [System.Windows.Forms.Panel]::new()
            $emptyPanel.Size = [System.Drawing.Size]::new($emptyWidth, 248)
            $emptyPanel.Margin = [System.Windows.Forms.Padding]::new(10, 8, 10, 8)
            $emptyPanel.BackColor = $script:surfaceColor
            $emptyPanel.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle

            $scan = [System.Windows.Forms.Panel]::new()
            $scan.SetBounds(28, 26, 160, 160)
            $scan.BackColor = [System.Drawing.Color]::Transparent
            $scan.TabStop = $false
            $scan.Add_Paint({
                param($sender, $eventArgs)
                $graphics = $eventArgs.Graphics
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
                $accentPen = [System.Drawing.Pen]::new($script:accentColor, 2)
                $softPen = [System.Drawing.Pen]::new($script:borderColor, 1)
                $dotBrush = [System.Drawing.SolidBrush]::new($script:accentColor)
                try {
                    foreach ($size in @(132, 92, 52)) {
                        $offset = [int]((150 - $size) / 2)
                        $graphics.DrawEllipse($softPen, $offset, $offset, $size, $size)
                    }
                    $graphics.DrawLine($softPen, 75, 3, 75, 147)
                    $graphics.DrawLine($softPen, 3, 75, 147, 75)
                    $graphics.DrawArc($accentPen, 9, 9, 132, 132, 294, 52)
                    $graphics.FillEllipse($dotBrush, 104, 45, 9, 9)
                    $graphics.FillEllipse($dotBrush, 48, 101, 6, 6)
                } finally {
                    $dotBrush.Dispose(); $softPen.Dispose(); $accentPen.Dispose()
                }
            })
            $emptyTitle = New-Label "첫 번역 프로젝트를 준비하세요" 218 34 ([Math]::Max(280, $emptyWidth - 250)) 32 $script:itemText 13 ([System.Drawing.FontStyle]::Bold)
            $emptyBody = New-Label "위에서 감지된 모드를 고르면 프로젝트와 원문 언어를 연결합니다.`r`n원본 모드는 읽기 전용으로 분석하며, 번역은 검수 프로젝트에 먼저 저장됩니다." 218 78 ([Math]::Max(280, $emptyWidth - 250)) 54 $script:itemMuted 9
            $detectedCount = if ($script:modCatalog) { @($script:modCatalog).Count } else { 0 }
            $emptyStatus = New-Label "감지된 모드 $detectedCount개  ·  프로젝트 데이터는 로컬에만 저장" 218 142 ([Math]::Max(280, $emptyWidth - 250)) 24 $script:itemSubtle 8.3 ([System.Drawing.FontStyle]::Bold)
            $emptyCreate = New-Button "선택한 모드로 시작" $projectAccent
            $emptyCreate.ForeColor = if ($script:accentTextColor) { $script:accentTextColor } else { [System.Drawing.Color]::White }
            $emptyCreate.SetBounds(218, 178, 154, 38)
            $emptyCreate.Enabled = $null -ne $cmbDashboardMods.SelectedItem
            Set-AccessibleControl $emptyCreate "선택한 모드로 첫 프로젝트 만들기" "프로젝트 대상 모드 목록에서 선택한 모드로 번역 프로젝트를 만듭니다." 0
            $emptyCreate.Add_Click({ $btnDashboardAddMod.PerformClick() })
            $emptyChoose = New-Button "폴더에서 찾기" $script:surfaceColor
            $emptyChoose.ForeColor = $script:textColor
            $emptyChoose.FlatAppearance.BorderColor = $script:borderColor
            $emptyChoose.SetBounds(382, 178, 118, 38)
            Set-AccessibleControl $emptyChoose "모드 폴더에서 첫 프로젝트 만들기" "자동 감지되지 않은 RimWorld 모드 폴더를 직접 선택합니다." 1
            $emptyChoose.Add_Click({ $btnDashboardChooseMod.PerformClick() })
            $emptyPanel.Controls.AddRange(@($scan, $emptyTitle, $emptyBody, $emptyStatus, $emptyCreate, $emptyChoose))
            [void]$flowDashboardProjects.Controls.Add($emptyPanel)
            $renderSucceeded = $true
            return
        }

        foreach ($project in $matchingProjects) {
            $stats = Get-ProjectReviewStats $project
            $hasReview = $project.latestReviewRoot -and (Test-Path -LiteralPath ([string]$project.latestReviewRoot) -PathType Container)
            $card = [System.Windows.Forms.Panel]::new()
            $card.Size = [System.Drawing.Size]::new($cardWidth, 204)
            $card.Margin = [System.Windows.Forms.Padding]::new(10)
            $card.BackColor = $script:itemCardBack
            $card.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
            $card.Tag = $project
            $card.Cursor = [System.Windows.Forms.Cursors]::Hand

            $name = [string]$project.name
            $accentLine = [System.Windows.Forms.Panel]::new()
            $accentLine.SetBounds(0, 0, 4, [Math]::Max(1, $card.Height - 2))
            $accentLine.BackColor = $projectAccent
            $lblName = New-Label $name 22 18 $cardInnerWidth 26 $script:itemText 11.5 ([System.Drawing.FontStyle]::Bold)
            $lblName.AutoEllipsis = $true
            if ($toolTip) { $toolTip.SetToolTip($lblName, $name) }
            $idText = if ($project.workshopId) { "Workshop $($project.workshopId)" } elseif ($project.packageId) { [string]$project.packageId } else { Split-Path -Leaf ([string]$project.modRoot) }
            $sourceFolder = if ($project.PSObject.Properties["sourceLanguageFolder"]) { [string]$project.sourceLanguageFolder } else { "Auto" }
            $sourceText = if ($sourceFolder -eq "Auto") { "자동" } else { Get-ProjectSourceLanguageName $sourceFolder }
            $lblId = New-Label "$idText  ·  원문 $sourceText" 22 48 $cardInnerWidth 20 $script:itemMuted 8.3
            $lblId.AutoEllipsis = $true
            $totalText = if ($hasReview) { "전체 $($stats.Total)" } else { "원문 미로드" }
            $coverageText = if ($hasReview) { "번역 $($stats.Translated)  ·  검토 $($stats.Approved)" } else { "열어서 원문을 불러오세요" }
            $lblTotal = New-Label $totalText 22 78 132 30 $script:itemText $(if ($hasReview) { 13 } else { 11 }) ([System.Drawing.FontStyle]::Bold)
            $lblCoverage = New-Label $coverageText 154 83 ([Math]::Max(140, $cardWidth - 176)) 24 $script:itemMuted 8.8
            $lblPending = New-Label ("미번역 " + $stats.Pending) 22 116 110 22 (Get-StatusColor "pending") 8.7 ([System.Drawing.FontStyle]::Bold)
            $lblUpdated = New-Label ("업데이트 변경 " + $stats.Updated) 154 116 ([Math]::Max(140, $cardWidth - 176)) 22 $(if ($stats.Updated -gt 0) { Get-UpdateColor } else { $script:itemSubtle }) 8.7 ([System.Drawing.FontStyle]::Bold)
            $lblPending.Visible = [bool]$hasReview
            $lblUpdated.Visible = [bool]$hasReview

            $progressTrack = [System.Windows.Forms.Panel]::new()
            $progressTrack.SetBounds(22, 146, $cardInnerWidth, 5)
            $progressTrack.BackColor = if (Get-IsWindowsDarkMode) { [System.Drawing.Color]::FromArgb(69, 70, 66) } else { [System.Drawing.Color]::FromArgb(220, 222, 216) }
            $progressFill = [System.Windows.Forms.Panel]::new()
            $completed = $stats.Translated + $stats.Approved
            $fillWidth = if ($stats.Total -gt 0) { [int][Math]::Round($cardInnerWidth * ($completed / [double]$stats.Total)) } else { 0 }
            $progressFill.SetBounds(0, 0, [Math]::Max(0, [Math]::Min($cardInnerWidth, $fillWidth)), 5)
            $progressFill.BackColor = $projectAccent
            $progressTrack.Controls.Add($progressFill)

            $lblTime = New-Label ("최근 작업 " + (Format-LocalTimeText ([string]$project.latestReviewAt))) 22 170 ([Math]::Max(110, $cardWidth - 218)) 20 $script:itemSubtle 8.1
            $btnOpen = New-Button "열기" $projectAccent
            $btnOpen.ForeColor = if ($script:accentTextColor) { $script:accentTextColor } else { [System.Drawing.Color]::White }
            $btnOpen.SetBounds(($cardWidth - 106), 158, 86, 36)
            $btnOpen.Tag = $project
            Set-AccessibleControl $btnOpen "$name 프로젝트 열기" "$name 모드의 번역 및 검수 작업 화면을 엽니다." 0
            $btnOpen.Add_Click({ Open-ProjectWorkspace $this.Tag })
            $btnDelete = New-Button "삭제" $deleteColor
            $btnDelete.ForeColor = [System.Drawing.Color]::White
            $btnDelete.SetBounds(($cardWidth - 184), 158, 70, 36)
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

function Get-ApiProviderKeyCount([string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return 0 }
    return @([System.Text.RegularExpressions.Regex]::Split($Text, "\r?\n") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count
}

function Get-ApiProviderValidationMessage([string]$Code) {
    switch ($Code) {
        "UrlMissing" { return "API URL을 입력해야 합니다." }
        "UrlInvalid" { return "API URL 형식이 올바르지 않습니다." }
        "HttpsRequired" { return "외부 API는 HTTPS만 허용합니다. HTTP는 로컬 루프백 시험에만 사용할 수 있습니다." }
        "UrlContainsCredential" { return "API URL에 키나 인증정보를 넣지 마세요. 키 입력란을 사용하세요." }
        "ModelMissing" { return "모델 ID를 입력해야 합니다." }
        "TemperatureOutOfRange" { return "Temperature는 모델 기본값 또는 0~2 범위여야 합니다." }
        "NoKeyUsesGoogleFallback" { return "키 없음: 실행 시 Google 번역으로 전환" }
        "ManualModel" { return "수동 모델 ID 유지" }
        "OfflineAvailabilityNotVerified" { return "모델 제공 여부 온라인 미확인" }
        "GooglePromptFeaturesUnavailable" { return "Google 번역: 용어집·추가 프롬프트 미지원" }
        default { return $Code }
    }
}

function Update-ApiProviderValidationNotice {
    if (-not $lblApiProviderNotice) { return }
    $profile = Get-ApiProviderProfile
    $config = Get-SelectedApiProviderConfig
    if (-not $profile -or -not $config) { return }
    $keyCount = Get-ApiProviderKeyCount ([string]$script:apiProviderKeys[$script:selectedApiProviderId])
    $result = Test-RimWorldApiProviderConfiguration -Profile $profile -Config $config -KeyCount $keyCount
    if (-not $result.Valid) {
        $messages = @($result.ErrorCodes | ForEach-Object { Get-ApiProviderValidationMessage ([string]$_) })
        $lblApiProviderNotice.Text = [string]::Join(" ", $messages)
        $lblApiProviderNotice.ForeColor = [System.Drawing.Color]::FromArgb(220, 104, 104)
    } elseif ([string]$profile.Provider -eq "Google") {
        $lblApiProviderNotice.Text = "API 키 없이 Google 기계 번역을 사용합니다. 용어집과 추가 프롬프트는 적용되지 않습니다."
        $lblApiProviderNotice.ForeColor = $script:mutedColor
    } else {
        $prefix = if ($result.WarningCodes -contains "NoKeyUsesGoogleFallback") {
            "키 없음: Google 전환"
        } elseif ($result.WarningCodes -contains "ManualModel") {
            "수동 모델 ID 유지"
        } else {
            "내장 프로필 일치"
        }
        $limit = if ([int]$result.Capabilities.rpm -gt 0) { " · $($result.Capabilities.rpm) RPM 프로필" } else { "" }
        $lblApiProviderNotice.Text = "$prefix$limit · 모델 온라인 미확인"
        $lblApiProviderNotice.ForeColor = if ($result.WarningCodes -contains "NoKeyUsesGoogleFallback") { [System.Drawing.Color]::FromArgb(226, 173, 84) } else { $script:mutedColor }
    }
    $lblApiProviderNotice.AccessibleDescription = "로컬 설정 점검 결과. API 호출 없이 URL, 모델 입력과 내장 프로필만 확인합니다. $($lblApiProviderNotice.Text)"
    if ($toolTip) { $toolTip.SetToolTip($lblApiProviderNotice, $lblApiProviderNotice.Text) }
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
    Update-ApiProviderValidationNotice
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
        if ($txtApiKeys) { $txtApiKeys.Text = [string]$script:apiProviderKeys[$script:selectedApiProviderId] }
    } finally {
        $script:syncingApiProvider = $false
    }
    Refresh-ApiProviderButtons
    Update-ApiProviderValidationNotice
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
    $lblDashSettingsNote.SetBounds(132, 332, [Math]::Max(180, $fieldWidth - 132), 24)
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

function Export-DiagnosticBundle {
    $dialog = [System.Windows.Forms.SaveFileDialog]::new()
    $dialog.Title = "진단 번들 저장"
    $dialog.Filter = "ZIP 파일 (*.zip)|*.zip"
    $dialog.DefaultExt = "zip"
    $dialog.AddExtension = $true
    $dialog.OverwritePrompt = $true
    $dialog.FileName = "RimWorldAiTranslator-diagnostics-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".zip"
    try {
        if ($dialog.ShowDialog($form) -ne [System.Windows.Forms.DialogResult]::OK) { return }
        $result = New-RimWorldDiagnosticBundle `
            -OutputPath $dialog.FileName `
            -AppDataRoot $script:appDataRoot `
            -ProductRoot $scriptRoot `
            -RuntimeLogLines @($txtLog.Lines) `
            -Force
        Add-Log "진단 번들을 생성했습니다. 원문·번역문·키·API 키·원시 로그는 포함하지 않았습니다."
        [System.Windows.Forms.MessageBox]::Show(
            "진단 번들을 저장했습니다.`r`n`r`n$($result.Path)`r`n`r`n원문, 번역문, 번역 키, API 키, 전체 경로와 원시 로그는 포함하지 않습니다.",
            "진단 번들",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Information
        ) | Out-Null
    } catch {
        Add-Log "진단 번들 생성 실패: $($_.Exception.GetType().Name)"
        [System.Windows.Forms.MessageBox]::Show("진단 번들을 만들지 못했습니다.`r`n$($_.Exception.Message)", "진단 번들", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    } finally {
        $dialog.Dispose()
    }
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
        $pnlAppearanceSettings.SetBounds($appearanceX, 66, $appearanceWidth, 410)
        $pnlRmkSettings.SetBounds(28, 536, $inner, 190)
        $dashSettingsPage.AutoScrollMinSize = [System.Drawing.Size]::new(0, 750)
    } else {
        $pnlApiSettings.SetBounds(28, 66, $inner, 440)
        $pnlAppearanceSettings.SetBounds(28, 532, $inner, 410)
        $pnlRmkSettings.SetBounds(28, 970, $inner, 220)
        $dashSettingsPage.AutoScrollMinSize = [System.Drawing.Size]::new(0, 1220)
    }
    $appearanceControlWidth = [Math]::Min(360, [Math]::Max(220, $pnlAppearanceSettings.ClientSize.Width))
    $cmbDashboardDesignPreset.Width = [Math]::Min(260, $appearanceControlWidth)
    $lblDashDesignDescription.Width = $appearanceControlWidth
    Resize-ApiProviderSettingsLayout
    Resize-RmkSettingsLayout
}

function Update-DashboardDesignPresetDescription {
    if (-not $lblDashDesignDescription -or -not $cmbDashboardDesignPreset) { return }
    $selected = $cmbDashboardDesignPreset.SelectedItem
    $lblDashDesignDescription.Text = if ($selected) { [string]$selected.Description } else { "화면 성격을 선택합니다." }
}

function Sync-DashboardSettingsFromMain {
    if (-not $txtDashboardApiKeys) { return }
    $script:syncingSettings = $true
    try {
        if ($txtApiKeys -and $txtApiKeys.Text -and -not $script:apiProviderKeys[$script:selectedApiProviderId]) {
            $script:apiProviderKeys[$script:selectedApiProviderId] = $txtApiKeys.Text
        }
        Show-ApiProviderControls -ProviderId $script:selectedApiProviderId -SkipCurrentSave
        $chkDashboardIncludePatches.Checked = $false
        $chkIncludePatches.Checked = $false
        $chkDashboardDryRun.Checked = $chkDryRun.Checked
        $presetIndex = -1
        for ($i = 0; $i -lt $cmbDashboardDesignPreset.Items.Count; $i++) {
            if ([string]$cmbDashboardDesignPreset.Items[$i].Id -eq $script:designPreset) { $presetIndex = $i; break }
        }
        $cmbDashboardDesignPreset.SelectedIndex = if ($presetIndex -ge 0) { $presetIndex } else { 0 }
        Update-DashboardDesignPresetDescription
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
        $chkIncludePatches.Checked = $false
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
    $txtDiffSource.Font = New-Font ([Math]::Max(8.5, $bodySize - 1))
    $txtDiffBefore.Font = New-Font ([Math]::Max(8.5, $bodySize - 1))
    $txtDiffAfter.Font = New-Font ([Math]::Max(8.5, $bodySize - 1))
    $txtMeta.Font = New-Font ([Math]::Max(9, $bodySize - 1))
    $script:historyTitleFont = New-Font ([Math]::Max(8.5, $bodySize - 1.5)) ([System.Drawing.FontStyle]::Bold)
    $script:historyBodyFont = New-Font ([Math]::Max(9, $bodySize - 0.5))
    $txtHistory.Font = $script:historyBodyFont
}

function Apply-DashboardPreferences {
    if ($script:syncingSettings) { return }
    if ($cmbDashboardDesignPreset.SelectedItem) { $script:designPreset = [string]$cmbDashboardDesignPreset.SelectedItem.Id }
    $script:themeMode = switch ($cmbDashboardTheme.SelectedIndex) { 1 { "Light" } 2 { "Dark" } default { "System" } }
    if ($cmbDashboardTextSize.SelectedItem) {
        $script:textSize = [Math]::Max(9, [Math]::Min(12, [int][string]$cmbDashboardTextSize.SelectedItem))
    }
    $script:highContrast = $chkDashboardHighContrast.Checked
    $script:autoSave = $chkDashboardAutoSave.Checked
    Update-DashboardDesignPresetDescription
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

function Get-CommandPaletteActions {
    $workspaceVisible = $main -and $main.Visible -and -not ($operationOverlay -and $operationOverlay.Visible)
    $hasReview = $script:rows.Count -gt 0 -and [bool]$script:reviewRoot
    $hasMod = [bool](Get-ActiveProjectModRoot)
    $running = $script:process -and -not $script:process.HasExited
    return @(
        [pscustomobject]@{ Id = "projects"; Name = "프로젝트 목록 열기"; Group = "이동"; Shortcut = "Ctrl+Home"; Enabled = $true },
        [pscustomobject]@{ Id = "search"; Name = "현재 화면 검색"; Group = "이동"; Shortcut = "Ctrl+F"; Enabled = $true },
        [pscustomobject]@{ Id = "compare"; Name = "선택 문자열 비교 열기"; Group = "검수"; Shortcut = "Alt+C"; Enabled = $workspaceVisible -and $hasReview },
        [pscustomobject]@{ Id = "quality"; Name = "프로젝트 품질 센터 열기"; Group = "검수"; Shortcut = "Alt+Q"; Enabled = $workspaceVisible -and $hasReview },
        [pscustomobject]@{ Id = "previous"; Name = "이전 문자열"; Group = "검수"; Shortcut = "Shift+F3"; Enabled = $workspaceVisible -and $hasReview },
        [pscustomobject]@{ Id = "next"; Name = "다음 문자열"; Group = "검수"; Shortcut = "F3"; Enabled = $workspaceVisible -and $hasReview },
        [pscustomobject]@{ Id = "approve-next"; Name = "검토 완료 후 다음"; Group = "검수"; Shortcut = "Ctrl+Enter"; Enabled = $workspaceVisible -and $hasReview },
        [pscustomobject]@{ Id = "save"; Name = "검수 내용 저장"; Group = "프로젝트"; Shortcut = "Ctrl+S"; Enabled = $workspaceVisible -and $hasReview },
        [pscustomobject]@{ Id = "source"; Name = "모드 원문 다시 분석"; Group = "프로젝트"; Shortcut = "F5"; Enabled = $workspaceVisible -and $hasMod -and -not $running },
        [pscustomobject]@{ Id = "translate"; Name = "AI 초벌 번역 준비"; Group = "프로젝트"; Shortcut = "F9"; Enabled = $workspaceVisible -and $hasMod -and -not $running },
        [pscustomobject]@{ Id = "report"; Name = "개인정보 보호 품질 보고서"; Group = "도구"; Shortcut = ""; Enabled = $workspaceVisible -and $hasReview },
        [pscustomobject]@{ Id = "folder"; Name = "현재 모드 폴더 열기"; Group = "도구"; Shortcut = ""; Enabled = $workspaceVisible -and $hasMod },
        [pscustomobject]@{ Id = "settings"; Name = "API 및 화면 설정 열기"; Group = "설정"; Shortcut = ""; Enabled = $true },
        [pscustomobject]@{ Id = "diagnostics"; Name = "개인정보 보호 진단 번들 저장"; Group = "도구"; Shortcut = ""; Enabled = $true }
    )
}

function Invoke-CommandPaletteAction([string]$Id) {
    switch ($Id) {
        "projects" { Show-Dashboard "projects" }
        "search" {
            $target = if ($dashboardPanel.Visible) { $txtDashboardSearch } else { $txtSearch }
            [void]$target.Focus(); $target.SelectAll()
        }
        "compare" { $tabs.SelectedTab = $tabHistory }
        "quality" { $tabs.SelectedTab = $tabIssues; Refresh-QualityCenter }
        "previous" { Move-Selection -1 }
        "next" { Move-Selection 1 }
        "approve-next" { Mark-Current "approved" $true }
        "save" { Save-ReviewWithDuplicatePrompt }
        "source" { Load-SourceOnlyForSelectedMod }
        "translate" { Start-Translation }
        "report" { Export-QualityReport }
        "folder" { Open-ModFolder }
        "settings" { Show-Dashboard "settings" }
        "diagnostics" { Export-DiagnosticBundle }
    }
}

function Show-CommandPalette {
    param([switch]$PreviewOnly)
    if ($operationOverlay -and $operationOverlay.Visible) { return }
    $dialog = [System.Windows.Forms.Form]::new()
    $dialog.Text = "명령 찾기"
    $dialog.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterParent
    $dialog.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
    $dialog.ClientSize = [System.Drawing.Size]::new(640, 476)
    $dialog.MinimizeBox = $false
    $dialog.MaximizeBox = $false
    $dialog.ShowInTaskbar = $false
    $dialog.ShowIcon = $false
    $dialog.BackColor = $script:surfaceColor
    $dialog.Font = New-Font 9
    $dialog.Tag = ""

    $accent = [System.Windows.Forms.Panel]::new()
    $accent.SetBounds(0, 0, 640, 4)
    $accent.BackColor = $script:accentColor
    $title = New-Label "명령 찾기" 24 20 400 30 $script:textColor 13 ([System.Drawing.FontStyle]::Bold)
    $hint = New-Label "작업 이름을 입력하고 Enter를 누르세요. 비활성 명령은 현재 화면에서 실행할 수 없습니다." 24 52 590 22 $script:mutedColor 8.5
    $search = New-TextBox
    $search.SetBounds(24, 86, 592, 34)
    $list = [System.Windows.Forms.ListView]::new()
    $list.SetBounds(24, 132, 592, 280)
    $list.View = [System.Windows.Forms.View]::Details
    $list.FullRowSelect = $true
    $list.HideSelection = $false
    $list.MultiSelect = $false
    $list.Font = New-Font 9
    $list.BackColor = $script:surfaceColor
    $list.ForeColor = $script:textColor
    [void]$list.Columns.Add("명령", 350)
    [void]$list.Columns.Add("영역", 90)
    [void]$list.Columns.Add("단축키", 120)
    $run = New-Button "실행" $script:accentColor
    $run.ForeColor = $script:accentTextColor
    $run.SetBounds(506, 426, 110, 36)
    $cancel = New-Button "닫기" $script:surfaceColor
    $cancel.ForeColor = $script:textColor
    $cancel.FlatAppearance.BorderColor = $script:borderColor
    $cancel.SetBounds(388, 426, 110, 36)
    Set-AccessibleControl $search "명령 검색" "프로젝트, 번역, 검수와 설정 명령을 이름으로 검색합니다." 0
    Set-AccessibleControl $list "사용 가능한 명령 목록" "검색 결과와 현재 화면에서 실행 가능한지 표시합니다." 1
    Set-AccessibleControl $run "선택한 명령 실행" "선택한 활성 명령을 실행합니다." 2
    Set-AccessibleControl $cancel "명령 찾기 닫기" "명령을 실행하지 않고 닫습니다." 3
    Set-CueBanner $search "명령 검색"

    $actions = @(Get-CommandPaletteActions)
    $paletteMutedColor = $script:mutedColor
    $refresh = {
        $query = $search.Text.Trim().ToLowerInvariant()
        $list.BeginUpdate()
        try {
            $list.Items.Clear()
            foreach ($action in $actions) {
                $blob = "$($action.Name) $($action.Group) $($action.Shortcut)".ToLowerInvariant()
                if ($query -and -not $blob.Contains($query)) { continue }
                $item = [System.Windows.Forms.ListViewItem]::new([string]$action.Name)
                [void]$item.SubItems.Add([string]$action.Group)
                [void]$item.SubItems.Add([string]$action.Shortcut)
                $item.Tag = $action
                if (-not [bool]$action.Enabled) { $item.ForeColor = $paletteMutedColor }
                [void]$list.Items.Add($item)
            }
            if ($list.Items.Count -gt 0) { $list.Items[0].Selected = $true }
        } finally { $list.EndUpdate() }
        $run.Enabled = $list.SelectedItems.Count -gt 0 -and [bool]$list.SelectedItems[0].Tag.Enabled
    }.GetNewClosure()
    $execute = {
        if ($list.SelectedItems.Count -eq 0) { return }
        $action = $list.SelectedItems[0].Tag
        if (-not [bool]$action.Enabled) { return }
        $dialog.Tag = [string]$action.Id
        $dialog.Close()
    }.GetNewClosure()
    $search.Add_TextChanged(({ & $refresh }).GetNewClosure())
    $list.Add_SelectedIndexChanged(({
        if ($list.IsDisposed -or $run.IsDisposed) { return }
        $hasSelection = $list.SelectedItems.Count -gt 0
        $run.Enabled = $hasSelection -and [bool]$list.SelectedItems[0].Tag.Enabled
    }).GetNewClosure())
    $list.Add_DoubleClick(({ & $execute }).GetNewClosure())
    $search.Add_KeyDown(({
        if ($_.KeyCode -eq [System.Windows.Forms.Keys]::Down -and $list.Items.Count -gt 0) { [void]$list.Focus(); $list.Items[0].Selected = $true; $_.SuppressKeyPress = $true }
        elseif ($_.KeyCode -eq [System.Windows.Forms.Keys]::Enter) { & $execute; $_.SuppressKeyPress = $true }
    }).GetNewClosure())
    $list.Add_KeyDown(({ if ($_.KeyCode -eq [System.Windows.Forms.Keys]::Enter) { & $execute; $_.SuppressKeyPress = $true } }).GetNewClosure())
    $run.Add_Click(({ & $execute }).GetNewClosure())
    $cancel.Add_Click(({ $dialog.Close() }).GetNewClosure())
    $dialog.CancelButton = $cancel
    $dialog.Controls.AddRange(@($accent, $title, $hint, $search, $list, $run, $cancel))
    & $refresh
    $dialog.Add_Shown(({ [void]$search.Focus() }).GetNewClosure())
    if ($PreviewOnly) {
        $dialog.Show($form)
        return $dialog
    }
    try {
        [void]$dialog.ShowDialog($form)
        $selectedAction = [string]$dialog.Tag
    } finally {
        $dialog.Dispose()
    }
    if ($selectedAction) { Invoke-CommandPaletteAction $selectedAction }
}

function Focus-NextWorkRegion([int]$Direction = 1) {
    if ($dashboardPanel.Visible) {
        if ($dashSettingsPage.Visible) {
            $targets = @($txtDashboardApiKeys, $cmbDashboardDesignPreset, $cmbDashboardTheme, $cmbDashboardTextSize, $chkDashboardAutoSave, $btnDashboardRmkChoose, $chkDashboardRmkUseExisting)
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
    $inactiveBack = if ($script:headerButtonColor) { $script:headerButtonColor } else { [System.Drawing.Color]::FromArgb(48, 53, 49) }
    $inactiveFore = if ($script:headerTextColor) { $script:headerTextColor } else { [System.Drawing.Color]::FromArgb(224, 229, 222) }
    foreach ($button in @($btnDashProjects, $btnDashActivity, $btnDashSettings)) {
        $button.BackColor = $inactiveBack
        $button.ForeColor = $inactiveFore
        $button.FlatAppearance.BorderColor = if ($script:borderColor) { $script:borderColor } else { [System.Drawing.Color]::FromArgb(77, 83, 77) }
        $button.FlatAppearance.BorderSize = 1
    }
    $active = if ($dashSettingsPage.Visible) { $btnDashSettings } elseif ($dashActivityPage.Visible) { $btnDashActivity } else { $btnDashProjects }
    $active.BackColor = $script:accentColor
    $active.ForeColor = if ($script:accentTextColor) { $script:accentTextColor } else { [System.Drawing.Color]::White }
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
    $dashProjectsPage.Visible = $false
    $dashActivityPage.Visible = $false
    $dashSettingsPage.Visible = $false

    switch ($Tab) {
        "activity" {
            $dashActivityPage.Visible = $true
            Refresh-DashboardActivity
        }
        "settings" {
            $dashSettingsPage.Visible = $true
            Refresh-RmkRoots
            Sync-DashboardSettingsFromMain
        }
        default {
            $dashProjectsPage.Visible = $true
            if ($script:startupRevealComplete) { Refresh-DashboardProjects }
        }
    }
    Refresh-DashboardTabButtons
    Update-OperationContentLayout
}

function Show-Workspace {
    $form.SuspendLayout()
    try {
        if ($dashboardPanel) { $dashboardPanel.Visible = $false }
        $top.Visible = $true
        $main.Visible = $false
        Sync-MainSettingsFromDashboard
        if (Get-Command Apply-AppTheme -ErrorAction SilentlyContinue) { Apply-AppTheme }
        Update-OperationContentLayout
        $main.Visible = $true
        $main.BringToFront()
        $top.BringToFront()
        if ($operationOverlay -and $operationOverlay.Visible) { $operationOverlay.BringToFront() }
    } finally {
        $form.ResumeLayout($true)
    }
}

Ensure-AppDataStore
try {
    Load-AppSettings
} catch {
    $script:startupSettingsError = $_.Exception.Message
}
if ($PreviewTheme -in @("System", "Light", "Dark")) { $script:themeMode = $PreviewTheme }
if ($PreviewDesignPreset -in @("Professional", "SciFi", "Vivid", "Studio", "Frontier")) { $script:designPreset = $PreviewDesignPreset }
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
$form.Opacity = 0.0
$form.ShowInTaskbar = $false
$script:startupRevealComplete = $false

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
    $metaHeight = if ($ultraCompact) { 42 } elseif ($veryCompact) { 82 } else { 92 }

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
    $translationAccent.SetBounds(0, [Math]::Max(0, $pnlTranslationFrame.ClientSize.Height - 3), [Math]::Max(1, $pnlTranslationFrame.ClientSize.Width), 3)

    $metaY = $translationBoxY + $translationHeight + $(if ($ultraCompact) { 8 } else { 14 })
    $txtMeta.SetBounds($pad, $metaY, $contentWidth, $metaHeight)
    $dividerY = $metaY + $metaHeight + $(if ($ultraCompact) { 4 } else { 8 })
    $editorDivider.SetBounds($pad, $dividerY, $contentWidth, 1)
    $toolbarY = $dividerY + $(if ($ultraCompact) { 8 } else { 12 })

    $utilityButtons = @($btnPrev, $btnNext, $btnUseCandidate, $btnUseExisting, $btnUseSource, $btnResetEdit)
    $utilityWidths = if ($ultraCompact) { @(36, 36, 62, 54, 50, 62) } else { @(38, 38, 72, 62, 58, 72) }
    $btnResetEdit.Text = if ($ultraCompact) { "↶" } else { "되돌리기" }
    $statusButtons = @($btnPending, $btnTranslated, $btnApprove, $btnApproveNext, $btnApproveAll)
    $statusWidths = if ($ultraCompact) { @(64, 68, 80, 96, 80) } else { @(72, 76, 88, 108, 88) }
    $gap = if ($ultraCompact) { 5 } else { 7 }
    $toolbarHeight = if ($ultraCompact) { 32 } else { 36 }
    $utilityTotal = ($utilityWidths | Measure-Object -Sum).Sum + ($gap * ($utilityWidths.Count - 1))
    $statusTotal = ($statusWidths | Measure-Object -Sum).Sum + ($gap * ($statusWidths.Count - 1))
    $singleRow = $contentWidth -ge ($utilityTotal + $statusTotal + 22)

    $x = $pad
    for ($i = 0; $i -lt $utilityButtons.Count; $i++) {
        $utilityButtons[$i].SetBounds($x, $toolbarY, $utilityWidths[$i], $toolbarHeight)
        $x += $utilityWidths[$i] + $gap
    }

    if ($singleRow) {
        $x = $pad + $contentWidth - $statusTotal
        $statusY = $toolbarY
        $toolbarBottom = $toolbarY + $toolbarHeight
    } else {
        $x = $pad
        $statusY = $toolbarY + $(if ($ultraCompact) { 36 } else { 44 })
        $toolbarBottom = $statusY + $toolbarHeight
    }
    for ($i = 0; $i -lt $statusButtons.Count; $i++) {
        $statusButtons[$i].SetBounds($x, $statusY, $statusWidths[$i], $toolbarHeight)
        $x += $statusWidths[$i] + $gap
    }

    $referenceTitleY = $toolbarBottom + $(if ($ultraCompact) { 8 } else { 17 })
    $suggestionLabelY = $referenceTitleY + $(if ($ultraCompact) { 21 } else { 25 })
    $lblReferenceTitle.SetBounds($pad, $referenceTitleY, $contentWidth, 20)
    $halfWidth = [Math]::Max(140, [int](($contentWidth - 14) / 2))
    $bottomBoxY = $suggestionLabelY + $(if ($ultraCompact) { 19 } else { 22 })
    $minimumBottomHeight = if ($ultraCompact) { 52 } else { 76 }
    $bottomMargin = if ($ultraCompact) { 8 } else { 18 }
    $bottomHeight = [Math]::Max($minimumBottomHeight, $center.ClientSize.Height - $bottomBoxY - $bottomMargin)
    $lblExisting.SetBounds($pad, $suggestionLabelY, $halfWidth, 18)
    $txtExisting.SetBounds($pad, $bottomBoxY, $halfWidth, $bottomHeight)
    $candidateX = $pad + $halfWidth + 14
    $lblCandidate.SetBounds($candidateX, $suggestionLabelY, $halfWidth, 18)
    $txtCandidate.SetBounds($candidateX, $bottomBoxY, $halfWidth, $bottomHeight)
    $requiredHeight = $bottomBoxY + [Math]::Max($minimumBottomHeight, $bottomHeight) + $bottomMargin
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
$tabHistory.Text = "비교"
$tabTerms = [System.Windows.Forms.TabPage]::new()
$tabTerms.Text = "용어"
$tabMemo = [System.Windows.Forms.TabPage]::new()
$tabMemo.Text = "메모"
$tabRmk = [System.Windows.Forms.TabPage]::new()
$tabRmk.Text = "RMK"
$tabIssues = [System.Windows.Forms.TabPage]::new()
$tabIssues.Text = "품질"
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
$txtHistory.ReadOnly = $true
$txtHistory.DetectUrls = $false
$txtHistory.Font = New-Font 9.5
$txtHistory.BackColor = [System.Drawing.Color]::FromArgb(248, 249, 250)
$script:historyTitleFont = New-Font 8.5 ([System.Drawing.FontStyle]::Bold)
$script:historyBodyFont = New-Font 9.5
$tabHistory.AutoScroll = $true
$lblDiffSummary = New-Label "비교할 문자열을 선택하세요" 12 12 300 24 ([System.Drawing.Color]::FromArgb(52, 61, 70)) 9 ([System.Drawing.FontStyle]::Bold)
$lblDiffSource = New-Label "원문" 12 44 280 18 ([System.Drawing.Color]::FromArgb(80, 88, 96)) 8.2 ([System.Drawing.FontStyle]::Bold)
$txtDiffSource = [System.Windows.Forms.RichTextBox]::new()
$txtDiffSource.ReadOnly = $true
$txtDiffSource.DetectUrls = $false
$txtDiffSource.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
$txtDiffSource.Font = New-Font 8.8
$lblDiffBefore = New-Label "기존 번역" 12 144 280 18 ([System.Drawing.Color]::FromArgb(80, 88, 96)) 8.2 ([System.Drawing.FontStyle]::Bold)
$txtDiffBefore = [System.Windows.Forms.RichTextBox]::new()
$txtDiffBefore.ReadOnly = $true
$txtDiffBefore.DetectUrls = $false
$txtDiffBefore.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
$txtDiffBefore.Font = New-Font 8.8
$lblDiffAfter = New-Label "현재 번역" 12 244 280 18 ([System.Drawing.Color]::FromArgb(80, 88, 96)) 8.2 ([System.Drawing.FontStyle]::Bold)
$txtDiffAfter = [System.Windows.Forms.RichTextBox]::new()
$txtDiffAfter.ReadOnly = $true
$txtDiffAfter.DetectUrls = $false
$txtDiffAfter.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
$txtDiffAfter.Font = New-Font 8.8
$lblHistoryDetail = New-Label "번역 기록" 12 344 280 18 ([System.Drawing.Color]::FromArgb(80, 88, 96)) 8.2 ([System.Drawing.FontStyle]::Bold)
$tabHistory.Controls.AddRange(@($lblDiffSummary, $lblDiffSource, $txtDiffSource, $lblDiffBefore, $txtDiffBefore, $lblDiffAfter, $txtDiffAfter, $lblHistoryDetail, $txtHistory))

function Resize-DiffTab {
    if (-not $tabHistory -or $tabHistory.ClientSize.Width -le 0) { return }
    $pad = 12
    $width = [Math]::Max(240, $tabHistory.ClientSize.Width - ($pad * 2))
    $wide = $width -ge 680
    if ($wide) {
        $gap = 10
        $column = [Math]::Max(190, [int](($width - ($gap * 2)) / 3))
        $labels = @($lblDiffSource, $lblDiffBefore, $lblDiffAfter)
        $boxes = @($txtDiffSource, $txtDiffBefore, $txtDiffAfter)
        for ($i = 0; $i -lt 3; $i++) {
            $x = $pad + (($column + $gap) * $i)
            $labels[$i].SetBounds($x, 44, $column, 18)
            $boxes[$i].SetBounds($x, 66, $column, 116)
        }
        $lblHistoryDetail.SetBounds($pad, 198, $width, 18)
        $txtHistory.SetBounds($pad, 220, $width, [Math]::Max(120, $tabHistory.ClientSize.Height - 234))
        $tabHistory.AutoScrollMinSize = [System.Drawing.Size]::new(0, 380)
    } else {
        $boxHeight = 74
        $y = 44
        foreach ($pair in @(
            [pscustomobject]@{ Label = $lblDiffSource; Box = $txtDiffSource },
            [pscustomobject]@{ Label = $lblDiffBefore; Box = $txtDiffBefore },
            [pscustomobject]@{ Label = $lblDiffAfter; Box = $txtDiffAfter }
        )) {
            $pair.Label.SetBounds($pad, $y, $width, 18)
            $pair.Box.SetBounds($pad, ($y + 22), $width, $boxHeight)
            $y += 110
        }
        $lblHistoryDetail.SetBounds($pad, $y, $width, 18)
        $txtHistory.SetBounds($pad, ($y + 22), $width, 170)
        $tabHistory.AutoScrollMinSize = [System.Drawing.Size]::new(0, $y + 214)
    }
    $lblDiffSummary.SetBounds($pad, 12, $width, 24)
}
$tabHistory.Add_Resize({ Resize-DiffTab })
Resize-DiffTab

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
$txtWarnings.ReadOnly = $true
$txtWarnings.BackColor = [System.Drawing.Color]::FromArgb(248, 249, 250)
$tabIssues.AutoScroll = $true
$lblQualitySummary = New-Label "품질 검사를 준비하는 중" 12 12 320 42 ([System.Drawing.Color]::FromArgb(52, 61, 70)) 9 ([System.Drawing.FontStyle]::Bold)
$cmbQualityCategory = [System.Windows.Forms.ComboBox]::new()
$cmbQualityCategory.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbQualityCategory.Font = New-Font 8.5
[void]$cmbQualityCategory.Items.AddRange(@("전체 문제", "오류", "경고", "미번역", "원문 변경", "토큰/태그", "길이 이상", "원문과 동일", "중복 식별자"))
$cmbQualityCategory.SelectedIndex = 0
$btnQualityRefresh = New-Button "다시 검사" ([System.Drawing.Color]::FromArgb(238, 241, 244))
$btnQualityReport = New-Button "보고서" ([System.Drawing.Color]::FromArgb(166, 124, 70))
$btnQualityReport.ForeColor = [System.Drawing.Color]::White
$lvQualityIssues = [System.Windows.Forms.ListView]::new()
$lvQualityIssues.View = [System.Windows.Forms.View]::Details
$lvQualityIssues.VirtualMode = $true
$lvQualityIssues.VirtualListSize = 0
$lvQualityIssues.FullRowSelect = $true
$lvQualityIssues.HideSelection = $false
$lvQualityIssues.MultiSelect = $false
$lvQualityIssues.Font = New-Font 8.3
[void]$lvQualityIssues.Columns.Add("등급", 48)
[void]$lvQualityIssues.Columns.Add("분류", 92)
[void]$lvQualityIssues.Columns.Add("키", 200)
$lblSelectedQuality = New-Label "선택 문자열 검사" 12 350 260 18 ([System.Drawing.Color]::FromArgb(80, 88, 96)) 8.2 ([System.Drawing.FontStyle]::Bold)
$btnQualityJump = New-Button "문자열로 이동" ([System.Drawing.Color]::FromArgb(62, 122, 82))
$btnQualityJump.ForeColor = [System.Drawing.Color]::White
$btnQualityJump.Enabled = $false
$tabIssues.Controls.AddRange(@($lblQualitySummary, $cmbQualityCategory, $btnQualityRefresh, $btnQualityReport, $lvQualityIssues, $lblSelectedQuality, $txtWarnings, $btnQualityJump))

function Resize-QualityTab {
    if (-not $tabIssues -or $tabIssues.ClientSize.Width -le 0) { return }
    $pad = 12
    $width = [Math]::Max(240, $tabIssues.ClientSize.Width - ($pad * 2))
    $lblQualitySummary.SetBounds($pad, 12, $width, 42)
    $filterWidth = [Math]::Max(110, $width - 184)
    $cmbQualityCategory.SetBounds($pad, 60, $filterWidth, 30)
    $btnQualityRefresh.SetBounds(($pad + $filterWidth + 6), 60, 82, 30)
    $btnQualityReport.SetBounds(($pad + $filterWidth + 94), 60, 78, 30)
    $listHeight = [Math]::Max(150, [int]($tabIssues.ClientSize.Height * 0.46))
    $lvQualityIssues.SetBounds($pad, 100, $width, $listHeight)
    if ($lvQualityIssues.Columns.Count -ge 3) { $lvQualityIssues.Columns[2].Width = [Math]::Max(90, $width - 146) }
    $detailY = 112 + $listHeight
    $lblSelectedQuality.SetBounds($pad, $detailY, [Math]::Max(120, $width - 122), 18)
    $btnQualityJump.SetBounds(($pad + $width - 114), ($detailY - 4), 114, 30)
    $txtWarnings.SetBounds($pad, ($detailY + 26), $width, [Math]::Max(82, $tabIssues.ClientSize.Height - $detailY - 38))
    $tabIssues.AutoScrollMinSize = [System.Drawing.Size]::new(0, $detailY + 126)
}
$tabIssues.Add_Resize({ Resize-QualityTab })
Resize-QualityTab

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
$btnCommandPalette = New-Button "명령  Ctrl+Shift+P" ([System.Drawing.Color]::FromArgb(37, 46, 55))
$btnCommandPalette.ForeColor = [System.Drawing.Color]::FromArgb(215, 226, 237)
$btnCommandPalette.SetBounds(1000, 16, 154, 36)
$dashHeader.Controls.AddRange(@($lblDashTitle, $lblDashSub, $btnDashProjects, $btnDashActivity, $btnDashSettings, $btnCommandPalette, $dashAccent))

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

$lblDashEyebrow = New-Label "RIMWORLD TRANSLATION WORKSPACE" 32 20 340 18 ([System.Drawing.Color]::FromArgb(190, 150, 92)) 7.8 ([System.Drawing.FontStyle]::Bold)
$lblDashProjects = New-Label "모드 번역 작업실" 32 42 340 34 ([System.Drawing.Color]::White) 15 ([System.Drawing.FontStyle]::Bold)
$lblDashIntro = New-Label "원문을 분석하고 초벌 번역을 만든 뒤, 한 줄씩 검토해 안전하게 적용합니다." 32 78 620 24 ([System.Drawing.Color]::FromArgb(160, 174, 188)) 8.8
$lblDashProviderStatus = New-Label "번역 엔진 확인 중" 760 28 380 28 ([System.Drawing.Color]::FromArgb(218, 228, 238)) 9 ([System.Drawing.FontStyle]::Bold)
$lblDashProviderHint = New-Label "API 키는 저장하지 않습니다." 760 58 380 22 ([System.Drawing.Color]::FromArgb(150, 164, 178)) 8.1
$dashWorkflow = [System.Windows.Forms.FlowLayoutPanel]::new()
$dashWorkflow.FlowDirection = [System.Windows.Forms.FlowDirection]::LeftToRight
$dashWorkflow.WrapContents = $false
$dashWorkflow.AutoScroll = $false
$dashWorkflow.Margin = [System.Windows.Forms.Padding]::new(0)
$dashWorkflow.Padding = [System.Windows.Forms.Padding]::new(0)
$dashWorkflow.BackColor = [System.Drawing.Color]::Transparent
$dashWorkflowSteps = New-Object "System.Collections.Generic.List[System.Windows.Forms.Label]"
$stepIndex = 0
foreach ($stepText in @("모드 선택", "원문 분석", "초벌 번역", "검토 · 적용")) {
    $stepIndex++
    $step = New-Label (("0{0}  {1}" -f $stepIndex, $stepText)) 0 0 132 28 ([System.Drawing.Color]::FromArgb(180, 190, 200)) 8.2 ([System.Drawing.FontStyle]::Bold)
    $step.Margin = [System.Windows.Forms.Padding]::new(0, 0, 14, 0)
    $step.TextAlign = [System.Drawing.ContentAlignment]::MiddleLeft
    [void]$dashWorkflowSteps.Add($step)
    [void]$dashWorkflow.Controls.Add($step)
}
$dashboardDivider = [System.Windows.Forms.Panel]::new()
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
$flowDashboardProjects.SetBounds(16, 216, 1418, 456)
$flowDashboardProjects.Anchor = [System.Windows.Forms.AnchorStyles]::Top -bor [System.Windows.Forms.AnchorStyles]::Bottom -bor [System.Windows.Forms.AnchorStyles]::Left -bor [System.Windows.Forms.AnchorStyles]::Right
$flowDashboardProjects.AutoScroll = $true
$flowDashboardProjects.BackColor = [System.Drawing.Color]::FromArgb(18, 24, 30)
$dashProjectsPage.Controls.AddRange(@($lblDashEyebrow, $lblDashProjects, $lblDashIntro, $lblDashProviderStatus, $lblDashProviderHint, $dashWorkflow, $dashboardDivider, $lblDashboardSearch, $txtDashboardSearch, $lblDashboardMod, $cmbDashboardMods, $btnDashboardAddMod, $btnDashboardChooseMod, $btnDashboardRefreshMods, $flowDashboardProjects))

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
$chkDashboardIncludePatches.Checked = $false
$chkDashboardIncludePatches.Visible = $false
$chkDashboardDryRun = [System.Windows.Forms.CheckBox]::new()
$chkDashboardDryRun.Text = "Dry run"
$chkDashboardDryRun.SetBounds(0, 332, 120, 26)
$chkDashboardDryRun.ForeColor = [System.Drawing.Color]::FromArgb(218, 226, 234)
$chkDashboardDryRun.BackColor = [System.Drawing.Color]::Transparent
$lblDashSettingsNote = New-Label "배치 크기 40 · 여러 키는 입력 순서대로 순환" 132 332 360 24 ([System.Drawing.Color]::FromArgb(150, 164, 178)) 8.3
$pnlApiDetail.Controls.AddRange(@($lblApiProviderTitle, $lblApiProviderDescription, $lblApiProviderCustomName, $txtApiProviderCustomName, $lblApiProviderKeys, $txtDashboardApiKeys, $lblApiProviderUrl, $txtApiProviderUrl, $lblApiProviderModel, $cmbApiProviderModel, $lblApiProviderTemperature, $cmbApiProviderTemperature, $lblApiProviderNotice, $chkDashboardIncludePatches, $chkDashboardDryRun, $lblDashSettingsNote))
$pnlApiSettings.Controls.AddRange(@($lblDashApi, $lblDashApiHint, $flowApiProviders, $apiProviderDivider, $pnlApiDetail))

$pnlAppearanceSettings = [System.Windows.Forms.Panel]::new()
$pnlAppearanceSettings.SetBounds(776, 66, 350, 410)
$settingsDivider = [System.Windows.Forms.Panel]::new()
$settingsDivider.SetBounds(0, 0, 350, 1)
$lblDashAppearance = New-Label "화면 및 편집" 0 0 240 28 ([System.Drawing.Color]::FromArgb(218, 228, 238)) 11 ([System.Drawing.FontStyle]::Bold)
$lblDashDesignPreset = New-Label "디자인 컨셉" 0 46 160 20 ([System.Drawing.Color]::FromArgb(180, 190, 200)) 8.5 ([System.Drawing.FontStyle]::Bold)
$cmbDashboardDesignPreset = [System.Windows.Forms.ComboBox]::new()
$cmbDashboardDesignPreset.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbDashboardDesignPreset.DisplayMember = "Name"
$cmbDashboardDesignPreset.ValueMember = "Id"
$cmbDashboardDesignPreset.Font = New-Font 9.5
$cmbDashboardDesignPreset.SetBounds(0, 70, 240, 30)
foreach ($preset in @(Get-RimWorldUiPresetCatalog)) { [void]$cmbDashboardDesignPreset.Items.Add($preset) }
$lblDashDesignDescription = New-Label "" 0 108 320 38 ([System.Drawing.Color]::FromArgb(150, 164, 178)) 8.3

$lblDashTheme = New-Label "밝기" 0 158 120 20 ([System.Drawing.Color]::FromArgb(180, 190, 200)) 8.5 ([System.Drawing.FontStyle]::Bold)
$cmbDashboardTheme = [System.Windows.Forms.ComboBox]::new()
$cmbDashboardTheme.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbDashboardTheme.Font = New-Font 9.5
$cmbDashboardTheme.SetBounds(0, 182, 220, 30)
[void]$cmbDashboardTheme.Items.AddRange(@("시스템 설정 따름", "밝게", "어둡게"))

$lblDashTextSize = New-Label "본문 글자 크기" 0 230 160 20 ([System.Drawing.Color]::FromArgb(180, 190, 200)) 8.5 ([System.Drawing.FontStyle]::Bold)
$cmbDashboardTextSize = [System.Windows.Forms.ComboBox]::new()
$cmbDashboardTextSize.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbDashboardTextSize.Font = New-Font 9.5
$cmbDashboardTextSize.SetBounds(0, 254, 220, 30)
[void]$cmbDashboardTextSize.Items.AddRange(@("9", "10", "11", "12"))

$chkDashboardHighContrast = [System.Windows.Forms.CheckBox]::new()
$chkDashboardHighContrast.Text = "고대비"
$chkDashboardHighContrast.SetBounds(0, 302, 150, 26)
$chkDashboardHighContrast.BackColor = [System.Drawing.Color]::Transparent
$chkDashboardAutoSave = [System.Windows.Forms.CheckBox]::new()
$chkDashboardAutoSave.Text = "편집 내용 자동 저장"
$chkDashboardAutoSave.SetBounds(0, 336, 210, 26)
$chkDashboardAutoSave.BackColor = [System.Drawing.Color]::Transparent
$btnExportDiagnostics = New-Button "진단 번들 저장" ([System.Drawing.Color]::FromArgb(72, 86, 100))
$btnExportDiagnostics.ForeColor = [System.Drawing.Color]::White
$btnExportDiagnostics.SetBounds(0, 374, 170, 32)
$pnlAppearanceSettings.Controls.AddRange(@($lblDashAppearance, $lblDashDesignPreset, $cmbDashboardDesignPreset, $lblDashDesignDescription, $lblDashTheme, $cmbDashboardTheme, $lblDashTextSize, $cmbDashboardTextSize, $chkDashboardHighContrast, $chkDashboardAutoSave, $btnExportDiagnostics))

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

$workspaceLoadCover = [System.Windows.Forms.Panel]::new()
$workspaceLoadCover.Visible = $false
$workspaceLoadCover.TabStop = $false
$workspaceLoadCover.BackColor = [System.Drawing.Color]::FromArgb(244, 245, 242)
$form.Controls.Add($workspaceLoadCover)

$lblWorkspaceLoadTitle = New-Label "프로젝트 구성 중" 0 0 480 28 ([System.Drawing.Color]::FromArgb(38, 44, 40)) 11 ([System.Drawing.FontStyle]::Bold)
$lblWorkspaceLoadTitle.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
$lblWorkspaceLoadDetail = New-Label "문자열과 검수 상태를 한 번에 준비하고 있습니다." 0 32 480 22 ([System.Drawing.Color]::FromArgb(103, 109, 104)) 8.5
$lblWorkspaceLoadDetail.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
$progressWorkspaceLoad = [System.Windows.Forms.ProgressBar]::new()
$progressWorkspaceLoad.Style = [System.Windows.Forms.ProgressBarStyle]::Marquee
$progressWorkspaceLoad.MarqueeAnimationSpeed = 0
$progressWorkspaceLoad.TabStop = $false
$progressWorkspaceLoad.AccessibleName = "프로젝트 로드 진행 상태"
$workspaceLoadCover.Controls.AddRange(@($lblWorkspaceLoadTitle, $lblWorkspaceLoadDetail, $progressWorkspaceLoad))
$workspaceLoadCover.Add_Resize({ Resize-WorkspaceLoadCover })

$operationOverlay = [System.Windows.Forms.Panel]::new()
$operationOverlay.Dock = [System.Windows.Forms.DockStyle]::None
$operationOverlay.Anchor = [System.Windows.Forms.AnchorStyles]::Top -bor [System.Windows.Forms.AnchorStyles]::Left -bor [System.Windows.Forms.AnchorStyles]::Right
$operationOverlay.Visible = $false
$operationOverlay.TabStop = $false
$form.Controls.Add($operationOverlay)

$operationCard = [System.Windows.Forms.Panel]::new()
$operationCard.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
$operationOverlay.Controls.Add($operationCard)

$operationAccent = [System.Windows.Forms.Panel]::new()
$operationAccent.Width = 4
$operationCard.Controls.Add($operationAccent)
$lblOperationEyebrow = New-Label "FRONTIER PROCESS CONTROL" 28 24 320 18 ([System.Drawing.Color]::FromArgb(190, 150, 92)) 7.8 ([System.Drawing.FontStyle]::Bold)
$lblOperationEyebrow.Visible = $false
$lblOperationTitle = New-Label "번역 작업" 72 10 170 24 ([System.Drawing.Color]::White) 10 ([System.Drawing.FontStyle]::Bold)
$lblOperationStage = New-Label "작업공간 확인" 250 8 500 22 ([System.Drawing.Color]::White) 9.5 ([System.Drawing.FontStyle]::Bold)
$lblOperationDetail = New-Label "실제 작업 로그를 기다리는 중입니다." 250 30 500 20 ([System.Drawing.Color]::FromArgb(180, 190, 200)) 8.3
$lblOperationCount = New-Label "작업 상태 확인 중" 72 50 164 18 ([System.Drawing.Color]::FromArgb(150, 164, 178)) 8 ([System.Drawing.FontStyle]::Bold)

$pnlOperationScan = [System.Windows.Forms.Panel]::new()
$pnlOperationScan.SetBounds(18, 18, 42, 42)
$pnlOperationScan.TabStop = $false
$pnlOperationScan.Add_Paint({
    param($sender, $eventArgs)
    $graphics = $eventArgs.Graphics
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $accentColor = if ($script:accentColor) { $script:accentColor } else { [System.Drawing.Color]::FromArgb(183, 131, 66) }
    $lineColor = if ($script:borderColor) { $script:borderColor } else { [System.Drawing.Color]::FromArgb(90, 96, 90) }
    $accentPen = [System.Drawing.Pen]::new($accentColor, 3)
    $linePen = [System.Drawing.Pen]::new($lineColor, 1)
    $dotBrush = [System.Drawing.SolidBrush]::new($accentColor)
    try {
        $diameter = [Math]::Max(12, [Math]::Min($sender.ClientSize.Width, $sender.ClientSize.Height) - 8)
        $offsetX = [int](($sender.ClientSize.Width - $diameter) / 2)
        $offsetY = [int](($sender.ClientSize.Height - $diameter) / 2)
        $graphics.DrawEllipse($linePen, $offsetX, $offsetY, $diameter, $diameter)
        $startAngle = [int](($script:operationPulse * 18) % 360)
        $sweep = if ($progressOperation -and $progressOperation.Style -ne [System.Windows.Forms.ProgressBarStyle]::Marquee -and $progressOperation.Maximum -gt 0) {
            [Math]::Max(8, [int](330 * ($progressOperation.Value / [double]$progressOperation.Maximum)))
        } else { 72 }
        $graphics.DrawArc($accentPen, $offsetX, $offsetY, $diameter, $diameter, $startAngle, $sweep)
        $dotRadians = ($startAngle + $sweep) * [Math]::PI / 180.0
        $radius = $diameter / 2.0
        $dotX = $offsetX + $radius + [Math]::Cos($dotRadians) * $radius
        $dotY = $offsetY + $radius + [Math]::Sin($dotRadians) * $radius
        $graphics.FillEllipse($dotBrush, [float]($dotX - 2.5), [float]($dotY - 2.5), 5, 5)
    } finally {
        $dotBrush.Dispose(); $linePen.Dispose(); $accentPen.Dispose()
    }
})

$progressOperation = [System.Windows.Forms.ProgressBar]::new()
$progressOperation.Style = [System.Windows.Forms.ProgressBarStyle]::Marquee
$progressOperation.MarqueeAnimationSpeed = 28
$progressOperation.TabStop = $false
$progressOperation.AccessibleName = "현재 작업 진행률"

$txtOperationLog = [System.Windows.Forms.RichTextBox]::new()
$txtOperationLog.ReadOnly = $true
$txtOperationLog.DetectUrls = $false
$txtOperationLog.BorderStyle = [System.Windows.Forms.BorderStyle]::None
$txtOperationLog.Font = New-Font 8.3
$txtOperationLog.ScrollBars = [System.Windows.Forms.RichTextBoxScrollBars]::Vertical
$txtOperationLog.Visible = $false
Set-AccessibleControl $txtOperationLog "현재 작업 로그 요약" "최근 작업 단계와 재시도, 오류 메시지를 최대 일곱 줄로 보여줍니다." 0

$operationDivider = [System.Windows.Forms.Panel]::new()
$operationDivider.Visible = $false
$btnOperationCancel = New-Button "중지" ([System.Drawing.Color]::FromArgb(167, 74, 69))
$btnOperationCancel.ForeColor = [System.Drawing.Color]::White
$btnOperationRetry = New-Button "다시 시도" ([System.Drawing.Color]::FromArgb(183, 131, 66))
$btnOperationReview = New-Button "검수 화면" ([System.Drawing.Color]::FromArgb(62, 122, 82))
$btnOperationReview.ForeColor = [System.Drawing.Color]::White
$btnOperationClose = New-Button "닫기" ([System.Drawing.Color]::FromArgb(72, 86, 100))

$btnOperationCancel.Add_Click({ Stop-Translation })
$btnOperationRetry.Add_Click({
    $operationType = [string]$script:lastOperationType
    Hide-OperationOverlay
    if ($operationType -eq "SourceOnly") { Load-SourceOnlyForSelectedMod } else { Start-Translation }
})
$btnOperationReview.Add_Click({
    if ($script:lastReviewOutputPath -and (Test-Path -LiteralPath $script:lastReviewOutputPath -PathType Container)) {
        if (-not $script:reviewRoot -or $script:reviewRoot -ne $script:lastReviewOutputPath) { Load-ReviewRoot $script:lastReviewOutputPath }
    }
    Hide-OperationOverlay
    Show-Workspace
})
$btnOperationClose.Add_Click({ Hide-OperationOverlay })
Set-AccessibleControl $btnOperationCancel "현재 작업 중지" "완료된 번역 배치를 보존하면서 현재 작업의 취소를 요청합니다." 0
Set-AccessibleControl $btnOperationRetry "실패한 작업 다시 시도" "같은 프로젝트에서 번역 준비 단계를 다시 엽니다." 1
Set-AccessibleControl $btnOperationReview "생성된 검수 프로젝트 열기" "현재까지 생성된 번역 결과를 검수 화면에서 엽니다." 2
Set-AccessibleControl $btnOperationClose "작업 상태 닫기" "작업 상태 화면을 닫고 현재 프로젝트로 돌아갑니다." 3

$operationCard.Controls.AddRange(@($lblOperationEyebrow, $lblOperationTitle, $pnlOperationScan, $lblOperationStage, $lblOperationDetail, $lblOperationCount, $progressOperation, $txtOperationLog, $operationDivider, $btnOperationCancel, $btnOperationRetry, $btnOperationReview, $btnOperationClose))

function Resize-OperationOverlay {
    if (-not $operationOverlay -or $operationOverlay.ClientSize.Width -le 0) { return }
    $cardWidth = $operationOverlay.ClientSize.Width
    $cardHeight = $operationOverlay.ClientSize.Height
    $operationCard.SetBounds(0, 0, $cardWidth, $cardHeight)
    $operationAccent.SetBounds(0, 0, 4, [Math]::Max(1, $operationCard.ClientSize.Height))
    $buttonY = 24
    $buttonX = $cardWidth - 16
    foreach ($buttonSpec in @(
        [pscustomobject]@{ Button = $btnOperationClose; Width = 72 },
        [pscustomobject]@{ Button = $btnOperationReview; Width = 96 },
        [pscustomobject]@{ Button = $btnOperationRetry; Width = 96 },
        [pscustomobject]@{ Button = $btnOperationCancel; Width = 72 }
    )) {
        $button = $buttonSpec.Button
        if (-not $button.Visible) { continue }
        $buttonX -= [int]$buttonSpec.Width
        $button.SetBounds($buttonX, $buttonY, [int]$buttonSpec.Width, 34)
        $buttonX -= 8
    }
    $contentRight = [Math]::Max(520, $buttonX - 8)
    $pnlOperationScan.SetBounds(18, 19, 42, 42)
    $lblOperationTitle.SetBounds(72, 10, 168, 24)
    $lblOperationCount.SetBounds(72, 48, 168, 18)
    $lblOperationStage.SetBounds(250, 8, [Math]::Max(220, $contentRight - 250), 22)
    $lblOperationDetail.SetBounds(250, 29, [Math]::Max(220, $contentRight - 250), 20)
    $progressOperation.SetBounds(250, 56, [Math]::Max(220, $contentRight - 250), 7)
}

$operationOverlay.Add_Resize({ Resize-OperationOverlay })

$operationDismissTimer = [System.Windows.Forms.Timer]::new()
$operationDismissTimer.Interval = 900
$operationDismissTimer.Add_Tick({
    $operationDismissTimer.Stop()
    Hide-OperationOverlay
})

function Update-OperationContentLayout {
    if (-not $form -or -not $operationOverlay) { return }
    $formWidth = [Math]::Max(1, $form.ClientSize.Width)
    $formHeight = [Math]::Max(1, $form.ClientSize.Height)
    $contentLayoutChanged = $false
    if ($dashboardPanel -and $dashboardPanel.Visible) {
        $operationOverlay.SetBounds(0, 70, $formWidth, 82)
        $contentHeight = [Math]::Max(1, $formHeight - 70)
        if ($dashContent.Left -ne 0 -or $dashContent.Top -ne 70 -or $dashContent.Width -ne $formWidth -or $dashContent.Height -ne $contentHeight) {
            $dashContent.SetBounds(0, 70, $formWidth, $contentHeight)
            $contentLayoutChanged = $true
        }
    } else {
        $operationOverlay.SetBounds(0, 78, $formWidth, 82)
        $contentHeight = [Math]::Max(1, $formHeight - 78)
        if ($main.Left -ne 0 -or $main.Top -ne 78 -or $main.Width -ne $formWidth -or $main.Height -ne $contentHeight) {
            $main.SetBounds(0, 78, $formWidth, $contentHeight)
            $contentLayoutChanged = $true
        }
    }
    if ($operationOverlay.Visible) {
        Resize-OperationOverlay
        $operationOverlay.BringToFront()
    }
    if ($contentLayoutChanged -and (Get-Command Update-SplitMinimumSizes -ErrorAction SilentlyContinue)) {
        Update-SplitMinimumSizes
    }
    if ($workspaceLoadCover -and $workspaceLoadCover.Visible) {
        Resize-WorkspaceLoadCover
        $workspaceLoadCover.BringToFront()
        if ($operationOverlay.Visible) { $operationOverlay.BringToFront() }
    }
}

$dashboardPanel.Add_Resize({
    $dashHeader.SetBounds(0, 0, $dashboardPanel.ClientSize.Width, 70)
    $dashAccent.SetBounds(0, 67, $dashboardPanel.ClientSize.Width, 3)
    Update-OperationContentLayout
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
            if ($rightHeight -gt ($rightSplit.SplitterWidth + 2)) {
                $sideHeight = if ($rightHeight -ge 470) {
                    [Math]::Min(250, [Math]::Max(170, [int]($rightHeight * 0.28)))
                } else {
                    [Math]::Min(170, [Math]::Max(100, [int]($rightHeight * 0.28)))
                }
                $maxDistance = [Math]::Max(1, $rightHeight - $rightSplit.SplitterWidth - 1)
                $desiredDistance = [Math]::Max(1, $rightHeight - $sideHeight - $rightSplit.SplitterWidth)
                $rightSplit.SplitterDistance = [Math]::Min($desiredDistance, $maxDistance)
            }
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

function Resize-DashboardLayout {
    if (-not $dashboardPanel -or $dashboardPanel.ClientSize.Width -le 0) { return }

    $dashWidth = [Math]::Max(860, $dashboardPanel.ClientSize.Width)
    $dashHeight = [Math]::Max(560, $dashboardPanel.ClientSize.Height)
    $lblDashTitle.SetBounds(28, 18, 300, 26)
    $lblDashSub.Visible = $false
    $btnDashProjects.SetBounds(350, 16, 92, 36)
    $btnDashActivity.SetBounds(450, 16, 82, 36)
    $btnDashSettings.SetBounds(540, 16, 82, 36)
    $btnCommandPalette.SetBounds(($dashWidth - 184), 16, 156, 36)
    $dashHeader.SetBounds(0, 0, $dashWidth, 70)
    $dashContent.SetBounds(0, 70, $dashWidth, [Math]::Max(1, $dashHeight - 70))
    $lblDashEyebrow.SetBounds(32, 22, 360, 18)
    $lblDashProjects.SetBounds(32, 42, 360, 34)
    $lblDashIntro.SetBounds(32, 78, [Math]::Max(360, [int]($dashWidth * 0.54)), 24)
    $providerWidth = [Math]::Max(250, [Math]::Min(420, [int]($dashWidth * 0.30)))
    $providerX = $dashWidth - 32 - $providerWidth
    $lblDashProviderStatus.SetBounds($providerX, 26, $providerWidth, 26)
    $lblDashProviderStatus.TextAlign = [System.Drawing.ContentAlignment]::MiddleRight
    $lblDashProviderHint.SetBounds($providerX, 54, $providerWidth, 22)
    $lblDashProviderHint.TextAlign = [System.Drawing.ContentAlignment]::MiddleRight
    $dashWorkflow.SetBounds(32, 110, [Math]::Max(420, $dashWidth - 64), 30)
    $dashboardDivider.SetBounds(32, 148, [Math]::Max(320, $dashWidth - 64), 1)
    $searchWidth = [Math]::Min(360, [Math]::Max(250, [int]($dashWidth * 0.25)))
    $buttonTotal = 126 + 90 + 90 + 16
    $modX = 32 + $searchWidth + 28
    $buttonX = $dashWidth - 32 - $buttonTotal
    $comboWidth = [Math]::Max(180, $buttonX - $modX - 14)
    $lblDashboardSearch.SetBounds(32, 166, 170, 20)
    $txtDashboardSearch.SetBounds(32, 190, $searchWidth, 34)
    $lblDashboardMod.SetBounds($modX, 166, 170, 20)
    $cmbDashboardMods.SetBounds($modX, 190, $comboWidth, 34)
    $btnDashboardAddMod.SetBounds($buttonX, 190, 126, 34)
    $btnDashboardChooseMod.SetBounds(($buttonX + 134), 190, 90, 34)
    $btnDashboardRefreshMods.SetBounds(($buttonX + 232), 190, 90, 34)
    $dashboardPageHeight = [Math]::Max(1, $dashHeight - 70)
    $flowDashboardProjects.SetBounds(22, 244, [Math]::Max(320, $dashWidth - 44), [Math]::Max(180, $dashboardPageHeight - 268))
    $lvDashboardActivity.SetBounds(24, 66, [Math]::Max(320, $dashWidth - 48), [Math]::Max(220, $dashboardPageHeight - 90))
    Resize-DashboardSettingsLayout
}

function Apply-AppTheme {
    param([switch]$Force)

    $isDark = Get-IsWindowsDarkMode
    $themeSignature = "$isDark|$($script:designPreset)|$($script:highContrast)|$($script:textSize)|$($form.ClientSize.Width)x$($form.ClientSize.Height)"
    if (-not $Force -and $script:appliedThemeSignature -eq $themeSignature) { return }
    $script:uiTokens = Get-RimWorldUiTokens -Mode $(if ($isDark) { "Dark" } else { "Light" }) -Preset $script:designPreset -HighContrast:$script:highContrast
    $colors = $script:uiTokens.Colors
    $bg = ConvertTo-RimWorldUiColor $colors.Canvas
    $surface = ConvertTo-RimWorldUiColor $colors.Surface
    $subtle = ConvertTo-RimWorldUiColor $colors.SurfaceMuted
    $line = ConvertTo-RimWorldUiColor $colors.Border
    $text = ConvertTo-RimWorldUiColor $colors.Text
    $muted = ConvertTo-RimWorldUiColor $colors.TextMuted
    $faint = ConvertTo-RimWorldUiColor $colors.TextFaint
    $header = ConvertTo-RimWorldUiColor $colors.Header
    $headerButton = ConvertTo-RimWorldUiColor $colors.HeaderSecondary
    $headerLine = ConvertTo-RimWorldUiColor $colors.Border
    $searchCard = ConvertTo-RimWorldUiColor $colors.Selection
    $headerText = if ($script:highContrast) { $text } else { ConvertTo-RimWorldUiColor $(if ($isDark) { $colors.Text } else { "#F5F2E9" }) }
    $headerMuted = ConvertTo-RimWorldUiColor $(if ($script:highContrast) { $colors.TextMuted } else { $(if ($isDark) { "#B8B9AF" } else { "#C4C9C2" }) })
    $primary = ConvertTo-RimWorldUiColor $colors.Accent
    $primarySoft = ConvertTo-RimWorldUiColor $colors.Selection
    $primaryText = ConvertTo-RimWorldUiColor $colors.AccentText
    $steel = ConvertTo-RimWorldUiColor $colors.Info
    $green = ConvertTo-RimWorldUiColor $colors.Success

    $script:itemCardBack = $surface
    $script:itemCardSelected = $primarySoft
    $script:itemText = $text
    $script:itemMuted = $muted
    $script:itemSubtle = $faint
    $script:tabBack = $surface
    $script:tabActive = $primary
    $script:tabText = $muted
    $script:tabActiveText = [System.Drawing.Color]::White
    $script:accentColor = $primary
    $script:accentTextColor = $primaryText
    $script:surfaceColor = $surface
    $script:textColor = $text
    $script:mutedColor = $muted
    $script:borderColor = $line
    $script:headerButtonColor = $headerButton
    $script:headerTextColor = $headerText

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
    foreach ($control in @($main, $main.Panel1, $main.Panel2, $left, $center, $side, $rightSplit, $rightSplit.Panel1, $rightSplit.Panel2, $dashboardPanel, $dashContent, $dashProjectsPage, $dashActivityPage, $dashSettingsPage, $flowDashboardProjects, $flowItems, $statusFilterBar, $pnlApiSettings, $pnlAppearanceSettings, $pnlRmkSettings, $operationOverlay, $workspaceLoadCover)) {
        if ($control) { $control.BackColor = $bg }
    }
    foreach ($panel in @($center, $side, $pnlApiDetail, $flowApiProviders, $operationCard)) {
        if ($panel) { $panel.BackColor = $surface }
    }
    $operationAccent.BackColor = $primary
    $operationDivider.BackColor = $line
    $lblOperationEyebrow.ForeColor = $primary
    $lblOperationTitle.ForeColor = $text
    $lblOperationStage.ForeColor = $text
    $lblOperationDetail.ForeColor = $muted
    $lblOperationCount.ForeColor = $faint
    $lblWorkspaceLoadTitle.ForeColor = $text
    $lblWorkspaceLoadDetail.ForeColor = $muted
    $txtOperationLog.BackColor = $subtle
    $txtOperationLog.ForeColor = $text
    $btnOperationRetry.BackColor = $primary
    $btnOperationRetry.ForeColor = $primaryText
    $btnOperationReview.BackColor = $green
    $btnOperationReview.ForeColor = [System.Drawing.Color]::White
    $btnOperationClose.BackColor = $surface
    $btnOperationClose.ForeColor = $text
    $btnOperationClose.FlatAppearance.BorderColor = $line

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

    foreach ($button in @($btnHome, $btnSave, $btnOpenFolder, $btnLoad, $btnDashboardChooseMod, $btnDashboardRefreshMods, $btnDashboardRmkAuto, $btnDashboardRmkChoose, $btnDashboardRmkOpen, $btnRmkRefresh, $btnRmkOpen, $btnDashActivity, $btnDashSettings, $btnCommandPalette, $btnExportDiagnostics)) {
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
            $button.ForeColor = $primaryText
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
    $compactWorkspace = $rightWidth -lt 1040
    try {
        $rightSplit.Panel1MinSize = 0
        $rightSplit.Panel2MinSize = 0
        if ($compactWorkspace) {
            $rightSplit.Orientation = [System.Windows.Forms.Orientation]::Horizontal
            $rightHeight = [Math]::Max(1, $rightSplit.ClientSize.Height)
            $sideHeight = [Math]::Min(250, [Math]::Max(170, [int]($rightHeight * 0.28)))
            $centerHeight = [Math]::Max(320, $rightHeight - $sideHeight - $rightSplit.SplitterWidth)
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

    foreach ($box in @($txtSearch, $txtSource, $txtTranslation, $txtMeta, $txtExisting, $txtCandidate, $txtHistory, $txtDiffSource, $txtDiffBefore, $txtDiffAfter, $txtTerms, $txtMemo, $txtRmkDetails, $txtWarnings, $txtDashboardSearch, $txtDashboardApiKeys, $txtDashboardRmkWorkspace, $txtApiKeys, $txtApiProviderUrl, $txtApiProviderCustomName)) {
        if ($box) {
            $box.BackColor = $surface
            $box.ForeColor = $text
        }
    }
    $txtSource.BackColor = $subtle
    $txtDiffSource.BackColor = $subtle
    $txtDiffBefore.BackColor = $surface
    $txtDiffAfter.BackColor = $surface
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

    foreach ($combo in @($cmbSearchField, $cmbStatus, $cmbSort, $cmbQualityCategory, $cmbModCatalog, $cmbProject, $cmbDashboardMods, $cmbDashboardDesignPreset, $cmbDashboardTheme, $cmbDashboardTextSize, $cmbApiProviderModel, $cmbApiProviderTemperature)) {
        if ($combo) {
            $combo.BackColor = $surface
            $combo.ForeColor = $text
            $combo.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
        }
    }
    foreach ($list in @($lvFiles, $lvDashboardActivity, $lvQualityIssues)) {
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
    foreach ($label in @($lblSearchCrumb, $lblProjectStats, $lblProgress, $lblExisting, $lblCandidate, $lblReferenceTitle, $lblRmkStatus, $lblDiffSummary, $lblDiffSource, $lblDiffBefore, $lblDiffAfter, $lblHistoryDetail, $lblQualitySummary, $lblSelectedQuality, $lblDashProjects, $lblDashIntro, $lblDashProviderStatus, $lblDashProviderHint, $lblDashActivity, $lblDashSettings, $lblDashApi, $lblDashApiHint, $lblApiProviderTitle, $lblApiProviderDescription, $lblApiProviderCustomName, $lblApiProviderKeys, $lblApiProviderUrl, $lblApiProviderModel, $lblApiProviderTemperature, $lblApiProviderNotice, $lblDashboardSearch, $lblDashboardMod, $lblDashSettingsNote, $lblDashAppearance, $lblDashDesignPreset, $lblDashDesignDescription, $lblDashTheme, $lblDashTextSize, $lblDashRmk, $lblDashboardRmkWorkspace, $lblDashboardRmkReference, $lblDashboardRmkNote)) {
        if ($label -and $label -ne $lblSearchCrumb) { $label.ForeColor = $text }
    }
    $lblDashEyebrow.ForeColor = $primary
    $lblDashIntro.ForeColor = $muted
    $lblDashProviderHint.ForeColor = $muted
    $lblDashDesignDescription.ForeColor = $muted
    foreach ($step in $dashWorkflowSteps) { $step.ForeColor = $muted }
    $dashboardDivider.BackColor = $line
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
    $btnQualityRefresh.BackColor = $surface
    $btnQualityRefresh.ForeColor = $text
    $btnQualityRefresh.FlatAppearance.BorderColor = $line
    $btnQualityReport.BackColor = $primary
    $btnQualityReport.ForeColor = $primaryText
    $btnQualityReport.FlatAppearance.BorderColor = $primary
    $btnQualityJump.BackColor = $green
    $btnQualityJump.ForeColor = [System.Drawing.Color]::White
    $btnQualityJump.FlatAppearance.BorderColor = $green
    Refresh-StatusFilterButtons
    Resize-ReviewEditorLayout
    Resize-DiffTab
    Resize-QualityTab

    $tabs.Appearance = [System.Windows.Forms.TabAppearance]::Normal
    $tabs.SizeMode = [System.Windows.Forms.TabSizeMode]::Fixed
    $tabWidth = [Math]::Max(48, [Math]::Min(68, [int](($side.ClientSize.Width - 8) / [Math]::Max(1, $tabs.TabPages.Count))))
    $tabs.ItemSize = [System.Drawing.Size]::new($tabWidth, 38)
    $tabs.BackColor = $surface
    $tabs.Invalidate()

    $lblDashTitle.ForeColor = $headerText
    Resize-DashboardLayout
    foreach ($button in @($btnDashboardChooseMod, $btnDashboardRefreshMods)) {
        $button.BackColor = $surface
        $button.ForeColor = $text
        $button.FlatAppearance.BorderColor = $line
    }
    Refresh-ApiProviderButtons
    if ($dashboardPanel.Visible) { Refresh-DashboardTabButtons }
    Update-SplitMinimumSizes
    Update-OperationContentLayout
    $script:appliedThemeSignature = $themeSignature
    if ($dashboardPanel.Visible -and $dashProjectsPage.Visible) { Refresh-DashboardProjects }
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
Set-AccessibleControl $tabs "검수 도구 탭" "번역 비교, 용어, 메모, RMK, 품질 검사와 로그를 전환합니다." 0
Set-AccessibleControl $txtDiffSource "비교 원문" "현재 문자열의 원문입니다." 0
Set-AccessibleControl $txtDiffBefore "비교 기준 번역" "기존 번역 또는 AI 후보이며 현재 번역과 달라진 부분이 강조됩니다." 1
Set-AccessibleControl $txtDiffAfter "현재 번역 비교" "현재 검수 번역이며 비교 기준과 달라진 부분이 강조됩니다." 2
Set-AccessibleControl $txtHistory "번역 상세 기록" "원문, 기존 번역, AI 후보와 현재 검수 번역의 전체 기록입니다." 3
Set-AccessibleControl $txtTerms "관련 용어와 로컬 번역 메모리" "현재 문자열과 관련된 RimWorld 용어와 동일 원문의 안전한 기존 번역을 출처와 함께 보여줍니다. 자동 적용하지 않습니다." 0
Set-AccessibleControl $txtMemo "검수 메모" "현재 문자열에 대한 로컬 메모를 편집합니다." 0
Set-AccessibleControl $txtRmkDetails "RMK 연결 정보" "현재 프로젝트의 RMK 번역 경로, 버전과 Git 작업 상태를 보여줍니다." 0
Set-AccessibleControl $btnRmkRefresh "RMK 상태 갱신" "RMK 구독본과 작업 클론에서 현재 프로젝트를 다시 찾습니다." 1
Set-AccessibleControl $btnRmkOpen "RMK 폴더 열기" "현재 RMK 번역 항목 또는 작업 클론 폴더를 엽니다." 2
Set-AccessibleControl $btnRmkBuild "RMK LoadFolders 빌드" "RMK 작업 클론의 LoadFoldersBuilder를 실행합니다." 3
Set-AccessibleControl $cmbQualityCategory "품질 문제 필터" "오류, 경고, 미번역, 원문 변경, 토큰과 길이 이상 등 프로젝트 품질 문제를 필터링합니다." 0
Set-AccessibleControl $btnQualityRefresh "프로젝트 품질 다시 검사" "현재 검수 프로젝트 전체 문자열의 품질 문제를 다시 계산합니다." 1
Set-AccessibleControl $btnQualityReport "개인정보 보호 품질 보고서" "원문과 번역문, API 키, 절대 경로를 제외한 집계 HTML 보고서를 저장합니다." 2
Set-AccessibleControl $lvQualityIssues "프로젝트 품질 문제 목록" "현재 프로젝트에서 찾은 미번역, 원문 변경, 토큰, 길이와 중복 문제입니다." 3
Set-AccessibleControl $txtWarnings "선택 품질 문제 설명" "선택한 문제의 원인과 현재 문자열의 안전 검사 결과를 보여줍니다." 4
Set-AccessibleControl $btnQualityJump "품질 문제 문자열로 이동" "선택한 품질 문제가 있는 번역 문자열로 이동합니다." 5
Set-AccessibleControl $txtLog "작업 로그" "원문 로드, 번역과 적용 과정의 로그입니다." 0

Set-AccessibleControl $btnDashProjects "프로젝트 탭" "로컬 번역 프로젝트를 표시합니다." 0
Set-AccessibleControl $btnDashActivity "활동 탭" "최근 번역과 검토 활동을 표시합니다." 1
Set-AccessibleControl $btnDashSettings "설정 탭" "API와 화면 설정을 표시합니다." 2
Set-AccessibleControl $btnCommandPalette "명령 찾기" "검색 가능한 작업 명령을 엽니다. 단축키 Ctrl+Shift+P." 3
Set-AccessibleControl $btnExportDiagnostics "진단 번들 저장" "원문, 번역문, 키, API 키, 전체 경로와 원시 로그를 제외한 로컬 진단 ZIP을 저장합니다." 6
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
Set-AccessibleControl $chkDashboardDryRun "시험 실행" "파일을 쓰지 않고 번역 대상을 점검합니다." 2
Set-AccessibleControl $cmbDashboardDesignPreset "디자인 컨셉" "프로페셔널, 사이파이, 비비드, 스튜디오 또는 프런티어 화면을 선택합니다." 3
Set-AccessibleControl $cmbDashboardTheme "밝기" "시스템 설정, 밝은 화면 또는 어두운 화면을 선택합니다." 4
Set-AccessibleControl $cmbDashboardTextSize "본문 글자 크기" "번역문과 참고 정보의 글자 크기를 9에서 12 사이로 선택합니다." 5
Set-AccessibleControl $chkDashboardHighContrast "고대비" "텍스트와 경계선 대비를 높입니다." 6
Set-AccessibleControl $chkDashboardAutoSave "자동 저장" "입력을 멈춘 뒤 편집 내용을 자동으로 저장합니다." 7
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
$toolTip.SetToolTip($btnCommandPalette, "명령 찾기 (Ctrl+Shift+P)")
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
$toolTip.SetToolTip($cmbDashboardDesignPreset, "화면 구조는 유지하면서 색상과 분위기를 바꿉니다.")
$toolTip.SetToolTip($cmbDashboardTheme, "기본값은 Windows 앱 테마를 따릅니다.")
$toolTip.SetToolTip($cmbDashboardTextSize, "번역문, 기존 번역, AI 후보와 참고 탭의 글자 크기")

Sync-DashboardSettingsFromMain
Apply-TextSize
Resize-DashboardSettingsLayout

$resizeLayoutTimer = [System.Windows.Forms.Timer]::new()
$resizeLayoutTimer.Interval = 90
$resizeLayoutTimer.Add_Tick({
    $resizeLayoutTimer.Stop()
    if ($script:layouting -or $form.WindowState -eq [System.Windows.Forms.FormWindowState]::Minimized) { return }
    $script:layouting = $true
    try {
        Apply-AppTheme
    } finally {
        $script:layouting = $false
    }
})

$form.Add_Resize({
    if ($form.WindowState -eq [System.Windows.Forms.FormWindowState]::Minimized) { return }
    $resizeLayoutTimer.Stop()
    $resizeLayoutTimer.Start()
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
$btnCommandPalette.Add_Click({ Show-CommandPalette })
$btnExportDiagnostics.Add_Click({ Export-DiagnosticBundle })
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
$cmbDashboardDesignPreset.Add_SelectedIndexChanged({ Apply-DashboardPreferences })
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
$cmbQualityCategory.Add_SelectedIndexChanged({ Refresh-QualityCenter })
$btnQualityRefresh.Add_Click({ Invoke-QualityCenterRefresh -Force })
$btnQualityReport.Add_Click({ Export-QualityReport })
$lvQualityIssues.Add_RetrieveVirtualItem({
    $index = [int]$_.ItemIndex
    if ($index -ge 0 -and $index -lt $script:visibleQualityIssues.Count) {
        $_.Item = New-QualityListViewItem $script:visibleQualityIssues[$index]
    } else {
        $_.Item = [System.Windows.Forms.ListViewItem]::new("")
    }
})
$lvQualityIssues.Add_SelectedIndexChanged({ Show-SelectedQualityIssue })
$lvQualityIssues.Add_DoubleClick({ Jump-ToSelectedQualityIssue })
$btnQualityJump.Add_Click({ Jump-ToSelectedQualityIssue })
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
    if ($tabs.SelectedTab -eq $tabIssues) {
        Save-CurrentEdit
        Invoke-QualityCenterRefresh
        return
    }
    if ($tabs.SelectedTab -ne $tabTerms) { return }
    $form.UseWaitCursor = $true
    try {
        Save-CurrentEdit
        if (-not $script:glossaryLoaded) { Load-Glossary }
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
        $script:qualityDirty = $true
        if ($script:currentRowIndex -ge 0 -and $script:currentRowIndex -lt $script:rows.Count) {
            $diffRow = $script:rows[$script:currentRowIndex]
            $diffDecision = Get-Decision $diffRow
            Update-TranslationDiffView -Source (ConvertTo-FlatString $diffRow.source) -Existing (ConvertTo-FlatString $diffRow.existing) -Candidate (ConvertTo-FlatString $diffRow.candidate) -Translation (ConvertTo-FlatString $txtTranslation.Text) -SourceChanged (ConvertTo-BoolValue $diffDecision.sourceChanged)
        }
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
    if ($_.Control -and $_.Shift -and $_.KeyCode -eq [System.Windows.Forms.Keys]::P) {
        Show-CommandPalette
        $_.SuppressKeyPress = $true
    } elseif ($_.Control -and $_.KeyCode -eq [System.Windows.Forms.Keys]::Home) {
        Show-Dashboard "projects"
        $_.SuppressKeyPress = $true
    } elseif ($_.Alt -and $_.KeyCode -eq [System.Windows.Forms.Keys]::Q -and -not $dashboardPanel.Visible) {
        $tabs.SelectedTab = $tabIssues
        $_.SuppressKeyPress = $true
    } elseif ($_.Alt -and $_.KeyCode -eq [System.Windows.Forms.Keys]::C -and -not $dashboardPanel.Visible) {
        $tabs.SelectedTab = $tabHistory
        $_.SuppressKeyPress = $true
    } elseif ($_.Control -and $_.KeyCode -eq [System.Windows.Forms.Keys]::F) {
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
    if ($operationOverlay -and $operationOverlay.Visible) {
        $script:operationPulse = ([int]$script:operationPulse + 1) % 1000
        if (($script:operationPulse % 2) -eq 0) { $pnlOperationScan.Invalidate() }
    }
    foreach ($line in (Read-NewProcessLogLines)) {
        Add-Log $line
        Update-ProgressFromLine $line
    }

    if ($script:stopRequested -and $script:process -and -not $script:process.HasExited -and $script:stopRequestedAt -and
        ((Get-Date) - $script:stopRequestedAt).TotalSeconds -ge 5) {
        Add-Log "취소 응답 제한 시간을 넘어 실행 프로세스를 종료합니다. 마지막 완료 배치까지 복구합니다."
        try { Stop-ProcessTree $script:process.Id } catch { Add-Log "프로세스 종료 실패: $($_.Exception.Message)" }
        $script:stopRequestedAt = $null
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
            $partialLoaded = $false
            if ($script:lastReviewOutputPath -and (Test-Path -LiteralPath $script:lastReviewOutputPath -PathType Container)) {
                try {
                    Load-ReviewRoot $script:lastReviewOutputPath
                    Register-ProjectRun -ReviewRoot $script:lastReviewOutputPath -Provider $script:lastProvider
                    $partialLoaded = $true
                } catch {
                    Add-Log "완료된 배치 복구 실패: $($_.Exception.Message)"
                }
            }
            if ($partialLoaded) {
                $lblRunStatus.Text = "중지됨 · 완료분 복구"
                Add-Log "중지 완료. 완료된 배치를 검수 화면에 복구했습니다."
                Complete-OperationOverlay -Kind "cancelled" -Title "작업을 중지하고 완료분을 복구했습니다" -Detail "완료된 배치는 검수 프로젝트에 남아 있습니다."
            } else {
                $lblRunStatus.Text = "중지됨"
                Add-Log "사용자 요청으로 중지 완료."
                Complete-OperationOverlay -Kind "cancelled" -Title "작업을 중지했습니다" -Detail "완료된 결과가 없거나 검수 프로젝트를 만들기 전 단계에서 중지되었습니다."
            }
        } elseif ($exitCode -eq 0) {
            if ($progressRun.Maximum -gt 0) { $progressRun.Value = $progressRun.Maximum }
            $overlayFailed = $false
            if ($script:lastReviewOutputPath -and (Test-Path -LiteralPath $script:lastReviewOutputPath -PathType Container)) {
                try {
                    if ($isSourceRefresh) {
                        $lblRunStatus.Text = "원문 목록 구성 중"
                        [System.Windows.Forms.Application]::DoEvents()
                    }
                    $replaceExisting = $script:activeAiTranslationMode -eq "Overwrite"
                    Load-ReviewRoot $script:lastReviewOutputPath -SkipPreviousDecisions:$replaceExisting
                    Register-ProjectRun -ReviewRoot $script:lastReviewOutputPath -Provider $script:lastProvider
                    if ($isSourceRefresh -and $script:operationReturnToDashboard) {
                        Show-Workspace
                    }
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
                    $overlayFailed = $true
                    Complete-OperationOverlay -Kind "error" -Title "결과는 생성됐지만 열지 못했습니다" -Detail $_.Exception.Message
                }
            } else {
                $lblRunStatus.Text = if ($isSourceRefresh) { "원문 로드 완료" } else { "완료" }
            }
            if (-not $overlayFailed) {
                if ($isSourceRefresh) {
                    Complete-OperationOverlay -Kind "completed" -Title "원문 분석이 완료되었습니다" -Detail "번역 가능한 문자열과 기존 번역을 검수 프로젝트에 불러왔습니다."
                } else {
                    Complete-OperationOverlay -Kind "completed" -Title "초벌 번역이 완료되었습니다" -Detail "새 번역 후보를 불러왔습니다. 이제 문제 항목을 확인하고 검토할 수 있습니다."
                }
            }
        } else {
            $lblRunStatus.Text = if ($isSourceRefresh) { "원문 로드 실패" } else { "종료 코드 $exitCode" }
            Complete-OperationOverlay -Kind "error" -Title $(if ($isSourceRefresh) { "원문 분석에 실패했습니다" } else { "번역 작업에 실패했습니다" }) -Detail "프로세스 종료 코드 $exitCode · 아래 작업 로그에서 원인을 확인하세요."
        }

        try { $script:process.Dispose() } catch {}
        $script:process = $null
        $script:stopRequested = $false
        $script:stopRequestedAt = $null
        $script:activeAiTranslationMode = ""
        Set-TranslationRunning $false
        Remove-TempFiles
        $script:cancellationFile = ""
    }
})
$timer.Start()

$form.Add_FormClosing({
    [void](Confirm-DuplicateSourceTranslation)
    if ($script:process -and -not $script:process.HasExited) {
        $runningResult = [System.Windows.Forms.MessageBox]::Show(
            "작업이 실행 중입니다. 완료된 배치를 보존하고 작업을 중지한 뒤 종료할까요?",
            "RimWorld AI Translator",
            [System.Windows.Forms.MessageBoxButtons]::OKCancel,
            [System.Windows.Forms.MessageBoxIcon]::Warning
        )
        if ($runningResult -ne [System.Windows.Forms.DialogResult]::OK) {
            $_.Cancel = $true
            return
        }
    }
    if ($script:dirty) {
        $result = [System.Windows.Forms.MessageBox]::Show("저장하지 않은 검수 내용이 있습니다. 저장할까요?", "RimWorld AI Translator", [System.Windows.Forms.MessageBoxButtons]::YesNoCancel, [System.Windows.Forms.MessageBoxIcon]::Question)
        if ($result -eq [System.Windows.Forms.DialogResult]::Cancel) {
            $_.Cancel = $true
            return
        }
        if ($result -eq [System.Windows.Forms.DialogResult]::Yes) { Save-Decisions }
    }

    if ($autoSaveTimer) { $autoSaveTimer.Stop() }
    if ($dashboardSearchTimer) { $dashboardSearchTimer.Stop() }
    if ($searchTimer) { $searchTimer.Stop() }
    if ($resizeLayoutTimer) { $resizeLayoutTimer.Stop() }
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
    if ($script:textFingerprintSha256) {
        try { $script:textFingerprintSha256.Dispose() } catch {}
        $script:textFingerprintSha256 = $null
    }
})

$form.Add_FormClosed({
    if ($resizeLayoutTimer) { $resizeLayoutTimer.Dispose() }
    if ($operationDismissTimer) { $operationDismissTimer.Dispose() }
    if ($script:previewPreflightDialog) { try { $script:previewPreflightDialog.Dispose() } catch {}; $script:previewPreflightDialog = $null }
    if ($script:previewCommandPaletteDialog) { try { $script:previewCommandPaletteDialog.Dispose() } catch {}; $script:previewCommandPaletteDialog = $null }
    foreach ($font in @($script:fontCache.Values)) {
        try { $font.Dispose() } catch {}
    }
    $script:fontCache.Clear()
})

Ensure-AppDataStore
Remove-StaleTempFiles
if ($script:startupSettingsError) {
    $message = "설정 파일을 읽을 수 없어 안전하게 시작을 중단했습니다.`r`n`r`n주 파일: $script:settingsPath`r`n백업: $script:settingsPath.bak`r`n`r`n두 파일은 보존했으며 기본 설정으로 덮어쓰지 않았습니다.`r`n`r`n오류: $script:startupSettingsError"
    [System.Windows.Forms.MessageBox]::Show($message, "설정 상태 복구 필요", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    $form.Dispose()
    return
}
try {
    Load-ProjectStore
} catch {
    $message = "프로젝트 목록을 읽을 수 없어 안전하게 시작을 중단했습니다.`r`n`r`n주 파일: $script:projectStorePath`r`n백업: $script:projectStorePath.bak`r`n`r`n두 파일은 그대로 보존했으며 새 빈 목록으로 덮어쓰지 않았습니다. 파일을 별도 위치에 복사한 뒤 정상 백업을 복원하거나 지원에 오류 내용을 전달하세요.`r`n`r`n오류: $($_.Exception.Message)"
    [System.Windows.Forms.MessageBox]::Show($message, "프로젝트 상태 복구 필요", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    $form.Dispose()
    return
}
Refresh-ProjectList

if ($script:jsonRecoveryNotices) {
    foreach ($notice in @($script:jsonRecoveryNotices)) { Add-Log $notice }
    $script:jsonRecoveryNotices.Clear()
}

Add-Log "프로그램 시작 안내"
Add-Log "1. 프로젝트 화면에서 모드를 골라 프로젝트를 만드세요. 프로젝트 하나가 모드 하나입니다."
Add-Log "2. 프로젝트가 열리면 그 프로젝트의 모드만 원문 로드, AI 번역, 적용 대상으로 사용됩니다."
Add-Log "3. API 키를 비우면 Google 번역 후보를 생성합니다."
Add-Log "4. AI 후보는 번역됨 상태이며, 직접 확인하면 검토됨으로 바꿀 수 있습니다."
Add-Log "5. 원문이 바뀐 키는 업데이트 변경으로 표시되고 미번역으로 내려가며 적용 대상에서 제외됩니다."

$script:initialViewError = ""

function Show-ReadyMainWindow {
    if ($script:startupRevealComplete) { return }
    $script:startupRevealComplete = $true
    try {
        # Keep the layered window invisible until every child control has its final layout.
        $form.ShowInTaskbar = $true
        $form.PerformLayout()
        [System.Windows.Forms.Application]::DoEvents()
        if ($resizeLayoutTimer) { $resizeLayoutTimer.Stop() }
        # Maximized ClientSize is finalized only after the native window is shown.
        if ($dashboardPanel.Visible) {
            Resize-DashboardLayout
            if ($dashProjectsPage.Visible) { Refresh-DashboardProjects }
            Update-OperationContentLayout
        } else {
            Apply-AppTheme -Force
        }
        $form.PerformLayout()
        $form.Invalidate($true)
        $form.Update()
        [System.Windows.Forms.Application]::DoEvents()
    } finally {
        $form.Opacity = 1.0
        $form.Activate()
    }
}

$form.Add_Load({
    try {
        $form.SuspendLayout()
        if ($LayoutSnapshotWidth -gt 0 -and $LayoutSnapshotHeight -gt 0) {
            $form.WindowState = [System.Windows.Forms.FormWindowState]::Normal
            $form.ClientSize = [System.Drawing.Size]::new($LayoutSnapshotWidth, $LayoutSnapshotHeight)
        }
        Apply-AppTheme
        if ($script:initialReviewRoot) {
            Load-ReviewRoot $script:initialReviewRoot
            Show-Workspace
            switch ($InitialWorkspaceSideTab.ToLowerInvariant()) {
                "terms" { $tabs.SelectedTab = $tabTerms }
                "memo" { $tabs.SelectedTab = $tabMemo }
                "issues" { $tabs.SelectedTab = $tabIssues }
                "log" { $tabs.SelectedTab = $tabLog }
                default { }
            }
        } else {
            $initialTab = if ($InitialDashboardTab -in @("projects", "activity", "settings")) { $InitialDashboardTab } else { "projects" }
            Show-Dashboard $initialTab
        }
        Update-RmkControls
        if (-not $DisableBackgroundDiscovery -and (Try-LoadModCatalogCache -FastValidation)) {
            $script:startupCatalogCacheLoaded = $true
            Update-ModCatalogControls
            $lblRunStatus.Text = "모드 $($script:modCatalog.Count)개 준비됨"
        }
    } catch {
        $script:initialViewError = $_.Exception.Message
        Add-Log "초기 화면 구성 실패: $($script:initialViewError)"
    } finally {
        $form.ResumeLayout($true)
    }
})

$form.Add_Shown({
    Show-ReadyMainWindow
    if ($script:startupWatch -and $script:startupWatch.IsRunning) {
        $script:startupWatch.Stop()
        Add-Log ("첫 화면 준비: {0:N3}초" -f $script:startupWatch.Elapsed.TotalSeconds)
    }
    if ($script:initialViewError -and [string]::IsNullOrWhiteSpace($script:layoutSnapshotPath)) {
        [System.Windows.Forms.MessageBox]::Show("검토 결과를 열지 못했습니다.`r`n$($script:initialViewError)", "RimWorld AI Translator") | Out-Null
    }
    try {
        switch ($PreviewOperationState) {
            "loading" {
                Show-OperationOverlay -Title "초벌 번역 실행" -Detail "Cerebras · English 원문 · 검수 프로젝트에만 저장" -OperationType "Translation"
                Update-OperationOverlay -Operation (Get-RimWorldOperationStateFromLine "Translating batch 3/12 (40 entries)...") -Line "Translating batch 3/12 (40 entries)..."
            }
            "error" {
                Show-OperationOverlay -Title "초벌 번역 실행" -Detail "검수 프로젝트를 준비했습니다." -OperationType "Translation"
                Add-OperationOverlayLog "Batch 3/12 failed after retries: HTTP 429"
                Complete-OperationOverlay -Kind "error" -Title "번역 작업에 실패했습니다" -Detail "요청 한도를 확인한 뒤 실패한 작업만 다시 시도할 수 있습니다."
            }
            "cancelled" {
                Show-OperationOverlay -Title "초벌 번역 실행" -Detail "완료된 배치를 보존합니다." -OperationType "Translation"
                Complete-OperationOverlay -Kind "cancelled" -Title "작업을 중지했습니다" -Detail "완료된 배치는 가능한 경우 검수 프로젝트에 남아 있습니다."
            }
            "completed" {
                Show-OperationOverlay -Title "초벌 번역 실행" -Detail "번역 후보를 검수 프로젝트에 저장했습니다." -OperationType "Translation"
                Complete-OperationOverlay -Kind "completed" -Title "초벌 번역이 완료되었습니다" -Detail "문제 항목을 확인하고 검토를 시작할 수 있습니다."
            }
        }
        if ($PreviewTranslationPreflight) {
            $script:previewPreflightDialog = Select-AiTranslationMode -ExistingInfo ([pscustomobject]@{
                ReviewTranslationCount = @($script:decisions.Values | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.text) }).Count
                KoreanFileCount = 4
                RmkFileCount = 12
                HasExistingTranslation = $true
            }) -PreviewOnly
        }
        if ($PreviewCommandPalette) { $script:previewCommandPaletteDialog = Show-CommandPalette -PreviewOnly }
    } catch {
        Add-Log "초기 화면 구성 실패: $($_.Exception.Message)"
        if ([string]::IsNullOrWhiteSpace($script:layoutSnapshotPath)) {
            [System.Windows.Forms.MessageBox]::Show("검토 결과를 열지 못했습니다.`r`n$($_.Exception.Message)", "RimWorld AI Translator") | Out-Null
        }
    }

    # 캐시가 없거나 바뀐 첫 실행에서만, 첫 화면을 그린 뒤 실제 모드 폴더를 검색한다.
    if (-not $DisableBackgroundDiscovery -and -not $script:startupCatalogCacheLoaded) {
        $script:startupCatalogTimer = [System.Windows.Forms.Timer]::new()
        $script:startupCatalogTimer.Interval = 250
        $script:startupCatalogTimer.Add_Tick({
            $script:startupCatalogTimer.Stop()
            try {
                Refresh-ModCatalog
            } catch {
                Add-Log "모드 자동 검색 실패: $($_.Exception.Message)"
            } finally {
                $script:startupCatalogTimer.Dispose()
                $script:startupCatalogTimer = $null
            }
        })
        $script:startupCatalogTimer.Start()
    }

    if (-not [string]::IsNullOrWhiteSpace($script:performanceReportPath)) {
        Write-WorkspacePerformanceReport -Path $script:performanceReportPath -Iterations $script:performanceIterations
    }

    if (-not [string]::IsNullOrWhiteSpace($script:layoutSnapshotPath)) {
        Start-WorkspaceLayoutSnapshot
    }
})

[void][System.Windows.Forms.Application]::Run($form)
