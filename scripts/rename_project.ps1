# Run from outside the project directory. Preserve the old path as a junction
# because the installed Conda environment contains absolute installation paths.
param([switch]$CreateAccessLink)
$ErrorActionPreference = 'Stop'
$sourcePath = [System.IO.Path]::GetFullPath('D:\image-neo').TrimEnd('\')
$destinationPath = [System.IO.Path]::GetFullPath('D:\imagine-neo').TrimEnd('\')
if ($sourcePath -ne 'D:\image-neo' -or $destinationPath -ne 'D:\imagine-neo') {
    throw 'Unexpected project paths. No changes were made.'
}
$source = Get-Item -LiteralPath $sourcePath -Force
if (($source.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    if ($source.LinkType -eq 'Junction' -and $source.Target -contains $destinationPath) {
        Write-Output "Project already renamed to $destinationPath."
        exit 0
    }
    throw 'The source is an unexpected link. No changes were made.'
}
$existingAccessLink = $false
if (Test-Path -LiteralPath $destinationPath) {
    $destination = Get-Item -LiteralPath $destinationPath -Force
    $existingAccessLink = $destination.LinkType -eq 'Junction' -and $destination.Target -contains $sourcePath
    if (-not $existingAccessLink) { throw 'The destination already exists. No files will be overwritten.' }
}
foreach ($relative in @('ui\PhotoLab.UI.csproj', 'ui\Assets\imagine-neo.ico', 'backend\environment.yml', 'scripts\run_all.cmd')) {
    if (-not (Test-Path -LiteralPath (Join-Path $sourcePath $relative) -PathType Leaf)) {
        throw "Project validation failed: $relative is missing."
    }
}
if ($CreateAccessLink) {
    if (-not $existingAccessLink) {
        New-Item -ItemType Junction -Path $destinationPath -Target $sourcePath | Out-Null
    }
    Get-Item -LiteralPath $destinationPath -Force | Select-Object FullName, LinkType, Target
    Write-Output 'Access link created. Physical rename is pending until the project is no longer in use.'
    exit 0
}
Set-Location -LiteralPath 'D:\'
if ($existingAccessLink) {
    # The exact paths and junction target were verified above. Delete only the link,
    # non-recursively; the project directory and its contents stay untouched.
    [System.IO.Directory]::Delete($destinationPath, $false)
}
try { Rename-Item -LiteralPath $sourcePath -NewName 'imagine-neo' }
catch {
    if ($existingAccessLink -and -not (Test-Path -LiteralPath $destinationPath)) {
        New-Item -ItemType Junction -Path $destinationPath -Target $sourcePath | Out-Null
    }
    throw 'The project is still in use. Close VS Code, Codex and project terminals, then run finish_rename.cmd again.'
}
try {
    New-Item -ItemType Junction -Path $sourcePath -Target $destinationPath | Out-Null
}
catch {
    # Restore the original directory name if the compatibility link cannot be made.
    if (-not (Test-Path -LiteralPath $sourcePath)) {
        Rename-Item -LiteralPath $destinationPath -NewName 'image-neo'
        if ($existingAccessLink) { New-Item -ItemType Junction -Path $destinationPath -Target $sourcePath | Out-Null }
    }
    throw
}
$record = [ordered]@{
    project_root = $destinationPath
    compatibility_junction = $sourcePath
    reason = 'Preserve absolute paths in the existing Conda installation and current workspace.'
    renamed_at = [DateTime]::UtcNow.ToString('o')
}
$reportPath = Join-Path $destinationPath 'artifacts\project-rename.json'
[System.IO.File]::WriteAllText($reportPath, ($record | ConvertTo-Json), [System.Text.UTF8Encoding]::new($false))
Write-Output 'Physical rename complete. The old path is now the Conda compatibility junction.'
Get-Item -LiteralPath $destinationPath, $sourcePath -Force | Select-Object FullName, LinkType, Target
