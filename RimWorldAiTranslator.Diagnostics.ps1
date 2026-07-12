function Get-RimWorldDiagnosticSha256([string]$Text) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes([string]$Text)
        return ([BitConverter]::ToString($sha.ComputeHash($bytes)).Replace("-", "").ToLowerInvariant())
    } finally {
        $sha.Dispose()
    }
}

function Get-RimWorldDiagnosticKnownValue([string]$Value, [string[]]$Allowed, [string]$Fallback = "other") {
    foreach ($candidate in @($Allowed)) {
        if ([string]::Equals([string]$Value, [string]$candidate, [System.StringComparison]::OrdinalIgnoreCase)) {
            return [string]$candidate
        }
    }
    if ([string]::IsNullOrWhiteSpace($Value)) { return "unspecified" }
    return $Fallback
}

function Get-RimWorldDiagnosticLanguageCategory([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return "unspecified" }
    if ($Value -match '(?i)^english') { return "english" }
    if ($Value -match '(?i)^(chinese|simplifiedchinese|traditionalchinese)') { return "chinese" }
    if ($Value -match '(?i)^japanese') { return "japanese" }
    return "other"
}

function Get-RimWorldDiagnosticErrorSummary([string[]]$Lines) {
    $counts = [ordered]@{
        rateLimit = 0
        timeout = 0
        network = 0
        json = 0
        xml = 0
        xlsx = 0
        accessDenied = 0
        path = 0
        cancellation = 0
        processExit = 0
        otherError = 0
        linesExamined = 0
        linesOmitted = 0
    }
    $lineValues = @($Lines)
    foreach ($lineValue in @($lineValues | Select-Object -First 20000)) {
        $line = [string]$lineValue
        if ($line.Length -gt 8192) { $line = $line.Substring(0, 8192) }
        $counts.linesExamined++
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -match '(?i)(\b429\b|rate.?limit|too many requests)') { $counts.rateLimit++; continue }
        if ($line -match '(?i)(timeout|timed out|\uC2DC\uAC04\s*\uCD08\uACFC)') { $counts.timeout++; continue }
        if ($line -match '(?i)(network|connection|dns|socket|http request|\uB124\uD2B8\uC6CC\uD06C|\uC5F0\uACB0\s*\uC2E4\uD328)') { $counts.network++; continue }
        if ($line -match '(?i)(json|schema|deserialize|\uC9C1\uB82C\uD654)') { $counts.json++; continue }
        if ($line -match '(?i)(xlsx|workbook|spreadsheet)') { $counts.xlsx++; continue }
        if ($line -match '(?i)(xml|languageData)') { $counts.xml++; continue }
        if ($line -match '(?i)(access.*denied|unauthorizedaccess|\uAD8C\uD55C|\uC561\uC138\uC2A4.*\uAC70\uBD80)') { $counts.accessDenied++; continue }
        if ($line -match '(?i)(path|directory|file not found|\uACBD\uB85C|\uD3F4\uB354|\uD30C\uC77C\uC744\s*\uCC3E)') { $counts.path++; continue }
        if ($line -match '(?i)(cancel|cancell|\uCDE8\uC18C|\uC911\uC9C0\s*\uC694\uCCAD)') { $counts.cancellation++; continue }
        if ($line -match '(?i)(exit.?code|process.*exit|\uD504\uB85C\uC138\uC2A4\s*\uC885\uB8CC|\uC885\uB8CC\s*\uCF54\uB4DC)') { $counts.processExit++; continue }
        if ($line -match '(?i)(error|failed|failure|exception|\uC624\uB958|\uC2E4\uD328)') { $counts.otherError++ }
    }
    $counts.linesOmitted = [Math]::Max(0, $lineValues.Count - [int]$counts.linesExamined)
    return [pscustomobject]$counts
}

function Read-RimWorldDiagnosticJson([string]$Path, [hashtable]$ReadErrors) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try {
        $info = Get-Item -LiteralPath $Path -ErrorAction Stop
        if ($info.Length -gt 16777216) { throw "JSON file exceeds the diagnostic read limit." }
        return [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    } catch {
        $ReadErrors[[System.IO.Path]::GetFileName($Path)] = $_.Exception.GetType().Name
        return $null
    }
}

