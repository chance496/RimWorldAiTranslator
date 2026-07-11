function Write-Utf8JsonFile([string]$Path, [object]$Value, [int]$Depth = 8) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if ($script:unreadableJsonStorePaths -and $script:unreadableJsonStorePaths.Contains($fullPath)) {
        throw "Refusing to overwrite a JSON store that could not be read: $fullPath"
    }
    $json = ConvertTo-Json -InputObject $Value -Depth $Depth -Compress
    $directory = [System.IO.Path]::GetDirectoryName($fullPath)
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    $tempPath = Join-Path $directory (".{0}.{1}.tmp" -f [System.IO.Path]::GetFileName($fullPath), [System.Guid]::NewGuid().ToString("N"))
    $backupPath = "$fullPath.bak"
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json)
    try {
        $stream = [System.IO.FileStream]::new($tempPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        } finally {
            $stream.Dispose()
        }

        if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            [System.IO.File]::Replace($tempPath, $fullPath, $backupPath, $true)
        } else {
            [System.IO.File]::Move($tempPath, $fullPath)
        }
    } finally {
        if (Test-Path -LiteralPath $tempPath -PathType Leaf) {
            Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Block-Utf8JsonStoreWrites([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $script:unreadableJsonStorePaths) {
        $script:unreadableJsonStorePaths = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::OrdinalIgnoreCase)
    }
    [void]$script:unreadableJsonStorePaths.Add($fullPath)
}

function Unblock-Utf8JsonStoreWrites([string]$Path) {
    if (-not $script:unreadableJsonStorePaths) { return }
    [void]$script:unreadableJsonStorePaths.Remove([System.IO.Path]::GetFullPath($Path))
}

function Test-Utf8JsonStoreExists([string]$Path) {
    return (Test-Path -LiteralPath $Path -PathType Leaf) -or (Test-Path -LiteralPath "$Path.bak" -PathType Leaf)
}

function Restore-Utf8JsonStoreFromBackup([string]$Path, [string]$BackupPath) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullBackupPath = [System.IO.Path]::GetFullPath($BackupPath)
    $directory = [System.IO.Path]::GetDirectoryName($fullPath)
    $tempPath = Join-Path $directory (".{0}.{1}.restore.tmp" -f [System.IO.Path]::GetFileName($fullPath), [System.Guid]::NewGuid().ToString("N"))
    $corruptPath = ""
    try {
        $bytes = [System.IO.File]::ReadAllBytes($fullBackupPath)
        $stream = [System.IO.FileStream]::new($tempPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        } finally {
            $stream.Dispose()
        }

        if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            $stamp = [DateTime]::Now.ToString("yyyyMMdd-HHmmss")
            $corruptPath = "$fullPath.corrupt-$stamp-$([System.Guid]::NewGuid().ToString('N').Substring(0, 8))"
            [System.IO.File]::Replace($tempPath, $fullPath, $corruptPath, $true)
        } else {
            [System.IO.File]::Move($tempPath, $fullPath)
        }
        return $corruptPath
    } finally {
        if (Test-Path -LiteralPath $tempPath -PathType Leaf) {
            Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Read-Utf8JsonFile([string]$Path, [switch]$AllowMissing) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $candidates = @($fullPath, "$fullPath.bak")
    $errors = New-Object "System.Collections.Generic.List[string]"
    $found = $false
    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $found = $true
        try {
            $raw = [System.IO.File]::ReadAllText($candidate, [System.Text.Encoding]::UTF8)
            if ([string]::IsNullOrWhiteSpace($raw)) { throw "JSON file is empty." }
            $value = $raw | ConvertFrom-Json -ErrorAction Stop
            if ($candidate -ne $fullPath) {
                $corruptPath = Restore-Utf8JsonStoreFromBackup -Path $fullPath -BackupPath $candidate
                if (-not $script:jsonRecoveryNotices) {
                    $script:jsonRecoveryNotices = New-Object "System.Collections.Generic.List[string]"
                }
                $notice = "손상된 상태 파일을 백업에서 복구했습니다: $fullPath"
                if ($corruptPath) { $notice += " (손상 파일 보존: $corruptPath)" }
                [void]$script:jsonRecoveryNotices.Add($notice)
            }
            Unblock-Utf8JsonStoreWrites $fullPath
            return $value
        } catch {
            [void]$errors.Add("$candidate : $($_.Exception.Message)")
        }
    }
    if ($AllowMissing -and -not $found) { return $null }
    if (-not $found) { throw "JSON file was not found: $fullPath" }
    Block-Utf8JsonStoreWrites $fullPath
    throw "JSON file and its backup could not be read. $([string]::Join(' | ', $errors))"
}

function Read-RimWorldProjectStore([string]$Path) {
    $json = Read-Utf8JsonFile $Path
    if (-not $json -or -not $json.PSObject.Properties["projects"]) {
        Block-Utf8JsonStoreWrites $Path
        throw "Project store is missing the projects collection."
    }
    if ($json.PSObject.Properties["version"] -and [int]$json.version -notin @(1, 2)) {
        Block-Utf8JsonStoreWrites $Path
        throw "Unsupported project store version: $($json.version)"
    }
    Unblock-Utf8JsonStoreWrites $Path
    return @($json.projects)
}
