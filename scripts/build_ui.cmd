@echo off
setlocal
set "CONFIG=%~1"
if not defined CONFIG set "CONFIG=Debug"
if /i not "%CONFIG%"=="Debug" if /i not "%CONFIG%"=="Release" (
  echo ERREUR : configuration attendue Debug ou Release.
  exit /b 2
)
where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERREUR : SDK .NET 8 x64 requis. Voir README.md.
  exit /b 1
)
echo Building Imagine Neo : %CONFIG% / x64 / Windows App SDK autonome.
dotnet build "%~dp0..\ui\PhotoLab.UI.csproj" -c %CONFIG% -p:Platform=x64 --verbosity minimal
if errorlevel 1 (
  echo ERREUR : compilation echouee. Consultez les diagnostics ci-dessus.
  exit /b 1
)
set "EXE=%~dp0..\ui\bin\x64\%CONFIG%\net8.0-windows10.0.22621.0\win-x64\ImagineNeo.exe"
if not exist "%EXE%" (
  echo ERREUR : executable absent du chemin attendu.
  exit /b 1
)
for %%F in ("%EXE%") do echo Executable : %%~fF
exit /b 0
