@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\Edit.ps1"
exit /b %errorlevel%
