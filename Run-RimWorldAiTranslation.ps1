param(
    [Parameter(Mandatory = $true)]
    [string]$TranslatorScript,

    [Parameter(Mandatory = $true)]
    [string]$ArgumentFile,

    [Parameter(Mandatory = $true)]
    [string]$LogFile
)

$ErrorActionPreference = "Stop"
trap {
    try {
        $failureLog = [System.IO.Path]::GetFullPath($LogFile)
        $failureParent = Split-Path -Parent $failureLog
        if ($failureParent -and (Test-Path -LiteralPath $failureParent -PathType Container)) {
            [System.IO.File]::AppendAllText($failureLog, ([string]$_.Exception.Message + [Environment]::NewLine), [System.Text.UTF8Encoding]::new($false))
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
    "TranslateMissingOnly", "PreserveTranslationFile", "IncludePatches", "DryRun", "SourceOnly",
    "TranslationProvider", "ProviderName", "BaseUrl", "Model", "Temperature",
    "ResponseFormatMode", "CompletionTokenParameter", "ReasoningEffort",
    "RequestsPerMinutePerKey", "InputTokensPerMinutePerKey", "DailyTokenBudgetPerKey",
    "MaxCompletionTokens"
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
    & $translatorFull @parameters *>&1 | ForEach-Object { $writer.WriteLine([string]$_) }
} catch {
    $writer.WriteLine([string]$_.Exception.Message)
    exit 1
} finally {
    $writer.Dispose()
}

exit 0
