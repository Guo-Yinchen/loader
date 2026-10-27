@echo off
setlocal
rem Double-click backup: asks for the target directory (an external disk is the point), then runs
rem tools/backup-server.ps1 with the release-signing key included.
set "DEFAULT_TARGET=D:\ChispaBackups"
set "TARGET="
set /p "TARGET=Backup target directory [%DEFAULT_TARGET%]: "
if "%TARGET%"=="" set "TARGET=%DEFAULT_TARGET%"
echo.
set "SIGNING=%~dp0.local\signing"
if exist "%SIGNING%" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\backup-server.ps1" -Target "%TARGET%" -IncludePath "%SIGNING%"
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\backup-server.ps1" -Target "%TARGET%"
)
set "RESULT=%errorlevel%"
echo.
if not "%RESULT%"=="0" (
  echo Backup FAILED. Nothing was reported as complete; read the message above.
) else (
  echo Backup finished. Keep a copy OFF this machine.
)
pause
exit /b %RESULT%
