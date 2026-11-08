@echo off
setlocal
set "CONFIG=%~1"
if not defined CONFIG set "CONFIG=Debug"
if /i not "%CONFIG%"=="Debug" if /i not "%CONFIG%"=="Release" (
  echo Usage: run_all.cmd [Debug^|Release] [--no-build^|--check]
  exit /b 2
)
set "MODE=%~2"
if defined MODE if /i not "%MODE%"=="--no-build" if /i not "%MODE%"=="--check" exit /b 2
if not "%~3"=="" exit /b 2
call "%~dp0find_conda.cmd"
if errorlevel 1 exit /b 1
call "%PHOTOLAB_CONDA%" activate photolab
if errorlevel 1 exit /b 1
for %%R in ("%~dp0..") do set "PHOTOLAB_ROOT=%%~fR"
set "PHOTOLAB_PYTHON=%CONDA_PREFIX%\python.exe"
if /i not "%MODE%"=="--no-build" (
  call "%~dp0build_ui.cmd" %CONFIG%
  if errorlevel 1 exit /b 1
)
set "EXE=%PHOTOLAB_ROOT%\ui\bin\x64\%CONFIG%\net8.0-windows10.0.22621.0\win-x64\ImagineNeo.exe"
if not exist "%EXE%" (
  echo ERREUR : executable absent. Lancez scripts\build_ui.cmd %CONFIG%.
  exit /b 1
)
pushd "%PHOTOLAB_ROOT%\backend"
if errorlevel 1 exit /b 1
"%PHOTOLAB_PYTHON%" -m app.check_plugins
if errorlevel 1 (
  popd
  exit /b 1
)
if /i "%MODE%"=="--check" (
  popd
  exit /b 0
)
"%PHOTOLAB_PYTHON%" -m app.launcher "%EXE%"
set "RESULT=%ERRORLEVEL%"
popd
exit /b %RESULT%
