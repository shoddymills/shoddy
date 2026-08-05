@echo off
setlocal enabledelayedexpansion
rem Build / run the simplex-from-mps mill (Windows).
rem Unix users: use build.sh (same commands).
rem
rem   build.cmd            build the program into bin\
rem   build.cmd build      same as above
rem   build.cmd run FILE   build if needed, then run on an MPS FILE
rem   build.cmd test       solve both fixtures and check the answers
rem   build.cmd clean      remove built binaries from bin\
rem
rem The build weaves simplex-mps.shoddy to a self-contained assembly and
rem drops every binary (the program, its runtimeconfig, Shoddy.Runtime.dll)
rem into bin\. To just run an already-built program, no rebuild:
rem
rem   dotnet bin\simplex-mps.dll files\blend.mps       rem or files\mix.mps
rem
rem Add -z (or --zero-lower) to force x >= 0 on every variable.
cd /d "%~dp0"

set "REPO=..\.."
set "MILL=%REPO%\bin\mill.exe"
set "SRC=simplex-mps.shoddy"
set "OUT=bin\simplex-mps.dll"

if "%~1"=="" goto build
if /i "%~1"=="build" goto build
if /i "%~1"=="run" goto run
if /i "%~1"=="test" goto test
if /i "%~1"=="clean" goto clean
echo usage: build.cmd [build^|run FILE.mps^|test^|clean]
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
move /y simplex-mps.dll bin\ >nul
move /y simplex-mps.runtimeconfig.json bin\ >nul
move /y Shoddy.*.dll bin\ >nul 2>nul
echo built -^> %OUT%
exit /b 0

:run
if "%~2"=="" (
    echo usage: build.cmd run FILE.mps [-z]
    exit /b 2
)
if not exist "%OUT%" ( call :build || exit /b 1 )
shift
set "ARGS="
:collect
if "%~1"=="" goto runit
set "ARGS=!ARGS! %1"
shift
goto collect
:runit
dotnet "%OUT%"!ARGS!
exit /b %errorlevel%

:test
call :ensure_mill || exit /b 1
"%MILL%" run test.shoddy
exit /b %errorlevel%

:clean
if exist bin del /q bin\*.dll bin\*.json 2>nul
echo cleaned.
exit /b 0
