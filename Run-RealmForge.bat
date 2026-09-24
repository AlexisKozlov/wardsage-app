@echo off
rem RealmForge Extractor launcher.
rem Starts RealmForge-Extractor.ps1 with a hidden console. The script itself asks Windows for
rem administrator rights (UAC): the game runs as administrator, so reading its memory needs them too.
rem It only READS game memory; see README.txt.

set "RF_SCRIPT=%~dp0RealmForge-Extractor.ps1"
if not exist "%RF_SCRIPT%" goto missing

start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%RF_SCRIPT%"
exit /b 0

:missing
echo RealmForge-Extractor.ps1 was not found next to this file.
echo Unpack the whole RealmForge-Extractor.zip into one folder and run Run-RealmForge.bat again.
echo.
echo Fajl RealmForge-Extractor.ps1 ne najden. Raspakujte ves' arhiv v odnu papku.
pause
exit /b 1
