@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo The .NET SDK is not installed. Get it from https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

dotnet run --project app\VoiceScan.Avalonia -c Release
if errorlevel 1 pause
