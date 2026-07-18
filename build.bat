@echo off
echo ==================================
echo PDF Editor - Build Script
echo ==================================
echo.

REM Check if .NET is installed
where dotnet >nul 2>nul
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: .NET SDK is not installed
    echo.
    echo Please install .NET 8.0 SDK:
    echo   winget install Microsoft.DotNet.SDK.8
    echo   or download from: https://dotnet.microsoft.com/download
    exit /b 1
)

echo [OK] .NET SDK found
dotnet --version
echo.

REM Navigate to project directory
cd /d "%~dp0\Excise.App"

echo Restoring NuGet packages...
dotnet restore

if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Failed to restore packages
    exit /b 1
)

echo.
echo Building project...
dotnet build -c Release

if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Build failed
    exit /b 1
)

echo.
echo ==================================
echo [OK] Build successful!
echo ==================================
echo.
echo To run the application:
echo   dotnet run --project Excise.App
echo.
echo To publish a standalone executable:
echo   dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
echo.
echo To build CLI with ReadyToRun (R2R) compilation for faster cold-start:
echo   dotnet publish Excise.Cli\Excise.Cli.csproj -c Release -r win-x64 --self-contained true
echo   Binaries will be in: Excise.Cli\bin\Release\net10.0\win-x64\publish\
echo   (R2R is auto-enabled in csproj)
echo.
pause
