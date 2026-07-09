if ([System.Threading.Thread]::CurrentThread.ApartmentState -ne "STA") {
    $self = $PSCommandPath
    if (-not $self) { $self = $MyInvocation.MyCommand.Path }
    Start-Process -FilePath "powershell.exe" -ArgumentList @("-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-File", "`"$self`"") -WindowStyle Hidden
    return
}

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$translatorScript = Join-Path $scriptRoot "Invoke-RimWorldAiTranslation.ps1"
$reviewApplyScript = Join-Path $scriptRoot "Apply-RimWorldAiReviewResults.ps1"

$script:process = $null
$script:processExitHandled = $false
$script:tempFiles = New-Object "System.Collections.Generic.List[string]"
$script:logFilePath = ""
$script:logFileOffset = 0L
$script:logPartialLine = ""
$script:lastLogWasSuppressed = $false
$script:startedAt = $null
$script:curatedGlossaryPath = ""
$script:stopRequested = $false

function New-Font([float]$Size, [System.Drawing.FontStyle]$Style = [System.Drawing.FontStyle]::Regular) {
    return New-Object System.Drawing.Font("Malgun Gothic", $Size, $Style)
}

function New-TextBox([int]$X, [int]$Y, [int]$W, [int]$H, [switch]$Multiline) {
    $box = New-Object System.Windows.Forms.TextBox
    $box.Location = New-Object System.Drawing.Point($X, $Y)
    $box.Size = New-Object System.Drawing.Size($W, $H)
    $box.Font = New-Font 10
    $box.BorderStyle = [System.Windows.Forms.BorderStyle]::FixedSingle
    $box.BackColor = [System.Drawing.Color]::White
    if ($Multiline) {
        $box.Multiline = $true
        $box.AcceptsReturn = $true
        $box.AcceptsTab = $true
        $box.ScrollBars = [System.Windows.Forms.ScrollBars]::Vertical
    }
    return $box
}

function New-Label([string]$Text, [int]$X, [int]$Y, [int]$W, [int]$H, [System.Drawing.Color]$Color, [float]$Size = 10, [System.Drawing.FontStyle]$Style = [System.Drawing.FontStyle]::Regular) {
    $label = New-Object System.Windows.Forms.Label
    $label.Text = $Text
    $label.Location = New-Object System.Drawing.Point($X, $Y)
    $label.Size = New-Object System.Drawing.Size($W, $H)
    $label.Font = New-Font $Size $Style
    $label.ForeColor = $Color
    $label.BackColor = [System.Drawing.Color]::Transparent
    return $label
}

function New-Button([string]$Text, [int]$X, [int]$Y, [int]$W, [int]$H, [System.Drawing.Color]$BackColor) {
    $button = New-Object System.Windows.Forms.Button
    $button.Text = $Text
    $button.Location = New-Object System.Drawing.Point($X, $Y)
    $button.Size = New-Object System.Drawing.Size($W, $H)
    $button.Font = New-Font 10 ([System.Drawing.FontStyle]::Bold)
    $button.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
    $button.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(60, 60, 60)
    $button.FlatAppearance.BorderSize = 1
    $button.BackColor = $BackColor
    $button.ForeColor = [System.Drawing.Color]::FromArgb(25, 25, 25)
    return $button
}

function Quote-Argument([string]$Value) {
    if ($null -eq $Value) { return '""' }
    return '"' + ($Value -replace '"', '\"') + '"'
}

function Quote-CmdArgument([string]$Value) {
    if ($null -eq $Value) { return '""' }
    return '"' + ($Value -replace '"', '\"') + '"'
}

function Get-ApiKeyLines([string]$Text) {
    $keys = New-Object "System.Collections.Generic.List[string]"
    foreach ($line in ([System.Text.RegularExpressions.Regex]::Split($Text, "\r\n|\n|\r"))) {
        $trim = $line.Trim()
        if (-not $trim -or $trim.StartsWith("#")) { continue }

        if ($trim -match "^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.+)\s*$") {
            $trim = $matches[2].Trim()
        }
        $trim = $trim.Trim('"').Trim("'")
        if ($trim.StartsWith("Bearer ", [System.StringComparison]::OrdinalIgnoreCase)) {
            $trim = $trim.Substring(7).Trim()
        }

        foreach ($part in ($trim -split "[,;]")) {
            $key = $part.Trim().Trim('"').Trim("'")
            if ($key) { [void]$keys.Add($key) }
        }
    }
    return $keys.ToArray()
}

function New-TempFilePath([string]$Prefix, [string]$Extension) {
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) "RimWorldAiTranslatorGui"
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    return Join-Path $dir ("{0}-{1}{2}" -f $Prefix, [System.Guid]::NewGuid().ToString("N"), $Extension)
}

function Remove-TempFiles {
    foreach ($path in @($script:tempFiles)) {
        try {
            if ($path -and (Test-Path -LiteralPath $path)) {
                Remove-Item -LiteralPath $path -Force
            }
        } catch {
        }
    }
    $script:tempFiles.Clear()
}

function Stop-ProcessTree([int]$ProcessId) {
    if ($ProcessId -le 0) { return }

    $children = @()
    try {
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $ProcessId" -ErrorAction SilentlyContinue)
    } catch {
        $children = @()
    }

    foreach ($child in $children) {
        Stop-ProcessTree ([int]$child.ProcessId)
    }

    try {
        $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($proc -and -not $proc.HasExited) {
            Stop-Process -Id $ProcessId -Force -ErrorAction Stop
        }
    } catch {
    }
}

function ConvertTo-GuiLogLine([string]$Text) {
    if ($null -eq $Text) { return "" }
    $line = $Text.TrimEnd()
    if ([string]::IsNullOrWhiteSpace($line)) { return "" }

    if ($line -match '^\s*\{"translations"\s*:\s*\[') {
        return "[로그 축약] 모델 응답 원문은 숨겼습니다. 재시도 로그와 감사 파일을 확인하세요."
    }
    if ($line -match '(\\u000a\s*){6,}') {
        return "[로그 축약] 모델 응답의 반복 개행 escape를 숨겼습니다."
    }
    if ($line.Length -gt 1000) {
        return $line.Substring(0, 1000) + "... [로그 축약]"
    }
    return $line
}

function Add-Log([string]$Text) {
    if ($null -eq $Text) { return }
    $Text = ConvertTo-GuiLogLine $Text
    if ([string]::IsNullOrWhiteSpace($Text)) { return }
    $isSuppressed = $Text.StartsWith("[로그 축약]", [System.StringComparison]::Ordinal)
    if ($isSuppressed -and $script:lastLogWasSuppressed) { return }
    $script:lastLogWasSuppressed = $isSuppressed

    $stamp = Get-Date -Format "HH:mm:ss"
    $txtLog.AppendText("[$stamp] $Text`r`n")
    $txtLog.SelectionStart = $txtLog.TextLength
    $txtLog.ScrollToCaret()
}

function Read-NewProcessLogLines {
    $lines = New-Object "System.Collections.Generic.List[string]"
    if (-not $script:logFilePath -or -not (Test-Path -LiteralPath $script:logFilePath)) { return $lines.ToArray() }

    try {
        $fs = [System.IO.File]::Open($script:logFilePath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            if ($fs.Length -le $script:logFileOffset) { return $lines.ToArray() }
            [void]$fs.Seek($script:logFileOffset, [System.IO.SeekOrigin]::Begin)
            $count = [int]($fs.Length - $script:logFileOffset)
            $bytes = New-Object byte[] $count
            $read = $fs.Read($bytes, 0, $count)
            $script:logFileOffset += $read
            if ($read -le 0) { return $lines.ToArray() }

            $text = [System.Text.Encoding]::UTF8.GetString($bytes, 0, $read)
            $combined = $script:logPartialLine + $text
            $parts = [System.Text.RegularExpressions.Regex]::Split($combined, "\r?\n")
            if ($combined -match "\r?\n$") {
                $script:logPartialLine = ""
                $complete = $parts
            } else {
                $script:logPartialLine = $parts[$parts.Count - 1]
                $complete = if ($parts.Count -gt 1) { $parts[0..($parts.Count - 2)] } else { @() }
            }

            foreach ($line in $complete) {
                if ($line.Length -gt 0) { [void]$lines.Add($line) }
            }
        } finally {
            $fs.Close()
        }
    } catch {
    }

    return $lines.ToArray()
}

function Set-RunningState([bool]$Running) {
    $btnStart.Enabled = -not $Running
    $btnStop.Enabled = $Running
    $btnBrowse.Enabled = -not $Running
    $btnGlossary.Enabled = -not $Running
    $btnApplyReview.Enabled = -not $Running
    $txtModRoot.Enabled = -not $Running
    $txtApiKeys.Enabled = -not $Running
    $txtExtraPrompt.Enabled = -not $Running
    $chkReviewOnly.Enabled = -not $Running
    $chkOverwrite.Enabled = -not $Running
    $chkDryRun.Enabled = -not $Running
    $chkIncludePatches.Enabled = -not $Running
}

function Update-ProgressFromLine([string]$Line) {
    if ($Line -match "Translating batch\s+(\d+)/(\d+)\s+\((\d+)\s+entries\)") {
        $current = [int]$matches[1]
        $total = [int]$matches[2]
        if ($total -gt 0) {
            $progress.Maximum = $total
            $progress.Value = [Math]::Min($current, $total)
            $lblStatus.Text = "번역 배치 $current / $total"
        }
    } elseif ($Line -match "^Waiting\s+(.+)$") {
        $lblStatus.Text = $Line
    } elseif ($Line -match "^Done\.$") {
        if ($progress.Maximum -gt 0) { $progress.Value = $progress.Maximum }
        $lblStatus.Text = "완료"
    } elseif ($Line -match "^Source entries:\s+(.+)$") {
        $lblStatus.Text = "원문 추출: $($matches[1])개"
    } elseif ($Line -match "^Detected source language:\s+(.+)$") {
        $lblStatus.Text = "원문 언어: $($matches[1])"
    } elseif ($Line -match "^Pending entries:\s+(.+)$") {
        $lblStatus.Text = "번역 대상: $($matches[1])개"
    } elseif ($Line -match "^Review output:\s+(.+)$") {
        $lblStatus.Text = "리뷰 출력 생성됨"
    }
}

function Clear-EventSubscriptions {
    Get-EventSubscriber -ErrorAction SilentlyContinue |
        Where-Object { $_.SourceIdentifier -like "*RimWorldAiTranslatorGui*" } |
        ForEach-Object {
            try { Unregister-Event -SubscriptionId $_.SubscriptionId -ErrorAction SilentlyContinue } catch {}
        }
}

function Start-Translation {
    if ($script:process -and -not $script:process.HasExited) {
        [System.Windows.Forms.MessageBox]::Show("이미 실행 중입니다.", "RimWorld AI Translator") | Out-Null
        return
    }

    $modRoot = $txtModRoot.Text.Trim()
    if (-not $modRoot -or -not (Test-Path -LiteralPath $modRoot)) {
        [System.Windows.Forms.MessageBox]::Show("모드 폴더 위치를 먼저 선택하세요.", "RimWorld AI Translator") | Out-Null
        return
    }

    $keys = @(Get-ApiKeyLines $txtApiKeys.Text)
    if ($keys.Count -eq 0 -and -not $chkDryRun.Checked) {
        [System.Windows.Forms.MessageBox]::Show("API 키를 한 줄에 하나씩 입력하세요.", "RimWorld AI Translator") | Out-Null
        return
    }

    Remove-TempFiles
    Clear-EventSubscriptions

    $logFile = New-TempFilePath "run-output" ".log"
    [System.IO.File]::WriteAllText($logFile, "", [System.Text.UTF8Encoding]::new($false))
    [void]$script:tempFiles.Add($logFile)
    $script:logFilePath = $logFile
    $script:logFileOffset = 0L
    $script:logPartialLine = ""
    $script:lastLogWasSuppressed = $false

    $promptFile = ""
    if (-not [string]::IsNullOrWhiteSpace($txtExtraPrompt.Text)) {
        $promptFile = New-TempFilePath "extra-prompt" ".txt"
        [System.IO.File]::WriteAllText($promptFile, $txtExtraPrompt.Text, [System.Text.UTF8Encoding]::new($false))
        [void]$script:tempFiles.Add($promptFile)
    }

    $args = New-Object "System.Collections.Generic.List[string]"
    foreach ($item in @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $translatorScript, "-ModRoot", $modRoot, "-LanguageFolderName", "Korean", "-MaxGeneratedGlossaryTermsPerBatch", "40")) {
        [void]$args.Add($item)
    }
    if ($promptFile) {
        [void]$args.Add("-ExtraPromptFile")
        [void]$args.Add($promptFile)
    }
    if ($script:curatedGlossaryPath) {
        [void]$args.Add("-UseCuratedGlossary")
        [void]$args.Add("-CuratedGlossaryPath")
        [void]$args.Add($script:curatedGlossaryPath)
    }
    if ($chkReviewOnly.Checked) { [void]$args.Add("-ReviewOnly") }
    if ($chkOverwrite.Checked) { [void]$args.Add("-Overwrite") }
    if ($chkDryRun.Checked) { [void]$args.Add("-DryRun") }
    if ($chkIncludePatches.Checked) { [void]$args.Add("-IncludePatches") }

    $txtLog.Clear()
    Add-Log "번역기를 시작합니다."
    Add-Log "모드: $modRoot"
    Add-Log "API 키: $($keys.Count)개 입력됨. 키 값은 로그에 남기지 않습니다."
    if ($keys.Count -gt 1) { Add-Log "여러 키는 입력 순서를 기준으로 요청 수/제한 상태에 맞춰 순환 사용됩니다." }
    if ($promptFile) { Add-Log "추가 프롬프트가 적용됩니다." }
    if ($script:curatedGlossaryPath) { Add-Log "추가 용어집: $script:curatedGlossaryPath" }

    $progress.Value = 0
    $progress.Maximum = 100
    $lblStatus.Text = "실행 준비 중"
    Set-RunningState $true

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $command = (Quote-CmdArgument "powershell.exe") + " " + ([string]::Join(" ", @($args | ForEach-Object { Quote-CmdArgument $_ }))) + " > " + (Quote-CmdArgument $logFile) + " 2>&1"
    $psi.FileName = "cmd.exe"
    $psi.Arguments = "/d /s /c `"$command`""
    $psi.WorkingDirectory = $scriptRoot
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $false
    $psi.RedirectStandardError = $false
    $psi.CreateNoWindow = $true
    if ($keys.Count -gt 0) {
        $psi.EnvironmentVariables["RIMWORLD_TRANSLATOR_API_KEYS"] = [string]::Join("`n", $keys)
    }

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    $proc.EnableRaisingEvents = $true
    $script:process = $proc
    $script:startedAt = Get-Date
    $script:processExitHandled = $false
    $script:stopRequested = $false

    try {
        [void]$proc.Start()
        Add-Log "자식 PowerShell 프로세스 시작됨. PID=$($proc.Id)"
    } catch {
        Set-RunningState $false
        Add-Log "실행 실패: $($_.Exception.Message)"
        Remove-TempFiles
    }
}

function Stop-Translation {
    if ($script:process -and -not $script:process.HasExited) {
        try {
            $script:stopRequested = $true
            $btnStop.Enabled = $false
            $lblStatus.Text = "중지 요청 중"
            Add-Log "사용자 요청으로 실행 중지를 요청했습니다."
            Stop-ProcessTree $script:process.Id
        } catch {
            Add-Log "중지 실패: $($_.Exception.Message)"
        }
    }
}

function Apply-ReviewResults {
    if ($script:process -and -not $script:process.HasExited) {
        [System.Windows.Forms.MessageBox]::Show("번역 실행 중에는 검토 결과를 적용할 수 없습니다.", "RimWorld AI Translator") | Out-Null
        return
    }

    $modRoot = $txtModRoot.Text.Trim()
    if (-not $modRoot -or -not (Test-Path -LiteralPath $modRoot)) {
        [System.Windows.Forms.MessageBox]::Show("먼저 적용할 모드 폴더 위치를 선택하세요.", "RimWorld AI Translator") | Out-Null
        return
    }
    if (-not (Test-Path -LiteralPath $reviewApplyScript)) {
        [System.Windows.Forms.MessageBox]::Show("검토 결과 적용 스크립트를 찾을 수 없습니다.`r`n$reviewApplyScript", "RimWorld AI Translator") | Out-Null
        return
    }

    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = "적용할 reviews\\모드명-날짜 검토 결과 폴더를 선택하세요."
    $reviewsRoot = Join-Path $scriptRoot "reviews"
    if (Test-Path -LiteralPath $reviewsRoot) {
        $dlg.SelectedPath = $reviewsRoot
    }
    if ($dlg.ShowDialog($form) -ne [System.Windows.Forms.DialogResult]::OK) { return }

    $reviewRoot = $dlg.SelectedPath
    $args = New-Object "System.Collections.Generic.List[string]"
    foreach ($item in @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $reviewApplyScript, "-ModRoot", $modRoot, "-ReviewRoot", $reviewRoot, "-LanguageFolderName", "Korean")) {
        [void]$args.Add($item)
    }
    if ($chkOverwrite.Checked) { [void]$args.Add("-Overwrite") }

    Add-Log "검토 결과 적용을 시작합니다."
    Add-Log "검토 폴더: $reviewRoot"
    Add-Log "대상 모드: $modRoot"
    if ($chkOverwrite.Checked) {
        Add-Log "기존 번역도 덮어씁니다."
    } else {
        Add-Log "기존 번역은 유지하고 없는 키만 적용합니다."
    }

    $lblStatus.Text = "검토 결과 적용 중"
    Set-RunningState $true
    try {
        $output = & powershell.exe @($args.ToArray()) 2>&1
        $exitCode = $LASTEXITCODE
        foreach ($line in @($output)) {
            Add-Log ([string]$line)
        }
        if ($exitCode -eq 0) {
            $lblStatus.Text = "검토 결과 적용 완료"
            Add-Log "검토 결과 적용 완료."
        } else {
            $lblStatus.Text = "적용 실패"
            Add-Log "검토 결과 적용 실패. ExitCode=$exitCode"
        }
    } catch {
        $lblStatus.Text = "적용 실패"
        Add-Log "검토 결과 적용 실패: $($_.Exception.Message)"
    } finally {
        Set-RunningState $false
    }
}

