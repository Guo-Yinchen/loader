@echo off
cd /d "%~dp0"
if not defined CHISPA_SERVER_DATA set "CHISPA_SERVER_DATA=%~dp0data"
if not defined CHISPA_SERVER_URL set "CHISPA_SERVER_URL=http://127.0.0.1:5115"
if not defined CHISPA_RELEASE_TRUST_KEYS set "CHISPA_RELEASE_TRUST_KEYS=%~dp0release-trust.json"
Chispa.Server.exe %*
