@echo off
title SAICONT Terminal Continuity
cd /d "%~dp0"
if exist "bin\SAICONT.exe" (
    "bin\SAICONT.exe" --gui
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\gui.ps1"
)
if errorlevel 1 pause
