@echo off
title Chispa Admin Setup
cd /d "%~dp0"
echo Creating administrator account: manager
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-local.ps1" admin-create manager
echo.
pause
