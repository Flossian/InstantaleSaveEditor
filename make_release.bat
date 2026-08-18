@echo off
setlocal

rem Build release zip for Instantale Save Editor.
rem   usage: make_release.bat [version]      e.g. make_release.bat 10.3
rem NOTE: keep this file ASCII only. cmd reads batch files by byte offset,
rem       so non-ASCII characters break execution under a different code page.

set "VER=%~1"
if "%VER%"=="" set "VER=10.4"

set "ROOT=%~dp0"
set "PUB=%ROOT%bin\Release\net8.0-windows\win-x64\publish"
set "OUT=%ROOT%release"
set "STAGE=%OUT%\InstantaleSaveEditor"
set "ZIP=%OUT%\InstantaleSaveEditor_v%VER%.zip"

rem set to 1 to bundle the shared item warehouse samples from publish\item\shared
set "INCLUDE_SAMPLE_ITEMS=0"

echo [1/4] build (dotnet publish -c Release)
pushd "%ROOT%"
dotnet publish -c Release
if errorlevel 1 (
  echo build failed.
  popd
  exit /b 1
)
popd

if not exist "%PUB%\InstantaleSaveEditor.exe" (
  echo exe not found: %PUB%\InstantaleSaveEditor.exe
  exit /b 1
)

echo [2/4] collect files
if not exist "%OUT%" mkdir "%OUT%"
if exist "%STAGE%" rd /s /q "%STAGE%"
mkdir "%STAGE%"

rem exe comes from the publish output, everything else from the source tree
rem (publish\ also holds saves / npc / item created while testing)
copy /y "%PUB%\InstantaleSaveEditor.exe" "%STAGE%\" >nul || goto :fail
xcopy /e /i /y /q "%ROOT%docs" "%STAGE%\docs" >nul || goto :fail
xcopy /e /i /y /q "%ROOT%lang" "%STAGE%\lang" >nul || goto :fail
xcopy /e /i /y /q "%ROOT%namelist" "%STAGE%\namelist" >nul || goto :fail

rem settings.json is user local config, created on first launch
mkdir "%STAGE%\setting"
copy /y "%ROOT%setting\field_options.json" "%STAGE%\setting\" >nul || goto :fail

rem npc\ item\ facility\ templates\ are created by the tool on demand
if "%INCLUDE_SAMPLE_ITEMS%"=="1" (
  if exist "%PUB%\item\shared" xcopy /e /i /y /q "%PUB%\item\shared" "%STAGE%\item\shared" >nul
)

echo [3/4] create zip
if exist "%ZIP%" del /q "%ZIP%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%STAGE%\*' -DestinationPath '%ZIP%' -CompressionLevel Optimal"
if errorlevel 1 goto :fail
if not exist "%ZIP%" goto :fail

rd /s /q "%STAGE%"
echo [4/4] done
for %%F in ("%ZIP%") do echo   %%~fF  (%%~zF bytes^)
endlocal
exit /b 0

:fail
echo failed to create release zip.
endlocal
exit /b 1
