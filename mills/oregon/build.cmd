@echo off
setlocal
rem Run The Oregon Trail (Windows).
rem Unix users: use build.sh (same commands).
rem
rem   build.cmd          run the game
rem   build.cmd run      same as above
rem   build.cmd test     run the headless model checks
rem
rem Oregon is a console program: prompts in, text out, no window. The
rem pure model in oregon-core.shoddy is what test.shoddy covers; this
rem wrapper just launches the interactive game. When told to TYPE a
rem word (BANG, BLAM, POW, WHAM), type it fast and press Enter.
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
"%MILL%" run oregon.shoddy
exit /b %errorlevel%

:test
call :ensure_mill || exit /b 1
"%MILL%" run test.shoddy
exit /b %errorlevel%
