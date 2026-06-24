@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Set-OllamaMode.ps1" GPU
if errorlevel 1 pause