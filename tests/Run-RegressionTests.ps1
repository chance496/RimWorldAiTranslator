[CmdletBinding()]
param(
    [ValidateSet("All", "Harness", "Syntax", "StateStore", "SecretHandling", "SourceExtraction", "LocalApply", "LocalRollback", "RmkExport")]
    [string]$Suite = "All"
)

$ErrorActionPreference = "Stop"
$script:RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$script:PowerShellExe = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Equal([object]$Expected, [object]$Actual, [string]$Message) {
    if (-not [object]::Equals($Expected, $Actual)) {
        throw "$Message Expected=[$Expected] Actual=[$Actual]"
    }
}

function Assert-Contains([object[]]$Values, [object]$Expected, [string]$Message) {
    if ($Expected -notin @($Values)) { throw "$Message Missing=[$Expected]" }
}

function New-TestWorkspace {
    $tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd("\", "/")
    $root = Join-Path $tempBase ("RimWorldAiTranslator-tests-" + [Guid]::NewGuid().ToString("N"))
    [System.IO.Directory]::CreateDirectory($root) | Out-Null
    $full = [System.IO.Path]::GetFullPath($root).TrimEnd("\", "/")
    $prefix = $tempBase + [System.IO.Path]::DirectorySeparatorChar
    Assert-True ($full.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) "Test root escaped the system temp directory."
    return $full
}

function Remove-TestWorkspace([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) { return }
    $tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd("\", "/")
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd("\", "/")
    $prefix = $tempBase + [System.IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not ([System.IO.Path]::GetFileName($full)).StartsWith("RimWorldAiTranslator-tests-", [System.StringComparison]::Ordinal)) {
        throw "Refusing to remove an unverified test directory: $full"
    }
    Remove-Item -LiteralPath $full -Recurse -Force -ErrorAction Stop
}

function New-SampleWorkspace {
    $root = New-TestWorkspace
    $modRoot = Join-Path $root "SampleMod"
    $reviewBase = Join-Path $root "reviews"
    Copy-Item -LiteralPath (Join-Path $script:RepoRoot "testdata\SampleMod") -Destination $modRoot -Recurse
    return [pscustomobject]@{ Root = $root; ModRoot = $modRoot; ReviewBase = $reviewBase }
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

function Invoke-RepositoryScript([string]$ScriptName, [object[]]$Arguments, [hashtable]$Environment = @{}) {
    Assert-True (Test-Path -LiteralPath $script:PowerShellExe -PathType Leaf) "Windows PowerShell was not found."
    $scriptPath = Join-Path $script:RepoRoot $ScriptName
    Assert-True (Test-Path -LiteralPath $scriptPath -PathType Leaf) "Repository script was not found: $ScriptName"
    $allArguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $scriptPath) + @($Arguments | ForEach-Object { [string]$_ })
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $script:PowerShellExe
    $startInfo.Arguments = [string]::Join(" ", @($allArguments | ForEach-Object { Quote-WindowsArgument $_ }))
    $startInfo.WorkingDirectory = $script:RepoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($name in @($Environment.Keys)) {
        $startInfo.EnvironmentVariables[[string]$name] = [string]$Environment[$name]
    }
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    [void]$process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.Result
    $stderr = $stderrTask.Result
    $exitCode = $process.ExitCode
    $process.Dispose()
    $output = @(@($stdout, $stderr) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    return [pscustomobject]@{
        ExitCode = $exitCode
        Lines = @($output | ForEach-Object { [string]$_ })
        Text = [string]::Join([Environment]::NewLine, $output)
    }
}

function Invoke-SourceOnly([object]$Workspace) {
    $result = Invoke-RepositoryScript "Invoke-RimWorldAiTranslation.ps1" @(
        "-ModRoot", $Workspace.ModRoot,
        "-SourceLanguageFolder", "English",
        "-SourceOnly",
        "-ReviewOnly",
        "-ReviewRoot", $Workspace.ReviewBase
    )
    Assert-Equal 0 $result.ExitCode "Source-only extraction failed. $($result.Text)"
    $runRoots = @(Get-ChildItem -LiteralPath $Workspace.ReviewBase -Directory -ErrorAction Stop)
    Assert-Equal 1 $runRoots.Count "Source-only extraction did not create exactly one review run."
    $comparison = @(Get-ChildItem -LiteralPath $runRoots[0].FullName -Recurse -File -Filter "*-comparison.json")
    Assert-Equal 1 $comparison.Count "Source-only extraction did not create exactly one comparison JSON."
    $parsedRows = [System.IO.File]::ReadAllText($comparison[0].FullName, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $rows = @(foreach ($row in $parsedRows) { $row })
    return [pscustomobject]@{ Result = $result; RunRoot = $runRoots[0].FullName; Comparison = $comparison[0].FullName; Rows = $rows }
}

function Write-Utf8Text([string]$Path, [string]$Text) {
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) { [System.IO.Directory]::CreateDirectory($parent) | Out-Null }
    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($false))
}

function Test-HarnessIsolation {
    $workspace = New-SampleWorkspace
    try {
        Assert-True (Test-Path -LiteralPath (Join-Path $workspace.ModRoot "About\About.xml")) "Sample fixture was not copied."
        $koreanRoot = Join-Path $workspace.ModRoot "Languages\Korean"
        $koreanFiles = if (Test-Path -LiteralPath $koreanRoot -PathType Container) { @(Get-ChildItem -LiteralPath $koreanRoot -Recurse -File) } else { @() }
        Assert-Equal 0 $koreanFiles.Count "Sample fixture unexpectedly contains Korean output files."
    } finally {
        Remove-TestWorkspace $workspace.Root
    }
    Assert-True (-not (Test-Path -LiteralPath $workspace.Root)) "Test workspace cleanup failed."
}

function Test-PowerShellSyntax {
    $files = New-Object "System.Collections.Generic.List[System.IO.FileInfo]"
    foreach ($file in Get-ChildItem -LiteralPath $script:RepoRoot -File -Filter "*.ps1") { [void]$files.Add($file) }
    foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter "*.ps1") { [void]$files.Add($file) }
    $errorsFound = New-Object "System.Collections.Generic.List[string]"
    foreach ($file in $files) {
        $tokens = $null
        $errors = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
        foreach ($error in @($errors)) { [void]$errorsFound.Add("$($file.Name):$($error.Extent.StartLineNumber): $($error.Message)") }
    }
    Assert-Equal 0 $errorsFound.Count ([string]::Join([Environment]::NewLine, $errorsFound))
}

function Test-StateStoreRecovery {
    $workspaceRoot = New-TestWorkspace
    try {
        . (Join-Path $script:RepoRoot "RimWorldAiTranslator.Storage.ps1")
        $storePath = Join-Path $workspaceRoot "state\projects.json"
        $backupPath = "$storePath.bak"
        Write-Utf8Text $storePath '{broken-main'
        Write-Utf8Text $backupPath '{broken-backup'
        $mainBefore = [System.IO.File]::ReadAllBytes($storePath)
        $backupBefore = [System.IO.File]::ReadAllBytes($backupPath)
        $failed = $false
        try {
            [void](Read-RimWorldProjectStore $storePath)
        } catch {
            $failed = $true
        }
        Assert-True $failed "A corrupt project store and backup were silently accepted."
        Assert-Equal ([Convert]::ToBase64String($mainBefore)) ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($storePath))) "Corrupt main store was modified after a failed read."
        Assert-Equal ([Convert]::ToBase64String($backupBefore)) ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($backupPath))) "Corrupt backup was modified after a failed read."
        $blockedWrite = $false
        try {
            Write-Utf8JsonFile -Path $storePath -Value ([ordered]@{ version = 2; projects = @() })
        } catch {
            $blockedWrite = $true
        }
        Assert-True $blockedWrite "A failed JSON read did not block a later destructive save."
        Assert-Equal ([Convert]::ToBase64String($mainBefore)) ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($storePath))) "Blocked save changed the corrupt main store."
        Assert-Equal ([Convert]::ToBase64String($backupBefore)) ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($backupPath))) "Blocked save changed the corrupt backup."

        $valid = [ordered]@{ version = 2; projects = @([ordered]@{ id = "project-1"; name = "Recovered" }) }
        Write-Utf8Text $backupPath ($valid | ConvertTo-Json -Depth 5 -Compress)
        $validBackupBefore = [System.IO.File]::ReadAllBytes($backupPath)
        $script:jsonRecoveryNotices = New-Object "System.Collections.Generic.List[string]"
        $projects = @(Read-RimWorldProjectStore $storePath)
        Assert-Equal 1 $projects.Count "Valid project backup was not loaded."
        Assert-Equal "Recovered" ([string]$projects[0].name) "Recovered project content is incorrect."
        Assert-Equal ([Convert]::ToBase64String($validBackupBefore)) ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($backupPath))) "Valid backup changed during recovery."
        $restored = [System.IO.File]::ReadAllText($storePath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        Assert-Equal "Recovered" ([string]$restored.projects[0].name) "Main store was not restored from backup."
        $corruptCopies = @(Get-ChildItem -LiteralPath (Split-Path -Parent $storePath) -File -Filter "projects.json.corrupt-*")
        Assert-Equal 1 $corruptCopies.Count "Corrupt main store was not preserved exactly once."
        Assert-Equal '{broken-main' ([System.IO.File]::ReadAllText($corruptCopies[0].FullName, [System.Text.Encoding]::UTF8)) "Preserved corrupt store content changed."
        Assert-Equal 1 $script:jsonRecoveryNotices.Count "Recovery was not surfaced to the caller."

        $updated = [ordered]@{ version = 2; projects = @([ordered]@{ id = "project-1"; name = "Updated" }) }
        Write-Utf8JsonFile -Path $storePath -Value $updated -Depth 5
        $saved = Read-RimWorldProjectStore $storePath
        Assert-Equal "Updated" ([string]$saved[0].name) "Recovered store could not be saved."
        $savedBackup = [System.IO.File]::ReadAllText($backupPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        Assert-Equal "Recovered" ([string]$savedBackup.projects[0].name) "Save replaced the valid backup with corrupt data."

        Write-Utf8Text $storePath '{"version":2}'
        $shapeFailed = $false
        try {
            [void](Read-RimWorldProjectStore $storePath)
        } catch {
            $shapeFailed = $true
        }
        Assert-True $shapeFailed "A project store without a projects collection was accepted."
        $shapeWriteBlocked = $false
        try {
            Write-Utf8JsonFile -Path $storePath -Value ([ordered]@{ version = 2; projects = @() })
        } catch {
            $shapeWriteBlocked = $true
        }
        Assert-True $shapeWriteBlocked "An invalid project-store shape could be overwritten in the same process."
    } finally {
        Remove-TestWorkspace $workspaceRoot
    }
}

