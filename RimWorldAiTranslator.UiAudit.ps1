$ErrorActionPreference = "Stop"

if (-not ("RimWorldTranslatorCaptureMethods" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
public struct RimWorldTranslatorCaptureRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

public static class RimWorldTranslatorCaptureMethods
{
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RimWorldTranslatorCaptureRect value, int valueSize);
}
"@
}

function Get-AccessibilityAuditRows([System.Windows.Forms.Control]$Parent, [string]$ParentPath = "Form") {
    $rows = New-Object "System.Collections.Generic.List[object]"
    $index = 0
    foreach ($control in $Parent.Controls) {
        $typeName = $control.GetType().Name
        $controlPath = "$ParentPath/$typeName`[$index`]"
        $interactive = $control.TabStop -or $control -is [System.Windows.Forms.Button] -or $control -is [System.Windows.Forms.TextBoxBase] -or $control -is [System.Windows.Forms.ComboBox] -or $control -is [System.Windows.Forms.CheckBox] -or $control -is [System.Windows.Forms.TabControl] -or $control -is [System.Windows.Forms.ListView]
        $layoutDetail = ""
        if ($control -is [System.Windows.Forms.TabControl]) {
            $layoutDetail = "client=$($control.ClientSize.Width)x$($control.ClientSize.Height);item=$($control.ItemSize.Width)x$($control.ItemSize.Height);pages=$($control.TabPages.Count)"
        }
        $clipped = $false
        if ($control.Visible -and -not $Parent.AutoScroll -and $Parent.ClientSize.Width -gt 0 -and $Parent.ClientSize.Height -gt 0) {
            $clipped = $control.Left -lt 0 -or $control.Top -lt 0 -or $control.Right -gt $Parent.ClientSize.Width -or $control.Bottom -gt $Parent.ClientSize.Height
        }
        $textClipped = $false
        $textMeasure = ""
        $visibleText = [string]$control.Text
        if ($control.Visible -and -not [string]::IsNullOrWhiteSpace($visibleText) -and $control.Font) {
            $measureWidth = 0
            $measureHeight = 0
            $availableWidth = 0
            $availableHeight = 0
            $flags = [System.Windows.Forms.TextFormatFlags]::NoPrefix -bor [System.Windows.Forms.TextFormatFlags]::NoPadding -bor [System.Windows.Forms.TextFormatFlags]::SingleLine
            $shouldMeasure = $false
            if ($control -is [System.Windows.Forms.Button]) {
                $availableWidth = [Math]::Max(1, $control.ClientSize.Width - $control.Padding.Horizontal - 12)
                $availableHeight = [Math]::Max(1, $control.ClientSize.Height - $control.Padding.Vertical - 8)
                $shouldMeasure = $true
            } elseif ($control -is [System.Windows.Forms.CheckBox]) {
                $availableWidth = [Math]::Max(1, $control.ClientSize.Width - $control.Padding.Horizontal - 22)
                $availableHeight = [Math]::Max(1, $control.ClientSize.Height - $control.Padding.Vertical - 4)
                $shouldMeasure = $true
            } elseif ($control -is [System.Windows.Forms.ComboBox]) {
                $availableWidth = [Math]::Max(1, $control.ClientSize.Width - 30)
                $availableHeight = [Math]::Max(1, $control.ClientSize.Height - 6)
                $shouldMeasure = $true
            } elseif ($control -is [System.Windows.Forms.Label] -and -not $control.AutoSize -and -not $control.AutoEllipsis) {
                $availableWidth = [Math]::Max(1, $control.ClientSize.Width - $control.Padding.Horizontal)
                $availableHeight = [Math]::Max(1, $control.ClientSize.Height - $control.Padding.Vertical)
                $shouldMeasure = $true
                $flags = [System.Windows.Forms.TextFormatFlags]::NoPrefix -bor [System.Windows.Forms.TextFormatFlags]::NoPadding -bor [System.Windows.Forms.TextFormatFlags]::WordBreak
            }
            if ($shouldMeasure) {
                $constraint = if (($flags -band [System.Windows.Forms.TextFormatFlags]::WordBreak) -ne 0) {
                    [System.Drawing.Size]::new($availableWidth, [int]::MaxValue)
                } else {
                    [System.Drawing.Size]::new([int]::MaxValue, [int]::MaxValue)
                }
                $measured = [System.Windows.Forms.TextRenderer]::MeasureText($visibleText, $control.Font, $constraint, $flags)
                $measureWidth = $measured.Width
                $measureHeight = $measured.Height
                $textClipped = $measureWidth -gt ($availableWidth + 2) -or $measureHeight -gt ($availableHeight + 2)
                $textMeasure = "measured=${measureWidth}x${measureHeight};available=${availableWidth}x${availableHeight}"
            }
        }
        [void]$rows.Add([pscustomobject]@{
            path = $controlPath
            type = $typeName
            visible = [bool]$control.Visible
            enabled = [bool]$control.Enabled
            interactive = [bool]$interactive
            tabStop = [bool]$control.TabStop
            tabIndex = [int]$control.TabIndex
            accessibleName = [string]$control.AccessibleName
            accessibleDescription = [string]$control.AccessibleDescription
            bounds = "$($control.Left),$($control.Top),$($control.Width),$($control.Height)"
            parentClient = "$($Parent.ClientSize.Width),$($Parent.ClientSize.Height)"
            clipped = [bool]$clipped
            textClipped = [bool]$textClipped
            textMeasure = $textMeasure
            layoutDetail = $layoutDetail
        })
        foreach ($child in @(Get-AccessibilityAuditRows -Parent $control -ParentPath $controlPath)) {
            [void]$rows.Add($child)
        }
        $index++
    }
    return $rows.ToArray()
}

