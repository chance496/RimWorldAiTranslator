[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidatePattern('^win-(x64|arm64)$')]
    [string]$RuntimeIdentifier = "win-x64",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$versionPath = Join-Path $projectRoot "VERSION"
$solution = Join-Path $projectRoot "RimWorldAiTranslator.sln"
$appProject = Join-Path $projectRoot "src\RimWorldAiTranslator.App\RimWorldAiTranslator.App.csproj"
$distRoot = Join-Path $projectRoot "dist"
$stagingRoot = Join-Path $distRoot "_publish-$RuntimeIdentifier"
$packageRoot = Join-Path $distRoot "RimWorldAiTranslator"

if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf)) { throw "VERSION file was not found: $versionPath" }
$version = [System.IO.File]::ReadAllText($versionPath, [System.Text.Encoding]::ASCII).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "VERSION must use major.minor.patch: $version" }
$zipPath = Join-Path $distRoot "RimWorldAiTranslator-v$version.zip"

function Assert-SafeBuildPath([string]$Path) {
    $root = [System.IO.Path]::GetFullPath($projectRoot).TrimEnd("\", "/")
    $full = [System.IO.Path]::GetFullPath($Path)
    $prefix = $root + [System.IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use a build path outside the repository: $full"
    }
    if (Test-Path -LiteralPath $full) {
        $item = Get-Item -LiteralPath $full -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to use a reparse-point build path: $full"
        }
    }
}

function Reset-BuildDirectory([string]$Path) {
    Assert-SafeBuildPath $Path
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force }
    [System.IO.Directory]::CreateDirectory($Path) | Out-Null
}

foreach ($path in @($distRoot, $stagingRoot, $packageRoot)) { Assert-SafeBuildPath $path }
if (-not (Test-Path -LiteralPath $distRoot)) { [System.IO.Directory]::CreateDirectory($distRoot) | Out-Null }

Write-Host "Building C# solution..."
& dotnet build $solution -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

if (-not $SkipTests) {
    Write-Host "Running C# regression tests..."
    & dotnet run --project (Join-Path $projectRoot "tests\RimWorldAiTranslator.Tests\RimWorldAiTranslator.Tests.csproj") -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw "C# regression tests failed with exit code $LASTEXITCODE." }
}

Reset-BuildDirectory $stagingRoot
Reset-BuildDirectory $packageRoot

Write-Host "Publishing self-contained $RuntimeIdentifier application..."
& dotnet publish $appProject `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    -o $stagingRoot `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$runtimeFiles = @(
    "RimWorldAiTranslator.exe",
    "glossary.generated.ko.json",
    "rimworld-def-field-rules.txt"
)
foreach ($name in $runtimeFiles) {
    $source = Join-Path $stagingRoot $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Published runtime file is missing: $name" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $packageRoot $name) -Force
}

foreach ($name in @("PACKAGE_README.txt", "RELEASE_NOTES.md", "sample-glossary.txt", "VERSION", "LICENSE")) {
    $source = Join-Path $projectRoot $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Package documentation is missing: $name" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $packageRoot $name) -Force
}

$unexpectedRuntime = Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Where-Object {
    $_.Extension -in ".ps1", ".psm1", ".cmd", ".bat" -or $_.Name -match '(?i)^powershell\.exe$|^pwsh\.exe$'
}
if ($unexpectedRuntime) {
    throw "PowerShell runtime files entered the package: $([string]::Join(', ', @($unexpectedRuntime.Name)))"
}

$exe = Join-Path $packageRoot "RimWorldAiTranslator.exe"
$versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
if ([string]$versionInfo.ProductVersion -ne $version -or [string]$versionInfo.FileVersion -ne "$version.0") {
    throw "Published executable version mismatch: file=$($versionInfo.FileVersion), product=$($versionInfo.ProductVersion), expected=$version.0/$version"
}

if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal

$smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("RimWorldAiTranslator-package-smoke-" + [Guid]::NewGuid().ToString("N"))
$tempPrefix = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd("\", "/") + [System.IO.Path]::DirectorySeparatorChar
$smokeFull = [System.IO.Path]::GetFullPath($smokeRoot)
if (-not $smokeFull.StartsWith($tempPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
    -not [System.IO.Path]::GetFileName($smokeFull).StartsWith("RimWorldAiTranslator-package-smoke-", [System.StringComparison]::Ordinal)) {
    throw "Refusing to use an unverified smoke-test path: $smokeFull"
}

try {
    $extractRoot = Join-Path $smokeFull "package"
    $dataRoot = Join-Path $smokeFull "appdata"
    [System.IO.Directory]::CreateDirectory($extractRoot) | Out-Null
    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractRoot -Force
    $smokeExe = Join-Path $extractRoot "RimWorldAiTranslator.exe"
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($smokeExe)
    $startInfo.UseShellExecute = $false
    $startInfo.Environment["RIMWORLD_TRANSLATOR_DATA_ROOT"] = $dataRoot
    $process = [System.Diagnostics.Process]::Start($startInfo)
    if (-not $process) { throw "Packaged application did not start." }
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited -and $process.MainWindowHandle -eq [IntPtr]::Zero) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
    }
    if ($process.HasExited) { throw "Packaged application exited during startup with code $($process.ExitCode)." }
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) { throw "Packaged application did not expose a main window within 25 seconds." }
    $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($process.Id)" -ErrorAction SilentlyContinue)
    $powerShellChildren = @($children | Where-Object { $_.Name -match '^(powershell|pwsh)\.exe$' })
    if ($powerShellChildren.Count -gt 0) { throw "Packaged application started a PowerShell child process." }
    [void]$process.CloseMainWindow()
    if (-not $process.WaitForExit(10000)) {
        $process.Kill($true)
        throw "Packaged application did not exit within 10 seconds."
    }
    if ($process.ExitCode -ne 0) { throw "Packaged application smoke test exited with code $($process.ExitCode)." }
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    if ($smokeFull.StartsWith($tempPrefix, [System.StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $smokeFull)) {
        Remove-Item -LiteralPath $smokeFull -Recurse -Force
    }
}

$hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
$zip = Get-Item -LiteralPath $zipPath
Write-Host "Package: $($zip.FullName)"
Write-Host "Bytes:   $($zip.Length)"
Write-Host "SHA-256: $($hash.Hash)"
