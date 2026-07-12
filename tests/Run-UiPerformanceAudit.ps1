[CmdletBinding()]
param(
    [string]$OutputRoot = "",
    [ValidateRange(100, 20000)]
    [int]$Rows = 5000,
    [ValidateRange(1, 20)]
    [int]$Iterations = 5
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$powerShellExe = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
$tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd("\", "/")
$workspace = Join-Path $tempBase ("RimWorldAiTranslator-tests-ui-" + [Guid]::NewGuid().ToString("N"))
if (-not $OutputRoot) { $OutputRoot = Join-Path $tempBase ("RimWorldAiTranslator-ui-audit-" + [Guid]::NewGuid().ToString("N")) }
$outputFull = [System.IO.Path]::GetFullPath($OutputRoot)
[System.IO.Directory]::CreateDirectory($workspace) | Out-Null
[System.IO.Directory]::CreateDirectory($outputFull) | Out-Null

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
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

function Write-Utf8Text([string]$Path, [string]$Text) {
    $parent = Split-Path -Parent $Path
    if ($parent -and -not (Test-Path -LiteralPath $parent -PathType Container)) { [System.IO.Directory]::CreateDirectory($parent) | Out-Null }
    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($false))
}

function Get-ImageSampleStats([string]$Path) {
    Add-Type -AssemblyName System.Drawing
    $bitmap = [System.Drawing.Bitmap]::new($Path)
    try {
        $colors = New-Object "System.Collections.Generic.HashSet[int]"
        $samples = 0
        $nonBlank = 0
        $stepX = [Math]::Max(1, [int]($bitmap.Width / 80))
        $stepY = [Math]::Max(1, [int]($bitmap.Height / 50))
        for ($y = 0; $y -lt $bitmap.Height; $y += $stepY) {
            for ($x = 0; $x -lt $bitmap.Width; $x += $stepX) {
                $argb = $bitmap.GetPixel($x, $y).ToArgb()
                [void]$colors.Add($argb)
                $samples++
                if (($argb -band 0x00FFFFFF) -notin @(0x00FFFFFF, 0x00000000)) { $nonBlank++ }
            }
        }
        return [pscustomobject]@{ width = $bitmap.Width; height = $bitmap.Height; uniqueColors = $colors.Count; nonBlankRatio = [Math]::Round($nonBlank / [double]$samples, 4) }
    } finally {
        $bitmap.Dispose()
    }
}

try {
    $reviewRoot = Join-Path $workspace "review"
    $auditRoot = Join-Path $reviewRoot "_TranslationAudit"
    $languageRoot = Join-Path $reviewRoot "Languages\Korean\Keyed"
    [System.IO.Directory]::CreateDirectory($auditRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($languageRoot) | Out-Null
    $target = Join-Path $languageRoot "Synthetic.xml"
    $korean = ConvertFrom-Json '"\uBC88\uC5ED\uBB38"'
    $rowsList = New-Object "System.Collections.Generic.List[object]" $Rows
    for ($index = 0; $index -lt $Rows; $index++) {
        $candidate = if (($index % 3) -eq 0) { "$korean $index" } else { "" }
        $existing = if (($index % 5) -eq 0) { "$korean existing $index" } else { "" }
        [void]$rowsList.Add([pscustomobject]@{
            id = "E{0:d6}" -f ($index + 1)
            key = "Synthetic.Entry.$index"
            kind = "Keyed"
            defClass = "Keyed"
            node = "Synthetic.Entry.$index"
            field = "text"
            target = $target
            source = if (($index % 17) -eq 0) { "needle source $index with a deliberately long localization sentence for layout stress" } else { "Synthetic source $index" }
            existing = $existing
            candidate = $candidate
            existingOrigin = if ($existing) { "mod" } else { "" }
            translationOrigin = if ($candidate) { "ai" } elseif ($existing) { "mod" } else { "" }
            rmkSourceChanged = $false
            safeToApply = [bool]$candidate
        })
    }
    Write-Utf8Text (Join-Path $auditRoot "synthetic-comparison.json") ($rowsList.ToArray() | ConvertTo-Json -Depth 6 -Compress)
    Write-Utf8Text (Join-Path $auditRoot "synthetic-skipped-internal-identifiers.json") "[]"

    $memoryReviewRoot = Join-Path $workspace "memory-review"
    $memoryAuditRoot = Join-Path $memoryReviewRoot "_TranslationAudit"
    $memoryTarget = Join-Path $memoryReviewRoot "Languages\Korean\Keyed\Memory.xml"
    [System.IO.Directory]::CreateDirectory($memoryAuditRoot) | Out-Null
    $memorySource = "Repeated source for local memory"
    $memoryTranslation = ConvertFrom-Json '"\uB85C\uCEEC \uBA54\uBAA8\uB9AC \uBC88\uC5ED"'
    $memoryRows = @(
        [pscustomobject]@{ id = "M000002"; key = "Memory.Two"; kind = "Keyed"; defClass = "Keyed"; node = "Memory.Two"; field = "text"; target = $memoryTarget; source = $memorySource; existing = ""; candidate = ""; translationOrigin = ""; rmkSourceChanged = $false },
        [pscustomobject]@{ id = "M000001"; key = "Memory.One"; kind = "Keyed"; defClass = "Keyed"; node = "Memory.One"; field = "text"; target = $memoryTarget; source = $memorySource; existing = ""; candidate = $memoryTranslation; translationOrigin = "local"; rmkSourceChanged = $false }
    )
    $memoryComparison = Join-Path $memoryAuditRoot "memory-comparison.json"
    Write-Utf8Text $memoryComparison ($memoryRows | ConvertTo-Json -Depth 6 -Compress)
    Write-Utf8Text (Join-Path $memoryAuditRoot "memory-skipped-internal-identifiers.json") "[]"
    $memoryDecisions = [ordered]@{
        version = 5
        sparse = $true
        reviewRoot = $memoryReviewRoot
        comparison = $memoryComparison
        updatedAt = [DateTime]::UtcNow.ToString("o")
        items = @([ordered]@{ id = "M000001"; key = "Memory.One"; target = "Keyed\Memory.xml"; status = "approved"; text = $memoryTranslation; translationOrigin = "local"; sourceText = $memorySource; sourceChanged = $false })
    }
    Write-Utf8Text (Join-Path $memoryReviewRoot "review-decisions.json") ($memoryDecisions | ConvertTo-Json -Depth 7 -Compress)

    $scenarios = @(
        [pscustomobject]@{ Name = "minimum-light-large-text"; Width = 900; Height = 600; Theme = "Light"; TextSize = 12; HighContrast = $false; Measure = $false },
        [pscustomobject]@{ Name = "notebook-light"; Width = 1280; Height = 720; Theme = "Light"; TextSize = 10; HighContrast = $false; Measure = $true },
        [pscustomobject]@{ Name = "desktop-dark"; Width = 1920; Height = 1080; Theme = "Dark"; TextSize = 10; HighContrast = $false; Measure = $false },
        [pscustomobject]@{ Name = "notebook-dark-high-contrast"; Width = 1280; Height = 720; Theme = "Dark"; TextSize = 12; HighContrast = $true; Measure = $false },
        [pscustomobject]@{ Name = "translation-memory-light"; Width = 1280; Height = 720; Theme = "Light"; TextSize = 10; HighContrast = $false; Measure = $false; ReviewRoot = $memoryReviewRoot; SideTab = "terms" }
    )
    $results = New-Object "System.Collections.Generic.List[object]"
    foreach ($scenario in $scenarios) {
        $snapshotPath = Join-Path $outputFull ($scenario.Name + ".png")
        $appDataRoot = Join-Path $workspace ("appdata-" + $scenario.Name)
        $scenarioReviewRoot = if ($scenario.PSObject.Properties["ReviewRoot"] -and $scenario.ReviewRoot) { [string]$scenario.ReviewRoot } else { $reviewRoot }
        $arguments = @(
            "-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $repoRoot "Start-RimWorldAiReviewGui.ps1"),
            "-ReviewRoot", $scenarioReviewRoot,
            "-LayoutSnapshotPath", $snapshotPath,
            "-LayoutSnapshotWidth", $scenario.Width,
            "-LayoutSnapshotHeight", $scenario.Height,
            "-PreviewTheme", $scenario.Theme,
            "-PreviewTextSize", $scenario.TextSize,
            "-AppDataRoot", $appDataRoot,
            "-DisableBackgroundDiscovery"
        )
        if ($scenario.PSObject.Properties["SideTab"] -and $scenario.SideTab) { $arguments += @("-InitialWorkspaceSideTab", [string]$scenario.SideTab) }
        $performancePath = ""
        if ($scenario.Measure) {
            $performancePath = Join-Path $outputFull "performance.json"
            $arguments += @("-PerformanceReportPath", $performancePath, "-PerformanceIterations", $Iterations)
        }
        if ($scenario.HighContrast) { $arguments += "-PreviewHighContrast" }
        $startInfo = New-Object System.Diagnostics.ProcessStartInfo
        $startInfo.FileName = $powerShellExe
        $startInfo.Arguments = [string]::Join(" ", @($arguments | ForEach-Object { Quote-WindowsArgument $_ }))
        $startInfo.WorkingDirectory = $repoRoot
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $startInfo
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        [void]$process.Start()
        if (-not $process.WaitForExit(60000)) {
            try { $process.Kill() } catch {}
            throw "UI audit timed out: $($scenario.Name)"
        }
        $watch.Stop()
        $exitCode = $process.ExitCode
        $process.Dispose()
        Assert-True ($exitCode -eq 0) "UI audit exited with code ${exitCode}: $($scenario.Name)"
        Assert-True (Test-Path -LiteralPath $snapshotPath -PathType Leaf) "UI snapshot was not created: $($scenario.Name)"
        $accessibilityPath = [System.IO.Path]::ChangeExtension($snapshotPath, ".accessibility.json")
        $runtimeLogPath = [System.IO.Path]::ChangeExtension($snapshotPath, ".runtime.log")
        $parsedAudit = [System.IO.File]::ReadAllText($accessibilityPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        $audit = @(foreach ($row in $parsedAudit) { $row })
        $missingNames = @($audit | Where-Object { $_.visible -and $_.interactive -and [string]::IsNullOrWhiteSpace([string]$_.accessibleName) })
        $clipped = @($audit | Where-Object { $_.visible -and $_.clipped })
        $image = Get-ImageSampleStats $snapshotPath
        Assert-True ($image.uniqueColors -ge 12 -and $image.nonBlankRatio -ge 0.1) "UI snapshot appears blank: $($scenario.Name)"
        Assert-True ($missingNames.Count -eq 0) "Visible interactive controls without accessible names: $($scenario.Name) count=$($missingNames.Count)"
        Assert-True ($clipped.Count -eq 0) "Visible controls extend outside a non-scroll parent: $($scenario.Name) count=$($clipped.Count)"
        $runtimeText = [System.IO.File]::ReadAllText($runtimeLogPath, [System.Text.Encoding]::UTF8)
        $loadLabel = ConvertFrom-Json '"\uAC80\uC218 \uD654\uBA74 \uB85C\uB4DC: "'
        $secondsSuffix = ConvertFrom-Json '"\uCD08"'
        $loadMatch = [regex]::Match($runtimeText, ([regex]::Escape($loadLabel) + '([0-9.,]+)' + [regex]::Escape($secondsSuffix)))
        [void]$results.Add([pscustomobject]@{
            name = $scenario.Name
            client = "$($scenario.Width)x$($scenario.Height)"
            theme = $scenario.Theme
            textSize = $scenario.TextSize
            highContrast = $scenario.HighContrast
            processElapsedMs = [Math]::Round($watch.Elapsed.TotalMilliseconds, 3)
            reviewLoadSeconds = if ($loadMatch.Success) { [double]($loadMatch.Groups[1].Value.Replace(",", "")) } else { -1 }
            visibleControls = @($audit | Where-Object { $_.visible }).Count
            missingAccessibleNames = $missingNames.Count
            clippedControls = $clipped.Count
            image = $image
        })
    }
    $performanceFile = Join-Path $outputFull "performance.json"
    $performance = if (Test-Path -LiteralPath $performanceFile -PathType Leaf) {
        [System.IO.File]::ReadAllText($performanceFile, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    } else { $null }
    Assert-True ($null -ne $performance) "UI performance report was not created."
    $expectedNeedle = [Math]::Floor(($Rows - 1) / 17) + 1
    foreach ($searchCase in @($performance.searchCases)) {
        $expectedMatches = if ([string]$searchCase.query -eq "needle") { $expectedNeedle } else { $Rows }
        Assert-True ([int]$searchCase.matches -eq $expectedMatches) "Search result count changed for '$($searchCase.query)': expected=$expectedMatches actual=$($searchCase.matches)"
    }
    $candidateCount = [Math]::Floor(($Rows - 1) / 3) + 1
    $existingCount = [Math]::Floor(($Rows - 1) / 5) + 1
    $overlapCount = [Math]::Floor(($Rows - 1) / 15) + 1
    $expectedTranslated = $candidateCount + $existingCount - $overlapCount
    $expectedPending = $Rows - $expectedTranslated
    $translatedStatus = ConvertFrom-Json '"\uBC88\uC5ED\uB428"'
    $pendingStatus = ConvertFrom-Json '"\uBBF8\uBC88\uC5ED"'
    Assert-True ([int]$performance.statusFilterMatches.PSObject.Properties[$translatedStatus].Value -eq $expectedTranslated) "Translated status filter count changed."
    Assert-True ([int]$performance.statusFilterMatches.PSObject.Properties[$pendingStatus].Value -eq $expectedPending) "Pending status filter count changed."

    $summary = [ordered]@{
        version = 1
        rows = $Rows
        iterations = $Iterations
        scenarios = $results.ToArray()
        performance = $performance
    }
    Write-Utf8Text (Join-Path $outputFull "summary.json") ($summary | ConvertTo-Json -Depth 10)
    Write-Host "UI audit output: $outputFull"
    Write-Host "RESULT scenarios=$($results.Count) rows=$Rows"
} finally {
    $workspaceFull = [System.IO.Path]::GetFullPath($workspace).TrimEnd("\", "/")
    $expectedPrefix = $tempBase + [System.IO.Path]::DirectorySeparatorChar + "RimWorldAiTranslator-tests-ui-"
    if ($workspaceFull.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $workspaceFull)) {
        Remove-Item -LiteralPath $workspaceFull -Recurse -Force -ErrorAction SilentlyContinue
    }
}
