$ErrorActionPreference = "Stop"

function Test-RimWorldPathStrictlyInsideRoot([string]$Path, [string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($Root)) { return $false }
    try {
        $pathFull = [System.IO.Path]::GetFullPath($Path).TrimEnd("\", "/")
        $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd("\", "/")
        return $pathFull.StartsWith($rootFull + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)
    } catch {
        return $false
    }
}

function Test-RimWorldPathContainsReparsePoint([string]$Path, [string]$StopRoot) {
    try {
        $current = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
        $stopFull = [System.IO.Path]::GetFullPath($StopRoot).TrimEnd("\", "/")
        while ($current) {
            if (($current.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { return $true }
            $currentFull = [System.IO.Path]::GetFullPath($current.FullName).TrimEnd("\", "/")
            if ($currentFull.Equals($stopFull, [System.StringComparison]::OrdinalIgnoreCase)) { break }
            $parentPath = Split-Path -Parent $current.FullName
            if (-not $parentPath) { break }
            $current = Get-Item -LiteralPath $parentPath -Force -ErrorAction Stop
        }
    } catch {
        return $true
    }
    return $false
}

function Get-RimWorldAppOwnedReviewDirectory([string]$Path, [string[]]$ReviewRoots, [string]$ModRoot = "") {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Container)) { return "" }
    try { $full = [System.IO.Path]::GetFullPath($Path).TrimEnd("\", "/") } catch { return "" }
    if ($ModRoot) {
        try { $modFull = [System.IO.Path]::GetFullPath($ModRoot).TrimEnd("\", "/") } catch { return "" }
        if ($full.Equals($modFull, [System.StringComparison]::OrdinalIgnoreCase) -or
            (Test-RimWorldPathStrictlyInsideRoot -Path $full -Root $modFull)) { return "" }
    }
    foreach ($root in @($ReviewRoots)) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        try { $rootFull = [System.IO.Path]::GetFullPath($root).TrimEnd("\", "/") } catch { continue }
        if (-not (Test-RimWorldPathStrictlyInsideRoot -Path $full -Root $rootFull)) { continue }
        if (Test-RimWorldPathContainsReparsePoint -Path $full -StopRoot $rootFull) { return "" }
        return $full
    }
    return ""
}

function Get-RimWorldProjectCleanupPlan([object]$Project, [string[]]$ReviewRoots) {
    $safePaths = New-Object "System.Collections.Generic.List[string]"
    $unsafePaths = New-Object "System.Collections.Generic.List[string]"
    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    $recorded = New-Object "System.Collections.Generic.List[string]"
    if ($Project -and $Project.latestReviewRoot) { [void]$recorded.Add([string]$Project.latestReviewRoot) }
    foreach ($run in @($Project.runs)) {
        if ($run -and $run.reviewRoot) { [void]$recorded.Add([string]$run.reviewRoot) }
    }
    $modRoot = if ($Project -and $Project.modRoot) { [string]$Project.modRoot } else { "" }
    foreach ($path in $recorded) {
        if (-not (Test-Path -LiteralPath $path -PathType Container)) { continue }
        try { $full = [System.IO.Path]::GetFullPath($path).TrimEnd("\", "/") } catch { [void]$unsafePaths.Add($path); continue }
        if (-not $seen.Add($full)) { continue }
        $safe = Get-RimWorldAppOwnedReviewDirectory -Path $full -ReviewRoots $ReviewRoots -ModRoot $modRoot
        if ($safe) { [void]$safePaths.Add($safe) } else { [void]$unsafePaths.Add($full) }
    }
    foreach ($root in @($ReviewRoots)) {
        if ([string]::IsNullOrWhiteSpace($root) -or -not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        foreach ($directory in Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue) {
            $markerPath = Join-Path $directory.FullName ".rimworld-ai-project.json"
            if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) { continue }
            try {
                $marker = [System.IO.File]::ReadAllText($markerPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
                if ([string]$marker.projectId -ne [string]$Project.id) { continue }
                $safe = Get-RimWorldAppOwnedReviewDirectory -Path $directory.FullName -ReviewRoots $ReviewRoots -ModRoot $modRoot
                if ($safe -and $seen.Add($safe)) { [void]$safePaths.Add($safe) }
            } catch {
            }
        }
    }
    return [pscustomobject]@{ SafePaths = $safePaths.ToArray(); UnsafePaths = $unsafePaths.ToArray() }
}

function Remove-RimWorldAppOwnedReviewDirectories([object]$Project, [string[]]$ReviewRoots, [string[]]$Paths) {
    $failures = New-Object "System.Collections.Generic.List[string]"
    $modRoot = if ($Project -and $Project.modRoot) { [string]$Project.modRoot } else { "" }
    foreach ($path in @($Paths)) {
        $verified = Get-RimWorldAppOwnedReviewDirectory -Path $path -ReviewRoots $ReviewRoots -ModRoot $modRoot
        if (-not $verified) { [void]$failures.Add("Safety boundary check failed: $path"); continue }
        try { Remove-Item -LiteralPath $verified -Recurse -Force -ErrorAction Stop } catch { [void]$failures.Add("$verified : $($_.Exception.Message)") }
    }
    return $failures.ToArray()
}
