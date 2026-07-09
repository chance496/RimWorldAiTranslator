[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$distRoot = Join-Path $projectRoot "dist"
$packageRoot = Join-Path $distRoot "RimWorldAiTranslator"
$zipPath = Join-Path $distRoot "RimWorldAiTranslator.zip"
$launcherSource = Join-Path $projectRoot "launcher\RimWorldAiTranslatorLauncher.cs"
$launcherExe = Join-Path $projectRoot "RimWorldAiTranslator.exe"

$cscCandidates = @(
    (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
    (Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe")
)

$csc = $cscCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $csc) {
    throw "Could not find .NET Framework csc.exe. Install .NET Framework Developer Pack or build the launcher manually."
}

& $csc `
    /nologo `
    /target:winexe `
    /platform:anycpu `
    /codepage:65001 `
    /reference:System.Windows.Forms.dll `
    /out:$launcherExe `
    $launcherSource

if ($LASTEXITCODE -ne 0) {
    throw "Launcher build failed with exit code $LASTEXITCODE."
}

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null

$packageFiles = @(
    "RimWorldAiTranslator.exe",
    "Start-RimWorldAiTranslatorGui.ps1",
    "Start-RimWorldAiTranslatorGui.cmd",
    "Invoke-RimWorldAiTranslation.ps1",
    "Build-RimWorldGlossary.ps1",
    "glossary.generated.ko.json",
    "sample-glossary.txt",
    "README.md",
    "PACKAGE_README.txt",
    "LICENSE"
)

foreach ($file in $packageFiles) {
    $source = Join-Path $projectRoot $file
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Missing package file: $file"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $packageRoot $file) -Force
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zipPath -Force

Write-Host "Package folder: $packageRoot"
Write-Host "Package zip:    $zipPath"
