@echo off
REM PDF Redaction Library Test Script - Windows Version
REM This script provides an easy command-line interface for testing the PDF redaction functionality

setlocal enabledelayedexpansion

echo =========================================
echo PDF Redaction Library - Test Script
echo =========================================
echo.

REM Find .NET SDK
where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: .NET SDK not found
    echo Please install .NET 8.0 SDK from: https://dotnet.microsoft.com/download
    exit /b 1
)

for /f "tokens=*" %%i in ('dotnet --version') do set DOTNET_VERSION=%%i
echo ✓ .NET SDK found: %DOTNET_VERSION%
echo.

REM Parse command-line arguments
set TEST_MODE=all
set VERBOSE=false
set OUTPUT_DIR=.\RedactionTestOutput

:parse_args
if "%~1"=="" goto end_parse
if /i "%~1"=="--demo" (
    set TEST_MODE=demo
    shift
    goto parse_args
)
if /i "%~1"=="--tests" (
    set TEST_MODE=tests
    shift
    goto parse_args
)
if /i "%~1"=="--integration" (
    set TEST_MODE=integration
    shift
    goto parse_args
)
if /i "%~1"=="--verbose" (
    set VERBOSE=true
    shift
    goto parse_args
)
if /i "%~1"=="-v" (
    set VERBOSE=true
    shift
    goto parse_args
)
if /i "%~1"=="--output" (
    set OUTPUT_DIR=%~2
    shift
    shift
    goto parse_args
)
if /i "%~1"=="-o" (
    set OUTPUT_DIR=%~2
    shift
    shift
    goto parse_args
)
if /i "%~1"=="--help" goto show_help
if /i "%~1"=="-h" goto show_help

echo Unknown option: %~1
echo Use --help for usage information
exit /b 1

:show_help
echo Usage: %0 [OPTIONS]
echo.
echo Options:
echo   --demo          Run demonstration program (creates sample PDFs and redacts them)
echo   --tests         Run unit tests only
echo   --integration   Run integration tests only
echo   --verbose, -v   Show detailed output
echo   --output DIR    Specify output directory for demo files (default: .\RedactionTestOutput)
echo   --help, -h      Show this help message
echo.
echo Default: Runs both demo and tests
exit /b 0

:end_parse

REM Execute based on test mode
if /i "%TEST_MODE%"=="demo" goto run_demo
if /i "%TEST_MODE%"=="tests" goto run_tests
if /i "%TEST_MODE%"=="integration" goto run_integration
if /i "%TEST_MODE%"=="all" goto run_all
goto end

:run_demo
echo =========================================
echo Running Redaction Demo Program
echo =========================================
echo.

echo Building demo program...
cd Excise.App.Demo

if "%VERBOSE%"=="true" (
    dotnet build -c Release
) else (
    dotnet build -c Release >nul 2>&1
)

if %errorlevel% neq 0 (
    echo ERROR: Failed to build demo program
    exit /b 1
)

echo ✓ Demo program built successfully
echo.

echo Running redaction demonstrations...
echo Output will be saved to: %OUTPUT_DIR%
echo.

if "%VERBOSE%"=="true" (
    dotnet run -c Release --no-build
) else (
    dotnet run -c Release --no-build | findstr /R "^=== ^--- ^Test ^✓ ^✗ Content"
)

cd ..

echo.
echo ✓ Demo completed!
echo Check the PDFs in: %OUTPUT_DIR%
echo.

if /i "%TEST_MODE%"=="all" goto run_tests
goto success

:run_tests
echo =========================================
echo Running Redaction Tests
echo =========================================
echo.

echo Building test project...
cd Excise.App.Tests

if "%VERBOSE%"=="true" (
    dotnet build -c Release
) else (
    dotnet build -c Release >nul 2>&1
)

if %errorlevel% neq 0 (
    echo ERROR: Failed to build test project
    exit /b 1
)

echo ✓ Test project built successfully
echo.

echo Running tests...
if "%VERBOSE%"=="true" (
    dotnet test -c Release --no-build --logger "console;verbosity=detailed"
) else (
    dotnet test -c Release --no-build --logger "console;verbosity=normal"
)

set TEST_RESULT=%errorlevel%
cd ..

echo.
if %TEST_RESULT% equ 0 (
    echo ✓ All tests passed!
) else (
    echo ✗ Some tests failed (exit code: %TEST_RESULT%)
    exit /b %TEST_RESULT%
)
echo.

goto success

:run_integration
echo =========================================
echo Running Integration Tests Only
echo =========================================
echo.

echo Building test project...
cd Excise.App.Tests

if "%VERBOSE%"=="true" (
    dotnet build -c Release
) else (
    dotnet build -c Release >nul 2>&1
)

if %errorlevel% neq 0 (
    echo ERROR: Failed to build test project
    exit /b 1
)

echo ✓ Test project built successfully
echo.

echo Running integration tests...
if "%VERBOSE%"=="true" (
    dotnet test -c Release --no-build --filter "FullyQualifiedName~Integration" --logger "console;verbosity=detailed"
) else (
    dotnet test -c Release --no-build --filter "FullyQualifiedName~Integration" --logger "console;verbosity=normal"
)

set TEST_RESULT=%errorlevel%
cd ..

echo.
if %TEST_RESULT% equ 0 (
    echo ✓ All integration tests passed!
) else (
    echo ✗ Some integration tests failed (exit code: %TEST_RESULT%)
    exit /b %TEST_RESULT%
)
echo.

goto success

:run_all
call :run_demo
if %errorlevel% neq 0 exit /b %errorlevel%
call :run_tests
if %errorlevel% neq 0 exit /b %errorlevel%
goto success

:success
echo =========================================
echo ✓ All operations completed successfully!
echo =========================================
echo.
echo Summary:
if /i "%TEST_MODE%"=="demo" (
    echo   - Demo PDFs created in: %OUTPUT_DIR%
    echo   - Open the PDFs to visually verify redactions
)
if /i "%TEST_MODE%"=="tests" (
    echo   - All tests passed
    echo   - Redaction library is functioning correctly
)
if /i "%TEST_MODE%"=="integration" (
    echo   - All integration tests passed
    echo   - Content-level redaction verified
)
if /i "%TEST_MODE%"=="all" (
    echo   - Demo PDFs created in: %OUTPUT_DIR%
    echo   - All tests passed
    echo   - Redaction library is fully functional
)
echo.

:end
endlocal
