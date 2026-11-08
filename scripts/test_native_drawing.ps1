param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
Add-Type -AssemblyName System.Windows.Forms,System.Drawing,UIAutomationClient,UIAutomationTypes
Add-Type -ReferencedAssemblies System.Windows.Forms,System.Drawing -Path (Join-Path $PSScriptRoot 'NativeDragProbe.cs')
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class DrawingInput {
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
$env:PHOTOLAB_PIPE_NAME = 'PhotoLab_native_drawing_' + [guid]::NewGuid().ToString('N')
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
    $draw = Find-Element $window 'DrawTool'
    $drawBounds = $draw.Current.BoundingRectangle
    [NativeDragProbe]::SetCursorPos([int]($drawBounds.Left + $drawBounds.Width/2), [int]($drawBounds.Top + $drawBounds.Height/2)) | Out-Null
    [DrawingInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [DrawingInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 400
    # Win2D's custom UserControl has no UIA peer. Derive its visible area from
    # the image title and library, which bracket the central canvas.
    $libraryBounds = (Find-Element $window 'ImageQueue').Current.BoundingRectangle
    $titleCondition = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty),'sample-color.png'
    $title = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $titleCondition).Current.BoundingRectangle
    if ($libraryBounds.IsEmpty -or $title.IsEmpty) { throw 'Canvas landmarks are unavailable.' }
    $canvas = New-Object System.Windows.Rect ($libraryBounds.Left, ($title.Bottom + 20), $libraryBounds.Width, ($libraryBounds.Top - $title.Bottom - 65))
    $cx = [int]($canvas.Left + $canvas.Width/2)
    $cy = [int]($canvas.Top + $canvas.Height/2)
    [NativeDragProbe]::SetCursorPos($cx - 40, $cy - 15) | Out-Null
    [DrawingInput]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
    try {
        for ($step = 1; $step -le 20; $step++) {
            [NativeDragProbe]::SetCursorPos(($cx - 40 + 4*$step), ($cy - 15 + 2*$step)) | Out-Null
            Start-Sleep -Milliseconds 20
        }
    } finally { [DrawingInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero) }
    Start-Sleep -Milliseconds 250
    [NativeDragProbe]::Capture($handle, (Join-Path $project 'artifacts\ui-drawing-preview.png'))
    if (!(Find-Element $window 'ApplyDrawing').Current.IsEnabled -or !(Find-Element $window 'Undo').Current.IsEnabled) { throw 'The real pointer gesture did not create a drawing.' }
    [NativeDragProbe]::SetCursorPos($cx, [int]$canvas.Top + 8) | Out-Null
    [NativeDragProbe]::Capture($handle, (Join-Path $project 'artifacts\ui-drawing-preview.png'))
    Invoke-Button $window 'Undo'
    Wait-Status $window 'Drawing step undone' | Out-Null
    if ((Find-Element $window 'ApplyDrawing').Current.IsEnabled) { throw 'Undo left a pending stroke.' }
    Invoke-Button $window 'Redo'
    Wait-Status $window 'Drawing step restored' | Out-Null
    Invoke-Button $window 'ApplyDrawing'
    Wait-Status $window 'Drawing: new version added' | Out-Null
    if ((Find-Element $window 'ApplyDrawing').Current.IsEnabled) { throw 'Applying the drawing left the draft active.' }
    [NativeDragProbe]::Capture($handle, (Join-Path $project 'artifacts\ui-drawing-applied.png'))
    Invoke-Button $window 'Undo'
    Wait-Status $window 'Undone: previous image version' | Out-Null
    Invoke-Button $window 'Redo'
    Wait-Status $window 'Redone: image version restored' | Out-Null
    [ordered]@{status='ok'; pointer_drawing=$true; undo_redo=$true; applied_drawing_undo_redo=$true; screenshot='ui-drawing-applied.png'} | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $project 'artifacts\native-drawing-report.json')
    Get-Content (Join-Path $project 'artifacts\native-drawing-report.json')
} finally {
    [DrawingInput]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
    [System.Windows.Forms.Cursor]::Position = $cursor
    if (!$process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (!$process.WaitForExit(10000)) { throw 'The test window did not close.' }
    }
    $process.Dispose()
}
