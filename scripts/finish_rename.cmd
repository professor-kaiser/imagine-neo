@echo off
setlocal
set "RENAME_SCRIPT=%~dp0rename_project.ps1"
echo Close VS Code, Codex and all terminals or Python backends using this project first.
echo The project will be renamed to D:\imagine-neo. Existing files are preserved.
cd /d D:\
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%RENAME_SCRIPT%"
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" echo Rename did not complete. Read the message above before trying again.
pause
exit /b %RESULT%
