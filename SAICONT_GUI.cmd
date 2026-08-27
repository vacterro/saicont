@echo off
title SAICONT GUI
cd /d "%~dp0"
if exist "bin\SAICONT.exe" (
    "bin\SAICONT.exe" --gui --config "%~dp0SAICONT.config.xml"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\gui.ps1"
)
if errorlevel 1 pause