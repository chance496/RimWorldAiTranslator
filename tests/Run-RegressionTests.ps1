[CmdletBinding()]
param(
    [ValidateSet("All", "Harness", "Syntax", "StateStore", "SecretHandling", "ProjectCleanup", "DryRun", "SourceExtraction", "DefSafety", "DuplicateIdentity", "TokenSafety", "ApiResilience", "DirectOutput", "LocalApply", "LocalRollback", "RmkExport", "RmkHistory")]
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

function New-FixtureWorkspace([string]$FixtureName) {
    $root = New-TestWorkspace
    $modRoot = Join-Path $root $FixtureName
    $reviewBase = Join-Path $root "reviews"
    $fixturePath = Join-Path (Join-Path $script:RepoRoot "testdata") $FixtureName
    Assert-True (Test-Path -LiteralPath $fixturePath -PathType Container) "Fixture was not found: $FixtureName"
    Copy-Item -LiteralPath $fixturePath -Destination $modRoot -Recurse
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

function Invoke-SourceOnly([object]$Workspace, [object[]]$ExtraArguments = @()) {
    $arguments = @(
        "-ModRoot", $Workspace.ModRoot,
        "-SourceLanguageFolder", "English",
        "-SourceOnly",
        "-ReviewOnly",
        "-ReviewRoot", $Workspace.ReviewBase
    ) + @($ExtraArguments)
    $result = Invoke-RepositoryScript "Invoke-RimWorldAiTranslation.ps1" $arguments
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

function Get-ZipEntryText([System.IO.Compression.ZipArchive]$Archive, [string]$Name) {
    $entry = $Archive.GetEntry($Name)
    Assert-True ($null -ne $entry) "Workbook ZIP entry is missing: $Name"
    $reader = New-Object System.IO.StreamReader($entry.Open(), [System.Text.Encoding]::UTF8, $true)
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Set-ZipEntryText([System.IO.Compression.ZipArchive]$Archive, [string]$Name, [string]$Text) {
    $existing = $Archive.GetEntry($Name)
    if ($existing) { $existing.Delete() }
    $entry = $Archive.CreateEntry($Name, [System.IO.Compression.CompressionLevel]::Optimal)
    $writer = New-Object System.IO.StreamWriter($entry.Open(), [System.Text.UTF8Encoding]::new($false))
    try { $writer.Write($Text) } finally { $writer.Dispose() }
}

function New-SpreadsheetCell([System.Xml.XmlDocument]$Document, [string]$Reference, [string]$Text) {
    $namespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
    $cell = $Document.CreateElement("c", $namespace)
    [void]$cell.SetAttribute("r", $Reference)
    [void]$cell.SetAttribute("t", "inlineStr")
    $inline = $Document.CreateElement("is", $namespace)
    $textNode = $Document.CreateElement("t", $namespace)
    [void]$textNode.SetAttribute("xml:space", "preserve")
    $textNode.InnerText = $Text
    [void]$inline.AppendChild($textNode)
    [void]$cell.AppendChild($inline)
    return $cell
}

function Add-RmkWorkbookMetadataFixture([string]$WorkbookPath) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::Open($WorkbookPath, [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        $sheet = New-Object System.Xml.XmlDocument
        $sheet.PreserveWhitespace = $true
        $sheet.LoadXml((Get-ZipEntryText $archive "xl/worksheets/sheet1.xml"))
        $namespaces = New-Object System.Xml.XmlNamespaceManager($sheet.NameTable)
        $namespaces.AddNamespace("s", "http://schemas.openxmlformats.org/spreadsheetml/2006/main")
        $rows = @($sheet.SelectNodes("/s:worksheet/s:sheetData/s:row", $namespaces))
        Assert-True ($rows.Count -ge 2) "Generated RMK workbook has no data row to decorate."
        [void]$rows[0].AppendChild((New-SpreadsheetCell $sheet "G1" "Reviewer Notes"))
        [void]$rows[1].AppendChild((New-SpreadsheetCell $sheet "G2" "KEEP_EXTRA_COLUMN"))
        $translationCell = $sheet.SelectSingleNode("/s:worksheet/s:sheetData/s:row[@r='2']/s:c[@r='F2']", $namespaces)
        Assert-True ($null -ne $translationCell) "Generated RMK translation cell F2 is missing."
        [void]$translationCell.SetAttribute("s", "1")
        $legacyDrawing = $sheet.CreateElement("legacyDrawing", "http://schemas.openxmlformats.org/spreadsheetml/2006/main")
        [void]$legacyDrawing.SetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships", "rId2")
        [void]$sheet.DocumentElement.AppendChild($legacyDrawing)
        Set-ZipEntryText $archive "xl/worksheets/sheet1.xml" $sheet.OuterXml

        $contentTypes = New-Object System.Xml.XmlDocument
        $contentTypes.PreserveWhitespace = $true
        $contentTypes.LoadXml((Get-ZipEntryText $archive "[Content_Types].xml"))
        $typesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types"
        foreach ($definition in @(
            @("Override", "PartName", "/xl/styles.xml", "ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"),
            @("Override", "PartName", "/xl/comments1.xml", "ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.comments+xml"),
            @("Default", "Extension", "vml", "ContentType", "application/vnd.openxmlformats-officedocument.vmlDrawing")
        )) {
            $node = $contentTypes.CreateElement($definition[0], $typesNamespace)
            [void]$node.SetAttribute($definition[1], $definition[2])
            [void]$node.SetAttribute($definition[3], $definition[4])
            [void]$contentTypes.DocumentElement.AppendChild($node)
        }
        Set-ZipEntryText $archive "[Content_Types].xml" $contentTypes.OuterXml

        $workbookRelationships = New-Object System.Xml.XmlDocument
        $workbookRelationships.PreserveWhitespace = $true
        $workbookRelationships.LoadXml((Get-ZipEntryText $archive "xl/_rels/workbook.xml.rels"))
        $relationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships"
        $styleRelationship = $workbookRelationships.CreateElement("Relationship", $relationshipNamespace)
        [void]$styleRelationship.SetAttribute("Id", "rId2")
        [void]$styleRelationship.SetAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles")
        [void]$styleRelationship.SetAttribute("Target", "styles.xml")
        [void]$workbookRelationships.DocumentElement.AppendChild($styleRelationship)
        Set-ZipEntryText $archive "xl/_rels/workbook.xml.rels" $workbookRelationships.OuterXml

        Set-ZipEntryText $archive "xl/styles.xml" '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment wrapText="1"/></xf></cellXfs></styleSheet>'
        Set-ZipEntryText $archive "xl/worksheets/_rels/sheet1.xml.rels" '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/comments" Target="../comments1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/vmlDrawing" Target="../drawings/vmlDrawing1.vml"/></Relationships>'
        Set-ZipEntryText $archive "xl/comments1.xml" '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><comments xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><authors><author>Codex fixture</author></authors><commentList><comment ref="F2" authorId="0"><text><t>KEEP_COMMENT</t></text></comment></commentList></comments>'
        Set-ZipEntryText $archive "xl/drawings/vmlDrawing1.vml" '<xml xmlns:v="urn:schemas-microsoft-com:vml" xmlns:x="urn:schemas-microsoft-com:office:excel"><v:shape id="_x0000_s1025" type="#_x0000_t202"><x:ClientData ObjectType="Note"><x:Row>1</x:Row><x:Column>5</x:Column></x:ClientData></v:shape></xml>'
    } finally {
        $archive.Dispose()
    }
}

function Get-RmkWorkbookMetadataSnapshot([string]$WorkbookPath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($WorkbookPath)
    try {
        $sheet = Get-ZipEntryText $archive "xl/worksheets/sheet1.xml"
        return [pscustomobject]@{
            ExtraColumn = $sheet.Contains("KEEP_EXTRA_COLUMN")
            Style = $sheet -match '<c[^>]+r="F2"[^>]+s="1"|<c[^>]+s="1"[^>]+r="F2"'
            LegacyDrawing = $sheet.Contains("legacyDrawing")
            Comment = (Get-ZipEntryText $archive "xl/comments1.xml").Contains("KEEP_COMMENT")
            Styles = (Get-ZipEntryText $archive "xl/styles.xml").Contains("wrapText")
            SheetRelationships = (Get-ZipEntryText $archive "xl/worksheets/_rels/sheet1.xml.rels").Contains("comments")
        }
    } finally {
        $archive.Dispose()
    }
}

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port } finally { $listener.Stop() }
}

function Wait-TestCondition([scriptblock]$Condition, [int]$TimeoutMilliseconds, [string]$FailureMessage) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $TimeoutMilliseconds) {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 50
    }
    throw $FailureMessage
}