function Get-PerformanceStatistics([double[]]$Values) {
    $ordered = @($Values | Sort-Object)
    if ($ordered.Count -eq 0) { return [pscustomobject]@{ medianMs = 0; maxMs = 0; samples = @() } }
    $middle = [int][Math]::Floor($ordered.Count / 2)
    $median = if (($ordered.Count % 2) -eq 0) { ($ordered[$middle - 1] + $ordered[$middle]) / 2.0 } else { $ordered[$middle] }
    return [pscustomobject]@{
        medianMs = [Math]::Round($median, 3)
        maxMs = [Math]::Round([double]$ordered[-1], 3)
        samples = @($Values | ForEach-Object { [Math]::Round($_, 3) })
    }
}

function Write-WorkspacePerformanceReport([string]$Path, [int]$Iterations) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not $script:reviewRoot) { return }
    $initialReviewLoad = $script:lastReviewLoadMetrics
    $visibleReloadWatch = [System.Diagnostics.Stopwatch]::StartNew()
    Load-ReviewRoot $script:reviewRoot -SkipPreviousDecisions
    $visibleReloadWatch.Stop()
    $visibleReviewReload = [pscustomobject]@{
        totalMilliseconds = [Math]::Round($visibleReloadWatch.Elapsed.TotalMilliseconds, 3)
        rows = $script:rows.Count
        atomicCoverUsed = [bool]$script:lastReviewLoadMetrics.atomicCoverUsed
    }
    $contentBoundsBeforeOperation = $main.Bounds
    Show-OperationOverlay -Title "Source preparation" -Detail "Verifying stable content bounds while status is visible." -OperationType "Audit"
    [System.Windows.Forms.Application]::DoEvents()
    $contentBoundsDuringOperation = $main.Bounds
    Hide-OperationOverlay
    $contentBoundsAfterOperation = $main.Bounds
    $operationLayout = [pscustomobject]@{
        before = $contentBoundsBeforeOperation.ToString()
        during = $contentBoundsDuringOperation.ToString()
        after = $contentBoundsAfterOperation.ToString()
        stableWhileVisible = $contentBoundsBeforeOperation.Equals($contentBoundsDuringOperation)
        stableAfterHide = $contentBoundsBeforeOperation.Equals($contentBoundsAfterOperation)
    }
    $searchTimes = New-Object "System.Collections.Generic.List[double]"
    $nextTimes = New-Object "System.Collections.Generic.List[double]"
    $saveTimes = New-Object "System.Collections.Generic.List[double]"
    $noChangeSaveTimes = New-Object "System.Collections.Generic.List[double]"
    $searchCases = New-Object "System.Collections.Generic.List[object]"
    $statusFilterMatches = [ordered]@{}
    $originalSearch = [string]$txtSearch.Text
    $originalStatus = $cmbStatus.SelectedIndex
    $originalRow = $script:currentRowIndex
    try {
        for ($iteration = 0; $iteration -lt $Iterations; $iteration++) {
            $txtSearch.Text = if (($iteration % 2) -eq 0) { "needle" } else { "Synthetic" }
            if ($searchTimer) { $searchTimer.Stop() }
            $watch = [System.Diagnostics.Stopwatch]::StartNew()
            Refresh-ItemList -SelectRowIndex $script:currentRowIndex
            $watch.Stop()
            [void]$searchTimes.Add($watch.Elapsed.TotalMilliseconds)
            [void]$searchCases.Add([pscustomobject]@{ query = [string]$txtSearch.Text; matches = [int]$flowItems.Items.Count })
        }
        $txtSearch.Text = ""
        if ($searchTimer) { $searchTimer.Stop() }
        Refresh-ItemList -SelectRowIndex $originalRow

        foreach ($statusIndex in @(1, 2)) {
            if ($statusIndex -ge $cmbStatus.Items.Count) { continue }
            $statusName = [string]$cmbStatus.Items[$statusIndex]
            $script:loading = $true
            try { $cmbStatus.SelectedIndex = $statusIndex } finally { $script:loading = $false }
            Refresh-ItemList -SelectRowIndex $originalRow
            $statusFilterMatches[$statusName] = [int]$flowItems.Items.Count
        }
        $script:loading = $true
        try { $cmbStatus.SelectedIndex = $originalStatus } finally { $script:loading = $false }
        Refresh-ItemList -SelectRowIndex $originalRow

        for ($iteration = 0; $iteration -lt $Iterations; $iteration++) {
            $watch = [System.Diagnostics.Stopwatch]::StartNew()
            for ($move = 0; $move -lt 25; $move++) { Move-Selection 1 }
            $watch.Stop()
            [void]$nextTimes.Add($watch.Elapsed.TotalMilliseconds / 25.0)
        }

        for ($iteration = 0; $iteration -lt $Iterations; $iteration++) {
            $watch = [System.Diagnostics.Stopwatch]::StartNew()
            Save-Decisions
            $watch.Stop()
            [void]$noChangeSaveTimes.Add($watch.Elapsed.TotalMilliseconds)
        }

        for ($iteration = 0; $iteration -lt $Iterations; $iteration++) {
            $script:dirty = $true
            $watch = [System.Diagnostics.Stopwatch]::StartNew()
            Save-Decisions
            $watch.Stop()
            [void]$saveTimes.Add($watch.Elapsed.TotalMilliseconds)
        }
    } finally {
        $txtSearch.Text = $originalSearch
        $cmbStatus.SelectedIndex = $originalStatus
        if ($searchTimer) { $searchTimer.Stop() }
        Refresh-ItemList -SelectRowIndex $originalRow
    }
    $process = [System.Diagnostics.Process]::GetCurrentProcess()
    $dpiX = 0.0
    $dpiY = 0.0
    $graphics = $null
    try {
        $graphics = $form.CreateGraphics()
        $dpiX = [Math]::Round([double]$graphics.DpiX, 1)
        $dpiY = [Math]::Round([double]$graphics.DpiY, 1)
    } finally {
        if ($graphics) { $graphics.Dispose() }
    }
    $report = [ordered]@{
        version = 1
        measuredAt = [DateTime]::UtcNow.ToString("o")
        rows = $script:rows.Count
        iterations = $Iterations
        reviewLoad = $initialReviewLoad
        visibleReviewReload = $visibleReviewReload
        operationLayout = $operationLayout
        search = Get-PerformanceStatistics -Values ($searchTimes.ToArray())
        searchCases = $searchCases.ToArray()
        statusFilterMatches = $statusFilterMatches
        nextItem = Get-PerformanceStatistics -Values ($nextTimes.ToArray())
        save = Get-PerformanceStatistics -Values ($saveTimes.ToArray())
        saveNoChange = Get-PerformanceStatistics -Values ($noChangeSaveTimes.ToArray())
        dpiX = $dpiX
        dpiY = $dpiY
        workingSetMb = [Math]::Round($process.WorkingSet64 / 1MB, 2)
        privateMemoryMb = [Math]::Round($process.PrivateMemorySize64 / 1MB, 2)
    }
    Write-Utf8JsonFile -Path ([System.IO.Path]::GetFullPath($Path)) -Value $report -Depth 8
    Add-Log ("Performance audit complete: search median {0:n1}ms, next item {1:n1}ms, save {2:n1}ms" -f $report.search.medianMs, $report.nextItem.medianMs, $report.save.medianMs)
}