$form = New-Object System.Windows.Forms.Form
$form.Text = "RimWorld AI Translator"
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
$form.AutoScaleMode = [System.Windows.Forms.AutoScaleMode]::Dpi
$form.ClientSize = New-Object System.Drawing.Size(980, 640)
$form.MinimumSize = New-Object System.Drawing.Size(1000, 680)
$form.BackColor = [System.Drawing.Color]::FromArgb(248, 247, 242)
$form.Font = New-Font 10

$title = New-Label "RimWorld AI Translator" 28 18 420 34 ([System.Drawing.Color]::FromArgb(20, 20, 20)) 15 ([System.Drawing.FontStyle]::Bold)
$form.Controls.Add($title)

$lblMod = New-Label "모드 폴더 위치" 55 66 160 26 ([System.Drawing.Color]::FromArgb(230, 75, 42)) 11 ([System.Drawing.FontStyle]::Bold)
$txtModRoot = New-TextBox 55 96 555 32
$txtModRoot.AllowDrop = $true
$txtModRoot.Add_DragEnter({
    if ($_.Data.GetDataPresent([System.Windows.Forms.DataFormats]::FileDrop)) {
        $_.Effect = [System.Windows.Forms.DragDropEffects]::Copy
    }
})
$txtModRoot.Add_DragDrop({
    $files = $_.Data.GetData([System.Windows.Forms.DataFormats]::FileDrop)
    if ($files -and $files.Count -gt 0) { $txtModRoot.Text = $files[0] }
})
$btnBrowse = New-Button "찾기" 620 95 82 34 ([System.Drawing.Color]::FromArgb(255, 235, 225))
$btnBrowse.Add_Click({
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = "번역할 RimWorld 모드 폴더를 선택하세요."
    if ($txtModRoot.Text.Trim() -and (Test-Path -LiteralPath $txtModRoot.Text.Trim())) {
        $dlg.SelectedPath = $txtModRoot.Text.Trim()
    }
    if ($dlg.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
        $txtModRoot.Text = $dlg.SelectedPath
    }
})
$form.Controls.AddRange(@($lblMod, $txtModRoot, $btnBrowse))

