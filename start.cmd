@echo off
chcp 65001 >nul
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo The .NET 10 SDK is missing. Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and open this file again.
    pause
    exit /b 1
)

rem Builds if needed and starts Fortnite Chest Counter. Arguments are passed through (for example --port 5400).
dotnet run -c Release --project src\ChestTracker -- %*
if errorlevel 1 pause
