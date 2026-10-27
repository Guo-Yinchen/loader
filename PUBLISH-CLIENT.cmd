@echo off
setlocal
title Build Chispa Launcher EXE (production)
cd /d "%~dp0"
echo Building the production single-file launcher from release\release.json ...
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-client.ps1" %*
set "code=%ERRORLEVEL%"
echo.
if not "%code%"=="0" (
  echo Publish FAILED with exit code %code%.
  echo The previous EXE in .local\distribution\client was kept; nothing was replaced.
) else (
  echo Done. Use the address and SHA-256 verified above.
)
pause
exit /b %code%
