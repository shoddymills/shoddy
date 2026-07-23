@echo off
setlocal
rem Run Shoddy Pac (Windows).
rem Unix users: use build.sh (same commands).
rem
rem   build.cmd          run the game
rem   build.cmd run      same as above
rem   build.cmd test     run the headless simulation smoke check
rem
rem Pac is a VT100 terminal program — no scribbler, no window: it draws
rem with escape sequences and reads keys through the InKey builtin, so
rem it wants a real ANSI-capable console at least 80x26 (Windows
rem Terminal, or any modern terminal). The pure model in pac-core.shoddy
rem is what the unit tests cover; this wrapper just launches the game.
rem Controls: WASD / arrows / keypad move, Q or Escape quits.
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
"%MILL%" run pac.shoddy
exit /b %errorlevel%

:test
call :ensure_mill || exit /b 1
"%MILL%" run test.shoddy
exit /b %errorlevel%
