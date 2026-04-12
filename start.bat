@echo off
chcp 65001 >nul
title NVevaAce

cd /d "%~dp0"

echo ========================================
echo      NVevaAce Launcher
echo ========================================
echo.

dotnet --version
echo.

echo [1/3] Restoring dependencies...
dotnet restore NVevaAce.csproj
if %errorlevel% neq 0 (
    echo ERROR: Restore failed
    pause
    exit /b 1
)

echo [2/3] Building project...
dotnet build NVevaAce.csproj -c Release
if %errorlevel% neq 0 (
    echo ERROR: Build failed
    pause
    exit /b 1
)

echo [3/3] Starting NVevaAce...
echo.
dotnet run --project NVevaAce.csproj -c Release

pause