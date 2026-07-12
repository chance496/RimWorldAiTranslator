function Get-RimWorldQualityProperty {
    param([object]$InputObject, [string[]]$Names, [object]$Default = $null)
    if ($null -eq $InputObject) { return $Default }
    foreach ($name in $Names) {
        if ($InputObject.PSObject.Properties[$name]) { return $InputObject.$name }
    }
    return $Default
}

function Get-RimWorldQualityIssues {
    param([AllowEmptyCollection()][object[]]$Entries = @())

    $issues = New-Object "System.Collections.Generic.List[object]"
    $identities = @{}
    $hasTokenValidator = $null -ne (Get-Command Test-RimWorldProtectedTokenStructure -ErrorAction SilentlyContinue)
    $index = -1
    foreach ($entry in @($Entries)) {
        $index++
        $source = [string](Get-RimWorldQualityProperty $entry @("source", "Source", "sourceText", "SourceText") "")
        $translation = [string](Get-RimWorldQualityProperty $entry @("translation", "Translation", "text", "Text") "")
        $existing = [string](Get-RimWorldQualityProperty $entry @("existing", "Existing", "defaultTranslation", "DefaultTranslation") "")
        $key = [string](Get-RimWorldQualityProperty $entry @("key", "Key") "")
        $target = [string](Get-RimWorldQualityProperty $entry @("target", "Target", "relativeTarget", "RelativeTarget") "")
        $defClass = [string](Get-RimWorldQualityProperty $entry @("defClass", "DefClass") "")
        $rowIndex = [int](Get-RimWorldQualityProperty $entry @("index", "Index") $index)
        $sourceChanged = [bool](Get-RimWorldQualityProperty $entry @("sourceChanged", "SourceChanged", "rmkSourceChanged") $false)
        $safe = [bool](Get-RimWorldQualityProperty $entry @("safeToApply", "SafeToApply", "safe", "Safe") $true)
        $tokenOrTagIssue = Get-RimWorldQualityProperty $entry @("tokenOrTagIssue", "TokenOrTagIssue") $null
        $identity = ($target.ToLowerInvariant() + "`u{1f}" + $key)
        $identityRow = [pscustomobject]@{ Index = $rowIndex; Key = $key; Target = $target; DefClass = $defClass }
        if (-not $identities.ContainsKey($identity)) {
            $identities[$identity] = $identityRow
        } elseif ($identities[$identity] -is [System.Collections.Generic.List[object]]) {
            [void]$identities[$identity].Add($identityRow)
        } else {
            $duplicates = New-Object "System.Collections.Generic.List[object]"
            [void]$duplicates.Add($identities[$identity])
            [void]$duplicates.Add($identityRow)
            $identities[$identity] = $duplicates
        }

        $base = [ordered]@{ Index = $rowIndex; Key = $key; Target = $target; DefClass = $defClass }
        if ([string]::IsNullOrWhiteSpace($translation)) {
            [void]$issues.Add([pscustomobject]($base + [ordered]@{ Category = "Missing"; Severity = "warning"; Detail = "번역문이 비어 있습니다." }))
            continue
        }
        if ($sourceChanged) {
            [void]$issues.Add([pscustomobject]($base + [ordered]@{ Category = "SourceChanged"; Severity = "warning"; Detail = "번역 이후 원문이 변경되었습니다." }))
        }
        if (-not $safe) {
            [void]$issues.Add([pscustomobject]($base + [ordered]@{ Category = "Unsafe"; Severity = "error"; Detail = "안전 검사에 통과하지 못했습니다." }))
        }
        if ($null -eq $tokenOrTagIssue -and $hasTokenValidator) {
            $tokenOrTagIssue = -not (Test-RimWorldProtectedTokenStructure -Source $source -Translation $translation)
        }
        if ([bool]$tokenOrTagIssue) {
            [void]$issues.Add([pscustomobject]($base + [ordered]@{ Category = "TokenOrTag"; Severity = "error"; Detail = "보호 토큰 또는 태그의 종류나 개수가 다릅니다." }))
        }
        if ($source.Length -ge 3 -and [string]::Equals($source.Trim(), $translation.Trim(), [System.StringComparison]::Ordinal)) {
            [void]$issues.Add([pscustomobject]($base + [ordered]@{ Category = "SameAsSource"; Severity = "info"; Detail = "번역문이 원문과 같습니다." }))
        }
        if ($source.Length -ge 20 -and $translation.Length -ge 1) {
            $ratio = $translation.Length / [double]$source.Length
            if ($ratio -lt 0.18) {
                [void]$issues.Add([pscustomobject]($base + [ordered]@{ Category = "TooShort"; Severity = "warning"; Detail = "번역문이 원문에 비해 매우 짧습니다." }))
            } elseif ($ratio -gt 4.0) {
                [void]$issues.Add([pscustomobject]($base + [ordered]@{ Category = "TooLong"; Severity = "warning"; Detail = "번역문이 원문에 비해 매우 깁니다." }))
            }
        }
        if ($existing -and -not [string]::Equals($existing, $translation, [System.StringComparison]::Ordinal)) {
            [void]$issues.Add([pscustomobject]($base + [ordered]@{ Category = "ExistingChanged"; Severity = "info"; Detail = "기존 번역과 현재 번역이 다릅니다." }))
        }
    }

    foreach ($pair in $identities.GetEnumerator()) {
        if ($pair.Value -isnot [System.Collections.Generic.List[object]]) { continue }
        foreach ($duplicate in $pair.Value.ToArray()) {
            [void]$issues.Add([pscustomobject]@{
                Index = [int]$duplicate.Index; Key = [string]$duplicate.Key; Target = [string]$duplicate.Target; DefClass = [string]$duplicate.DefClass
                Category = "DuplicateIdentity"; Severity = "error"; Detail = "같은 대상 파일과 키가 둘 이상 존재합니다."
            })
        }
    }
    return $issues.ToArray()
}

