@echo off
setlocal
call "%~dp0find_conda.cmd"
if errorlevel 1 exit /b 1
call "%PHOTOLAB_CONDA%" activate photolab
if errorlevel 1 exit /b 1
pushd "%~dp0..\backend"
python tests\run_ui_smoke.py %*
set "RESULT=%ERRORLEVEL%"
popd
exit /b %RESULT%