$lblApi = New-Label "API 키 입력 (Enter로 여러 개)" 55 162 320 26 ([System.Drawing.Color]::FromArgb(20, 20, 20)) 10.5 ([System.Drawing.FontStyle]::Bold)
$txtApiKeys = New-TextBox 55 192 600 118 -Multiline
$form.Controls.AddRange(@($lblApi, $txtApiKeys))

$lblPrompt = New-Label "추가 프롬프트 입력" 735 66 190 26 ([System.Drawing.Color]::FromArgb(20, 20, 180)) 10.5 ([System.Drawing.FontStyle]::Bold)
$txtExtraPrompt = New-TextBox 735 96 210 145 -Multiline
$txtExtraPrompt.Text = ""
$form.Controls.AddRange(@($lblPrompt, $txtExtraPrompt))

$btnGlossary = New-Button "추가 용어집 로드" 735 265 210 42 ([System.Drawing.Color]::FromArgb(255, 238, 222))
$lblGlossary = New-Label "공식 본편+DLC 용어집만 기본 사용" 735 314 220 38 ([System.Drawing.Color]::FromArgb(95, 95, 95)) 8.5
$btnGlossary.Add_Click({
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Title = "추가 용어집 선택"
    $dlg.Filter = "Glossary files (*.txt;*.tsv;*.json)|*.txt;*.tsv;*.json|Text glossary (*.txt)|*.txt|JSON glossary (*.json)|*.json|All files (*.*)|*.*"
    $dlg.InitialDirectory = $scriptRoot
    if ($dlg.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
        $script:curatedGlossaryPath = $dlg.FileName
        $lblGlossary.Text = "추가 용어집 적용:`r`n$([System.IO.Path]::GetFileName($dlg.FileName))"
    }
})
$form.Controls.AddRange(@($btnGlossary, $lblGlossary))