function New-RimWorldQualityReportModel {
    param(
        [AllowEmptyCollection()][object[]]$Entries = @(),
        [AllowEmptyCollection()][object[]]$Issues = @(),
        [datetime]$GeneratedAt = [DateTime]::UtcNow
    )
    $statuses = [ordered]@{}
    foreach ($entry in @($Entries)) {
        $status = [string](Get-RimWorldQualityProperty $entry @("status", "Status") "unknown")
        if ([string]::IsNullOrWhiteSpace($status)) { $status = "unknown" }
        if (-not $statuses.Contains($status)) { $statuses[$status] = 0 }
        $statuses[$status]++
    }
    $categories = [ordered]@{}
    $severities = [ordered]@{}
    foreach ($issue in @($Issues)) {
        $category = [string]$issue.Category
        $severity = [string]$issue.Severity
        if (-not $categories.Contains($category)) { $categories[$category] = 0 }
        if (-not $severities.Contains($severity)) { $severities[$severity] = 0 }
        $categories[$category]++
        $severities[$severity]++
    }
    return [pscustomobject]@{
        Product = "RimWorld AI Translator"
        ReportVersion = 1
        GeneratedUtc = $GeneratedAt.ToUniversalTime().ToString("o")
        Privacy = [pscustomobject]@{
            IncludesSourceText = $false
            IncludesTranslationText = $false
            IncludesApiKeys = $false
            IncludesAbsolutePaths = $false
        }
        Totals = [pscustomobject]@{ Entries = @($Entries).Count; Issues = @($Issues).Count }
        Statuses = [pscustomobject]$statuses
        IssueCategories = [pscustomobject]$categories
        Severities = [pscustomobject]$severities
    }
}

