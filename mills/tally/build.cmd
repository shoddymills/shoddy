@echo off
setlocal
rem Build / run the tally mill (Windows).
rem Unix users: use build.sh (same commands).
rem
rem   build.cmd run [SPEC]       read the spec, print the report, show the chart
rem   build.cmd capture [SPEC]   the same, with nothing on screen - the PNG is the output
rem   build.cmd test             the headless suite: no window, no display
rem   build.cmd build            weave the program into bin\
rem   build.cmd clean            remove bin\
rem
rem SPEC defaults to files\grades.spec. Paths INSIDE a spec (data.file,
rem window.capture) are relative to the directory you run from, which the
rem run target makes the repo root - so a spec written for build.cmd says
rem mills/tally/files/... The shipped specs do exactly that.
rem
rem capture passes --no-window, which opens every scribbler hidden and stops
rem windows outliving the program. Pair it with window.show = no in the spec:
rem the flag says "put nothing on screen", the spec key says "do not wait for
rem anyone to dismiss it". A spec that shows, run with --no-window, would
rem otherwise be waiting on a window nobody can see.
rem
rem The test target needs NOTHING - no display, no network. Everything
rem between a file's text and a finished report is pure, which is the whole
rem reason tally-core.shoddy and tally.shoddy are separate files.
cd /d "%~dp0"

set "REPO=..\.."
set "MILL=%REPO%\bin\mill.exe"

if "%~1"=="" goto run
if /i "%~1"=="run" goto run
if /i "%~1"=="capture" goto capture
if /i "%~1"=="test" goto test
if /i "%~1"=="build" goto build
if /i "%~1"=="clean" goto clean
echo usage: build.cmd [run SPEC^|capture SPEC^|test^|build^|clean]
exit /b 2

:ensure_mill
if not exist "%MILL%" (
    echo mill toolchain not built; building it into %REPO%\bin ...
    dotnet publish "%REPO%\src\Shoddy.Mill" -c Release -o "%REPO%\bin" || exit /b 1
)
exit /b 0

:run
call :ensure_mill || exit /b 1
set "SPEC=%~2"
if "%SPEC%"=="" set "SPEC=mills/tally/files/grades.spec"
pushd "%REPO%"
bin\mill.exe run mills\tally\tally.shoddy %SPEC%
popd
exit /b %errorlevel%

:capture
call :ensure_mill || exit /b 1
set "SPEC=%~2"
if "%SPEC%"=="" set "SPEC=mills/tally/files/grades.spec"
pushd "%REPO%"
bin\mill.exe run --no-window mills\tally\tally.shoddy %SPEC%
popd
exit /b %errorlevel%

:test
call :ensure_mill || exit /b 1
pushd "%REPO%"
bin\mill.exe run mills\tally\test.shoddy < nul
popd
exit /b %errorlevel%

:build
call :ensure_mill || exit /b 1
if not exist bin mkdir bin
"%MILL%" weave tally.shoddy -o bin\tally.dll
echo woven into bin\ - but note that a woven program has no window
echo backend: charts need "mill run". Reports and captures are fine.
exit /b %errorlevel%

:clean
if exist bin rmdir /s /q bin
exit /b 0