$chkReviewOnly = New-Object System.Windows.Forms.CheckBox
$chkReviewOnly.Text = "비교/검토 모드"
$chkReviewOnly.Location = New-Object System.Drawing.Point(55, 335)
$chkReviewOnly.Size = New-Object System.Drawing.Size(130, 24)
$chkReviewOnly.BackColor = [System.Drawing.Color]::Transparent
$chkReviewOnly.Font = New-Font 9

$chkOverwrite = New-Object System.Windows.Forms.CheckBox
$chkOverwrite.Text = "기존 번역 덮어쓰기"
$chkOverwrite.Location = New-Object System.Drawing.Point(190, 335)
$chkOverwrite.Size = New-Object System.Drawing.Size(150, 24)
$chkOverwrite.BackColor = [System.Drawing.Color]::Transparent
$chkOverwrite.Font = New-Font 9

$chkDryRun = New-Object System.Windows.Forms.CheckBox
$chkDryRun.Text = "Dry run"
$chkDryRun.Location = New-Object System.Drawing.Point(345, 335)
$chkDryRun.Size = New-Object System.Drawing.Size(80, 24)
$chkDryRun.BackColor = [System.Drawing.Color]::Transparent
$chkDryRun.Font = New-Font 9

$chkIncludePatches = New-Object System.Windows.Forms.CheckBox
$chkIncludePatches.Text = "Patches 포함"
$chkIncludePatches.Location = New-Object System.Drawing.Point(430, 335)
$chkIncludePatches.Size = New-Object System.Drawing.Size(120, 24)
$chkIncludePatches.BackColor = [System.Drawing.Color]::Transparent
$chkIncludePatches.Font = New-Font 9

