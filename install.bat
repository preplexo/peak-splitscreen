@echo off
setlocal EnableExtensions EnableDelayedExpansion
title PEAK Split Screen - installer

set "HERE=%~dp0"
set "DLL=%HERE%dist\PeakSplitScreen.dll"
set "BEPINEX_VERSION=5.4.23.6"
set "BEPINEX_URL=https://github.com/BepInEx/BepInEx/releases/download/v%BEPINEX_VERSION%/BepInEx_win_x64_%BEPINEX_VERSION%.zip"

echo.
echo  PEAK Split Screen - installer
echo  -----------------------------
echo.

if not exist "%DLL%" (
  echo  [error] dist\PeakSplitScreen.dll is missing next to this installer.
  echo          Download the whole folder, or build it from source ^(see README^).
  goto :fail
)

rem ---- 1. find the game -------------------------------------------------
set "GAME="
if not "%~1"=="" set "GAME=%~1"
if defined GAME goto :check

set "TRY=C:\Program Files (x86)\Steam\steamapps\common\PEAK"
if exist "!TRY!\PEAK.exe" set "GAME=!TRY!"
if defined GAME goto :check

rem ask Steam where its libraries are
for /f "usebackq delims=" %%G in (`powershell -NoProfile -Command "$s=(Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath; if($s){$libs=@($s); $v=Join-Path $s 'steamapps\libraryfolders.vdf'; if(Test-Path $v){$q=[char]34; foreach($m in [regex]::Matches((Get-Content $v -Raw),$q+'path'+$q+'\s+'+$q+'([^'+$q+']+)')){$libs+=$m.Groups[1].Value.Replace('\\','\')}}; foreach($l in $libs){$p=Join-Path $l 'steamapps\common\PEAK'; if(Test-Path (Join-Path $p 'PEAK.exe')){(Resolve-Path $p).Path; break}}}"`) do set "GAME=%%G"
if defined GAME goto :check

echo  Could not find PEAK automatically.
echo  In Steam: right-click PEAK ^> Manage ^> Browse local files, then copy the folder path.
echo.
set /p "GAME=  Paste the PEAK folder path here: "
set "GAME=!GAME:"=!"

:check
if not exist "!GAME!\PEAK.exe" (
  echo  [error] No PEAK.exe in: !GAME!
  goto :fail
)
echo  Game folder : !GAME!

tasklist /fi "imagename eq PEAK.exe" 2>nul | find /i "PEAK.exe" >nul
if not errorlevel 1 (
  echo  [error] PEAK is running. Close the game and run this installer again.
  goto :fail
)

rem ---- 2. BepInEx (the mod loader) -------------------------------------
if exist "!GAME!\BepInEx\core\BepInEx.dll" (
  echo  BepInEx     : already installed
  goto :plugin
)

echo  BepInEx     : not found, downloading %BEPINEX_VERSION% ...
set "ZIP=%TEMP%\BepInEx_win_x64_%BEPINEX_VERSION%.zip"
curl -L --fail --silent --show-error -o "!ZIP!" "%BEPINEX_URL%"
if errorlevel 1 (
  echo  [error] Download failed. Check your internet connection, or install
  echo          BepInEx 5 ^(x64^) by hand from https://github.com/BepInEx/BepInEx/releases
  goto :fail
)
tar -xf "!ZIP!" -C "!GAME!"
if errorlevel 1 (
  echo  [error] Could not unpack BepInEx into the game folder.
  goto :fail
)
del "!ZIP!" >nul 2>&1
if not exist "!GAME!\BepInEx\core\BepInEx.dll" (
  echo  [error] BepInEx did not unpack correctly.
  goto :fail
)
echo  BepInEx     : installed

:plugin
rem ---- 3. the mod itself ------------------------------------------------
set "DEST=!GAME!\BepInEx\plugins\PeakSplitScreen"
if not exist "!DEST!" mkdir "!DEST!"
copy /y "%DLL%" "!DEST!\PeakSplitScreen.dll" >nul
if errorlevel 1 (
  echo  [error] Could not copy the mod into: !DEST!
  echo          Try right-click ^> Run as administrator.
  goto :fail
)
echo  Mod         : installed to BepInEx\plugins\PeakSplitScreen

echo.
echo  Done. How to play:
echo    1. Plug in a gamepad ^(player 2^). Player 1 uses keyboard + mouse.
echo    2. Start PEAK from Steam and play OFFLINE.
echo    3. Player 2 joins automatically and the screen splits in two.
echo.
if "%~2"=="" pause
exit /b 0

:fail
echo.
if "%~2"=="" pause
exit /b 1
