@echo off
title SAICONT Windows GUI
cd /d "%~dp0"
if exist "bin\SAICONT.exe" (
    start "" "bin\SAICONT.exe" --app --config "%~dp0SAICONT.config.xml"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\gui_win.ps1"
)