function Test-SecretHandling {
    $workspaceRoot = New-TestWorkspace
    try {
        $sentinel = "TEST_SECRET_7cbe1e9da65b4d8eb493"
        $translatorPath = Join-Path $workspaceRoot "FakeTranslator.ps1"
        $argumentPath = Join-Path $workspaceRoot "arguments.json"
        $logPath = Join-Path $workspaceRoot "runner.log"
        Write-Utf8Text $translatorPath @'
param([string]$ModRoot, [string[]]$ApiKey)
Write-Output ("ENV_EMPTY=" + [string]::IsNullOrEmpty($env:RIMWORLD_TRANSLATOR_API_KEYS))
Write-Output ("KEY=" + $ApiKey[0])
throw ("Authorization: Bearer " + $ApiKey[0])
'@
        $arguments = [ordered]@{ version = 1; parameters = [ordered]@{ ModRoot = $workspaceRoot } }
        Write-Utf8Text $argumentPath ($arguments | ConvertTo-Json -Depth 4 -Compress)
        Write-Utf8Text $logPath ""
        $result = Invoke-RepositoryScript "Run-RimWorldAiTranslation.ps1" @(
            "-TranslatorScript", $translatorPath,
            "-ArgumentFile", $argumentPath,
            "-LogFile", $logPath
        ) @{ RIMWORLD_TRANSLATOR_API_KEYS = $sentinel; CEREBRAS_API_KEY = "" }
        Assert-True ($result.ExitCode -ne 0) "Fake translator failure unexpectedly succeeded."
        $log = [System.IO.File]::ReadAllText($logPath, [System.Text.Encoding]::UTF8)
        Assert-True (-not $log.Contains($sentinel)) "API key leaked into the runner log."
        Assert-True ($log.Contains("[REDACTED]")) "Runner did not mark redacted credentials."
        Assert-True ($log.Contains("ENV_EMPTY=True")) "API key environment variable remained visible to the translator."
        $argumentJson = [System.IO.File]::ReadAllText($argumentPath, [System.Text.Encoding]::UTF8)
        Assert-True (-not $argumentJson.Contains($sentinel)) "API key leaked into the argument JSON."
        Assert-True ($argumentJson -notmatch '(?i)api.?key') "Argument JSON unexpectedly contains an API key field."
    } finally {
        Remove-TestWorkspace $workspaceRoot
    }
}

