[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$AppDataRoot = (Join-Path $env:LOCALAPPDATA "RimWorldAiTranslator"),
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$modulePath = Join-Path $PSScriptRoot "RimWorldAiTranslator.Diagnostics.ps1"
if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf)) { throw "Diagnostic component was not found: $modulePath" }
. $modulePath
$result = New-RimWorldDiagnosticBundle -OutputPath $OutputPath -AppDataRoot $AppDataRoot -ProductRoot $PSScriptRoot -Force:$Force
Write-Output ("Diagnostic bundle: {0} ({1} bytes, {2} entries)" -f $result.Path, $result.Bytes, $result.Entries)