function Get-RimWorldDiagnosticUrlSummary([string]$Url) {
    $uri = $null
    if ([string]::IsNullOrWhiteSpace($Url) -or -not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$uri)) {
        return [pscustomobject]@{ configured = -not [string]::IsNullOrWhiteSpace($Url); valid = $false; scheme = ""; loopback = $false; hasQuery = $false }
    }
    return [pscustomobject]@{ configured = $true; valid = $true; scheme = [string]$uri.Scheme; loopback = [bool]$uri.IsLoopback; hasQuery = -not [string]::IsNullOrWhiteSpace($uri.Query) }
}

function New-RimWorldDiagnosticBundle {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$OutputPath,
        [string]$AppDataRoot = (Join-Path $env:LOCALAPPDATA "RimWorldAiTranslator"),
        [string]$ProductRoot = $PSScriptRoot,
        [string[]]$RuntimeLogLines = @(),
        [switch]$Force
    )

    $outputFull = [System.IO.Path]::GetFullPath($OutputPath)
    if ([System.IO.Path]::GetExtension($outputFull) -ine ".zip") { throw "Diagnostic output must use the .zip extension." }
    $outputParent = Split-Path -Parent $outputFull
    if ([string]::IsNullOrWhiteSpace($outputParent) -or -not (Test-Path -LiteralPath $outputParent -PathType Container)) {
        throw "Diagnostic output directory does not exist."
    }
    if ((Test-Path -LiteralPath $outputFull) -and -not $Force) { throw "Diagnostic output already exists." }

    $tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd("\", "/")
    $workspace = Join-Path $tempBase ("RimWorldAiTranslator-diagnostics-" + [Guid]::NewGuid().ToString("N"))
    $workspaceFull = [System.IO.Path]::GetFullPath($workspace).TrimEnd("\", "/")
    $expectedPrefix = $tempBase + [System.IO.Path]::DirectorySeparatorChar + "RimWorldAiTranslator-diagnostics-"
    if (-not $workspaceFull.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Diagnostic workspace escaped the temp directory." }
    [System.IO.Directory]::CreateDirectory($workspaceFull) | Out-Null

    $temporaryZip = Join-Path $outputParent (([System.IO.Path]::GetFileName($outputFull)) + ".tmp-" + [Guid]::NewGuid().ToString("N"))
    try {
        $readErrors = @{}
        $appDataFull = [System.IO.Path]::GetFullPath($AppDataRoot)
        $settings = Read-RimWorldDiagnosticJson -Path (Join-Path $appDataFull "settings.json") -ReadErrors $readErrors
        $projects = Read-RimWorldDiagnosticJson -Path (Join-Path $appDataFull "projects.json") -ReadErrors $readErrors

        $providerSummaries = New-Object "System.Collections.Generic.List[object]"
        if ($settings -and $settings.PSObject.Properties["apiProviders"] -and $settings.apiProviders) {
            foreach ($property in @($settings.apiProviders.PSObject.Properties | Sort-Object Name)) {
                $provider = $property.Value
                $model = if ($provider.PSObject.Properties["model"]) { [string]$provider.model } else { "" }
                [void]$providerSummaries.Add([pscustomobject]@{
                    id = Get-RimWorldDiagnosticKnownValue -Value ([string]$property.Name) -Allowed @("Cerebras", "OpenAI", "Gemini", "DeepSeek", "Qwen", "Groq", "Mistral", "OpenRouter", "ZAI", "Custom", "Google")
                    url = Get-RimWorldDiagnosticUrlSummary $(if ($provider.PSObject.Properties["url"]) { [string]$provider.url } else { "" })
                    modelConfigured = -not [string]::IsNullOrWhiteSpace($model)
                    modelHash12 = if ($model) { (Get-RimWorldDiagnosticSha256 $model).Substring(0, 12) } else { "" }
                    temperature = if ($provider.PSObject.Properties["temperature"]) { [double]$provider.temperature } else { $null }
                })
            }
        }
        $settingsSummary = [ordered]@{
            present = $null -ne $settings
            schemaVersion = if ($settings -and $settings.PSObject.Properties["version"]) { [int]$settings.version } else { 0 }
            themeMode = if ($settings) { Get-RimWorldDiagnosticKnownValue -Value ([string]$settings.themeMode) -Allowed @("System", "Light", "Dark") } else { "unspecified" }
            textSize = if ($settings -and $settings.PSObject.Properties["textSize"]) { [int]$settings.textSize } else { 0 }
            highContrast = if ($settings -and $settings.PSObject.Properties["highContrast"]) { [bool]$settings.highContrast } else { $false }
            autoSave = if ($settings -and $settings.PSObject.Properties["autoSave"]) { [bool]$settings.autoSave } else { $false }
            selectedProvider = if ($settings) { Get-RimWorldDiagnosticKnownValue -Value ([string]$settings.apiProviderId) -Allowed @("Cerebras", "OpenAI", "Gemini", "DeepSeek", "Qwen", "Groq", "Mistral", "OpenRouter", "ZAI", "Custom", "Google") } else { "unspecified" }
            rmkWorkspaceConfigured = $settings -and $settings.PSObject.Properties["rmkWorkspaceRoot"] -and -not [string]::IsNullOrWhiteSpace([string]$settings.rmkWorkspaceRoot)
            rmkUseExisting = if ($settings -and $settings.PSObject.Properties["rmkUseExisting"]) { [bool]$settings.rmkUseExisting } else { $false }
            providers = $providerSummaries.ToArray()
        }

        $projectItems = if ($projects -and $projects.PSObject.Properties["projects"]) { @($projects.projects) } elseif ($projects -is [array]) { @($projects) } else { @() }
        $sourceLanguages = @{}
        $withReview = 0
        foreach ($project in $projectItems) {
            $language = if ($project.PSObject.Properties["sourceLanguage"] -and $project.sourceLanguage) { Get-RimWorldDiagnosticLanguageCategory ([string]$project.sourceLanguage) } else { "unspecified" }
            if ($sourceLanguages.ContainsKey($language)) { $sourceLanguages[$language]++ } else { $sourceLanguages[$language] = 1 }
            if ($project.PSObject.Properties["latestReviewRoot"] -and -not [string]::IsNullOrWhiteSpace([string]$project.latestReviewRoot)) { $withReview++ }
        }
        $projectSummary = [ordered]@{
            present = $null -ne $projects
            schemaVersion = if ($projects -and $projects.PSObject.Properties["version"]) { [int]$projects.version } else { 0 }
            count = $projectItems.Count
            withReview = $withReview
            sourceLanguages = $sourceLanguages
        }

        $reviewSummary = [ordered]@{ folders = 0; decisionFiles = 0; items = 0; sourceChanged = 0; statuses = @{}; origins = @{}; unreadable = 0 }
        $reviewsRoot = Join-Path $appDataFull "reviews"
        if (Test-Path -LiteralPath $reviewsRoot -PathType Container) {
            $reviewFolders = @(Get-ChildItem -LiteralPath $reviewsRoot -Directory -ErrorAction SilentlyContinue | Select-Object -First 50)
            $reviewSummary.folders = $reviewFolders.Count
            foreach ($folder in $reviewFolders) {
                $decisionPath = Join-Path $folder.FullName "review-decisions.json"
                if (-not (Test-Path -LiteralPath $decisionPath -PathType Leaf)) { continue }
                $reviewSummary.decisionFiles++
                $decisionData = Read-RimWorldDiagnosticJson -Path $decisionPath -ReadErrors $readErrors
                if (-not $decisionData) { $reviewSummary.unreadable++; continue }
                foreach ($item in @($decisionData.items)) {
                    $reviewSummary.items++
                    $status = if ($item.status) { Get-RimWorldDiagnosticKnownValue -Value ([string]$item.status) -Allowed @("pending", "translated", "approved", "reviewed", "excluded", "failed") } else { "pending" }
                    $origin = if ($item.PSObject.Properties["translationOrigin"] -and $item.translationOrigin) { Get-RimWorldDiagnosticKnownValue -Value ([string]$item.translationOrigin) -Allowed @("ai", "local", "rmk", "mod", "existing", "google") } else { "unspecified" }
                    if ($reviewSummary.statuses.ContainsKey($status)) { $reviewSummary.statuses[$status]++ } else { $reviewSummary.statuses[$status] = 1 }
                    if ($reviewSummary.origins.ContainsKey($origin)) { $reviewSummary.origins[$origin]++ } else { $reviewSummary.origins[$origin] = 1 }
                    if ($item.PSObject.Properties["sourceChanged"] -and [bool]$item.sourceChanged) { $reviewSummary.sourceChanged++ }
                }
            }
        }

        $productFiles = @(
            "RimWorldAiTranslator.exe", "RimWorldAiTranslator.Native.dll", "Start-RimWorldAiReviewGui.ps1",
            "Invoke-RimWorldAiTranslation.ps1", "Apply-RimWorldAiReviewResults.ps1", "Export-RimWorldAiReviewToRmk.ps1",
            "RimWorldAiTranslator.Storage.ps1", "RimWorldAiTranslator.Validation.ps1",
            "RimWorldAiTranslator.ProviderValidation.ps1", "RimWorldAiTranslator.TranslationMemory.ps1",
            "RimWorldAiTranslator.Diagnostics.ps1", "Export-RimWorldAiTranslatorDiagnostics.ps1"
        )
        $integrity = New-Object "System.Collections.Generic.List[object]"
        foreach ($name in $productFiles) {
            $path = Join-Path $ProductRoot $name
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                [void]$integrity.Add([pscustomobject]@{ name = $name; present = $false; bytes = 0; sha256 = ""; fileVersion = "" })
                continue
            }
            $info = Get-Item -LiteralPath $path
            $hash = Get-FileHash -LiteralPath $path -Algorithm SHA256
            $fileVersion = ""
            try { $fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($path).FileVersion } catch { $fileVersion = "unavailable" }
            [void]$integrity.Add([pscustomobject]@{ name = $name; present = $true; bytes = [int64]$info.Length; sha256 = [string]$hash.Hash.ToLowerInvariant(); fileVersion = [string]$fileVersion })
        }

        $manifest = [ordered]@{
            schemaVersion = 1
            generatedAtUtc = [DateTime]::UtcNow.ToString("o")
            privacy = [ordered]@{
                includesSourceText = $false
                includesTranslationText = $false
                includesLocalizationKeys = $false
                includesApiKeys = $false
                includesRawLogs = $false
                includesAbsolutePaths = $false
            }
            runtime = [ordered]@{
                osVersion = [Environment]::OSVersion.VersionString
                powerShellVersion = $PSVersionTable.PSVersion.ToString()
                clrVersion = [Environment]::Version.ToString()
                process64Bit = [Environment]::Is64BitProcess
                os64Bit = [Environment]::Is64BitOperatingSystem
                culture = [Globalization.CultureInfo]::CurrentCulture.Name
                uiCulture = [Globalization.CultureInfo]::CurrentUICulture.Name
            }
            readErrors = $readErrors
        }
        $runtimeSummary = Get-RimWorldDiagnosticErrorSummary -Lines $RuntimeLogLines

        $jsonFiles = [ordered]@{
            "manifest.json" = $manifest
            "settings-summary.json" = $settingsSummary
            "projects-summary.json" = $projectSummary
            "reviews-summary.json" = $reviewSummary
            "errors-summary.json" = $runtimeSummary
            "product-integrity.json" = $integrity.ToArray()
        }
        foreach ($entry in $jsonFiles.GetEnumerator()) {
            $path = Join-Path $workspaceFull ([string]$entry.Key)
            $jsonText = $entry.Value | ConvertTo-Json -Depth 12
            if ($jsonText -match '(?i)(csk-|sk-[A-Za-z0-9_-]{16,}|[A-Za-z]:\\|\\\\[^\\\r\n]+\\)') {
                throw "Diagnostic privacy validation rejected a secret-like value or absolute path in $($entry.Key)."
            }
            [System.IO.File]::WriteAllText($path, $jsonText, [System.Text.UTF8Encoding]::new($false))
        }

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        if (Test-Path -LiteralPath $temporaryZip) { Remove-Item -LiteralPath $temporaryZip -Force }
        [System.IO.Compression.ZipFile]::CreateFromDirectory($workspaceFull, $temporaryZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
        $archive = [System.IO.Compression.ZipFile]::OpenRead($temporaryZip)
        try {
            $entryNames = @($archive.Entries | ForEach-Object { $_.FullName })
            foreach ($required in $jsonFiles.Keys) {
                if ([string]$required -notin $entryNames) { throw "Diagnostic archive is missing $required." }
            }
        } finally {
            $archive.Dispose()
        }

        if (Test-Path -LiteralPath $outputFull) {
            $backupPath = $outputFull + ".bak"
            [System.IO.File]::Replace($temporaryZip, $outputFull, $backupPath, $true)
        } else {
            [System.IO.File]::Move($temporaryZip, $outputFull)
        }
        return [pscustomobject]@{ Path = $outputFull; Entries = $jsonFiles.Count; Bytes = (Get-Item -LiteralPath $outputFull).Length }
    } finally {
        if (Test-Path -LiteralPath $temporaryZip -PathType Leaf) { Remove-Item -LiteralPath $temporaryZip -Force -ErrorAction SilentlyContinue }
        if ($workspaceFull.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $workspaceFull -PathType Container)) {
            Remove-Item -LiteralPath $workspaceFull -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