function Start-TestPowerShellProcess([string]$ScriptPath, [object[]]$Arguments, [hashtable]$Environment = @{}) {
    $allArguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $ScriptPath) + @($Arguments | ForEach-Object { [string]$_ })
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $script:PowerShellExe
    $startInfo.Arguments = [string]::Join(" ", @($allArguments | ForEach-Object { Quote-WindowsArgument $_ }))
    $startInfo.WorkingDirectory = $script:RepoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    foreach ($name in @($Environment.Keys)) { $startInfo.EnvironmentVariables[[string]$name] = [string]$Environment[$name] }
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    [void]$process.Start()
    return $process
}

function Stop-TestProcess([System.Diagnostics.Process]$Process) {
    if (-not $Process) { return }
    try {
        if (-not $Process.HasExited) { $Process.Kill() }
        [void]$Process.WaitForExit(3000)
    } catch {
    } finally {
        $Process.Dispose()
    }
}

function New-FakeOpenAiServer([string]$WorkspaceRoot, [int]$FailFirst = 0, [int]$DelayOnRequest = 0, [int]$DelayMilliseconds = 0, [int]$MaxRequests = 1) {
    $serverPath = Join-Path $WorkspaceRoot ("fake-openai-" + [Guid]::NewGuid().ToString("N") + ".ps1")
    $statePath = Join-Path $WorkspaceRoot ("fake-openai-state-" + [Guid]::NewGuid().ToString("N") + ".json")
    $readyPath = Join-Path $WorkspaceRoot ("fake-openai-ready-" + [Guid]::NewGuid().ToString("N") + ".signal")
    $port = Get-FreeTcpPort
    Write-Utf8Text $serverPath @'
param([int]$Port, [string]$StatePath, [string]$ReadyPath, [int]$FailFirst, [int]$DelayOnRequest, [int]$DelayMilliseconds, [int]$MaxRequests)
$ErrorActionPreference = "Stop"
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
$listener.Start()
[System.IO.File]::WriteAllText($ReadyPath, "ready", [System.Text.UTF8Encoding]::new($false))
$count = 0
try {
    while ($count -lt $MaxRequests) {
        $client = $listener.AcceptTcpClient()
        try {
            $stream = $client.GetStream()
            $memory = New-Object System.IO.MemoryStream
            $buffer = New-Object byte[] 8192
            $headerEnd = -1
            $contentLength = 0
            while ($true) {
                $read = $stream.Read($buffer, 0, $buffer.Length)
                if ($read -le 0) { break }
                $memory.Write($buffer, 0, $read)
                $bytes = $memory.ToArray()
                $text = [System.Text.Encoding]::UTF8.GetString($bytes)
                if ($headerEnd -lt 0) {
                    $headerEnd = $text.IndexOf("`r`n`r`n", [System.StringComparison]::Ordinal)
                    if ($headerEnd -ge 0) {
                        $headers = $text.Substring(0, $headerEnd)
                        $match = [System.Text.RegularExpressions.Regex]::Match($headers, '(?im)^Content-Length:\s*(\d+)\s*$')
                        if ($match.Success) { $contentLength = [int]$match.Groups[1].Value }
                    }
                }
                if ($headerEnd -ge 0 -and $bytes.Length -ge ($headerEnd + 4 + $contentLength)) { break }
            }
            $count++
            [System.IO.File]::WriteAllText($StatePath, (@{ requests = $count } | ConvertTo-Json -Compress), [System.Text.UTF8Encoding]::new($false))
            if ($count -eq $DelayOnRequest -and $DelayMilliseconds -gt 0) { Start-Sleep -Milliseconds $DelayMilliseconds }
            $bytes = $memory.ToArray()
            $bodyOffset = $headerEnd + 4
            $bodyText = if ($contentLength -gt 0) { [System.Text.Encoding]::UTF8.GetString($bytes, $bodyOffset, $contentLength) } else { "" }
            if ($count -le $FailFirst) {
                $status = "500 Internal Server Error"
                $responseText = '{"error":{"message":"fixture transient failure"}}'
            } else {
                $request = $bodyText | ConvertFrom-Json
                $payload = ([string]$request.messages[-1].content) | ConvertFrom-Json
                $prefix = ConvertFrom-Json '"\uBC88\uC5ED"'
                $translations = @($payload.entries | ForEach-Object { [ordered]@{ id = [string]$_.id; text = "$prefix $([string]$_.text)" } })
                $content = [ordered]@{ translations = $translations } | ConvertTo-Json -Depth 8 -Compress
                $responseText = [ordered]@{
                    choices = @([ordered]@{ message = [ordered]@{ content = $content } })
                    usage = [ordered]@{ prompt_tokens = 20; completion_tokens = 20; total_tokens = 40 }
                } | ConvertTo-Json -Depth 10 -Compress
                $status = "200 OK"
            }
            $responseBytes = [System.Text.UTF8Encoding]::new($false).GetBytes($responseText)
            $headerText = "HTTP/1.1 $status`r`nContent-Type: application/json; charset=utf-8`r`nContent-Length: $($responseBytes.Length)`r`nConnection: close`r`n`r`n"
            $headerBytes = [System.Text.Encoding]::ASCII.GetBytes($headerText)
            $stream.Write($headerBytes, 0, $headerBytes.Length)
            $stream.Write($responseBytes, 0, $responseBytes.Length)
            $stream.Flush()
        } finally {
            $client.Dispose()
        }
    }
} finally {
    $listener.Stop()
}
'@
    $process = Start-TestPowerShellProcess -ScriptPath $serverPath -Arguments @(
        "-Port", $port,
        "-StatePath", $statePath,
        "-ReadyPath", $readyPath,
        "-FailFirst", $FailFirst,
        "-DelayOnRequest", $DelayOnRequest,
        "-DelayMilliseconds", $DelayMilliseconds,
        "-MaxRequests", $MaxRequests
    )
    Wait-TestCondition { Test-Path -LiteralPath $readyPath -PathType Leaf } 5000 "Fake OpenAI server did not become ready."
    return [pscustomobject]@{ Process = $process; Port = $port; StatePath = $statePath; ReadyPath = $readyPath; ScriptPath = $serverPath }
}

function Get-FakeServerRequestCount([string]$StatePath) {
    if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) { return 0 }
    try { return [int](([System.IO.File]::ReadAllText($StatePath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json).requests) } catch { return 0 }
}

