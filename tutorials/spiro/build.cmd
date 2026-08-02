@echo off
setlocal
rem Run the spirograph (Windows).
rem Unix users: build.sh, same commands.
rem
rem   build.cmd          draw the spiral in a window
rem   build.cmd run      same as above
rem   build.cmd test     run the headless checks (no window; works in CI)
rem
rem The drawing opens a window, so it runs under `mill run` - a woven
rem `dotnet spiro.dll` has no window backend. The pure half in
rem spiro-core.shoddy is what test.shoddy covers, and that needs no display.
rem
rem Finding a mill, in order: %MILL% if you set it, the repo's own
rem bin\mill.exe when this folder sits inside a checkout, then whatever
rem `mill` is on the PATH - the VS Code extension carries one. Copy this
rem file beside your own program and it keeps working wherever the folder
rem lives.
cd /d "%~dp0"

if defined MILL goto have_mill
set "MILL=..\..\bin\mill.exe"
if exist "%MILL%" goto have_mill
set "MILL=mill"
where mill >nul 2>&1 && goto have_mill
echo no mill found - set MILL=path\to\mill.exe, or install the VS Code >&2
echo extension (it carries one) and use its Run button. >&2
exit /b 1

:have_mill
if "%~1"=="" goto run
if /i "%~1"=="run" goto run
if /i "%~1"=="test" goto test
echo usage: build.cmd [run^|test]
exit /b 2

:run
"%MILL%" run spiro.shoddy
exit /b %errorlevel%

:test
"%MILL%" run test.shoddy
exit /b %errorlevel%
