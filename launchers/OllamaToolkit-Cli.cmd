@echo off
setlocal
set SCRIPT_DIR=%~dp0
set CLI_DIR=%SCRIPT_DIR%..\publish\OllamaToolkit.Cli
if exist "%CLI_DIR%\OllamaToolkit.Cli.exe" (
    "%CLI_DIR%\OllamaToolkit.Cli.exe" %*
    exit /b %ERRORLEVEL%
)
dotnet run --project "%SCRIPT_DIR%..\src\OllamaToolkit.Cli\OllamaToolkit.Cli.csproj" -c Release -- %*
exit /b %ERRORLEVEL%