function New-TranslationRunnerArguments([object]$Workspace, [object]$Server, [string]$ArgumentPath, [string]$LogPath, [string]$CancellationPath, [int]$BatchSize = 7, [string]$PreservePath = "") {
    $parameters = [ordered]@{
        ModRoot = $Workspace.ModRoot
        LanguageFolderName = "Korean"
        SourceLanguageFolder = "English"
        ReviewOnly = $true
        ReviewRoot = $Workspace.ReviewBase
        BatchSize = $BatchSize
        TranslationProvider = "OpenAICompatible"
        ProviderName = "LoopbackFixture"
        BaseUrl = "http://127.0.0.1:$($Server.Port)/v1/chat/completions"
        Model = "fixture-model"
        ResponseFormatMode = "JsonSchema"
        CompletionTokenParameter = "max_tokens"
        RequestsPerMinutePerKey = 0
        InputTokensPerMinutePerKey = 0
        DailyTokenBudgetPerKey = 0
        MaxCompletionTokens = 4096
        TimeoutSec = 5
        MaxRetries = 3
        AllowInsecureLoopback = $true
        CancellationFile = $CancellationPath
    }
    if ($PreservePath) {
        $parameters.TranslateMissingOnly = $true
        $parameters.PreserveTranslationFile = $PreservePath
    }
    Write-Utf8Text $ArgumentPath ([ordered]@{ version = 1; parameters = $parameters } | ConvertTo-Json -Depth 6)
    Write-Utf8Text $LogPath ""
    return @(
        "-TranslatorScript", (Join-Path $script:RepoRoot "Invoke-RimWorldAiTranslation.ps1"),
        "-ArgumentFile", $ArgumentPath,
        "-LogFile", $LogPath
    )
}

