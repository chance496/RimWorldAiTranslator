function Get-RimWorldTranslationMemorySourceKey([string]$Source) {
    if ($null -eq $Source) { return "" }
    return $Source.Replace("`r`n", "`n").Replace("`r", "`n").Trim()
}

function Get-RimWorldTranslationMemoryRank([string]$Status, [string]$Origin) {
    $statusValue = if ($Status) { $Status.ToLowerInvariant() } else { "" }
    $originValue = if ($Origin) { $Origin.ToLowerInvariant() } else { "" }
    $statusRank = switch ($statusValue) { "approved" { 400 }; "translated" { 200 }; default { 0 } }
    $originRank = switch ($originValue) { "local" { 90 }; "rmk" { 80 }; "existing" { 70 }; "mod" { 60 }; "ai" { 40 }; default { 10 } }
    return $statusRank + $originRank
}

function Select-RimWorldTranslationMemorySuggestions {
    [CmdletBinding()]
    param(
        [object[]]$Entries,
        [Parameter(Mandatory = $true)]
        [string]$Source,
        [string]$ExcludeIdentity = "",
        [ValidateRange(1, 20)]
        [int]$Maximum = 5
    )

    $sourceKey = Get-RimWorldTranslationMemorySourceKey $Source
    if ([string]::IsNullOrWhiteSpace($sourceKey)) { return @() }
    $eligible = New-Object "System.Collections.Generic.List[object]"
    foreach ($entry in @($Entries)) {
        if (-not $entry) { continue }
        $entrySource = Get-RimWorldTranslationMemorySourceKey ([string]$entry.Source)
        if (-not [string]::Equals($sourceKey, $entrySource, [System.StringComparison]::Ordinal)) { continue }
        if ($ExcludeIdentity -and [string]$entry.Identity -eq $ExcludeIdentity) { continue }
        if ($entry.PSObject.Properties["SourceChanged"] -and [bool]$entry.SourceChanged) { continue }
        if (-not $entry.PSObject.Properties["SafeToApply"] -or -not [bool]$entry.SafeToApply) { continue }
        $status = ([string]$entry.Status).ToLowerInvariant()
        if ($status -notin @("approved", "translated")) { continue }
        $translation = if ($entry.PSObject.Properties["Translation"]) { ([string]$entry.Translation).Replace("`r`n", "`n").Replace("`r", "`n").Trim() } else { "" }
        if ([string]::IsNullOrWhiteSpace($translation) -or [string]::Equals($translation, $sourceKey, [System.StringComparison]::Ordinal)) { continue }
        $updatedTicks = [long]0
        $parsed = [datetime]::MinValue
        if ($entry.PSObject.Properties["UpdatedAt"] -and [datetime]::TryParse([string]$entry.UpdatedAt, [ref]$parsed)) {
            $updatedTicks = $parsed.ToUniversalTime().Ticks
        }
        [void]$eligible.Add([pscustomobject]@{
            Text = $translation
            Origin = [string]$entry.Origin
            Status = $status
            Target = [string]$entry.Target
            Identity = [string]$entry.Identity
            UpdatedAt = [string]$entry.UpdatedAt
            Rank = Get-RimWorldTranslationMemoryRank -Status $status -Origin ([string]$entry.Origin)
            UpdatedTicks = $updatedTicks
        })
    }

    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::Ordinal)
    $result = New-Object "System.Collections.Generic.List[object]"
    foreach ($entry in @($eligible.ToArray() | Sort-Object @{ Expression = { [int]$_.Rank }; Descending = $true }, @{ Expression = { [long]$_.UpdatedTicks }; Descending = $true }, @{ Expression = { [string]$_.Identity }; Ascending = $true })) {
        if (-not $seen.Add([string]$entry.Text)) { continue }
        [void]$result.Add($entry)
        if ($result.Count -ge $Maximum) { break }
    }
    return $result.ToArray()
}
