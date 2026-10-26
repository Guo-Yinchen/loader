@echo off
title Build Chispa Launcher EXE
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-client.ps1"
echo.
pause