function Get-SingleReviewRun([string]$ReviewBase) {
    $runs = @(Get-ChildItem -LiteralPath $ReviewBase -Directory -ErrorAction Stop)
    Assert-Equal 1 $runs.Count "Expected exactly one review run."
    $comparison = @(Get-ChildItem -LiteralPath $runs[0].FullName -Recurse -File -Filter "*-comparison.json")
    $progress = @(Get-ChildItem -LiteralPath $runs[0].FullName -Recurse -File -Filter "*-progress.json")
    Assert-Equal 1 $comparison.Count "Review comparison checkpoint is missing."
    Assert-Equal 1 $progress.Count "Review progress checkpoint is missing."
    $parsedRows = [System.IO.File]::ReadAllText($comparison[0].FullName, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $rows = @(foreach ($row in $parsedRows) { $row })
    $parsedProgress = [System.IO.File]::ReadAllText($progress[0].FullName, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $progressRows = @(foreach ($row in $parsedProgress) { $row })
    return [pscustomobject]@{
        Root = $runs[0].FullName
        Rows = $rows
        Progress = $progressRows[0]
    }
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

function Get-DirectoryFingerprint([string]$Root) {
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd("\", "/")
    $prefix = $rootFull + [System.IO.Path]::DirectorySeparatorChar
    $parts = @(Get-ChildItem -LiteralPath $rootFull -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($prefix.Length)
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        "$relative|$($_.Length)|$hash"
    })
    return [string]::Join("`n", $parts)
}

function Test-ProjectCleanupBoundary {
    $workspaceRoot = New-TestWorkspace
    try {
        . (Join-Path $script:RepoRoot "RimWorldAiTranslator.ProjectCleanup.ps1")
        $reviewRoot = Join-Path $workspaceRoot "app-reviews"
        $safeRun = Join-Path $reviewRoot "safe-run"
        $markedRun = Join-Path $reviewRoot "marked-run"
        $outside = Join-Path $workspaceRoot "outside-user-data"
        $modRoot = Join-Path $workspaceRoot "WorkshopMod"
        $koreanFile = Join-Path $modRoot "Languages\Korean\Keyed\Keep.xml"
        foreach ($directory in @($safeRun, $markedRun, $outside)) { [System.IO.Directory]::CreateDirectory($directory) | Out-Null }
        Write-Utf8Text $koreanFile '<LanguageData><Keep.Value>KEEP</Keep.Value></LanguageData>'
        Write-Utf8Text (Join-Path $safeRun "review-decisions.json") '{}'
        Write-Utf8Text (Join-Path $markedRun ".rimworld-ai-project.json") ([ordered]@{ version = 1; projectId = "project-1" } | ConvertTo-Json -Compress)
        Write-Utf8Text (Join-Path $outside "keep.txt") "KEEP_OUTSIDE"
        $project = [pscustomobject]@{
            id = "project-1"
            modRoot = $modRoot
            latestReviewRoot = $safeRun
            runs = @([pscustomobject]@{ reviewRoot = $outside }, [pscustomobject]@{ reviewRoot = $modRoot })
        }

        Assert-True (-not (Test-RimWorldPathStrictlyInsideRoot -Path $reviewRoot -Root $reviewRoot)) "Cleanup accepted the app review root itself."
        Assert-True (-not (Get-RimWorldAppOwnedReviewDirectory -Path $outside -ReviewRoots @($reviewRoot) -ModRoot $modRoot)) "Cleanup accepted an outside directory."
        Assert-True (-not (Get-RimWorldAppOwnedReviewDirectory -Path $modRoot -ReviewRoots @($reviewRoot) -ModRoot $modRoot)) "Cleanup accepted the source mod."
        Assert-Equal ([System.IO.Path]::GetFullPath($safeRun)) (Get-RimWorldAppOwnedReviewDirectory -Path $safeRun -ReviewRoots @($reviewRoot) -ModRoot $modRoot) "Cleanup rejected an app-owned review run."

        $plan = Get-RimWorldProjectCleanupPlan -Project $project -ReviewRoots @($reviewRoot)
        Assert-Contains @($plan.SafePaths) ([System.IO.Path]::GetFullPath($safeRun)) "Recorded app-owned review run was not in the cleanup plan."
        Assert-Contains @($plan.SafePaths) ([System.IO.Path]::GetFullPath($markedRun)) "Marker-owned review run was not in the cleanup plan."
        Assert-Contains @($plan.UnsafePaths) ([System.IO.Path]::GetFullPath($outside)) "Outside user data was not flagged as unsafe."
        Assert-Contains @($plan.UnsafePaths) ([System.IO.Path]::GetFullPath($modRoot)) "Source mod was not flagged as unsafe."

        $failures = @(Remove-RimWorldAppOwnedReviewDirectories -Project $project -ReviewRoots @($reviewRoot) -Paths @($plan.SafePaths))
        Assert-Equal 0 $failures.Count "App-owned review cleanup failed."
        Assert-True (-not (Test-Path -LiteralPath $safeRun)) "Recorded review run was not removed."
        Assert-True (-not (Test-Path -LiteralPath $markedRun)) "Marker-owned review run was not removed."
        Assert-True (Test-Path -LiteralPath $outside -PathType Container) "Cleanup removed outside user data."
        Assert-True (Test-Path -LiteralPath $modRoot -PathType Container) "Cleanup removed the source mod."
        Assert-Equal "KEEP" ([string](Select-Xml -Path $koreanFile -XPath "/LanguageData/Keep.Value").Node.InnerText) "Cleanup changed the Korean translation folder."

        $blocked = @(Remove-RimWorldAppOwnedReviewDirectories -Project $project -ReviewRoots @($reviewRoot) -Paths @($outside, $modRoot))
        Assert-Equal 2 $blocked.Count "Cleanup did not reject unsafe paths at deletion time."
        Assert-True (Test-Path -LiteralPath (Join-Path $outside "keep.txt") -PathType Leaf) "Rejected outside path was modified."
        Assert-True (Test-Path -LiteralPath $koreanFile -PathType Leaf) "Rejected source mod path was modified."
    } finally {
        Remove-TestWorkspace $workspaceRoot
    }
}

function Test-TranslationDryRun {
    $workspace = New-SampleWorkspace
    try {
        $before = Get-DirectoryFingerprint $workspace.ModRoot
        $result = Invoke-RepositoryScript "Invoke-RimWorldAiTranslation.ps1" @(
            "-ModRoot", $workspace.ModRoot,
            "-SourceLanguageFolder", "English",
            "-SourceOnly",
            "-ReviewOnly",
            "-ReviewRoot", $workspace.ReviewBase,
            "-DryRun"
        )
        Assert-Equal 0 $result.ExitCode "Dry run failed. $($result.Text)"
        Assert-True ($result.Text -match "Dry run complete") "Dry run did not report its no-write result."
        Assert-Equal $before (Get-DirectoryFingerprint $workspace.ModRoot) "Dry run changed source mod files."
        Assert-True (-not (Test-Path -LiteralPath $workspace.ReviewBase)) "Dry run created a review output folder."
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $workspace.ModRoot "_TranslationAudit"))) "Dry run created an audit folder in the source mod."
    } finally {
        Remove-TestWorkspace $workspace.Root
    }
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
    $launcherText = [System.IO.File]::ReadAllText((Join-Path $script:RepoRoot "Start-RimWorldAiTranslatorGui.ps1"), [System.Text.Encoding]::UTF8)
    $expectedEscapedText = '\uD30C\uC77C\uC744 \uCC3E\uC744 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4'
    Assert-True ($launcherText.Contains($expectedEscapedText)) "Direct launcher missing-file message does not contain the expected Korean Unicode text."
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

function Test-DefSafety {
    $workspace = New-FixtureWorkspace "DefSafetyMod"
    try {
        $source = Invoke-SourceOnly $workspace
        $keys = @($source.Rows | ForEach-Object { [string]$_.key })
        Assert-Contains $keys "Codex_RenderTree.label" "PawnRenderTreeDef display label was not extracted."
        Assert-Contains $keys "Codex_RenderTree.nodes.0.label" "PawnRenderTreeDef nested display label was not extracted."
        Assert-Contains $keys "Codex_AlienRace.label" "AlienRace display label was not extracted."
        Assert-Contains $keys "Codex_AlienRace.alienRace.generalSettings.alienPartGenerator.bodyAddons.0.label" "AlienRace addon display label was not extracted."
        foreach ($forbiddenKey in @(
            "Codex_RenderTree.renderTree",
            "Codex_RenderTree.name",
            "Codex_RenderTree.nodes.0.tagDef",
            "Codex_RenderTree.nodes.0.texPath",
            "Codex_AlienRace.alienRace.generalSettings.alienPartGenerator.colorChannels.0.name",
            "Codex_AlienRace.alienRace.generalSettings.alienPartGenerator.bodyAddons.0.name"
        )) {
            Assert-True ($forbiddenKey -notin $keys) "Internal Def identifier entered the review list: $forbiddenKey"
        }
        $skippedFiles = @(Get-ChildItem -LiteralPath (Join-Path $source.RunRoot "_TranslationAudit") -File -Filter "*-skipped-internal-identifiers.json")
        Assert-Equal 1 $skippedFiles.Count "Def safety audit file was not created."
        $parsedSkipped = [System.IO.File]::ReadAllText($skippedFiles[0].FullName, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        $skipped = @(foreach ($row in $parsedSkipped) { $row })
        $skippedKeys = @($skipped | ForEach-Object { [string]$_.key })
        Assert-Contains $skippedKeys "Codex_RenderTree.renderTree" "PawnRenderTreeDef runtime field was not recorded in the exclusion audit."
        Assert-Contains $skippedKeys "Codex_AlienRace.alienRace.generalSettings.alienPartGenerator.colorChannels.0.name" "AlienRace color-channel name was not recorded in the exclusion audit."
        foreach ($row in $skipped) {
            Assert-True (-not [string]::IsNullOrWhiteSpace([string]$row.reason)) "Excluded Def row is missing an audit reason."
        }
    } finally {
        Remove-TestWorkspace $workspace.Root
    }
}

function Test-DuplicateIdentity {
    $workspace = New-FixtureWorkspace "DuplicateIdentityMod"
    try {
        $source = Invoke-SourceOnly $workspace
        Assert-Equal 3 $source.Rows.Count "Same key in distinct RimWorld localization namespaces was collapsed."
        $sharedRows = @($source.Rows | Where-Object { $_.key -eq "Shared.label" })
        Assert-Equal 3 $sharedRows.Count "Expected Keyed, ThingDef, and HediffDef rows."
        $keyed = @($sharedRows | Where-Object { $_.kind -eq "Keyed" })
        $thing = @($sharedRows | Where-Object { $_.defClass -eq "ThingDef" })
        $hediff = @($sharedRows | Where-Object { $_.defClass -eq "HediffDef" })
        Assert-Equal 1 $keyed.Count "Duplicate Keyed entry was not collapsed within its namespace."
        Assert-Equal 1 $thing.Count "ThingDef row was lost or duplicated."
        Assert-Equal 1 $hediff.Count "HediffDef row was lost or duplicated."
        Assert-Equal "keyed source first" ([string]$keyed[0].source) "Keyed duplicate selection is not deterministic by source file order."
        Assert-Equal (ConvertFrom-Json '"\uD0A4"') ([string]$keyed[0].existing) "Keyed existing translation crossed namespaces."
        Assert-Equal (ConvertFrom-Json '"\uC0AC\uBB3C"') ([string]$thing[0].existing) "ThingDef existing translation crossed namespaces."
        Assert-Equal (ConvertFrom-Json '"\uC0C1\uD0DC"') ([string]$hediff[0].existing) "HediffDef existing translation crossed namespaces."
        Assert-True ($source.Result.Text -match "Skipped duplicate:\s+1") "Duplicate count did not report the same-namespace duplicate."

        $keyedTarget = Join-Path $workspace.ModRoot "Languages\Korean\Keyed\Existing.xml"
        $thingTarget = Join-Path $workspace.ModRoot "Languages\Korean\DefInjected\ThingDef\Existing.xml"
        $hediffTarget = Join-Path $workspace.ModRoot "Languages\Korean\DefInjected\HediffDef\Existing.xml"
        $reviewLanguageRoot = [System.IO.Path]::GetFullPath((Join-Path $source.RunRoot "Languages\Korean")).TrimEnd("\", "/")
        $reviewPrefix = $reviewLanguageRoot + [System.IO.Path]::DirectorySeparatorChar
        $keyedRelativeTarget = [System.IO.Path]::GetFullPath([string]$keyed[0].target).Substring($reviewPrefix.Length)
        $thingRelativeTarget = [System.IO.Path]::GetFullPath([string]$thing[0].target).Substring($reviewPrefix.Length)
        $hediffRelativeTarget = [System.IO.Path]::GetFullPath([string]$hediff[0].target).Substring($reviewPrefix.Length)
        Assert-Equal "Keyed\Existing.xml" $keyedRelativeTarget "Keyed row did not retain its existing translation file."
        Assert-Equal "DefInjected\ThingDef\Existing.xml" $thingRelativeTarget "ThingDef row did not retain its existing translation file."
        Assert-Equal "DefInjected\HediffDef\Existing.xml" $hediffRelativeTarget "HediffDef row did not retain its existing translation file."

        $keyedNew = ConvertFrom-Json '"\uD0A4\uB4DC \uC218\uC815"'
        $thingNew = ConvertFrom-Json '"\uC0AC\uBB3C \uC218\uC815"'
        $hediffNew = ConvertFrom-Json '"\uC0C1\uD0DC \uC218\uC815"'
        $decisions = New-ReviewDecisions -Source $source -Items @(
            [pscustomobject]@{ Row = $keyed[0]; Text = $keyedNew },
            [pscustomobject]@{ Row = $thing[0]; Text = $thingNew },
            [pscustomobject]@{ Row = $hediff[0]; Text = $hediffNew }
        )
        Write-Utf8Text (Join-Path $source.RunRoot "review-decisions.json") ($decisions | ConvertTo-Json -Depth 8)
        $apply = Invoke-RepositoryScript "Apply-RimWorldAiReviewResults.ps1" @(
            "-ModRoot", $workspace.ModRoot,
            "-ReviewRoot", $source.RunRoot,
            "-Overwrite",
            "-ApplyStatus", "ApprovedOnly"
        )
        Assert-Equal 0 $apply.ExitCode "Namespace-aware local apply failed. $($apply.Text)"
        foreach ($expectation in @(
            [pscustomobject]@{ Path = $keyedTarget; Text = $keyedNew },
            [pscustomobject]@{ Path = $thingTarget; Text = $thingNew },
            [pscustomobject]@{ Path = $hediffTarget; Text = $hediffNew }
        )) {
            $xml = New-Object System.Xml.XmlDocument
            $xml.Load($expectation.Path)
            Assert-Equal ([string]$expectation.Text) ([string]$xml.LanguageData."Shared.label") "Namespace-aware apply wrote the wrong translation: $($expectation.Path)"
        }
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $workspace.ModRoot "Languages\Korean\Keyed\A.xml"))) "Apply created a duplicate Keyed file instead of updating the existing key."
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $workspace.ModRoot "Languages\Korean\DefInjected\ThingDef\SharedDefs.xml"))) "Apply created a duplicate ThingDef file instead of updating the existing key."
    } finally {
        Remove-TestWorkspace $workspace.Root
    }
}

