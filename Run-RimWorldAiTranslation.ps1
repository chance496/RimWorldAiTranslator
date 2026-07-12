param(
    [Parameter(Mandatory = $true)]
    [string]$TranslatorScript,

    [Parameter(Mandatory = $true)]
    [string]$ArgumentFile,

    [Parameter(Mandatory = $true)]
    [string]$LogFile
)

$ErrorActionPreference = "Stop"
$script:sensitiveLogValues = New-Object "System.Collections.Generic.List[string]"
foreach ($rawValue in @($env:RIMWORLD_TRANSLATOR_API_KEYS, $env:CEREBRAS_API_KEY)) {
    foreach ($candidate in [System.Text.RegularExpressions.Regex]::Split([string]$rawValue, "[,;\r\n]+")) {
        $trimmed = ([string]$candidate).Trim()
        if ($trimmed.StartsWith("Bearer ", [System.StringComparison]::OrdinalIgnoreCase)) {
            $trimmed = $trimmed.Substring(7).Trim()
        }
        if ($trimmed.Length -ge 4 -and -not $script:sensitiveLogValues.Contains($trimmed)) {
            [void]$script:sensitiveLogValues.Add($trimmed)
        }
    }
}

function Protect-SensitiveLogText([object]$Value) {
    $text = [string]$Value
    foreach ($secret in @($script:sensitiveLogValues | Sort-Object Length -Descending)) {
        if (-not [string]::IsNullOrEmpty([string]$secret)) { $text = $text.Replace([string]$secret, "[REDACTED]") }
    }
    $text = [System.Text.RegularExpressions.Regex]::Replace($text, '(?i)(authorization\s*[:=]\s*bearer\s+)[^\s,;"'']+', '$1[REDACTED]')
    $text = [System.Text.RegularExpressions.Regex]::Replace($text, '(?i)((?:api[_-]?key|access[_-]?token)\s*[:=]\s*)[^\s,;"'']+', '$1[REDACTED]')
    return $text
}

trap {
    try {
        $failureLog = [System.IO.Path]::GetFullPath($LogFile)
        $failureParent = Split-Path -Parent $failureLog
        if ($failureParent -and (Test-Path -LiteralPath $failureParent -PathType Container)) {
            [System.IO.File]::AppendAllText($failureLog, ((Protect-SensitiveLogText $_.Exception.Message) + [Environment]::NewLine), [System.Text.UTF8Encoding]::new($false))
        }
    } catch {
    }
    exit 1
}

function Resolve-RequiredFile([string]$Path, [string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw "$Name is required." }
    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction Stop
    if (-not (Test-Path -LiteralPath $resolved.Path -PathType Leaf)) { throw "$Name is not a file." }
    return [System.IO.Path]::GetFullPath($resolved.Path)
}

$translatorFull = Resolve-RequiredFile $TranslatorScript "TranslatorScript"
$argumentFull = Resolve-RequiredFile $ArgumentFile "ArgumentFile"
$argumentInfo = Get-Item -LiteralPath $argumentFull -ErrorAction Stop
if ($argumentInfo.Length -gt 1048576) { throw "ArgumentFile is too large." }

$payload = [System.IO.File]::ReadAllText($argumentFull, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$allowedParameters = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($name in @(
    "ModRoot", "LanguageFolderName", "SourceLanguageFolder", "ReviewOnly", "ReviewRoot",
    "BatchSize", "MaxGeneratedGlossaryTermsPerBatch", "ReferenceLanguageRoot", "ReferenceSourceWorkbook",
    "TranslateMissingOnly", "PreserveTranslationFile", "IncludePatches", "Overwrite", "DryRun", "SourceOnly",
    "CancellationFile",
    "TranslationProvider", "ProviderName", "BaseUrl", "Model", "Temperature",
    "ResponseFormatMode", "CompletionTokenParameter", "ReasoningEffort",
    "RequestsPerMinutePerKey", "InputTokensPerMinutePerKey", "DailyTokenBudgetPerKey",
    "MaxCompletionTokens", "TimeoutSec", "MaxRetries", "AllowInsecureLoopback"
)) { [void]$allowedParameters.Add($name) }
$parameters = @{}
if (-not $payload.parameters) { throw "ArgumentFile does not contain parameters." }
foreach ($property in @($payload.parameters.PSObject.Properties)) {
    if (-not $allowedParameters.Contains([string]$property.Name)) {
        throw "ArgumentFile contains an unsupported parameter: $($property.Name)"
    }
    $parameters[[string]$property.Name] = $property.Value
}
if ($parameters.Count -eq 0 -or $parameters.Count -gt $allowedParameters.Count -or -not $parameters.ContainsKey("ModRoot")) {
    throw "ArgumentFile contains an invalid parameter set."
}
$apiKeys = @($script:sensitiveLogValues)
if ($apiKeys.Count -gt 0) { $parameters["ApiKey"] = $apiKeys }
$env:RIMWORLD_TRANSLATOR_API_KEYS = ""
$env:CEREBRAS_API_KEY = ""

$logFull = [System.IO.Path]::GetFullPath($LogFile)
$logParent = Split-Path -Parent $logFull
if (-not $logParent -or -not (Test-Path -LiteralPath $logParent -PathType Container)) {
    throw "LogFile parent directory does not exist."
}

$logStream = New-Object System.IO.FileStream(
    $logFull,
    [System.IO.FileMode]::Create,
    [System.IO.FileAccess]::Write,
    [System.IO.FileShare]::ReadWrite
)
$writer = New-Object System.IO.StreamWriter($logStream, (New-Object System.Text.UTF8Encoding($false)))
$writer.AutoFlush = $true
try {
    & $translatorFull @parameters *>&1 | ForEach-Object { $writer.WriteLine((Protect-SensitiveLogText $_)) }
} catch {
    $writer.WriteLine((Protect-SensitiveLogText $_.Exception.Message))
    exit 1
} finally {
    $env:RIMWORLD_TRANSLATOR_API_KEYS = ""
    $env:CEREBRAS_API_KEY = ""
    $writer.Dispose()
}

exit 0
