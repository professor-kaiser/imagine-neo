@echo off
rem Called by other scripts; intentionally shares their environment.
if defined PHOTOLAB_CONDA if exist "%PHOTOLAB_CONDA%" exit /b 0
if exist "%~dp0..\.tools\miniforge3\condabin\conda.bat" set "PHOTOLAB_CONDA=%~dp0..\.tools\miniforge3\condabin\conda.bat"
if defined PHOTOLAB_CONDA exit /b 0
for /f "delims=" %%C in ('where conda.bat 2^>nul') do if not defined PHOTOLAB_CONDA set "PHOTOLAB_CONDA=%%C"
if defined PHOTOLAB_CONDA exit /b 0
echo ERREUR : Conda introuvable. Installez Miniforge ou renseignez PHOTOLAB_CONDA.
exit /b 1