function Start-WorkspaceLayoutSnapshot {
    if ([string]::IsNullOrWhiteSpace($script:layoutSnapshotPath)) { return }
    $script:layoutSnapshotTimer = [System.Windows.Forms.Timer]::new()
    $script:layoutSnapshotTimer.Interval = 1200
    $script:layoutSnapshotTimer.Add_Tick({
        $script:layoutSnapshotTimer.Stop()
        try {
            $snapshotDir = Split-Path -Parent $script:layoutSnapshotPath
            if ($snapshotDir -and -not (Test-Path -LiteralPath $snapshotDir)) {
                New-Item -ItemType Directory -Force -Path $snapshotDir | Out-Null
            }
            $form.Invalidate($true)
            $form.Update()
            [System.Windows.Forms.Application]::DoEvents()
            [System.Threading.Thread]::Sleep(180)
            $form.Refresh()
            [System.Windows.Forms.Application]::DoEvents()
            $bitmap = [System.Drawing.Bitmap]::new($form.ClientSize.Width, $form.ClientSize.Height)
            try {
                $captured = $false
                $screenGraphics = $null
                try {
                    if ($script:previewPreflightDialog -and $script:previewPreflightDialog.Visible) {
                        $script:previewPreflightDialog.Activate()
                        $script:previewPreflightDialog.BringToFront()
                    } elseif ($script:previewCommandPaletteDialog -and $script:previewCommandPaletteDialog.Visible) {
                        $script:previewCommandPaletteDialog.Activate()
                        $script:previewCommandPaletteDialog.BringToFront()
                    } else {
                        $form.Activate()
                        $form.BringToFront()
                    }
                    [System.Windows.Forms.Application]::DoEvents()
                    $clientOrigin = $form.PointToScreen([System.Drawing.Point]::Empty)
                    $clientEnd = $form.PointToScreen([System.Drawing.Point]::new($form.ClientSize.Width, $form.ClientSize.Height))
                    $physicalWidth = [Math]::Max(1, $clientEnd.X - $clientOrigin.X)
                    $physicalHeight = [Math]::Max(1, $clientEnd.Y - $clientOrigin.Y)
                    $frameRect = [RimWorldTranslatorCaptureRect]::new()
                    $frameResult = [RimWorldTranslatorCaptureMethods]::DwmGetWindowAttribute(
                        $form.Handle,
                        9,
                        [ref]$frameRect,
                        [System.Runtime.InteropServices.Marshal]::SizeOf([type][RimWorldTranslatorCaptureRect])
                    )
                    if ($frameResult -eq 0 -and $form.Bounds.Width -gt 0 -and $form.Bounds.Height -gt 0) {
                        $scaleX = ($frameRect.Right - $frameRect.Left) / [double]$form.Bounds.Width
                        $scaleY = ($frameRect.Bottom - $frameRect.Top) / [double]$form.Bounds.Height
                        if ($scaleX -ge 0.75 -and $scaleX -le 3.0 -and $scaleY -ge 0.75 -and $scaleY -le 3.0) {
                            $relativeClientX = $clientOrigin.X - $form.Bounds.Left
                            $relativeClientY = $clientOrigin.Y - $form.Bounds.Top
                            $clientOrigin = [System.Drawing.Point]::new(
                                [int][Math]::Round($frameRect.Left + ($relativeClientX * $scaleX)),
                                [int][Math]::Round($frameRect.Top + ($relativeClientY * $scaleY))
                            )
                            $physicalWidth = [Math]::Max(1, [int][Math]::Round($form.ClientSize.Width * $scaleX))
                            $physicalHeight = [Math]::Max(1, [int][Math]::Round($form.ClientSize.Height * $scaleY))
                        }
                    }
                    $screenBitmap = [System.Drawing.Bitmap]::new($physicalWidth, $physicalHeight)
                    try {
                        $screenGraphics = [System.Drawing.Graphics]::FromImage($screenBitmap)
                        $screenGraphics.CopyFromScreen($clientOrigin, [System.Drawing.Point]::Empty, [System.Drawing.Size]::new($physicalWidth, $physicalHeight), [System.Drawing.CopyPixelOperation]::SourceCopy)
                        $screenGraphics.Dispose()
                        $screenGraphics = [System.Drawing.Graphics]::FromImage($bitmap)
                        $screenGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                        $screenGraphics.DrawImage($screenBitmap, [System.Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height))
                    } finally {
                        $screenBitmap.Dispose()
                    }
                    $captured = $true
                } catch {
                    $captured = $false
                } finally {
                    if ($screenGraphics) { $screenGraphics.Dispose() }
                }
                if (-not $captured) {
                    $rect = [System.Drawing.Rectangle]::new(0, 0, $form.ClientSize.Width, $form.ClientSize.Height)
                    $form.DrawToBitmap($bitmap, $rect)
                }
                $bitmap.Save($script:layoutSnapshotPath, [System.Drawing.Imaging.ImageFormat]::Png)
            } finally {
                $bitmap.Dispose()
            }
            $auditPath = [System.IO.Path]::ChangeExtension($script:layoutSnapshotPath, ".accessibility.json")
            $auditRows = New-Object "System.Collections.Generic.List[object]"
            foreach ($auditRow in @(Get-AccessibilityAuditRows -Parent $form)) { [void]$auditRows.Add($auditRow) }
            foreach ($previewDialog in @($script:previewPreflightDialog, $script:previewCommandPaletteDialog)) {
                if (-not $previewDialog -or -not $previewDialog.Visible) { continue }
                foreach ($auditRow in @(Get-AccessibilityAuditRows -Parent $previewDialog -ParentPath $previewDialog.Text)) { [void]$auditRows.Add($auditRow) }
            }
            [System.IO.File]::WriteAllText(
                $auditPath,
                ($auditRows.ToArray() | ConvertTo-Json -Depth 5),
                (New-Object System.Text.UTF8Encoding($false))
            )
            $runtimeLogPath = [System.IO.Path]::ChangeExtension($script:layoutSnapshotPath, ".runtime.log")
            [System.IO.File]::WriteAllText($runtimeLogPath, [string]$txtLog.Text, [System.Text.UTF8Encoding]::new($false))
        } finally {
            foreach ($previewDialog in @($script:previewPreflightDialog, $script:previewCommandPaletteDialog)) {
                if ($previewDialog -and -not $previewDialog.IsDisposed) { try { $previewDialog.Close() } catch {} }
            }
            $form.Close()
        }
    })
    $script:layoutSnapshotTimer.Start()
}