function ConvertTo-RimWorldQualityHtml([object]$Model) {
    $encode = { param([object]$Value) [System.Net.WebUtility]::HtmlEncode([string]$Value) }
    $statusRows = New-Object System.Text.StringBuilder
    foreach ($property in @($Model.Statuses.PSObject.Properties | Sort-Object Name)) {
        [void]$statusRows.Append("<tr><td>$(& $encode $property.Name)</td><td>$([int]$property.Value)</td></tr>")
    }
    $issueRows = New-Object System.Text.StringBuilder
    foreach ($property in @($Model.IssueCategories.PSObject.Properties | Sort-Object Name)) {
        [void]$issueRows.Append("<tr><td>$(& $encode $property.Name)</td><td>$([int]$property.Value)</td></tr>")
    }
    return @"
<!doctype html>
<html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>번역 품질 보고서</title><style>
body{font-family:"Malgun Gothic",sans-serif;margin:0;background:#efeee8;color:#20251f}main{max-width:920px;margin:40px auto;padding:0 24px}
h1{font-size:26px;margin:0 0 8px}.meta{color:#636c65;margin-bottom:24px}.summary{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px;margin-bottom:24px}
.metric{background:#fff;border:1px solid #b7bdb6;border-top:3px solid #b78342;padding:18px}.metric strong{display:block;font-size:28px;margin-top:8px}
section{background:#faf9f4;border:1px solid #d4d8d2;margin:12px 0;padding:18px}table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:9px;border-bottom:1px solid #d4d8d2}
.privacy{font-size:13px;color:#636c65}@media(max-width:640px){.summary{grid-template-columns:1fr}main{margin:20px auto}}
</style></head><body><main><h1>번역 품질 보고서</h1><div class="meta">생성 시각: $(& $encode $Model.GeneratedUtc)</div>
<div class="summary"><div class="metric">전체 문자열<strong>$([int]$Model.Totals.Entries)</strong></div><div class="metric">검사 항목<strong>$([int]$Model.Totals.Issues)</strong></div></div>
<section><h2>번역 상태</h2><table><thead><tr><th>상태</th><th>개수</th></tr></thead><tbody>$($statusRows.ToString())</tbody></table></section>
<section><h2>품질 검사</h2><table><thead><tr><th>분류</th><th>개수</th></tr></thead><tbody>$($issueRows.ToString())</tbody></table></section>
<p class="privacy">이 보고서는 집계 수치만 포함합니다. 원문, 번역문, API 키, 절대 경로는 포함하지 않습니다.</p>
</main></body></html>
"@
}

function Export-RimWorldQualityReport {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [AllowEmptyCollection()][object[]]$Entries = @(),
        [AllowEmptyCollection()][object[]]$Issues = @()
    )
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if ([System.IO.Path]::GetExtension($fullPath) -ne ".html") { throw "Quality reports must use the .html extension." }
    $directory = [System.IO.Path]::GetDirectoryName($fullPath)
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { [System.IO.Directory]::CreateDirectory($directory) | Out-Null }
    $model = New-RimWorldQualityReportModel -Entries $Entries -Issues $Issues
    $html = ConvertTo-RimWorldQualityHtml $model
    $tempPath = Join-Path $directory (".{0}.{1}.tmp" -f [System.IO.Path]::GetFileName($fullPath), [Guid]::NewGuid().ToString("N"))
    $backupPath = $fullPath + ".bak"
    try {
        $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($html)
        $stream = [System.IO.FileStream]::new($tempPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            [System.IO.File]::Replace($tempPath, $fullPath, $backupPath, $true)
        } else {
            [System.IO.File]::Move($tempPath, $fullPath)
        }
    } finally {
        if (Test-Path -LiteralPath $tempPath -PathType Leaf) { Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue }
    }
    return [pscustomobject]@{ Path = $fullPath; BackupPath = if (Test-Path -LiteralPath $backupPath) { $backupPath } else { "" }; Model = $model }
}
