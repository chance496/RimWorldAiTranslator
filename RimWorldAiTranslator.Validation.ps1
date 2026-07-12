function Get-RimWorldProtectedTokenCounts([string]$Text) {
    $counts = New-Object "System.Collections.Generic.Dictionary[string,int]" ([System.StringComparer]::Ordinal)
    $pattern = '(\\r\\n|\\[nrt]|\{[^}\r\n]+\}|\[[A-Za-z0-9_.:;''" -]+\]|</?[A-Za-z][^>\r\n]*>|\$[A-Za-z_][A-Za-z0-9_]*\$?|%[0-9.]*[sdif]|\b[A-Z]{2,}_[A-Z0-9_]+\b|\b[A-Za-z][A-Za-z0-9_]*->)'
    foreach ($match in [System.Text.RegularExpressions.Regex]::Matches([string]$Text, $pattern)) {
        $token = [string]$match.Value
        if ($counts.ContainsKey($token)) { $counts[$token]++ } else { $counts[$token] = 1 }
    }
    return $counts
}

function Get-RimWorldTokenPreservationIssues([string]$Source, [string]$Target) {
    $sourceCounts = Get-RimWorldProtectedTokenCounts $Source
    $targetCounts = Get-RimWorldProtectedTokenCounts $Target
    $missing = New-Object "System.Collections.Generic.List[string]"
    $unexpected = New-Object "System.Collections.Generic.List[string]"
    $countMismatches = New-Object "System.Collections.Generic.List[string]"
    foreach ($token in $sourceCounts.Keys) {
        $sourceCount = [int]$sourceCounts[$token]
        $targetCount = if ($targetCounts.ContainsKey($token)) { [int]$targetCounts[$token] } else { 0 }
        if ($targetCount -lt $sourceCount) { [void]$missing.Add($token) }
        if ($targetCount -ne $sourceCount) { [void]$countMismatches.Add("$token ($sourceCount->$targetCount)") }
    }
    foreach ($token in $targetCounts.Keys) {
        $targetCount = [int]$targetCounts[$token]
        $sourceCount = if ($sourceCounts.ContainsKey($token)) { [int]$sourceCounts[$token] } else { 0 }
        if ($targetCount -gt $sourceCount) { [void]$unexpected.Add($token) }
    }
    $grammarPrefix = [System.Text.RegularExpressions.Regex]::Match([string]$Source, '^\s*([A-Za-z][A-Za-z0-9_]*->)')
    $grammarPrefixMoved = $false
    if ($grammarPrefix.Success -and -not [System.Text.RegularExpressions.Regex]::IsMatch([string]$Target, ('^\s*' + [regex]::Escape($grammarPrefix.Groups[1].Value)))) {
        $grammarPrefixMoved = $true
        if (-not $missing.Contains($grammarPrefix.Groups[1].Value)) { [void]$missing.Add($grammarPrefix.Groups[1].Value) }
    }
    return [pscustomobject]@{
        MissingTokens = $missing.ToArray()
        UnexpectedTokens = $unexpected.ToArray()
        TokenCountMismatches = $countMismatches.ToArray()
        GrammarPrefixMoved = $grammarPrefixMoved
    }
}

function Test-RimWorldProtectedTokenStructure([string]$Source, [string]$Translation) {
    $issues = Get-RimWorldTokenPreservationIssues -Source $Source -Target $Translation
    return @($issues.MissingTokens).Count -eq 0 -and
        @($issues.UnexpectedTokens).Count -eq 0 -and
        @($issues.TokenCountMismatches).Count -eq 0 -and
        -not [bool]$issues.GrammarPrefixMoved
}

function Get-RimWorldInvalidKoreanParticleNotations([string]$Text) {
    $result = New-Object "System.Collections.Generic.List[string]"
    if ([string]::IsNullOrWhiteSpace($Text)) { return $result.ToArray() }
    $seen = New-Object "System.Collections.Generic.HashSet[string]" ([System.StringComparer]::Ordinal)
    $reversed = '(\uC740\(\uB294\)|\uB294\(\uC740\)|\uC774\(\uAC00\)|\uAC00\(\uC774\)|\uC744\(\uB97C\)|\uB97C\(\uC744\)|\uACFC\(\uC640\)|\uC640\(\uACFC\)|\uC73C\uB85C\(\uB85C\)|\uB85C\(\uC73C\uB85C\))'
    $placeholder = '(?:\[[^\]\r\n]+\]|\{[^}\r\n]+\}|\$[A-Za-z_][A-Za-z0-9_]*\$?)'
    $bareParticle = '(?:\uC73C\uB85C|\uC740|\uB294|\uC774|\uAC00|\uC744|\uB97C|\uACFC|\uC640|\uB85C)(?=$|[\s.,!?\u2026:;\uFF0C\u3002\uFF01\uFF1F\u3001])'
    $pattern = "$reversed|$placeholder$bareParticle"
    foreach ($match in [System.Text.RegularExpressions.Regex]::Matches($Text, $pattern)) {
        if ($seen.Add($match.Value)) { [void]$result.Add($match.Value) }
    }
    return $result.ToArray()
}

function Test-RimWorldPathologicalTranslation([string]$Text) {
    if ([string]::IsNullOrEmpty($Text)) { return $false }
    if ($Text -match "(\r?\n\s*){8,}" -or $Text -match "(\\u000a\s*){8,}") { return $true }
    $newlineCount = [System.Text.RegularExpressions.Regex]::Matches($Text, "\r?\n").Count
    return $newlineCount -ge 20 -and $Text.Length -lt 4000
}
