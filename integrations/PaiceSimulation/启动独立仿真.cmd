@echo off
chcp 65001 >nul
pwsh -NoProfile -File "%~dp0..\..\tools\Start-PaiceSimulation.ps1" %*
if errorlevel 1 pause
