@echo off
chcp 65001 >nul
cd /d "%~dp0"

rem Builds dist\FortniteChestCounter.exe: a single file that does not need .NET installed.
rem (A build made here does not update itself; the GitHub release workflow builds the ones that do.)
dotnet publish src\ChestTracker -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
if errorlevel 1 (
    pause
    exit /b 1
)
echo.
echo Done: dist\FortniteChestCounter.exe
pause