function Test-TokenSafety {
    $workspace = New-FixtureWorkspace "TokenSafetyMod"
    try {
        . (Join-Path $script:RepoRoot "RimWorldAiTranslator.Validation.ps1")
        $source = Invoke-SourceOnly $workspace
        Assert-Equal 4 $source.Rows.Count "Token fixture extraction count changed."
        $rowByKey = @{}
        foreach ($row in $source.Rows) { $rowByKey[[string]$row.key] = $row }
        $validGrammar = ConvertFrom-Json '"r_logentry->[INITIATOR_nameDef](\uC774)\uAC00 [RECIPIENT_nameDef]\uC5D0\uAC8C \uC885\uC774\uC811\uAE30\uB97C \uC92C\uB2E4."'
        $missingLodgerToken = ConvertFrom-Json '"[lodgersLabelSingOrPluralDef](\uC774)\uAC00 \uB3C4\uCC29\uD588\uB2E4. [lodgersObjective](\uC744)\uB97C [shuttleDelayTicks_duration] \uB3D9\uC548 \uBCF4\uD638\uD558\uB77C."'
        $validFormat = ConvertFrom-Json '"{0}(\uC740)\uB294 %s <color=red>$POWER_nameDef$</color>(\uC744)\uB97C \uC0AC\uC6A9\uD55C\uB2E4.\\n\uC900\uBE44\uB428."'
        $invalidParticle = ConvertFrom-Json '"[PAWN_nameDef]\uC740(\uB294) \uC900\uBE44\uB410\uB2E4."'

        Assert-True (Test-RimWorldProtectedTokenStructure -Source ([string]$rowByKey["Token.Grammar"].source) -Translation $validGrammar) "Valid grammar translation failed token validation."
        Assert-Equal 0 @(Get-RimWorldInvalidKoreanParticleNotations $validGrammar).Count "Correct RimWorld particle notation was rejected."
        $lodgerIssues = Get-RimWorldTokenPreservationIssues -Source ([string]$rowByKey["Token.Lodgers"].source) -Target $missingLodgerToken
        Assert-Contains @($lodgerIssues.MissingTokens) "[helpersArrivalLetterEnd]" "Missing bracket token was not detected."
        Assert-True (-not (Test-RimWorldProtectedTokenStructure -Source ([string]$rowByKey["Token.Lodgers"].source) -Translation $missingLodgerToken)) "Missing-token translation was accepted."
        Assert-True ((Get-RimWorldInvalidKoreanParticleNotations $invalidParticle).Count -gt 0) "Reversed Korean particle notation was not detected."
        $movedPrefix = $validGrammar.Substring("r_logentry->".Length) + " r_logentry->"
        $movedIssues = Get-RimWorldTokenPreservationIssues -Source ([string]$rowByKey["Token.Grammar"].source) -Target $movedPrefix
        Assert-True ([bool]$movedIssues.GrammarPrefixMoved) "Moved grammar prefix was not detected."

        $decisions = New-ReviewDecisions -Source $source -Items @(
            [pscustomobject]@{ Row = $rowByKey["Token.Grammar"]; Text = $validGrammar },
            [pscustomobject]@{ Row = $rowByKey["Token.Lodgers"]; Text = $missingLodgerToken },
            [pscustomobject]@{ Row = $rowByKey["Token.Format"]; Text = $validFormat },
            [pscustomobject]@{ Row = $rowByKey["Token.Particle"]; Text = $invalidParticle }
        )
        Write-Utf8Text (Join-Path $source.RunRoot "review-decisions.json") ($decisions | ConvertTo-Json -Depth 8)
        $localApply = Invoke-RepositoryScript "Apply-RimWorldAiReviewResults.ps1" @(
            "-ModRoot", $workspace.ModRoot,
            "-ReviewRoot", $source.RunRoot,
            "-Overwrite",
            "-ApplyStatus", "ApprovedOnly"
        )
        Assert-Equal 0 $localApply.ExitCode "Token-safe local apply failed. $($localApply.Text)"
        $localPath = Join-Path $workspace.ModRoot "Languages\Korean\Keyed\Tokens.xml"
        $localXml = New-Object System.Xml.XmlDocument
        $localXml.Load($localPath)
        Assert-Equal $validGrammar ([string]$localXml.LanguageData."Token.Grammar") "Valid grammar translation was not applied locally."
        Assert-Equal $validFormat ([string]$localXml.LanguageData."Token.Format") "Valid format translation was not applied locally."
        Assert-True ($null -eq $localXml.LanguageData."Token.Lodgers") "Missing-token translation was applied locally."
        Assert-True ($null -eq $localXml.LanguageData."Token.Particle") "Invalid particle translation was applied locally."

        $rmkRoot = Join-Path $workspace.Root "RmkTokenEntry"
        [System.IO.Directory]::CreateDirectory($rmkRoot) | Out-Null
        $rmkExport = Invoke-RepositoryScript "Export-RimWorldAiReviewToRmk.ps1" @(
            "-RmkEntryRoot", $rmkRoot,
            "-ReviewRoot", $source.RunRoot,
            "-RmkLanguageFolderName", "Korean",
            "-WorkbookPath", (Join-Path $rmkRoot "history.xlsx"),
            "-Overwrite",
            "-ApplyStatus", "ApprovedOnly"
        )
        Assert-Equal 0 $rmkExport.ExitCode "Token-safe RMK export failed. $($rmkExport.Text)"
        $rmkXml = New-Object System.Xml.XmlDocument
        $rmkXml.Load((Join-Path $rmkRoot "Languages\Korean\Keyed\Tokens.xml"))
        Assert-Equal $validGrammar ([string]$rmkXml.LanguageData."Token.Grammar") "RMK accepted a different grammar decision than local apply."
        Assert-Equal $validFormat ([string]$rmkXml.LanguageData."Token.Format") "RMK accepted a different format decision than local apply."
        Assert-True ($null -eq $rmkXml.LanguageData."Token.Lodgers") "RMK applied a missing-token translation."
        Assert-True ($null -eq $rmkXml.LanguageData."Token.Particle") "RMK applied an invalid particle translation."
    } finally {
        Remove-TestWorkspace $workspace.Root
    }
}

