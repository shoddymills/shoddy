@echo off
setlocal
rem Run Devil's Dust (Windows).
rem Unix users: use build.sh (same commands).
rem
rem   build.cmd          run the demo
rem   build.cmd run      same as above
rem   build.cmd test     run the headless model checks
rem
rem Devil's Dust is a scribbler program: it opens a window, so it runs
rem under `mill run` (a woven `dotnet FILE.dll` has no window backend).
rem The pure model in devils-dust-core.shoddy is what the tests cover;
rem this wrapper just launches the windowed demo. Q or Escape quits.
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
"%MILL%" run devils-dust.shoddy
exit /b %errorlevel%

:test
call :ensure_mill || exit /b 1
"%MILL%" run test.shoddy
exit /b %errorlevel%
