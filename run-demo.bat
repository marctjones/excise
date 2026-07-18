@echo off
setlocal

echo === PDF Redaction Demo Runner ===
echo.

REM Check if .NET is installed
where dotnet >nul 2>nul
if %ERRORLEVEL% NEQ 0 (
    echo Error: .NET 8.0 SDK is not installed
    echo Please install from: https://dotnet.microsoft.com/download
    exit /b 1
)

for /f "delims=" %%i in ('dotnet --version') do set DOTNET_VERSION=%%i
echo ✓ .NET SDK found: %DOTNET_VERSION%
echo.

REM Build and run the demo
cd Excise.App.Demo

echo Building demo program...
dotnet build

echo.
echo Running demonstration...
echo.

dotnet run

echo.
echo === Demo Complete ===
echo.
echo Check the RedactionDemo directory for generated PDFs:
dir /B RedactionDemo\

pause
