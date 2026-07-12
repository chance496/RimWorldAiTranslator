[CmdletBinding()]
param(
    [string]$PackagePath = "",
    [string]$OutputRoot = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd("\", "/")
$workspace = Join-Path $tempBase ("RimWorldAiTranslator-tests-startup-" + [Guid]::NewGuid().ToString("N"))
if (-not $OutputRoot) {
    $OutputRoot = Join-Path $tempBase ("RimWorldAiTranslator-startup-audit-" + [Guid]::NewGuid().ToString("N"))
}
$outputFull = [System.IO.Path]::GetFullPath($OutputRoot)

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-TempWorkspace([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd("\", "/")
    $prefix = $tempBase + [System.IO.Path]::DirectorySeparatorChar
    Assert-True $full.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) "Audit workspace escaped the system temp directory."
    Assert-True ([System.IO.Path]::GetFileName($full)).StartsWith("RimWorldAiTranslator-tests-startup-", [System.StringComparison]::Ordinal) "Audit workspace prefix is invalid."
}

function Quote-WindowsArgument([string]$Value) {
    if ($null -eq $Value -or $Value.Length -eq 0) { return '""' }
    $result = New-Object System.Text.StringBuilder
    [void]$result.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq [char]92) { $backslashes++; continue }
        if ($character -eq '"') {
            [void]$result.Append([char]92, (($backslashes * 2) + 1))
            [void]$result.Append('"')
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0) { [void]$result.Append([char]92, $backslashes); $backslashes = 0 }
        [void]$result.Append($character)
    }
    if ($backslashes -gt 0) { [void]$result.Append([char]92, ($backslashes * 2)) }
    [void]$result.Append('"')
    return $result.ToString()
}

function Get-WindowSample([System.Diagnostics.Process]$Process) {
    $Process.Refresh()
    $handle = $Process.MainWindowHandle
    $visible = $false
    $alpha = -1
    $flags = 0
    if ($handle -ne [IntPtr]::Zero) {
        $visible = [StartupVisualAuditNative]::IsWindowVisible($handle)
        $key = [uint32]0
        $windowAlpha = [byte]0
        $windowFlags = [uint32]0
        if ([StartupVisualAuditNative]::GetLayeredWindowAttributes($handle, [ref]$key, [ref]$windowAlpha, [ref]$windowFlags)) {
            $alpha = [int]$windowAlpha
            $flags = [int]$windowFlags
        }
    }
    return [pscustomobject]@{
        Handle = $handle
        Visible = $visible
        Alpha = $alpha
        Flags = $flags
        Ready = $visible -and ((($flags -band 2) -eq 0) -or $alpha -gt 0)
    }
}

function Write-StartupCoverRender([string]$ExecutablePath, [string]$Path) {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    $assembly = [System.Reflection.Assembly]::LoadFile($ExecutablePath)
    $type = $assembly.GetType("Program+StartupForm", $true)
    $form = [System.Activator]::CreateInstance($type, $true)
    try {
        $privateInstance = [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::NonPublic
        $timerField = $type.GetField("animationTimer", $privateInstance)
        $animationTimer = $timerField.GetValue($form)
        Assert-True ($animationTimer.Enabled) "Startup cover animation timer is not running."
        Assert-True ($animationTimer.Interval -ge 30 -and $animationTimer.Interval -le 100) "Startup cover animation interval is outside the lightweight range."

        $renderFrame = {
            param([System.Drawing.Bitmap]$Target)
            $graphics = [System.Drawing.Graphics]::FromImage($Target)
            $paintArguments = $null
            try {
                $paintArguments = [System.Windows.Forms.PaintEventArgs]::new(
                    $graphics,
                    [System.Drawing.Rectangle]::new(0, 0, $Target.Width, $Target.Height)
                )
                $binding = [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::NonPublic
                $backgroundMethod = $type.GetMethod("OnPaintBackground", $binding)
                $paintMethod = $type.GetMethod("OnPaint", $binding)
                [void]$backgroundMethod.Invoke($form, @($paintArguments))
                [void]$paintMethod.Invoke($form, @($paintArguments))
            } finally {
                if ($paintArguments) { $paintArguments.Dispose() }
                $graphics.Dispose()
            }
        }

        $bitmap = [System.Drawing.Bitmap]::new($form.Width, $form.Height)
        try {
            [void](& $renderFrame $bitmap)
            $headerPixel = $bitmap.GetPixel(24, 24)
            $bodyPixel = $bitmap.GetPixel(24, 96)
            $headerBrightness = [int]$headerPixel.R + [int]$headerPixel.G + [int]$headerPixel.B
            $bodyBrightness = [int]$bodyPixel.R + [int]$bodyPixel.G + [int]$bodyPixel.B
            Assert-True ($headerBrightness -gt 45) "Startup cover header rendered as black."
            Assert-True ($bodyBrightness -gt 450) "Startup cover body rendered as black or incomplete."
            Assert-True ([Math]::Abs($bodyBrightness - $headerBrightness) -gt 250) "Startup cover lacks the expected header/body contrast."
            $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)

            Start-Sleep -Milliseconds 220
            $activeBitmap = [System.Drawing.Bitmap]::new($form.Width, $form.Height)
            try {
                [void](& $renderFrame $activeBitmap)
                $changedTrackPixels = 0
                for ($x = 24; $x -lt ($bitmap.Width - 24); $x++) {
                    if ($bitmap.GetPixel($x, 132).ToArgb() -ne $activeBitmap.GetPixel($x, 132).ToArgb()) {
                        $changedTrackPixels++
                    }
                }
                Assert-True ($changedTrackPixels -ge 24) "Startup cover activity indicator did not animate."
                return $changedTrackPixels
            } finally {
                $activeBitmap.Dispose()
            }
        } finally {
            $bitmap.Dispose()
        }
    } finally {
        $form.Dispose()
    }
}

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class StartupVisualAuditNative
{
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetLayeredWindowAttributes(
        IntPtr windowHandle,
        out uint colorKey,
        out byte alpha,
        out uint flags);
}
"@

if (-not $PackagePath) {
    $PackagePath = Join-Path $repoRoot "dist\RimWorldAiTranslator\RimWorldAiTranslator.exe"
}
$packageFull = [System.IO.Path]::GetFullPath($PackagePath)
Assert-True (Test-Path -LiteralPath $packageFull -PathType Leaf) "Packaged executable was not found: $packageFull"
Assert-TempWorkspace $workspace
[System.IO.Directory]::CreateDirectory($workspace) | Out-Null
[System.IO.Directory]::CreateDirectory($outputFull) | Out-Null

$appDataRoot = Join-Path $workspace "appdata"
[System.IO.Directory]::CreateDirectory($appDataRoot) | Out-Null
$layoutPath = Join-Path $outputFull "main-complete.png"
$coverPath = Join-Path $outputFull "startup-cover.png"
$timelinePath = Join-Path $outputFull "timeline.json"
$summaryPath = Join-Path $outputFull "summary.json"
$process = $null

try {
    $animatedTrackPixels = Write-StartupCoverRender $packageFull $coverPath
    Assert-True ((Get-Item -LiteralPath $coverPath).Length -gt 1000) "Startup cover render is unexpectedly empty."

    $arguments = @(
        "-LayoutSnapshotPath", $layoutPath,
        "-InitialDashboardTab", "projects",
        "-PreviewTheme", "Dark",
        "-AppDataRoot", $appDataRoot,
        "-DisableBackgroundDiscovery"
    )
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $packageFull
    $startInfo.Arguments = [string]::Join(" ", @($arguments | ForEach-Object { Quote-WindowsArgument $_ }))
    $startInfo.WorkingDirectory = Split-Path -Parent $packageFull
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    [void]$process.Start()

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $samples = New-Object "System.Collections.Generic.List[object]"
    $childProcess = $null
    while (-not $process.HasExited -and $watch.ElapsedMilliseconds -lt 15000) {
        $launcher = Get-WindowSample $process
        if (-not $childProcess -or $childProcess.HasExited) {
            $childInfo = Get-CimInstance Win32_Process -Filter ("ParentProcessId=" + $process.Id) -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -ieq "powershell.exe" } |
                Select-Object -First 1
            if ($childInfo) { $childProcess = Get-Process -Id $childInfo.ProcessId -ErrorAction SilentlyContinue }
        }
        $child = if ($childProcess -and -not $childProcess.HasExited) { Get-WindowSample $childProcess } else { $null }
        [void]$samples.Add([pscustomobject]@{
            ms = $watch.ElapsedMilliseconds
            launcherVisible = [bool]$launcher.Visible
            childVisible = [bool]($child -and $child.Visible)
            childAlpha = if ($child) { [int]$child.Alpha } else { -1 }
            childFlags = if ($child) { [int]$child.Flags } else { 0 }
            childReady = [bool]($child -and $child.Ready)
        })
        Start-Sleep -Milliseconds 35
        $process.Refresh()
    }
    if (-not $process.HasExited) {
        try { $process.Kill() } catch {}
        throw "Packaged app did not exit within 15 seconds."
    }
    $watch.Stop()

    $sampleArray = @($samples.ToArray())
    [System.IO.File]::WriteAllText($timelinePath, ($sampleArray | ConvertTo-Json -Depth 4), [System.Text.UTF8Encoding]::new($false))
    $firstSplash = @($sampleArray | Where-Object { $_.launcherVisible } | Select-Object -First 1)
    $firstReady = @($sampleArray | Where-Object { $_.childReady } | Select-Object -First 1)
    $hiddenMain = @($sampleArray | Where-Object { $_.childVisible -and (($_.childFlags -band 2) -ne 0) -and $_.childAlpha -eq 0 })
    Assert-True ($firstSplash.Count -eq 1) "Startup cover was not observed."
    Assert-True ($hiddenMain.Count -gt 0) "The main window was not observed in its hidden initialization state."
    Assert-True ($firstReady.Count -eq 1) "The completed main window was not observed."
    Assert-True ([long]$firstSplash[0].ms -lt [long]$firstReady[0].ms) "The startup cover did not precede the main window reveal."
    $coverGap = @($sampleArray | Where-Object {
        $_.ms -gt $firstSplash[0].ms -and $_.ms -lt $firstReady[0].ms -and -not $_.launcherVisible
    })
    Assert-True ($coverGap.Count -eq 0) "The startup cover disappeared before the main window was ready."
    Assert-True ($process.ExitCode -eq 0) "Packaged app exited with code $($process.ExitCode)."
    Assert-True (Test-Path -LiteralPath $layoutPath -PathType Leaf) "Completed main-window snapshot was not created."

    $accessibilityPath = [System.IO.Path]::ChangeExtension($layoutPath, ".accessibility.json")
    Assert-True (Test-Path -LiteralPath $accessibilityPath -PathType Leaf) "Accessibility audit was not created."
    $parsedAuditRows = [System.IO.File]::ReadAllText($accessibilityPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $auditRows = @(foreach ($auditRow in $parsedAuditRows) { $auditRow })
    Assert-True (@($auditRows | Where-Object { $_.clipped -or $_.textClipped }).Count -eq 0) "Completed main window contains clipped controls or text."
    $commandAccessibleName = ConvertFrom-Json '"\uba85\ub839 \ucc3e\uae30"'
    $commandRows = @($auditRows | Where-Object { $_.accessibleName -eq $commandAccessibleName })
    Assert-True ($commandRows.Count -eq 1) "Maximized dashboard command button was not found."
    $commandBounds = @($commandRows[0].bounds -split "," | ForEach-Object { [int]$_.Trim() })
    $commandParent = @($commandRows[0].parentClient -split "," | ForEach-Object { [int]$_.Trim() })
    $commandRightGap = $commandParent[0] - ($commandBounds[0] + $commandBounds[2])
    Assert-True ($commandRightGap -ge 12 -and $commandRightGap -le 48) "Maximized startup layout retained stale normal-window bounds. Command right gap: $commandRightGap px."

    $summary = [ordered]@{
        package = $packageFull
        exitCode = $process.ExitCode
        elapsedMs = $watch.ElapsedMilliseconds
        samples = $sampleArray.Count
        firstSplashMs = [long]$firstSplash[0].ms
        firstReadyMs = [long]$firstReady[0].ms
        hiddenMainSamples = $hiddenMain.Count
        coverGapSamples = $coverGap.Count
        clippedRows = 0
        animatedTrackPixels = [int]$animatedTrackPixels
        maximizedClientWidth = [int]$commandParent[0]
        commandRightGap = [int]$commandRightGap
    }
    [System.IO.File]::WriteAllText($summaryPath, ($summary | ConvertTo-Json -Depth 4), [System.Text.UTF8Encoding]::new($false))
    Write-Host "PASS Startup.VisualSequence"
    Write-Host "Startup audit output: $outputFull"
} finally {
    if ($process) { $process.Dispose() }
    if (Test-Path -LiteralPath $workspace -PathType Container) {
        Assert-TempWorkspace $workspace
        Remove-Item -LiteralPath $workspace -Recurse -Force -ErrorAction Stop
    }
}
