@echo off
setlocal
rem Run Mungo Caverns (Windows).
rem Unix users: use build.sh (same commands).
rem
rem   build.cmd          play the game
rem   build.cmd run      same as above
rem   build.cmd test     run every headless suite (test.shoddy, tests\test-*)
rem   build.cmd smoke    replay 16 recorded transcripts   (~3 minutes)
rem   build.cmd check    replay all 107                   (~18 minutes)
rem
rem mungo-caverns is a console program: prompts in, text out, no window.
rem Words are significant to five letters, as they have been since 1977
rem -- XYZZY, PLUGH, and PLOVER all still work.
rem
rem The cave lives in the mungo-caverns-*.shoddy tables, which are
rem hand-edited source.
rem
rem Testing comes in layers.  test.shoddy covers the generator, the tables
rem and the parser; tests\test-tables.shoddy asserts what must be true of
rem the cave itself; tests\test-turn.shoddy drives whole turns as a
rem function; tests\test-walk.shoddy plays a game to the gold and back;
rem and tests\test-fuzz.shoddy throws random commands at it.  Above them,
rem tests\run.sh replays the recorded transcripts, which are the port's
rem specification.  See tests\README.
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
"%MILL%" run mungo-caverns.shoddy
exit /b %errorlevel%

:test
call :ensure_mill || exit /b 1
"%MILL%" run test.shoddy < nul || exit /b 1
for %%s in (tests\test-*.shoddy) do (
    echo --- %%s
    "%MILL%" run "%%s" < nul || exit /b 1
)
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
