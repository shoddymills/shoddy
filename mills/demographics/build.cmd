@echo off
setlocal enabledelayedexpansion
rem Build / run the demographics mill (Windows).
rem Unix users: use build.sh (same commands).
rem
rem   build.cmd            build the program into bin\
rem   build.cmd build      same as above
rem   build.cmd run        build if needed, then run the demo
rem   build.cmd clean      remove built binaries from bin\
rem
rem The build weaves demographics.shoddy to a self-contained assembly and
rem drops every binary (the program, its runtimeconfig, Shoddy.Runtime.dll)
rem into bin\. To just run an already-built program, no rebuild:
rem
rem   dotnet bin\demographics.dll
rem
rem Run takes no arguments - the data paths are fixed at dat\, relative
rem to this directory, so run from here.
cd /d "%~dp0"

set "REPO=..\.."
set "MILL=%REPO%\bin\mill.exe"
set "SRC=demographics.shoddy"
set "OUT=bin\demographics.dll"

if "%~1"=="" goto build
if /i "%~1"=="build" goto build
if /i "%~1"=="run" goto run
if /i "%~1"=="clean" goto clean
echo usage: build.cmd [build^|run^|clean]
exit /b 2

:ensure_mill
if not exist "%MILL%" (
    echo mill toolchain not built; building it into %REPO%\bin ...
    dotnet publish "%REPO%\src\Shoddy.Mill" -c Release -o "%REPO%\bin" || exit /b 1
)
exit /b 0

:build
call :ensure_mill || exit /b 1
"%MILL%" weave "%SRC%" || exit /b 1
if not exist bin mkdir bin
move /y demographics.dll bin\ >nul
move /y demographics.runtimeconfig.json bin\ >nul
move /y Shoddy.*.dll bin\ >nul 2>nul
echo built -^> %OUT%
exit /b 0

:run
if not exist "%OUT%" ( call :build || exit /b 1 )
dotnet "%OUT%"
exit /b %errorlevel%

:clean
if exist bin del /q bin\*.dll bin\*.json 2>nul
echo cleaned.
exit /b 0
