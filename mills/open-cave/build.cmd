@echo off
setlocal
rem Run Colossal Cave Adventure (Windows).
rem Unix users: use build.sh (same commands).
rem
rem   build.cmd          play the game
rem   build.cmd run      same as above
rem   build.cmd test     run the headless model checks
rem   build.cmd gen      regenerate cave-data.shoddy from adventure.yaml
rem
rem open-cave is a console program: prompts in, text out, no window. The
rem pure model in cave-core.shoddy is what test.shoddy covers; this
rem wrapper just launches the game. Words are significant to five
rem letters, as they have been since 1977 -- XYZZY, PLUGH, and PLOVER
rem all still work.
rem
rem The cave itself lives in cave-data.shoddy, generated from
rem adventure.yaml by gen.shoddy and committed, so playing needs nothing
rem but the mill. The generator is Shoddy too: it reads the 154K YAML,
rem resolves its anchors, and writes the tables out as Shoddy source.
cd /d "%~dp0"

set "REPO=..\.."
set "MILL=%REPO%\bin\mill.exe"

if "%~1"=="" goto run
if /i "%~1"=="run" goto run
if /i "%~1"=="test" goto test
if /i "%~1"=="gen" goto gen
echo usage: build.cmd [run^|test^|gen]
exit /b 2

:ensure_mill
if not exist "%MILL%" (
    echo mill toolchain not built; building it into %REPO%\bin ...
    dotnet publish "%REPO%\src\Shoddy.Mill" -c Release -o "%REPO%\bin" || exit /b 1
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

:gen
call :ensure_mill || exit /b 1
"%MILL%" run gen.shoddy
exit /b %errorlevel%
