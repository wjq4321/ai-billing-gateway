@echo off
REM ============================================================
REM  AI Billing Gateway - build script
REM  (ASCII only: cmd.exe reads .cmd as GBK by default, so any
REM   non-ASCII text here would be garbled and break parsing.)
REM
REM  Builds each project separately. The .slnx solution restore is
REM  broken with this SDK build, but building per project is fine.
REM
REM  Offline note: if nuget.org is unreachable and you created
REM  NuGet.Config.local, this script will use it automatically.
REM ============================================================
setlocal

set "ROOT=%~dp0"
if "%DOTNET_CLI_HOME%"=="" set "DOTNET_CLI_HOME=%ROOT%.dotnet-home"
if not exist "%DOTNET_CLI_HOME%" mkdir "%DOTNET_CLI_HOME%"

set "CONFIG=Release"

REM Use local NuGet override when present (offline / restricted networks)
set "CFGFILE="
if exist "%ROOT%NuGet.Config.local" set "CFGFILE=--configfile "%ROOT%NuGet.Config.local""

echo.
echo === Building AiBilling.Core ===
dotnet build "%ROOT%src\AiBilling.Core\AiBilling.Core.csproj" -c %CONFIG% --nologo %CFGFILE%
if errorlevel 1 goto fail

echo.
echo === Building AiBilling.Gateway ===
dotnet build "%ROOT%src\AiBilling.Gateway\AiBilling.Gateway.csproj" -c %CONFIG% --nologo %CFGFILE%
if errorlevel 1 goto fail

echo.
echo === Building AiBilling.Cli ===
dotnet build "%ROOT%src\AiBilling.Cli\AiBilling.Cli.csproj" -c %CONFIG% --nologo %CFGFILE%
if errorlevel 1 goto fail

echo.
echo === Building AiBilling.Tests ===
dotnet build "%ROOT%tests\AiBilling.Tests\AiBilling.Tests.csproj" -c %CONFIG% --nologo %CFGFILE%
if errorlevel 1 goto fail

echo.
echo ============================================================
echo  Build succeeded
echo.
echo  Start gateway:
echo    start-gateway.cmd
echo.
echo  Admin UI : http://localhost:8080/admin
echo  CLI      : dotnet "%ROOT%src\AiBilling.Cli\bin\%CONFIG%\net8.0\aibilling.dll" test
echo ============================================================
echo.
exit /b 0

:fail
echo.
echo *** BUILD FAILED - see errors above ***
echo.
exit /b 1
