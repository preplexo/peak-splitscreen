@echo off
setlocal EnableExtensions EnableDelayedExpansion
title PEAK Split Screen - uninstaller

echo.
echo  PEAK Split Screen - uninstaller
echo  -------------------------------
echo.

set "GAME="
if not "%~1"=="" set "GAME=%~1"
if defined GAME goto :check

set "TRY=C:\Program Files (x86)\Steam\steamapps\common\PEAK"
if exist "!TRY!\PEAK.exe" set "GAME=!TRY!"
if defined GAME goto :check

for /f "usebackq delims=" %%G in (`powershell -NoProfile -Command "$s=(Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath; if($s){$libs=@($s); $v=Join-Path $s 'steamapps\libraryfolders.vdf'; if(Test-Path $v){$q=[char]34; foreach($m in [regex]::Matches((Get-Content $v -Raw),$q+'path'+$q+'\s+'+$q+'([^'+$q+']+)')){$libs+=$m.Groups[1].Value.Replace('\\','\')}}; foreach($l in $libs){$p=Join-Path $l 'steamapps\common\PEAK'; if(Test-Path (Join-Path $p 'PEAK.exe')){(Resolve-Path $p).Path; break}}}"`) do set "GAME=%%G"
if defined GAME goto :check

set /p "GAME=  Paste the PEAK folder path here: "
set "GAME=!GAME:"=!"

:check
if not exist "!GAME!\PEAK.exe" (
  echo  [error] No PEAK.exe in: !GAME!
  goto :end
)

tasklist /fi "imagename eq PEAK.exe" 2>nul | find /i "PEAK.exe" >nul
if not errorlevel 1 (
  echo  [error] PEAK is running. Close the game first.
  goto :end
)

if exist "!GAME!\BepInEx\plugins\PeakSplitScreen" (
  rmdir /s /q "!GAME!\BepInEx\plugins\PeakSplitScreen"
  echo  Removed the mod.
) else (
  echo  The mod was not installed.
)
if exist "!GAME!\BepInEx\config\orchen17.peak.splitscreen.cfg" (
  del "!GAME!\BepInEx\config\orchen17.peak.splitscreen.cfg"
  echo  Removed its settings file.
)

echo.
echo  BepInEx ^(the mod loader^) was left in place because other mods may use it.
echo  To remove it too, delete these from the game folder:
echo    BepInEx\   winhttp.dll   doorstop_config.ini   .doorstop_version   changelog.txt
echo.

:end
if "%~2"=="" pause