function Test-SourceExtraction {
    $workspace = New-SampleWorkspace
    try {
        $source = Invoke-SourceOnly $workspace
        Assert-Equal 7 $source.Rows.Count "Unexpected source entry count."
        $keys = @($source.Rows | ForEach-Object { [string]$_.key })
        foreach ($key in @(
            "CodexTranslator.SampleButton",
            "CodexTranslator.SampleMessage",
            "Codex_TestWorkbench.label",
            "Codex_TestWorkbench.description",
            "Codex_TestWorkbench.comps.CompPowerTrader.gizmoLabel",
            "Codex_TestWorkbench.comps.CompPowerTrader.gizmoDescription",
            "Codex_TestJob.reportString"
        )) { Assert-Contains $keys $key "Expected source key was not extracted." }

        $skippedFile = @(Get-ChildItem -LiteralPath $source.RunRoot -Recurse -File -Filter "*-skipped-internal-identifiers.json")
        Assert-Equal 1 $skippedFile.Count "Internal identifier audit file is missing."
        $parsedSkipped = [System.IO.File]::ReadAllText($skippedFile[0].FullName, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        $skipped = @(foreach ($item in $parsedSkipped) { $item })
        Assert-Equal 1 $skipped.Count "Unexpected excluded identifier count."
        Assert-True ([string]$skipped[0].field -eq "compClass") "The expected compClass identifier was not excluded."
        $koreanRoot = Join-Path $workspace.ModRoot "Languages\Korean"
        $koreanFiles = if (Test-Path -LiteralPath $koreanRoot -PathType Container) { @(Get-ChildItem -LiteralPath $koreanRoot -Recurse -File) } else { @() }
        Assert-Equal 0 $koreanFiles.Count "Review-only extraction wrote files into the fixture mod."

        $runPrefix = [System.IO.Path]::GetFullPath($source.RunRoot).TrimEnd("\", "/") + [System.IO.Path]::DirectorySeparatorChar
        foreach ($row in $source.Rows) {
            $target = [System.IO.Path]::GetFullPath([string]$row.target)
            Assert-True ($target.StartsWith($runPrefix, [System.StringComparison]::OrdinalIgnoreCase)) "Review target escaped the review run: $target"
        }
    } finally {
        Remove-TestWorkspace $workspace.Root
    }
}

function Test-LocalApply {
    $workspace = New-SampleWorkspace
    try {
        $existingPath = Join-Path $workspace.ModRoot "Languages\Korean\Keyed\SampleKeys.xml"
        Write-Utf8Text $existingPath @'
<?xml version="1.0" encoding="utf-8"?>
<LanguageData>
  <Existing.Keep>KEEP_ME</Existing.Keep>
  <CodexTranslator.SampleMessage>OLD_MESSAGE</CodexTranslator.SampleMessage>
</LanguageData>
'@
        $source = Invoke-SourceOnly $workspace
        $safeRow = @($source.Rows | Where-Object { $_.key -eq "CodexTranslator.SampleButton" })[0]
        $unsafeRow = @($source.Rows | Where-Object { $_.key -eq "CodexTranslator.SampleMessage" })[0]
        Assert-True ($null -ne $safeRow -and $null -ne $unsafeRow) "Apply test source rows are missing."
        $safeText = ConvertFrom-Json '"\uBC88\uC5ED \uC2DC\uC791"'
        $unsafeText = ConvertFrom-Json '"\uBC88\uC5ED \uC644\uB8CC"'
        $reviewLanguageRoot = [System.IO.Path]::GetFullPath((Join-Path $source.RunRoot "Languages\Korean")).TrimEnd("\", "/")
        $reviewLanguagePrefix = $reviewLanguageRoot + [System.IO.Path]::DirectorySeparatorChar
        $safeTarget = [System.IO.Path]::GetFullPath([string]$safeRow.target).Substring($reviewLanguagePrefix.Length)
        $unsafeTarget = [System.IO.Path]::GetFullPath([string]$unsafeRow.target).Substring($reviewLanguagePrefix.Length)
        $decisions = [ordered]@{
            version = 5
            sparse = $true
            reviewRoot = $source.RunRoot
            comparison = $source.Comparison
            updatedAt = [DateTime]::UtcNow.ToString("o")
            items = @(
                [ordered]@{ id = $safeRow.id; key = $safeRow.key; target = $safeTarget; status = "approved"; text = $safeText; sourceText = $safeRow.source; sourceChanged = $false },
                [ordered]@{ id = $unsafeRow.id; key = $unsafeRow.key; target = $unsafeTarget; status = "approved"; text = $unsafeText; sourceText = $unsafeRow.source; sourceChanged = $false }
            )
        }
        Write-Utf8Text (Join-Path $source.RunRoot "review-decisions.json") ($decisions | ConvertTo-Json -Depth 8)

        $apply = Invoke-RepositoryScript "Apply-RimWorldAiReviewResults.ps1" @(
            "-ModRoot", $workspace.ModRoot,
            "-ReviewRoot", $source.RunRoot,
            "-Overwrite",
            "-ApplyStatus", "ApprovedOnly"
        )
        Assert-Equal 0 $apply.ExitCode "Local apply failed. $($apply.Text)"
        $xml = New-Object System.Xml.XmlDocument
        $xml.PreserveWhitespace = $true
        $xml.Load($existingPath)
        Assert-Equal "KEEP_ME" $xml.LanguageData."Existing.Keep" "Existing translation was not preserved."
        Assert-Equal "OLD_MESSAGE" $xml.LanguageData."CodexTranslator.SampleMessage" "Unsafe missing-token translation overwrote the existing value."
        Assert-Equal $safeText $xml.LanguageData."CodexTranslator.SampleButton" "Approved safe translation was not applied."
        $names = @($xml.LanguageData.ChildNodes | Where-Object { $_.NodeType -eq [System.Xml.XmlNodeType]::Element } | ForEach-Object { $_.LocalName })
        Assert-Equal $names.Count @($names | Select-Object -Unique).Count "Duplicate XML keys were created."
        $backupPath = "$existingPath.bak"
        Assert-True (Test-Path -LiteralPath $backupPath -PathType Leaf) "Apply did not keep a persistent backup."
        $backupXml = New-Object System.Xml.XmlDocument
        $backupXml.Load($backupPath)
        Assert-Equal "KEEP_ME" $backupXml.LanguageData."Existing.Keep" "Backup did not preserve the previous file."
        Assert-Equal "OLD_MESSAGE" $backupXml.LanguageData."CodexTranslator.SampleMessage" "Backup did not preserve the previous translation."
        Assert-True ($null -eq $backupXml.LanguageData."CodexTranslator.SampleButton") "Backup unexpectedly contains the newly applied translation."
    } finally {
        Remove-TestWorkspace $workspace.Root
    }
}

function Test-LocalApplyRollback {
    $workspace = New-SampleWorkspace
    try {
        $source = Invoke-SourceOnly $workspace
        $defRow = @($source.Rows | Where-Object { $_.key -eq "Codex_TestWorkbench.label" })[0]
        $keyedRow = @($source.Rows | Where-Object { $_.key -eq "CodexTranslator.SampleButton" })[0]
        Assert-True ($null -ne $defRow -and $null -ne $keyedRow) "Rollback test source rows are missing."

        $defPath = Join-Path $workspace.ModRoot "Languages\Korean\DefInjected\ThingDef\CodexAI.xml"
        $defBefore = @'
<?xml version="1.0" encoding="utf-8"?>
<LanguageData>
  <Existing.Rollback>BEFORE</Existing.Rollback>
</LanguageData>
'@
        Write-Utf8Text $defPath $defBefore
        $defBeforeBytes = [System.IO.File]::ReadAllBytes($defPath)

        $blockedTarget = Join-Path $workspace.ModRoot "Languages\Korean\Keyed\SampleKeys.xml"
        [System.IO.Directory]::CreateDirectory($blockedTarget) | Out-Null
        $reviewLanguageRoot = [System.IO.Path]::GetFullPath((Join-Path $source.RunRoot "Languages\Korean")).TrimEnd("\", "/")
        $reviewLanguagePrefix = $reviewLanguageRoot + [System.IO.Path]::DirectorySeparatorChar
        $defTarget = [System.IO.Path]::GetFullPath([string]$defRow.target).Substring($reviewLanguagePrefix.Length)
        $keyedTarget = [System.IO.Path]::GetFullPath([string]$keyedRow.target).Substring($reviewLanguagePrefix.Length)
        $defText = ConvertFrom-Json '"\uC2DC\uD5D8 \uC791\uC5C5\uB300"'
        $keyedText = ConvertFrom-Json '"\uBC88\uC5ED \uC2DC\uC791"'
        $decisions = [ordered]@{
            version = 5
            sparse = $true
            reviewRoot = $source.RunRoot
            comparison = $source.Comparison
            updatedAt = [DateTime]::UtcNow.ToString("o")
            items = @(
                [ordered]@{ id = $defRow.id; key = $defRow.key; target = $defTarget; status = "approved"; text = $defText; sourceText = $defRow.source; sourceChanged = $false },
                [ordered]@{ id = $keyedRow.id; key = $keyedRow.key; target = $keyedTarget; status = "approved"; text = $keyedText; sourceText = $keyedRow.source; sourceChanged = $false }
            )
        }
        Write-Utf8Text (Join-Path $source.RunRoot "review-decisions.json") ($decisions | ConvertTo-Json -Depth 8)

        $apply = Invoke-RepositoryScript "Apply-RimWorldAiReviewResults.ps1" @(
            "-ModRoot", $workspace.ModRoot,
            "-ReviewRoot", $source.RunRoot,
            "-Overwrite",
            "-ApplyStatus", "ApprovedOnly"
        )
        Assert-True ($apply.ExitCode -ne 0) "Injected second-file failure unexpectedly succeeded."
        Assert-True ($apply.Text -match "rolled back") "Apply failure did not report rollback."
        Assert-Equal ([Convert]::ToBase64String($defBeforeBytes)) ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($defPath))) "First file was not restored after a later write failed."
        Assert-True (Test-Path -LiteralPath $blockedTarget -PathType Container) "Pre-existing blocking directory was modified during rollback."
        $transactionFiles = @(Get-ChildItem -LiteralPath (Join-Path $workspace.ModRoot "Languages\Korean") -Recurse -File -Filter "*.transaction.bak")
        Assert-Equal 0 $transactionFiles.Count "Transaction snapshots were left behind."
    } finally {
        Remove-TestWorkspace $workspace.Root
    }
}

function New-ReviewDecisions([object]$Source, [object[]]$Items) {
    $reviewLanguageRoot = [System.IO.Path]::GetFullPath((Join-Path $Source.RunRoot "Languages\Korean")).TrimEnd("\", "/")
    $reviewLanguagePrefix = $reviewLanguageRoot + [System.IO.Path]::DirectorySeparatorChar
    $decisions = foreach ($item in $Items) {
        $row = $item.Row
        $target = [System.IO.Path]::GetFullPath([string]$row.target).Substring($reviewLanguagePrefix.Length)
        [ordered]@{ id = $row.id; key = $row.key; target = $target; status = "approved"; text = [string]$item.Text; sourceText = $row.source; sourceChanged = $false }
    }
    return [ordered]@{
        version = 5
        sparse = $true
        reviewRoot = $Source.RunRoot
        comparison = $Source.Comparison
        updatedAt = [DateTime]::UtcNow.ToString("o")
        items = @($decisions)
    }
}

function Test-RmkExportTransaction {
    $workspace = New-SampleWorkspace
    try {
        $source = Invoke-SourceOnly $workspace
        $defRow = @($source.Rows | Where-Object { $_.key -eq "Codex_TestWorkbench.label" })[0]
        $keyedRow = @($source.Rows | Where-Object { $_.key -eq "CodexTranslator.SampleButton" })[0]
        Assert-True ($null -ne $defRow -and $null -ne $keyedRow) "RMK test source rows are missing."
        $defText = ConvertFrom-Json '"\uC2DC\uD5D8 \uC791\uC5C5\uB300"'
        $keyedText = ConvertFrom-Json '"\uBC88\uC5ED \uC2DC\uC791"'
        $decisionPath = Join-Path $source.RunRoot "review-decisions.json"
        Write-Utf8Text $decisionPath ((New-ReviewDecisions -Source $source -Items @([pscustomobject]@{ Row = $defRow; Text = $defText })) | ConvertTo-Json -Depth 8)

        $rmkRoot = Join-Path $workspace.Root "RmkEntry"
        [System.IO.Directory]::CreateDirectory($rmkRoot) | Out-Null
        $defPath = Join-Path $rmkRoot "Languages\Korean\DefInjected\ThingDef\CodexAI.xml"
        $defOriginal = @'
<?xml version="1.0" encoding="utf-8"?>
<LanguageData>
  <Existing.Rmk>BEFORE</Existing.Rmk>
</LanguageData>
'@
        Write-Utf8Text $defPath $defOriginal
        $defOriginalBytes = [System.IO.File]::ReadAllBytes($defPath)
        $workbookPath = Join-Path $rmkRoot "history.xlsx"
        $first = Invoke-RepositoryScript "Export-RimWorldAiReviewToRmk.ps1" @(
            "-RmkEntryRoot", $rmkRoot,
            "-ReviewRoot", $source.RunRoot,
            "-RmkLanguageFolderName", "Korean",
            "-WorkbookPath", $workbookPath,
            "-Overwrite",
            "-ApplyStatus", "ApprovedOnly"
        )
        Assert-Equal 0 $first.ExitCode "Initial RMK export failed. $($first.Text)"
        Assert-True (Test-Path -LiteralPath $workbookPath -PathType Leaf) "RMK workbook was not created."
        Assert-True (Test-Path -LiteralPath "$defPath.bak" -PathType Leaf) "RMK XML backup was not retained."
        Assert-Equal ([Convert]::ToBase64String($defOriginalBytes)) ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes("$defPath.bak"))) "RMK XML backup did not preserve the prior file."
        $defBeforeFailure = [System.IO.File]::ReadAllBytes($defPath)
        $workbookBeforeFailure = [System.IO.File]::ReadAllBytes($workbookPath)

        Write-Utf8Text $decisionPath ((New-ReviewDecisions -Source $source -Items @(
            [pscustomobject]@{ Row = $defRow; Text = $defText },
            [pscustomobject]@{ Row = $keyedRow; Text = $keyedText }
        )) | ConvertTo-Json -Depth 8)
        $blockedTarget = Join-Path $rmkRoot "Languages\Korean\Keyed\SampleKeys.xml"
        [System.IO.Directory]::CreateDirectory($blockedTarget) | Out-Null
        $failed = Invoke-RepositoryScript "Export-RimWorldAiReviewToRmk.ps1" @(
            "-RmkEntryRoot", $rmkRoot,
            "-ReviewRoot", $source.RunRoot,
            "-RmkLanguageFolderName", "Korean",
            "-WorkbookPath", $workbookPath,
            "-Overwrite",
            "-ApplyStatus", "ApprovedOnly"
        )
        Assert-True ($failed.ExitCode -ne 0) "Injected RMK XML failure unexpectedly succeeded."
        Assert-True ($failed.Text -match "rolled back") "RMK failure did not report transaction rollback."
        Assert-Equal ([Convert]::ToBase64String($defBeforeFailure)) ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($defPath))) "RMK XML was not restored after failure."
        Assert-Equal ([Convert]::ToBase64String($workbookBeforeFailure)) ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($workbookPath))) "RMK workbook was not restored after failure."
        Assert-True (Test-Path -LiteralPath "$workbookPath.bak" -PathType Leaf) "RMK workbook backup was not retained."
        Assert-Equal ([Convert]::ToBase64String($workbookBeforeFailure)) ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes("$workbookPath.bak"))) "RMK workbook backup does not contain the pre-run workbook."
        Assert-True (Test-Path -LiteralPath $blockedTarget -PathType Container) "RMK rollback modified the pre-existing blocking directory."
        $transactionFiles = @(Get-ChildItem -LiteralPath $rmkRoot -Recurse -File -Filter "*.transaction.bak")
        Assert-Equal 0 $transactionFiles.Count "RMK transaction snapshots were left behind."

        Remove-Item -LiteralPath $blockedTarget -Recurse -Force
        $second = Invoke-RepositoryScript "Export-RimWorldAiReviewToRmk.ps1" @(
            "-RmkEntryRoot", $rmkRoot,
            "-ReviewRoot", $source.RunRoot,
            "-RmkLanguageFolderName", "Korean",
            "-WorkbookPath", $workbookPath,
            "-Overwrite",
            "-ApplyStatus", "ApprovedOnly"
        )
        Assert-Equal 0 $second.ExitCode "RMK export did not recover after the injected failure. $($second.Text)"
        Assert-True (Test-Path -LiteralPath $blockedTarget -PathType Leaf) "Recovered RMK export did not create the keyed XML."
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead($workbookPath)
        try {
            Assert-True ($archive.Entries.Count -gt 0) "RMK workbook ZIP is empty."
        } finally {
            $archive.Dispose()
        }
    } finally {
        Remove-TestWorkspace $workspace.Root
    }
}

