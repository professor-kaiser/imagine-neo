@echo off
setlocal
set "CONFIG=%~1"
if not defined CONFIG set "CONFIG=Debug"
if /i not "%CONFIG%"=="Debug" if /i not "%CONFIG%"=="Release" exit /b 2
set "EXE=%~dp0..\ui\bin\x64\%CONFIG%\net8.0-windows10.0.22621.0\win-x64\ImagineNeo.exe"
if not exist "%EXE%" (
  echo ERREUR : compilez d'abord avec scripts\build_ui.cmd %CONFIG%.
  exit /b 1
)
for %%R in ("%~dp0..") do set "PHOTOLAB_ROOT=%%~fR"
if defined CONDA_PREFIX set "PHOTOLAB_PYTHON=%CONDA_PREFIX%\python.exe"
start "" /wait "%EXE%"
exit /b %ERRORLEVEL%
