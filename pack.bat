@echo off
setlocal
rem Builds the Thunderstore zip from dist\PeakSplitScreen.dll and the files in thunderstore\.
set "HERE=%~dp0"
set "STAGE=%TEMP%\peak-splitscreen-pack"
set "OUT=%HERE%dist\Spleak-thunderstore.zip"

if not exist "%HERE%dist\PeakSplitScreen.dll" (
  echo  [error] dist\PeakSplitScreen.dll is missing. Build first, then copy it to dist\.
  goto :end
)

if exist "%STAGE%" rmdir /s /q "%STAGE%"
mkdir "%STAGE%"
copy /y "%HERE%thunderstore\manifest.json" "%STAGE%\" >nul
copy /y "%HERE%thunderstore\icon.png" "%STAGE%\" >nul
copy /y "%HERE%thunderstore\README.md" "%STAGE%\" >nul
copy /y "%HERE%CHANGELOG.md" "%STAGE%\" >nul
copy /y "%HERE%LICENSE" "%STAGE%\" >nul
copy /y "%HERE%dist\PeakSplitScreen.dll" "%STAGE%\" >nul

if exist "%OUT%" del "%OUT%"
tar -a -c -f "%OUT%" -C "%STAGE%" manifest.json icon.png README.md CHANGELOG.md LICENSE PeakSplitScreen.dll
if errorlevel 1 (
  echo  [error] Could not create the zip.
  goto :end
)
rmdir /s /q "%STAGE%"
echo  Created %OUT%

:end
if "%~1"=="" pause