$tests = @(
    [pscustomobject]@{ Name = "Harness.Isolation"; Suite = "Harness"; Body = { Test-HarnessIsolation } },
    [pscustomobject]@{ Name = "Syntax.PowerShell"; Suite = "Syntax"; Body = { Test-PowerShellSyntax } },
    [pscustomobject]@{ Name = "StateStore.Recovery"; Suite = "StateStore"; Body = { Test-StateStoreRecovery } },
    [pscustomobject]@{ Name = "Security.ApiKeyHandling"; Suite = "SecretHandling"; Body = { Test-SecretHandling } },
    [pscustomobject]@{ Name = "Source.Extraction"; Suite = "SourceExtraction"; Body = { Test-SourceExtraction } },
    [pscustomobject]@{ Name = "Apply.Local"; Suite = "LocalApply"; Body = { Test-LocalApply } },
    [pscustomobject]@{ Name = "Apply.LocalRollback"; Suite = "LocalRollback"; Body = { Test-LocalApplyRollback } },
    [pscustomobject]@{ Name = "Export.RmkTransaction"; Suite = "RmkExport"; Body = { Test-RmkExportTransaction } }
)

$selected = @(if ($Suite -eq "All") { $tests } else { $tests | Where-Object { $_.Suite -eq $Suite } })
if ($selected.Count -eq 0) { throw "No tests were selected for suite: $Suite" }

$failures = New-Object "System.Collections.Generic.List[string]"
$started = Get-Date
foreach ($test in $selected) {
    $testStarted = Get-Date
    try {
        & $test.Body
        $elapsed = [Math]::Round(((Get-Date) - $testStarted).TotalSeconds, 3)
        Write-Host "PASS $($test.Name) (${elapsed}s)"
    } catch {
        $elapsed = [Math]::Round(((Get-Date) - $testStarted).TotalSeconds, 3)
        $message = "$($test.Name): $($_.Exception.Message)"
        [void]$failures.Add($message)
        Write-Host "FAIL $message (${elapsed}s)"
    }
}

$totalElapsed = [Math]::Round(((Get-Date) - $started).TotalSeconds, 3)
Write-Host "RESULT total=$($selected.Count) passed=$($selected.Count - $failures.Count) failed=$($failures.Count) elapsed=${totalElapsed}s"
if ($failures.Count -gt 0) { exit 1 }
exit 0