function Test-ApiRetryCancellationAndResume {
    $secret = "TEST_LOOPBACK_KEY_" + [Guid]::NewGuid().ToString("N")

    $retryWorkspace = New-SampleWorkspace
    $retryServer = $null
    try {
        $retryServer = New-FakeOpenAiServer -WorkspaceRoot $retryWorkspace.Root -FailFirst 1 -MaxRequests 2
        $argumentPath = Join-Path $retryWorkspace.Root "retry-arguments.json"
        $logPath = Join-Path $retryWorkspace.Root "retry.log"
        $cancellationPath = Join-Path $retryWorkspace.Root "retry-cancel.signal"
        $runnerArguments = New-TranslationRunnerArguments -Workspace $retryWorkspace -Server $retryServer -ArgumentPath $argumentPath -LogPath $logPath -CancellationPath $cancellationPath
        $result = Invoke-RepositoryScript "Run-RimWorldAiTranslation.ps1" $runnerArguments @{
            RIMWORLD_TRANSLATOR_API_KEYS = $secret
            CEREBRAS_API_KEY = ""
        }
        $retryLog = [System.IO.File]::ReadAllText($logPath, [System.Text.Encoding]::UTF8)
        Assert-Equal 0 $result.ExitCode "Loopback retry translation failed. $($result.Text) $retryLog"
        Assert-Equal 2 (Get-FakeServerRequestCount $retryServer.StatePath) "Transient API failure used an unexpected number of attempts."
        $retryRun = Get-SingleReviewRun $retryWorkspace.ReviewBase
        Assert-True ([bool]$retryRun.Progress.complete) "Successful retry did not mark the checkpoint complete."
        Assert-Equal 1 ([int]$retryRun.Progress.completedBatches) "Successful retry checkpoint batch count is wrong."
        Assert-Equal 7 $retryRun.Rows.Count "Successful retry lost comparison rows."
        Assert-Equal 7 @($retryRun.Rows | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.candidate) }).Count "Successful retry did not preserve all candidates."
        Assert-True (-not $retryLog.Contains($secret)) "Loopback API key leaked into the translation log."
        Assert-Equal 1 @([regex]::Matches($retryLog, "retrying")).Count "Transient failure was retried by nested retry loops."
    } finally {
        if ($retryServer) { Stop-TestProcess $retryServer.Process }
        Remove-TestWorkspace $retryWorkspace.Root
    }

    $cancelWorkspace = New-SampleWorkspace
    $cancelServer = $null
    $runnerProcess = $null
    try {
        $cancelServer = New-FakeOpenAiServer -WorkspaceRoot $cancelWorkspace.Root -DelayOnRequest 2 -DelayMilliseconds 1200 -MaxRequests 2
        $argumentPath = Join-Path $cancelWorkspace.Root "cancel-arguments.json"
        $logPath = Join-Path $cancelWorkspace.Root "cancel.log"
        $cancellationPath = Join-Path $cancelWorkspace.Root "cancel.signal"
        $runnerArguments = New-TranslationRunnerArguments -Workspace $cancelWorkspace -Server $cancelServer -ArgumentPath $argumentPath -LogPath $logPath -CancellationPath $cancellationPath -BatchSize 2
        $runnerProcess = Start-TestPowerShellProcess -ScriptPath (Join-Path $script:RepoRoot "Run-RimWorldAiTranslation.ps1") -Arguments $runnerArguments -Environment @{
            RIMWORLD_TRANSLATOR_API_KEYS = $secret
            CEREBRAS_API_KEY = ""
        }
        Wait-TestCondition { (Get-FakeServerRequestCount $cancelServer.StatePath) -ge 2 } 10000 "Translation did not reach the delayed second batch."
        Write-Utf8Text $cancellationPath "cancel"
        Assert-True ($runnerProcess.WaitForExit(10000)) "Cancelled translation process did not exit promptly."
        Assert-True ($runnerProcess.ExitCode -ne 0) "Cancelled translation unexpectedly returned success."
        $runnerProcess.Dispose()
        $runnerProcess = $null

        $partialRun = Get-SingleReviewRun $cancelWorkspace.ReviewBase
        Assert-True (-not [bool]$partialRun.Progress.complete) "Cancelled checkpoint was marked complete."
        Assert-Equal 1 ([int]$partialRun.Progress.completedBatches) "Cancelled run did not stop after the last completed batch."
        Assert-Equal 2 $partialRun.Rows.Count "Cancelled run did not preserve exactly the completed batch."
        Assert-Equal 2 @($partialRun.Rows | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.candidate) }).Count "Cancelled run lost completed candidates."
        $sourceKoreanRoot = Join-Path $cancelWorkspace.ModRoot "Languages\Korean"
        $sourceKoreanFiles = if (Test-Path -LiteralPath $sourceKoreanRoot -PathType Container) { @(Get-ChildItem -LiteralPath $sourceKoreanRoot -Recurse -File) } else { @() }
        Assert-Equal 0 $sourceKoreanFiles.Count "Cancelled review-only translation modified the source mod."

        $partialLanguageRoot = [System.IO.Path]::GetFullPath((Join-Path $partialRun.Root "Languages\Korean")).TrimEnd("\", "/")
        $partialPrefix = $partialLanguageRoot + [System.IO.Path]::DirectorySeparatorChar
        $preservedItems = @($partialRun.Rows | ForEach-Object {
            $target = [System.IO.Path]::GetFullPath([string]$_.target)
            Assert-True ($target.StartsWith($partialPrefix, [System.StringComparison]::OrdinalIgnoreCase)) "Partial review target escaped its language root."
            [ordered]@{
                key = [string]$_.key
                kind = [string]$_.kind
                defClass = [string]$_.defClass
                target = $target.Substring($partialPrefix.Length)
                text = [string]$_.candidate
                origin = "ai"
            }
        })
        $preservePath = Join-Path $cancelWorkspace.Root "preserved-partial.json"
        Write-Utf8Text $preservePath ([ordered]@{ version = 1; items = $preservedItems } | ConvertTo-Json -Depth 6)

        $resumeWorkspace = [pscustomobject]@{
            Root = $cancelWorkspace.Root
            ModRoot = $cancelWorkspace.ModRoot
            ReviewBase = Join-Path $cancelWorkspace.Root "reviews-resumed"
        }
        $resumeServer = $null
        try {
            $resumeServer = New-FakeOpenAiServer -WorkspaceRoot $cancelWorkspace.Root -MaxRequests 3
            $resumeArgumentPath = Join-Path $cancelWorkspace.Root "resume-arguments.json"
            $resumeLogPath = Join-Path $cancelWorkspace.Root "resume.log"
            $resumeCancellationPath = Join-Path $cancelWorkspace.Root "resume-cancel.signal"
            $resumeArguments = New-TranslationRunnerArguments -Workspace $resumeWorkspace -Server $resumeServer -ArgumentPath $resumeArgumentPath -LogPath $resumeLogPath -CancellationPath $resumeCancellationPath -BatchSize 2 -PreservePath $preservePath
            $resumeResult = Invoke-RepositoryScript "Run-RimWorldAiTranslation.ps1" $resumeArguments @{
                RIMWORLD_TRANSLATOR_API_KEYS = $secret
                CEREBRAS_API_KEY = ""
            }
            Assert-Equal 0 $resumeResult.ExitCode "Translation did not resume from the partial checkpoint. $($resumeResult.Text)"
            Assert-Equal 3 (Get-FakeServerRequestCount $resumeServer.StatePath) "Resume retranslated preserved entries or skipped missing entries."
            $resumedRun = Get-SingleReviewRun $resumeWorkspace.ReviewBase
            Assert-True ([bool]$resumedRun.Progress.complete) "Resumed translation checkpoint is incomplete."
            Assert-Equal 7 $resumedRun.Rows.Count "Resumed translation lost rows."
            Assert-Equal 2 @($resumedRun.Rows | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.candidate) -and -not [string]::IsNullOrWhiteSpace([string]$_.existing) }).Count "Resumed translation did not reuse the completed batch."
            Assert-Equal 5 @($resumedRun.Rows | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.candidate) }).Count "Resumed translation did not translate only missing rows."
            $resumeLog = [System.IO.File]::ReadAllText($resumeLogPath, [System.Text.Encoding]::UTF8)
            Assert-True (-not $resumeLog.Contains($secret)) "API key leaked during resumed translation."
        } finally {
            if ($resumeServer) { Stop-TestProcess $resumeServer.Process }
        }
    } finally {
        if ($runnerProcess) { Stop-TestProcess $runnerProcess }
        if ($cancelServer) { Stop-TestProcess $cancelServer.Process }
        Remove-TestWorkspace $cancelWorkspace.Root
    }
}

