@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run ChessKit with crash logging.ps1"
exit /b %ERRORLEVEL%
