param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
Add-Type -AssemblyName System.Windows.Forms,System.Drawing,UIAutomationClient,UIAutomationTypes
Add-Type -ReferencedAssemblies System.Windows.Forms,System.Drawing -Path (Join-Path $PSScriptRoot 'NativeDragProbe.cs')
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class DrawingInput {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
"@
[NativeDragProbe]::SetProcessDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null
function Find-Element($Window, [string]$Id) {
    $condition = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty),$Id
    return $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Invoke-Button($Window, [string]$Id) {
    $element = Find-Element $Window $Id
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Wait-Status($Window, [string]$Pattern) {
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        $status = (Find-Element $Window 'ImportStatus').Current.Name
        if ($status -match $Pattern) { return $status }
        Start-Sleep -Milliseconds 150
    }
    throw "Status timeout: $status"
}
function Read-Number($Window, [string]$Id) {
    $control = Find-Element $Window $Id
    if (!$control) { throw "Missing number input: $Id" }
    $provider = $null
    if ($control.TryGetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern, [ref]$provider)) { return $provider.Current.Value }
    if ($control.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$provider)) { return [double]::Parse($provider.Current.Value, [Globalization.CultureInfo]::InvariantCulture) }
    $condition = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty),([System.Windows.Automation.ControlType]::Edit)
    $input = $control.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    return [double]::Parse($input.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value, [Globalization.CultureInfo]::InvariantCulture)
}
function Drag-Points($Points) {
    [NativeDragProbe]::SetCursorPos([int]$Points[0].X, [int]$Points[0].Y) | Out-Null
    [DrawingInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
    try {
        foreach ($point in $Points) {
            [NativeDragProbe]::SetCursorPos([int]$point.X, [int]$point.Y) | Out-Null
            Start-Sleep -Milliseconds 25
        }
    } finally { [DrawingInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero) }
    Start-Sleep -Milliseconds 250
}
$env:PHOTOLAB_PIPE_NAME = 'PhotoLab_native_transform_' + [guid]::NewGuid().ToString('N')
$env:PHOTOLAB_ROOT = $project
$env:PHOTOLAB_PYTHON = Join-Path $project '.tools\miniforge3\envs\photolab\python.exe'
$executable = Join-Path $project "ui\bin\x64\$Configuration\net8.0-windows10.0.22621.0\win-x64\ImagineNeo.exe"
$process = Start-Process -FilePath $executable -WindowStyle Hidden -PassThru
$cursor = [System.Windows.Forms.Cursor]::Position
try {
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        Start-Sleep -Milliseconds 150
        $process.Refresh()
        if ($process.HasExited) { throw 'Imagine Neo exited before the drawing test.' }
        if ($process.MainWindowHandle -ne 0) { break }
    }
    $handle = $process.MainWindowHandle
    if ($handle -eq 0) { throw 'No test window.' }
    [NativeDragProbe]::SetWindowPos($handle, [IntPtr]::Zero, 24, 24, 1180, 860, 0) | Out-Null
    [NativeDragProbe]::SetForegroundWindow($handle) | Out-Null
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    Wait-Status $window 'Ready' | Out-Null
    Start-Sleep -Milliseconds 500
    $target = (Find-Element $window 'EmptyImportImages').Current.BoundingRectangle
    $effect = [NativeDragProbe]::Drop(@((Join-Path $project 'artifacts\fixtures\sample-color.png')), [int]($target.Left + $target.Width/2), [int]($target.Top + $target.Height/2))
    if ($effect -ne 'Copy') { throw "Import failed: $effect" }
    Wait-Status $window '^1 image' | Out-Null
    [NativeDragProbe]::SetWindowPos($handle, [IntPtr]::new(-1), 24, 24, 1180, 860, 0) | Out-Null
    [NativeDragProbe]::SetForegroundWindow($handle) | Out-Null
    $tool = Find-Element $window 'rotate_scale_image'
    if (!$tool) { throw 'Rotation + scale is missing from the toolbox.' }
    $tool.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 350
    Invoke-Button $window 'Angle90'
    Invoke-Button $window 'Scale50'
    if ((Read-Number $window 'TransformAngle') -ne 90 -or (Read-Number $window 'TransformScale') -ne 50) { throw 'Preset buttons did not update the inputs.' }
    Invoke-Button $window 'ResetTransform'
    Invoke-Button $window 'FitTransform'
    Start-Sleep -Milliseconds 250
    $canvasElement = Find-Element $window 'EditorCanvas'
    if (!$canvasElement) { throw 'Canvas is missing from UI Automation.' }
    $canvas = $canvasElement.Current.BoundingRectangle
    $dpi = [DrawingInput]::GetDpiForWindow($handle) / 96.0
    $cx = $canvas.Left + $canvas.Width / 2
    $cy = $canvas.Top + $canvas.Height / 2
    $zoom = [Math]::Min(($canvas.Width - 88*$dpi)/128, ($canvas.Height - 88*$dpi)/96)
    $radius = 48*$zoom + 32*$dpi
    $points = @()
    for ($step=0; $step -le 20; $step++) {
        $angle = (-90 + $step*1.5)*[Math]::PI/180
        $points += [System.Drawing.PointF]::new(($cx + $radius*[Math]::Cos($angle)), ($cy + $radius*[Math]::Sin($angle)))
    }
    Drag-Points $points
    $rotation = Read-Number $window 'TransformAngle'
    [NativeDragProbe]::Capture($handle, (Join-Path $project 'artifacts\ui-transform-preview.png'))
    if ([Math]::Abs($rotation - 30) -gt 1) { throw "Round handle did not rotate the image: $rotation degrees" }
    Invoke-Button $window 'FitTransform'
    Start-Sleep -Milliseconds 200
    $radians = $rotation*[Math]::PI/180
    $outWidth = [Math]::Ceiling(128*[Math]::Abs([Math]::Cos($radians)) + 96*[Math]::Abs([Math]::Sin($radians)) - 1e-9)
    $outHeight = [Math]::Ceiling(128*[Math]::Abs([Math]::Sin($radians)) + 96*[Math]::Abs([Math]::Cos($radians)) - 1e-9)
    $zoom = [Math]::Min(($canvas.Width - 88*$dpi)/$outWidth, ($canvas.Height - 88*$dpi)/$outHeight)
    $vx = (64*[Math]::Cos($radians) - 48*[Math]::Sin($radians))*$zoom
    $vy = (64*[Math]::Sin($radians) + 48*[Math]::Cos($radians))*$zoom
    $points = @()
    for ($step=0; $step -le 20; $step++) {
        $factor = 1 - $step*0.01
        $points += [System.Drawing.PointF]::new(($cx + $vx*$factor), ($cy + $vy*$factor))
    }
    Drag-Points $points
    $scale = Read-Number $window 'TransformScale'
    if ([Math]::Abs($scale - 80) -gt 1.5) { throw "Corner handle did not scale the image: $scale percent" }
    if ([Math]::Abs((Read-Number $window 'TransformAngle') - $rotation) -gt 0.05) { throw 'Scaling unexpectedly changed the angle.' }
    Invoke-Button $window 'ApplyOperation'
    Wait-Status $window '^Preview retained' | Out-Null
    if ([Math]::Abs((Read-Number $window 'TransformScale') - $scale) -gt 0.05) { throw 'The placeholder discarded the settings.' }
    [NativeDragProbe]::SetCursorPos([int]$canvas.Left + 4, [int]$canvas.Top + 4) | Out-Null
    [NativeDragProbe]::Capture($handle, (Join-Path $project 'artifacts\ui-transform-preview.png'))
    [ordered]@{status='ok'; presets=$true; pointer_rotation=$rotation; pointer_scale_percent=$scale; placeholder_retains_preview=$true; screenshot='ui-transform-preview.png'} | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $project 'artifacts\native-transform-report.json')
    Get-Content (Join-Path $project 'artifacts\native-transform-report.json')
} catch {
    if ($handle -and !$process.HasExited) { [NativeDragProbe]::Capture($handle, (Join-Path $project 'artifacts\ui-transform-preview.png')) }
    throw
} finally {
    [DrawingInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
    [System.Windows.Forms.Cursor]::Position = $cursor
    if (!$process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (!$process.WaitForExit(10000)) { throw 'The test window did not close.' }
    }
    $process.Dispose()
}
