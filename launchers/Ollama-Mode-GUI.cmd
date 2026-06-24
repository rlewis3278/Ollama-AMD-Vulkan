@echo off
setlocal
set SCRIPT_DIR=%~dp0
set APP_DIR=%SCRIPT_DIR%..\publish\OllamaToolkit.App
if exist "%APP_DIR%\OllamaToolkit.App.exe" (
    start "" "%APP_DIR%\OllamaToolkit.App.exe"
    exit /b 0
)
dotnet run --project "%SCRIPT_DIR%..\src\OllamaToolkit.App\OllamaToolkit.App.csproj" -c Release
exit /b %ERRORLEVEL%