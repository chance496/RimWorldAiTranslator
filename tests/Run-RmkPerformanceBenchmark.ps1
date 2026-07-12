[CmdletBinding()]
param(
    [ValidateRange(100, 20000)]
    [int]$Rows = 5000,
    [ValidateRange(1, 20)]
    [int]$Iterations = 3,
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$powerShellExe = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
$tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd("\", "/")
$workspace = Join-Path $tempBase ("RimWorldAiTranslator-tests-rmk-perf-" + [Guid]::NewGuid().ToString("N"))

function Write-Utf8Text([string]$Path, [string]$Text) {
    $parent = Split-Path -Parent $Path
    if ($parent -and -not (Test-Path -LiteralPath $parent -PathType Container)) {
        [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    }
    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($false))
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

function Invoke-MeasuredProcess([string[]]$Arguments) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $powerShellExe
    $startInfo.WorkingDirectory = $repoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Arguments = [string]::Join(" ", @($Arguments | ForEach-Object { Quote-WindowsArgument $_ }))
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        [void]$process.Start()
        $peakBytes = [long]0
        while (-not $process.WaitForExit(50)) {
            try {
                $process.Refresh()
                $peakBytes = [Math]::Max($peakBytes, [long]$process.WorkingSet64)
            } catch {}
        }
        try {
            $process.Refresh()
            $peakBytes = [Math]::Max($peakBytes, [long]$process.PeakWorkingSet64)
        } catch {}
        $standardOutput = $process.StandardOutput.ReadToEnd()
        $standardError = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        $watch.Stop()
        $peakMb = [Math]::Round($peakBytes / 1MB, 2)
        if ($process.ExitCode -ne 0) {
            throw "RMK export benchmark failed with exit code $($process.ExitCode). $standardOutput $standardError"
        }
        return [pscustomobject]@{
            elapsedMs = [Math]::Round($watch.Elapsed.TotalMilliseconds, 3)
            peakWorkingSetMb = $peakMb
        }
    } finally {
        $process.Dispose()
    }
}

function Get-Statistics([double[]]$Values) {
    $ordered = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($ordered.Count / 2)
    $median = if (($ordered.Count % 2) -eq 0) { ($ordered[$middle - 1] + $ordered[$middle]) / 2.0 } else { $ordered[$middle] }
    return [ordered]@{
        medianMs = [Math]::Round($median, 3)
        maxMs = [Math]::Round([double]$ordered[-1], 3)
        samples = @($Values | ForEach-Object { [Math]::Round($_, 3) })
    }
}

try {
    $reviewRoot = Join-Path $workspace "review"
    $auditRoot = Join-Path $reviewRoot "_TranslationAudit"
    $target = Join-Path $reviewRoot "Languages\Korean\Keyed\Synthetic.xml"
    $rmkRoot = Join-Path $workspace "rmk-entry"
    $workbookPath = Join-Path $rmkRoot "history.xlsx"
    [System.IO.Directory]::CreateDirectory($auditRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($rmkRoot) | Out-Null

    $korean = ConvertFrom-Json '"\uD569\uC131 \uBC88\uC5ED"'
    $rowsList = New-Object "System.Collections.Generic.List[object]" $Rows
    $decisionList = New-Object "System.Collections.Generic.List[object]" $Rows
    for ($index = 0; $index -lt $Rows; $index++) {
        $id = "E{0:d6}" -f ($index + 1)
        $key = "Synthetic.Entry.$index"
        $source = "Synthetic source $index"
        $translation = "$korean $index"
        [void]$rowsList.Add([pscustomobject]@{
            id = $id
            key = $key
            kind = "Keyed"
            target = $target
            source = $source
            existing = ""
            candidate = $translation
        })
        [void]$decisionList.Add([pscustomobject]@{
            id = $id
            key = $key
            target = "Keyed\Synthetic.xml"
            status = "approved"
            text = $translation
            sourceText = $source
            sourceChanged = $false
        })
    }
    Write-Utf8Text (Join-Path $auditRoot "synthetic-comparison.json") ($rowsList.ToArray() | ConvertTo-Json -Depth 5 -Compress)
    $decisionPayload = [ordered]@{
        version = 5
        sparse = $true
        reviewRoot = $reviewRoot
        comparison = (Join-Path $auditRoot "synthetic-comparison.json")
        updatedAt = [DateTime]::UtcNow.ToString("o")
        items = $decisionList.ToArray()
    }
    Write-Utf8Text (Join-Path $reviewRoot "review-decisions.json") ($decisionPayload | ConvertTo-Json -Depth 6 -Compress)

    $arguments = @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $repoRoot "Export-RimWorldAiReviewToRmk.ps1"),
        "-RmkEntryRoot", $rmkRoot,
        "-ReviewRoot", $reviewRoot,
        "-RmkLanguageFolderName", "Korean",
        "-WorkbookPath", $workbookPath,
        "-SourceLanguage", "English",
        "-Overwrite",
        "-ApplyStatus", "ApprovedOnly"
    )

    $create = Invoke-MeasuredProcess $arguments
    $updateTimes = New-Object "System.Collections.Generic.List[double]"
    $peakMemory = [double]$create.peakWorkingSetMb
    for ($iteration = 0; $iteration -lt $Iterations; $iteration++) {
        $measurement = Invoke-MeasuredProcess $arguments
        [void]$updateTimes.Add([double]$measurement.elapsedMs)
        $peakMemory = [Math]::Max($peakMemory, [double]$measurement.peakWorkingSetMb)
    }

    if (-not (Test-Path -LiteralPath $workbookPath -PathType Leaf)) { throw "RMK workbook was not created." }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($workbookPath)
    try {
        if (-not $archive.GetEntry("xl/worksheets/sheet1.xml")) { throw "RMK workbook worksheet is missing." }
    } finally {
        $archive.Dispose()
    }

    $result = [ordered]@{
        version = 1
        measuredAt = [DateTime]::UtcNow.ToString("o")
        rows = $Rows
        updateIterations = $Iterations
        create = $create
        update = Get-Statistics -Values ($updateTimes.ToArray())
        peakWorkingSetMb = [Math]::Round($peakMemory, 2)
        workbookBytes = (Get-Item -LiteralPath $workbookPath).Length
    }
    $json = $result | ConvertTo-Json -Depth 6 -Compress
    if ($OutputPath) { Write-Utf8Text ([System.IO.Path]::GetFullPath($OutputPath)) $json }
    Write-Output $json
} finally {
    $workspaceFull = [System.IO.Path]::GetFullPath($workspace).TrimEnd("\", "/")
    $expectedPrefix = $tempBase + [System.IO.Path]::DirectorySeparatorChar + "RimWorldAiTranslator-tests-rmk-perf-"
    if ($workspaceFull.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $workspaceFull -PathType Container)) {
        Remove-Item -LiteralPath $workspaceFull -Recurse -Force -ErrorAction SilentlyContinue
    }
}
