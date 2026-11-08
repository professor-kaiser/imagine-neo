@echo off
setlocal
call "%~dp0find_conda.cmd"
if errorlevel 1 exit /b 1
call "%PHOTOLAB_CONDA%" env update --name photolab --file "%~dp0..\backend\environment.yml"
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" echo ERREUR : configuration Conda echouee.
exit /b %RESULT%
