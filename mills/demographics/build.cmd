@echo off
setlocal enabledelayedexpansion
rem Build / run the demographics mill (Windows).
rem Unix users: use build.sh (same commands).
rem
rem   build.cmd            build both programs into bin\
rem   build.cmd build      same as above
rem   build.cmd train      build if needed, then train the model
rem                        (writes dat\people-model.bin; ~9 minutes)
rem   build.cmd run        build if needed, then predict interactively
rem   build.cmd test       grade the shipped model against the data files
rem   build.cmd clean      remove built binaries from bin\
rem
rem The build weaves demographics-train.shoddy (the trainer) and
rem demographics.shoddy (the predictor) to self-contained assemblies and
rem drops every binary (the programs, runtimeconfigs, Shoddy.Runtime.dll)
rem into bin\. To just run already-built programs, no rebuild:
rem
rem   dotnet bin\demographics-train.dll
rem   dotnet bin\demographics.dll
rem
rem Neither takes arguments - the data and model paths are fixed at
rem dat\, relative to this directory, so run from here. Train first;
rem the predictor aborts (politely) without dat\people-model.bin.
cd /d "%~dp0"

set "REPO=..\.."
set "MILL=%REPO%\bin\mill.exe"
set "TRAINOUT=bin\demographics-train.dll"
set "RUNOUT=bin\demographics.dll"

if "%~1"=="" goto build
if /i "%~1"=="build" goto build
if /i "%~1"=="train" goto train
if /i "%~1"=="run" goto run
if /i "%~1"=="test" goto test
if /i "%~1"=="clean" goto clean
echo usage: build.cmd [build^|train^|run^|test^|clean]
exit /b 2

:ensure_mill
if not exist "%MILL%" (
    echo mill toolchain not built; building it into %REPO%\bin ...
    dotnet publish "%REPO%\src\Shoddy.Mill" -c Release -o "%REPO%\bin" || exit /b 1
)
exit /b 0

:build
call :ensure_mill || exit /b 1
"%MILL%" weave demographics-train.shoddy || exit /b 1
"%MILL%" weave demographics.shoddy || exit /b 1
if not exist bin mkdir bin
move /y demographics-train.dll bin\ >nul
move /y demographics-train.runtimeconfig.json bin\ >nul
move /y demographics.dll bin\ >nul
move /y demographics.runtimeconfig.json bin\ >nul
move /y Shoddy.*.dll bin\ >nul 2>nul
echo built -^> %TRAINOUT%, %RUNOUT%
exit /b 0

:train
if not exist "%TRAINOUT%" ( call :build || exit /b 1 )
dotnet "%TRAINOUT%"
exit /b %errorlevel%

:run
if not exist "%RUNOUT%" ( call :build || exit /b 1 )
dotnet "%RUNOUT%"
exit /b %errorlevel%

:test
rem Run from source, not from bin\: the point is to grade what is in the
rem tree. No training - nine minutes, and it would rewrite the model
rem being graded. test.shoddy says so at more length.
call :ensure_mill || exit /b 1
"%MILL%" run test.shoddy
exit /b %errorlevel%

:clean
if exist bin del /q bin\*.dll bin\*.json 2>nul
echo cleaned.
exit /b 0
