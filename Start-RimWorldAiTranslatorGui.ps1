param(
    [string]$ReviewRoot = ""
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$reviewGuiScript = Join-Path $scriptRoot "Start-RimWorldAiReviewGui.ps1"
$powershellExe = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"

if (-not (Test-Path -LiteralPath $reviewGuiScript -PathType Leaf)) {
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show(
        "Start-RimWorldAiReviewGui.ps1 파일을 찾을 수 없습니다.`nEXE와 스크립트를 같은 폴더에 두세요.",
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
