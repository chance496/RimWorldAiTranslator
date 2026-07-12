param(
    [string]$ReviewRoot = ""
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$reviewGuiScript = Join-Path $scriptRoot "Start-RimWorldAiReviewGui.ps1"
$powershellExe = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"

if (-not (Test-Path -LiteralPath $reviewGuiScript -PathType Leaf)) {
    Add-Type -AssemblyName System.Windows.Forms
    $missingMessage = ConvertFrom-Json '"Start-RimWorldAiReviewGui.ps1 \uD30C\uC77C\uC744 \uCC3E\uC744 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4.\nEXE\uC640 \uC2A4\uD06C\uB9BD\uD2B8\uB97C \uAC19\uC740 \uD3F4\uB354\uC5D0 \uB450\uC138\uC694."'
    [System.Windows.Forms.MessageBox]::Show(
        $missingMessage,
        "RimWorld AI Translator",
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Error
    ) | Out-Null
    exit 2
}
if (-not (Test-Path -LiteralPath $powershellExe -PathType Leaf)) {
    throw "Windows PowerShell was not found at the expected system path: $powershellExe"
}

$launchArguments = @("-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-File", $reviewGuiScript)
if (-not [string]::IsNullOrWhiteSpace($ReviewRoot)) {
    $launchArguments += @("-ReviewRoot", $ReviewRoot)
}

& $powershellExe @launchArguments
exit $LASTEXITCODE
