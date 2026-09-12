@echo off
REM ============================================================
REM  AI Billing Gateway - start script
REM  (ASCII only: cmd.exe reads .cmd as GBK by default, so any
REM   non-ASCII text here would be garbled and break parsing.)
REM ============================================================
setlocal

set "ROOT=%~dp0"
if "%DOTNET_CLI_HOME%"=="" set "DOTNET_CLI_HOME=%ROOT%.dotnet-home"
if not exist "%DOTNET_CLI_HOME%" mkdir "%DOTNET_CLI_HOME%"

set "CONFIG=Release"
if not exist "%ROOT%src\AiBilling.Gateway\bin\Release\net8.0\AiBilling.Gateway.dll" set "CONFIG=Debug"

set "DLL=%ROOT%src\AiBilling.Gateway\bin\%CONFIG%\net8.0\AiBilling.Gateway.dll"

if not exist "%DLL%" (
    echo.
    echo [ERROR] Gateway not built yet:
    echo         %DLL%
    echo.
    echo  Run build.cmd first.
    echo.
    pause
    exit /b 1
)

echo.
echo  Starting AI Billing Gateway (%CONFIG%) ...
echo    Admin UI : http://localhost:8080/admin
echo    Gateway  : http://localhost:8080
echo    Press Ctrl+C to stop.
echo.

cd /d "%ROOT%src\AiBilling.Gateway"
dotnet "%DLL%"

echo.
echo Gateway stopped.
pause
