@echo off
title SAICONT Terminal Continuity
cd /d "%~dp0\.."
if exist "bin\SAICONT.exe" (
    "bin\SAICONT.exe" --gui --config "%~dp0\..\SAICONT.config.xml"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0gui.ps1"
)
if errorlevel 1 pause