function Test-DirectOutputTransaction {
    $workspace = New-SampleWorkspace
    $server = $null
    try {
        $defPath = Join-Path $workspace.ModRoot "Languages\Korean\DefInjected\ThingDef\CodexAI.xml"
        Write-Utf8Text $defPath @'
<?xml version="1.0" encoding="utf-8"?>
<LanguageData>
  <Existing.Rollback>BEFORE</Existing.Rollback>
  <Codex_TestWorkbench.label>OLD_LABEL</Codex_TestWorkbench.label>
</LanguageData>
'@
        $defBefore = [System.IO.File]::ReadAllBytes($defPath)
        $blockedKeyedPath = Join-Path $workspace.ModRoot "Languages\Korean\Keyed\SampleKeys.xml"
        [System.IO.Directory]::CreateDirectory($blockedKeyedPath) | Out-Null

        $server = New-FakeOpenAiServer -WorkspaceRoot $workspace.Root -MaxRequests 1
        $argumentPath = Join-Path $workspace.Root "direct-failure-arguments.json"
        $logPath = Join-Path $workspace.Root "direct-failure.log"
        $parameters = [ordered]@{
            ModRoot = $workspace.ModRoot
            LanguageFolderName = "Korean"
            SourceLanguageFolder = "English"
            BatchSize = 7
            TranslationProvider = "OpenAICompatible"
            ProviderName = "LoopbackFixture"
            BaseUrl = "http://127.0.0.1:$($server.Port)/v1/chat/completions"
            Model = "fixture-model"
            ResponseFormatMode = "JsonSchema"
            CompletionTokenParameter = "max_tokens"
            RequestsPerMinutePerKey = 0
            InputTokensPerMinutePerKey = 0
            DailyTokenBudgetPerKey = 0
            MaxCompletionTokens = 4096
            TimeoutSec = 5
            MaxRetries = 2
            AllowInsecureLoopback = $true
            Overwrite = $true
        }
        Write-Utf8Text $argumentPath ([ordered]@{ version = 1; parameters = $parameters } | ConvertTo-Json -Depth 6)
        Write-Utf8Text $logPath ""
        $runnerArguments = @(
            "-TranslatorScript", (Join-Path $script:RepoRoot "Invoke-RimWorldAiTranslation.ps1"),
            "-ArgumentFile", $argumentPath,
            "-LogFile", $logPath
        )
        $secret = "TEST_DIRECT_KEY_" + [Guid]::NewGuid().ToString("N")
        $failed = Invoke-RepositoryScript "Run-RimWorldAiTranslation.ps1" $runnerArguments @{
            RIMWORLD_TRANSLATOR_API_KEYS = $secret
            CEREBRAS_API_KEY = ""
        }
        $failureLog = [System.IO.File]::ReadAllText($logPath, [System.Text.Encoding]::UTF8)
        Assert-True ($failed.ExitCode -ne 0) "Injected direct-output failure unexpectedly succeeded."
        Assert-True ($failureLog -match "rolled back") "Direct-output failure did not report rollback."
        Assert-True (-not $failureLog.Contains($secret)) "Direct-output failure log exposed the API key."
        Assert-Equal ([Convert]::ToBase64String($defBefore)) ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($defPath))) "Direct-output rollback did not restore the first file."
        Assert-True (Test-Path -LiteralPath $blockedKeyedPath -PathType Container) "Direct-output rollback changed the blocking directory."
        Assert-Equal 0 @(Get-ChildItem -LiteralPath (Join-Path $workspace.ModRoot "Languages\Korean") -Recurse -File -Filter "*.transaction.bak").Count "Direct-output transaction snapshots were left behind."
        $progressFile = @(Get-ChildItem -LiteralPath (Join-Path $workspace.ModRoot "_TranslationAudit") -File -Filter "*-progress.json" | Sort-Object LastWriteTimeUtc -Descending)[0]
        $failedProgress = @([System.IO.File]::ReadAllText($progressFile.FullName, [System.Text.Encoding]::UTF8) | ConvertFrom-Json)[0]
        Assert-True (-not [bool]$failedProgress.complete) "Failed direct output was marked complete."
        Assert-Equal 1 ([int]$failedProgress.completedBatches) "Failed direct output lost its completed API batch checkpoint."
        Stop-TestProcess $server.Process
        $server = $null

        Remove-Item -LiteralPath $blockedKeyedPath -Recurse -Force
        $successServer = $null
        try {
            $successServer = New-FakeOpenAiServer -WorkspaceRoot $workspace.Root -MaxRequests 1
            $parameters.BaseUrl = "http://127.0.0.1:$($successServer.Port)/v1/chat/completions"
            $successArgumentPath = Join-Path $workspace.Root "direct-success-arguments.json"
            $successLogPath = Join-Path $workspace.Root "direct-success.log"
            Write-Utf8Text $successArgumentPath ([ordered]@{ version = 1; parameters = $parameters } | ConvertTo-Json -Depth 6)
            Write-Utf8Text $successLogPath ""
            $successArguments = @(
                "-TranslatorScript", (Join-Path $script:RepoRoot "Invoke-RimWorldAiTranslation.ps1"),
                "-ArgumentFile", $successArgumentPath,
                "-LogFile", $successLogPath
            )
            $succeeded = Invoke-RepositoryScript "Run-RimWorldAiTranslation.ps1" $successArguments @{
                RIMWORLD_TRANSLATOR_API_KEYS = $secret
                CEREBRAS_API_KEY = ""
            }
            $successLog = [System.IO.File]::ReadAllText($successLogPath, [System.Text.Encoding]::UTF8)
            Assert-Equal 0 $succeeded.ExitCode "Direct output did not recover after rollback. $successLog"
            $defXml = New-Object System.Xml.XmlDocument
            $defXml.Load($defPath)
            Assert-Equal "BEFORE" ([string]$defXml.LanguageData."Existing.Rollback") "Successful direct output removed an unrelated existing translation."
            Assert-True ([string]$defXml.LanguageData."Codex_TestWorkbench.label" -match "^\uBC88\uC5ED") "Successful direct output did not write the translated Def value."
            $keyedXml = New-Object System.Xml.XmlDocument
            $keyedXml.Load($blockedKeyedPath)
            Assert-True ([string]$keyedXml.LanguageData."CodexTranslator.SampleButton" -match "^\uBC88\uC5ED") "Successful direct output did not write the translated Keyed value."
            Assert-True (Test-Path -LiteralPath "$defPath.bak" -PathType Leaf) "Successful direct output did not retain a persistent backup."
            Assert-Equal 0 @(Get-ChildItem -LiteralPath (Join-Path $workspace.ModRoot "Languages\Korean") -Recurse -File -Filter "*.transaction.bak").Count "Successful direct output left transaction snapshots."
        } finally {
            if ($successServer) { Stop-TestProcess $successServer.Process }
        }
    } finally {
        if ($server) { Stop-TestProcess $server.Process }
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

function Test-RmkSourceHistoryRoundTrip {
    $workspace = New-SampleWorkspace
    try {
        $initial = Invoke-SourceOnly $workspace
        $initialRow = @($initial.Rows | Where-Object { $_.key -eq "CodexTranslator.SampleButton" })[0]
        Assert-True ($null -ne $initialRow) "RMK history source row is missing."
        $translation = ConvertFrom-Json '"\uBC88\uC5ED \uC2DC\uC791"'
        Write-Utf8Text (Join-Path $initial.RunRoot "review-decisions.json") ((New-ReviewDecisions -Source $initial -Items @(
            [pscustomobject]@{ Row = $initialRow; Text = $translation }
        )) | ConvertTo-Json -Depth 8)

        $rmkRoot = Join-Path $workspace.Root "RmkHistoryEntry"
        [System.IO.Directory]::CreateDirectory($rmkRoot) | Out-Null
        $workbookPath = Join-Path $rmkRoot "history.xlsx"
        $firstExport = Invoke-RepositoryScript "Export-RimWorldAiReviewToRmk.ps1" @(
            "-RmkEntryRoot", $rmkRoot,
            "-ReviewRoot", $initial.RunRoot,
            "-RmkLanguageFolderName", "Korean",
            "-WorkbookPath", $workbookPath,
            "-SourceLanguage", "English",
            "-Overwrite",
            "-ApplyStatus", "ApprovedOnly"
        )
        Assert-Equal 0 $firstExport.ExitCode "Initial RMK history export failed. $($firstExport.Text)"
        Add-RmkWorkbookMetadataFixture $workbookPath
        $before = Get-RmkWorkbookMetadataSnapshot $workbookPath
        foreach ($property in @("ExtraColumn", "Style", "LegacyDrawing", "Comment", "Styles", "SheetRelationships")) {
            Assert-True ([bool]$before.$property) "RMK metadata fixture was not created: $property"
        }

        $englishPath = Join-Path $workspace.ModRoot "Languages\English\Keyed\SampleKeys.xml"
        $english = [System.IO.File]::ReadAllText($englishPath, [System.Text.Encoding]::UTF8)
        $english = $english.Replace("Translate now", "Translate immediately")
        Write-Utf8Text $englishPath $english

        $updatedWorkspace = [pscustomobject]@{
            Root = $workspace.Root
            ModRoot = $workspace.ModRoot
            ReviewBase = Join-Path $workspace.Root "reviews-updated"
        }
        $referenceRoot = Join-Path $rmkRoot "Languages\Korean"
        $updated = Invoke-SourceOnly $updatedWorkspace @(
            "-ReferenceLanguageRoot", $referenceRoot,
            "-ReferenceSourceWorkbook", $workbookPath
        )
        $updatedRow = @($updated.Rows | Where-Object { $_.key -eq "CodexTranslator.SampleButton" })[0]
        Assert-True ($null -ne $updatedRow) "Updated RMK source row is missing."
        Assert-Equal $translation ([string]$updatedRow.existing) "RMK translation was not imported after a source update."
        Assert-Equal "rmk" ([string]$updatedRow.existingOrigin) "RMK translation origin was lost."
        Assert-True ([bool]$updatedRow.rmkSourceChanged) "RMK source change was not detected."
        Assert-Equal "Translate now" ([string]$updatedRow.rmkHistoricalSource) "Historical RMK source was overwritten."
        Assert-Equal "Translate immediately" ([string]$updatedRow.rmkCurrentSource) "Current source was not captured for RMK comparison."

        $reviewLanguageRoot = [System.IO.Path]::GetFullPath((Join-Path $updated.RunRoot "Languages\Korean")).TrimEnd("\", "/")
        $reviewLanguagePrefix = $reviewLanguageRoot + [System.IO.Path]::DirectorySeparatorChar
        $relativeTarget = [System.IO.Path]::GetFullPath([string]$updatedRow.target).Substring($reviewLanguagePrefix.Length)
        $pendingDecisions = [ordered]@{
            version = 5
            sparse = $true
            reviewRoot = $updated.RunRoot
            comparison = $updated.Comparison
            updatedAt = [DateTime]::UtcNow.ToString("o")
            items = @([ordered]@{
                id = $updatedRow.id
                key = $updatedRow.key
                target = $relativeTarget
                status = "pending"
                text = $translation
                sourceText = $updatedRow.source
                previousSourceText = $updatedRow.rmkHistoricalSource
                sourceChanged = $true
            })
        }
        Write-Utf8Text (Join-Path $updated.RunRoot "review-decisions.json") ($pendingDecisions | ConvertTo-Json -Depth 8)
        $secondExport = Invoke-RepositoryScript "Export-RimWorldAiReviewToRmk.ps1" @(
            "-RmkEntryRoot", $rmkRoot,
            "-ReviewRoot", $updated.RunRoot,
            "-RmkLanguageFolderName", "Korean",
            "-WorkbookPath", $workbookPath,
            "-SourceLanguage", "English",
            "-Overwrite",
            "-ApplyStatus", "ApprovedOnly"
        )
        Assert-Equal 0 $secondExport.ExitCode "RMK history preservation export failed. $($secondExport.Text)"

        $after = Get-RmkWorkbookMetadataSnapshot $workbookPath
        foreach ($property in @("ExtraColumn", "Style", "LegacyDrawing", "Comment", "Styles", "SheetRelationships")) {
            Assert-True ([bool]$after.$property) "RMK workbook update removed metadata: $property"
        }
        $rmkXml = New-Object System.Xml.XmlDocument
        $rmkXml.Load((Join-Path $referenceRoot "Keyed\SampleKeys.xml"))
        Assert-Equal $translation ([string]$rmkXml.LanguageData."CodexTranslator.SampleButton") "Pending changed source overwrote the RMK translation."

        $verifyWorkspace = [pscustomobject]@{
            Root = $workspace.Root
            ModRoot = $workspace.ModRoot
            ReviewBase = Join-Path $workspace.Root "reviews-verify"
        }
        $verified = Invoke-SourceOnly $verifyWorkspace @(
            "-ReferenceLanguageRoot", $referenceRoot,
            "-ReferenceSourceWorkbook", $workbookPath
        )
        $verifiedRow = @($verified.Rows | Where-Object { $_.key -eq "CodexTranslator.SampleButton" })[0]
        Assert-True ([bool]$verifiedRow.rmkSourceChanged) "Unreviewed RMK source change was cleared by export."
        Assert-Equal "Translate now" ([string]$verifiedRow.rmkHistoricalSource) "RMK history no longer contains the translation-time source."
        Assert-Equal "Translate immediately" ([string]$verifiedRow.rmkCurrentSource) "RMK current source comparison changed unexpectedly."
    } finally {
        Remove-TestWorkspace $workspace.Root
    }
}

$tests = @(
    [pscustomobject]@{ Name = "Harness.Isolation"; Suite = "Harness"; Body = { Test-HarnessIsolation } },
    [pscustomobject]@{ Name = "Syntax.PowerShell"; Suite = "Syntax"; Body = { Test-PowerShellSyntax } },
    [pscustomobject]@{ Name = "StateStore.Recovery"; Suite = "StateStore"; Body = { Test-StateStoreRecovery } },
    [pscustomobject]@{ Name = "Security.ApiKeyHandling"; Suite = "SecretHandling"; Body = { Test-SecretHandling } },
    [pscustomobject]@{ Name = "Project.CleanupBoundary"; Suite = "ProjectCleanup"; Body = { Test-ProjectCleanupBoundary } },
    [pscustomobject]@{ Name = "Translation.DryRun"; Suite = "DryRun"; Body = { Test-TranslationDryRun } },
    [pscustomobject]@{ Name = "Source.Extraction"; Suite = "SourceExtraction"; Body = { Test-SourceExtraction } },
    [pscustomobject]@{ Name = "Source.DefSafety"; Suite = "DefSafety"; Body = { Test-DefSafety } },
    [pscustomobject]@{ Name = "Source.DuplicateIdentity"; Suite = "DuplicateIdentity"; Body = { Test-DuplicateIdentity } },
    [pscustomobject]@{ Name = "Translation.TokenSafety"; Suite = "TokenSafety"; Body = { Test-TokenSafety } },
    [pscustomobject]@{ Name = "Translation.ApiResilience"; Suite = "ApiResilience"; Body = { Test-ApiRetryCancellationAndResume } },
    [pscustomobject]@{ Name = "Translation.DirectOutputTransaction"; Suite = "DirectOutput"; Body = { Test-DirectOutputTransaction } },
    [pscustomobject]@{ Name = "Apply.Local"; Suite = "LocalApply"; Body = { Test-LocalApply } },
    [pscustomobject]@{ Name = "Apply.LocalRollback"; Suite = "LocalRollback"; Body = { Test-LocalApplyRollback } },
    [pscustomobject]@{ Name = "Export.RmkTransaction"; Suite = "RmkExport"; Body = { Test-RmkExportTransaction } },
    [pscustomobject]@{ Name = "Export.RmkSourceHistory"; Suite = "RmkHistory"; Body = { Test-RmkSourceHistoryRoundTrip } }
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
