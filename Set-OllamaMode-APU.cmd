@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Set-OllamaMode.ps1" APU
if errorlevel 1 pause