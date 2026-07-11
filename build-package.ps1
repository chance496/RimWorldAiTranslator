[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Write-Verbose "Build configuration: $Configuration"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$distRoot = Join-Path $projectRoot "dist"
$packageRoot = Join-Path $distRoot "RimWorldAiTranslator"
$zipPath = Join-Path $distRoot "RimWorldAiTranslator.zip"
$launcherBuildRoot = Join-Path $distRoot "_launcher-build"
$launcherSource = Join-Path $projectRoot "launcher\RimWorldAiTranslatorLauncher.cs"
$launcherExe = Join-Path $launcherBuildRoot "RimWorldAiTranslator.exe"
$projectLauncherExe = Join-Path $projectRoot "RimWorldAiTranslator.exe"
$nativeSource = Join-Path $projectRoot "native\RimWorldTranslatorNative.cs"
$nativeDll = Join-Path $launcherBuildRoot "RimWorldAiTranslator.Native.dll"
$projectNativeDll = Join-Path $projectRoot "RimWorldAiTranslator.Native.dll"

function Assert-SafeBuildPath([string]$Path) {
    $projectFull = [System.IO.Path]::GetFullPath($projectRoot).TrimEnd("\", "/")
    $pathFull = [System.IO.Path]::GetFullPath($Path)
    $prefix = $projectFull + [System.IO.Path]::DirectorySeparatorChar
    if (-not $pathFull.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use a build path outside the project root: $pathFull"
    }
    if (Test-Path -LiteralPath $pathFull) {
        $item = Get-Item -LiteralPath $pathFull -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to use a reparse-point build path: $pathFull"
        }
    }
}

Assert-SafeBuildPath $distRoot
Assert-SafeBuildPath $packageRoot
Assert-SafeBuildPath $launcherBuildRoot

if (Test-Path -LiteralPath $launcherBuildRoot) {
    Get-ChildItem -LiteralPath $launcherBuildRoot -Force | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Force -Path $launcherBuildRoot | Out-Null
}

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
    /reference:System.Drawing.dll `
    /out:$launcherExe `
    $launcherSource

if ($LASTEXITCODE -ne 0) {
    throw "Launcher build failed with exit code $LASTEXITCODE."
}

& $csc `
    /nologo `
    /target:library `
    /optimize+ `
    /platform:anycpu `
    /codepage:65001 `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Xml.dll `
    /reference:System.Xml.Linq.dll `
    /reference:System.IO.Compression.dll `
    /reference:System.IO.Compression.FileSystem.dll `
    /out:$nativeDll `
    $nativeSource

if ($LASTEXITCODE -ne 0) {
    throw "Native reader build failed with exit code $LASTEXITCODE."
}

try {
    Copy-Item -LiteralPath $launcherExe -Destination $projectLauncherExe -Force -ErrorAction Stop
} catch {
    Write-Warning "The development launcher is currently in use and was not replaced. The packaged launcher is still current."
}
try {
    Copy-Item -LiteralPath $nativeDll -Destination $projectNativeDll -Force -ErrorAction Stop
} catch {
    Write-Warning "The development native reader is currently in use and was not replaced. The packaged reader is still current."
}

if (Test-Path -LiteralPath $packageRoot) {
    Get-ChildItem -LiteralPath $packageRoot -Force | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
}

$packageFiles = @(
    "RimWorldAiTranslator.exe",
    "RimWorldAiTranslator.Native.dll",
    "Start-RimWorldAiTranslatorGui.ps1",
    "Start-RimWorldAiTranslatorGui.cmd",
    "Start-RimWorldAiReviewGui.ps1",
    "Invoke-RimWorldAiTranslation.ps1",
    "Run-RimWorldAiTranslation.ps1",
    "Apply-RimWorldAiReviewResults.ps1",
    "Export-RimWorldAiReviewToRmk.ps1",
    "Build-RimWorldGlossary.ps1",
    "glossary.generated.ko.json",
    "sample-glossary.txt",
    "README.md",
    "PACKAGE_README.txt",
    "LICENSE"
)

foreach ($file in $packageFiles) {
    $source = if ($file -eq "RimWorldAiTranslator.exe") {
        $launcherExe
    } elseif ($file -eq "RimWorldAiTranslator.Native.dll") {
        $nativeDll
    } else {
        Join-Path $projectRoot $file
    }
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Missing package file: $file"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $packageRoot $file) -Force
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zipPath -Force

Get-ChildItem -LiteralPath $launcherBuildRoot -Force | Remove-Item -Recurse -Force
Remove-Item -LiteralPath $launcherBuildRoot -Force

Write-Host "Package folder: $packageRoot"
Write-Host "Package zip:    $zipPath"