$btnApplyReview = New-Button "검토 결과 적용" 565 370 150 38 ([System.Drawing.Color]::FromArgb(226, 236, 248))
$btnStart = New-Button "번역 시작" 735 370 120 38 ([System.Drawing.Color]::FromArgb(226, 244, 230))
$btnStop = New-Button "중지" 865 370 80 38 ([System.Drawing.Color]::FromArgb(245, 224, 224))
$btnStop.Enabled = $false
$btnApplyReview.Add_Click({ Apply-ReviewResults })
$btnStart.Add_Click({ Start-Translation })
$btnStop.Add_Click({ Stop-Translation })
$form.Controls.AddRange(@($chkReviewOnly, $chkOverwrite, $chkDryRun, $chkIncludePatches, $btnApplyReview, $btnStart, $btnStop))

$lblProgress = New-Label "진행도 및 Debug" 55 420 170 24 ([System.Drawing.Color]::FromArgb(20, 20, 20)) 10.5 ([System.Drawing.FontStyle]::Bold)
$lblStatus = New-Label "대기 중" 225 422 720 24 ([System.Drawing.Color]::FromArgb(70, 70, 70)) 9
$progress = New-Object System.Windows.Forms.ProgressBar
$progress.Location = New-Object System.Drawing.Point(55, 450)
$progress.Size = New-Object System.Drawing.Size(890, 18)
$progress.Minimum = 0
$progress.Maximum = 100
$progress.Value = 0

