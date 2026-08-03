@echo off
setlocal
rem Run Mungo Caverns (Windows).
rem Unix users: use build.sh (same commands).
rem
rem   build.cmd          play the game
rem   build.cmd run      same as above
rem   build.cmd test     run every headless suite (test.shoddy, tests\test-*)
rem
rem mungo-caverns is a console program: prompts in, text out, no window.
rem Words are significant to five letters, as they have been since 1977
rem -- XYZZY, PLUGH, and PLOVER all still work.
rem
rem The cave lives in the mungo-caverns-*.shoddy tables, which are
rem hand-edited source.
rem
rem Testing is five headless suites.  test.shoddy covers the generator,
rem the tables and the parser; tests\test-tables.shoddy asserts what must
rem be true of the cave itself; tests\test-turn.shoddy drives whole turns
rem as a function; tests\test-walk.shoddy plays a game to the gold and
rem back; and tests\test-fuzz.shoddy throws random commands at it.  194
rem assertions, about twenty seconds, and "test" runs the lot.  See
rem tests\README.
cd /d "%~dp0"

set "REPO=..\.."
set "MILL=%REPO%\bin\mill.exe"

if "%~1"=="" goto run
if /i "%~1"=="run" goto run
if /i "%~1"=="test" goto test
echo usage: build.cmd [run^|test]
exit /b 2

:ensure_mill
if not exist "%MILL%" (
    echo mill toolchain not built; building it into %REPO%\bin ...
    dotnet publish "%REPO%\src\Shoddy.Mill" -c Release -o "%REPO%\bin" || exit /b 1
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
