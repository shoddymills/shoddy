@echo off
setlocal
rem Run Colossal Cave Adventure (Windows).
rem Unix users: use build.sh (same commands).
rem
rem   build.cmd          play the game
rem   build.cmd run      same as above
rem   build.cmd test     run the headless model checks
rem   build.cmd smoke    replay 16 recorded transcripts   (~3 minutes)
rem   build.cmd check    replay all 107                   (~18 minutes)
rem
rem open-cave is a console program: prompts in, text out, no window.
rem Words are significant to five letters, as they have been since 1977
rem -- XYZZY, PLUGH, and PLOVER all still work.
rem
rem The cave lives in the cave-*.shoddy tables, which are hand-edited
rem source: the YAML and the generator that first produced them are in
rem obsolete\ and are not run. test.shoddy covers the pure model in
rem cave-core.shoddy; everything below the parser is covered by the
rem recorded transcripts in tests\, which are the port's real
rem specification. See tests\README.
rem
rem The transcript harness is a shell script and needs the bash that
rem ships with Git for Windows on PATH.
cd /d "%~dp0"

set "REPO=..\.."
set "MILL=%REPO%\bin\mill.exe"

if "%~1"=="" goto run
if /i "%~1"=="run" goto run
if /i "%~1"=="test" goto test
if /i "%~1"=="smoke" goto smoke
if /i "%~1"=="check" goto check
echo usage: build.cmd [run^|test^|smoke^|check]
exit /b 2

:ensure_mill
if not exist "%MILL%" (
    echo mill toolchain not built; building it into %REPO%\bin ...
    dotnet publish "%REPO%\src\Shoddy.Mill" -c Release -o "%REPO%\bin" || exit /b 1
)
exit /b 0

:ensure_bash
where bash >nul 2>&1 || (
    echo bash not found on PATH; the transcript harness needs the bash
    echo that ships with Git for Windows.
    exit /b 1
)
exit /b 0

:run
call :ensure_mill || exit /b 1
"%MILL%" run cave.shoddy
exit /b %errorlevel%

:test
call :ensure_mill || exit /b 1
"%MILL%" run test.shoddy
exit /b %errorlevel%

:smoke
call :ensure_mill || exit /b 1
call :ensure_bash || exit /b 1
bash tests/run.sh --smoke
exit /b %errorlevel%

:check
call :ensure_mill || exit /b 1
call :ensure_bash || exit /b 1
bash tests/run.sh
exit /b %errorlevel%
