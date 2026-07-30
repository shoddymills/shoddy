@echo off
setlocal enabledelayedexpansion
rem Build / run the iris mill (Windows).
rem Unix users: use build.sh (same commands).
rem
rem   build.cmd            build both programs into bin\
rem   build.cmd build      same as above
rem   build.cmd train      build if needed, then train the model
rem                        (writes dat\iris-model.bin; a few seconds)
rem   build.cmd run        build if needed, then classify interactively
rem   build.cmd clean      remove built binaries from bin\
rem
rem The build weaves iris-train.shoddy (the trainer) and iris.shoddy
rem (the predictor) to self-contained assemblies and drops every binary
rem (the programs, runtimeconfigs, Shoddy.Runtime.dll) into bin\. To
rem just run already-built programs, no rebuild:
rem
rem   dotnet bin\iris-train.dll
rem   dotnet bin\iris.dll
rem
rem Neither takes arguments - the data and model paths are fixed at
rem dat\, relative to this directory, so run from here. Train first;
rem the predictor aborts (politely) without dat\iris-model.bin.
rem
rem This is the classification counterpart to the demographics mill.
rem Demographics predicts a number and is scored on how close it gets;
rem this predicts a category, is scored on how often it is right, and
rem reports how sure it was. Training takes seconds rather than nine
rem minutes, which makes it the one to reach for when changing
rem machines\neural.shoddy and wanting to know quickly whether it still
rem works.
cd /d "%~dp0"

set "REPO=..\.."
set "MILL=%REPO%\bin\mill.exe"
set "TRAINOUT=bin\iris-train.dll"
set "RUNOUT=bin\iris.dll"

if "%~1"=="" goto build
if /i "%~1"=="build" goto build
if /i "%~1"=="train" goto train
if /i "%~1"=="run" goto run
if /i "%~1"=="clean" goto clean
echo usage: build.cmd [build^|train^|run^|clean]
exit /b 2

:ensure_mill
if not exist "%MILL%" (
    echo mill toolchain not built; building it into %REPO%\bin ...
    dotnet publish "%REPO%\src\Shoddy.Mill" -c Release -o "%REPO%\bin" || exit /b 1
)
exit /b 0

:build
call :ensure_mill || exit /b 1
"%MILL%" weave iris-train.shoddy || exit /b 1
"%MILL%" weave iris.shoddy || exit /b 1
if not exist bin mkdir bin
move /y iris-train.dll bin\ >nul
move /y iris-train.runtimeconfig.json bin\ >nul
move /y iris.dll bin\ >nul
move /y iris.runtimeconfig.json bin\ >nul
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

:clean
if exist bin del /q bin\*.dll bin\*.json 2>nul
echo cleaned.
exit /b 0
