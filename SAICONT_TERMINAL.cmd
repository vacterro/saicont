@echo off
title SAICONT TERMINAL
cd /d "%~dp0"
if exist "bin\SAICONT.exe" (
    "bin\SAICONT.exe" --terminal --config "%~dp0SAICONT.config.xml"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\terminal.ps1"
)
if errorlevel 1 pause