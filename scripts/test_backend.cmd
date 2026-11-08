@echo off
setlocal
call "%~dp0find_conda.cmd"
if errorlevel 1 exit /b 1
call "%PHOTOLAB_CONDA%" activate photolab
if errorlevel 1 exit /b 1
pushd "%~dp0..\backend"
python -m pytest %*
set "RESULT=%ERRORLEVEL%"
popd
if not "%RESULT%"=="0" echo ERREUR : tests backend en echec.
exit /b %RESULT%
