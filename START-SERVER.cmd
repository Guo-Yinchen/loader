@echo off
title Chispa Server
cd /d "%~dp0"
set "CHISPA_OPEN_ADMIN=1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-local.ps1"
echo.
echo Server stopped or failed to start. Read the message above.
pause
