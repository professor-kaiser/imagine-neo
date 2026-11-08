param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
Add-Type -AssemblyName System.Windows.Forms,System.Drawing,UIAutomationClient,UIAutomationTypes
Add-Type -ReferencedAssemblies System.Windows.Forms,System.Drawing -Path (Join-Path $PSScriptRoot 'NativeDragProbe.cs')
[NativeDragProbe]::SetProcessDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null
function Find-Element([System.Windows.Automation.AutomationElement]$Window, [string]$Id) {
    $condition = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty),$Id
    return $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Wait-Status([System.Windows.Automation.AutomationElement]$Window, [string]$Pattern) {
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        $status = (Find-Element $Window 'ImportStatus').Current.Name
        if ($status -match $Pattern) { return $status }
        Start-Sleep -Milliseconds 200
    }
    throw "Status timeout: $status"
}
$env:PHOTOLAB_PIPE_NAME = 'PhotoLab_native_drop_' + [guid]::NewGuid().ToString('N')
$env:PHOTOLAB_ROOT = $project
$env:PHOTOLAB_PYTHON = Join-Path $project '.tools\miniforge3\envs\photolab\python.exe'
$executable = Join-Path $project "ui\bin\x64\$Configuration\net8.0-windows10.0.22621.0\win-x64\ImagineNeo.exe"
$process = Start-Process -FilePath $executable -WindowStyle Hidden -PassThru
try {
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        Start-Sleep -Milliseconds 150
        $process.Refresh()
        if ($process.HasExited) { throw 'Imagine Neo exited before the drop test.' }
        if ($process.MainWindowHandle -ne 0) { break }
    }
    $handle = $process.MainWindowHandle
    if ($handle -eq 0) { throw 'No Imagine Neo window.' }
    [NativeDragProbe]::SetWindowPos($handle, [IntPtr]::Zero, 24, 24, 1180, 860, 0) | Out-Null
    [NativeDragProbe]::SetForegroundWindow($handle) | Out-Null
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    Wait-Status $window 'Ready' | Out-Null
    Start-Sleep -Milliseconds 800
    [NativeDragProbe]::Capture($handle, (Join-Path $project 'artifacts\ui-redesign-empty.png'))
    $canvas = (Find-Element $window 'EmptyImportImages').Current.BoundingRectangle
    if (!$canvas -or $canvas.IsEmpty) { throw 'The canvas import target is missing from UI Automation.' }
    $files = @((Join-Path $project 'artifacts\fixtures\sample-alpha.png'), (Join-Path $project 'artifacts\fixtures\sample-color.png'))
    $effect = [NativeDragProbe]::Drop($files, [int]($canvas.Left + $canvas.Width / 2), [int]($canvas.Top + $canvas.Height / 2))
    [ordered]@{effect=$effect; is_elevated=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)} | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $project 'artifacts\native-drop-attempt.json')
    $status = Wait-Status $window '^2 image\(s\) imported'
    Start-Sleep -Milliseconds 600
    [NativeDragProbe]::SetForegroundWindow($handle) | Out-Null
    [NativeDragProbe]::Capture($handle, (Join-Path $project 'artifacts\ui-redesign-document.png'))
    $library = (Find-Element $window 'ImageQueue').Current.BoundingRectangle
    $libraryEffect = [NativeDragProbe]::Drop(@($files[0]), [int]($library.Left + 50), [int]($library.Top + 30))
    $libraryStatus = Wait-Status $window '^1 image\(s\) imported'
    # Select an actual toolbox item through UI Automation and inspect its contextual panel.
    $colorTool = Find-Element $window 'pick_color'
    if (!$colorTool) {
        $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object {
            [PSCustomObject]@{ Name=$_.Current.Name; Id=$_.Current.AutomationId; Type=$_.Current.ControlType.ProgrammaticName }
        } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $project 'artifacts\ui-automation-tree.json')
        throw 'The color tool is not accessible.'
    }
    $selection = $colorTool.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
    Start-Sleep -Milliseconds 600
    [NativeDragProbe]::Capture($handle, (Join-Path $project 'artifacts\ui-redesign-color.png'))
    $result = [ordered]@{ status='ok'; canvas_effect=$effect; canvas_status=$status; library_effect=$libraryEffect; library_status=$libraryStatus; toolbox_selection_verified=$true; scope='External OLE FileDrop source, real pointer drag onto the empty canvas and an existing library item; toolbox selection through UI Automation.' }
    $result.is_elevated = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($effect -ne 'Copy' -or $libraryEffect -ne 'Copy') { throw "Incorrect drag effects: $effect / $libraryEffect" }
    $result | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $project 'artifacts\native-drop-report.json')
    $result | ConvertTo-Json
} finally {
    if (!$process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (!$process.WaitForExit(10000)) { throw 'The test window did not close.' }
    }
    $process.Dispose()
}