$txtLog = New-TextBox 55 480 890 120 -Multiline
$txtLog.ReadOnly = $true
$txtLog.BackColor = [System.Drawing.Color]::FromArgb(252, 252, 252)
$txtLog.Font = New-Object System.Drawing.Font("Consolas", 9)
$form.Controls.AddRange(@($lblProgress, $lblStatus, $progress, $txtLog))

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 250
$timer.Add_Tick({
    foreach ($line in (Read-NewProcessLogLines)) {
        Add-Log $line
        Update-ProgressFromLine $line
    }

    if ($script:process -and $script:process.HasExited -and -not $script:processExitHandled) {
        foreach ($line in (Read-NewProcessLogLines)) {
            Add-Log $line
            Update-ProgressFromLine $line
        }
        if (-not [string]::IsNullOrEmpty($script:logPartialLine)) {
            Add-Log $script:logPartialLine
            Update-ProgressFromLine $script:logPartialLine
            $script:logPartialLine = ""
        }
        $script:processExitHandled = $true
        $exitCode = $script:process.ExitCode
        $elapsed = if ($script:startedAt) { [Math]::Round(((Get-Date) - $script:startedAt).TotalSeconds, 1) } else { 0 }
        Add-Log "프로세스 종료. ExitCode=$exitCode, 경과 ${elapsed}s"
        if ($script:stopRequested) {
            $lblStatus.Text = "중지됨"
            Add-Log "사용자 요청으로 중지 완료."
        } elseif ($exitCode -eq 0) {
            if ($progress.Maximum -gt 0) { $progress.Value = $progress.Maximum }
            $lblStatus.Text = "완료"
        } else {
            $lblStatus.Text = "종료 코드 $exitCode"
        }
        try { $script:process.Dispose() } catch {}
        $script:stopRequested = $false
        Set-RunningState $false
        Remove-TempFiles
        Clear-EventSubscriptions
    }
})
$timer.Start()

$form.Add_FormClosing({
    if ($script:process -and -not $script:process.HasExited) {
        try { Stop-ProcessTree $script:process.Id } catch {}
    }
    Remove-TempFiles
    Clear-EventSubscriptions
})

Add-Log "프로그램 시작 안내"
Add-Log "1. 모드 폴더를 선택하거나 위 칸에 드래그하세요."
Add-Log "2. API 키는 한 줄에 하나씩 입력하세요. 여러 개면 입력 순서를 기준으로 순환 사용합니다."
Add-Log "3. 추가 프롬프트와 추가 용어집은 선택 사항입니다. 기본은 공식 본편+DLC 용어집만 사용합니다."
Add-Log "4. 바로 쓰기가 부담되면 먼저 Dry run 또는 비교/검토 모드로 확인하세요."
Add-Log "5. 검토 결과가 마음에 들면 검토 결과 적용 버튼으로 안전 후보만 적용할 수 있습니다."

[void][System.Windows.Forms.Application]::Run($form)
