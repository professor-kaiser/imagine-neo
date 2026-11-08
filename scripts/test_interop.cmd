@echo off
setlocal
call "%~dp0find_conda.cmd"
if errorlevel 1 exit /b 1
call "%PHOTOLAB_CONDA%" activate photolab
if errorlevel 1 exit /b 1
set "PHOTOLAB_PYTHON=%CONDA_PREFIX%\python.exe"
for %%R in ("%~dp0..") do set "PROJECT_ROOT=%%~fR"
dotnet run --project "%PROJECT_ROOT%\tests\PhotoLab.Interop\PhotoLab.Interop.csproj" -- "%PROJECT_ROOT%"
exit /b %ERRORLEVEL%
