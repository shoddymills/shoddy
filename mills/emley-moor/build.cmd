@echo off
setlocal
rem Build / run the emley-moor mill (Windows).
rem Unix users: use build.sh (same commands).
rem
rem   build.cmd            serve on http://127.0.0.1:8080/
rem   build.cmd run        same as above
rem   build.cmd test       the routing tests - no network, no --allow-net
rem   build.cmd build      weave the server into bin\
rem   build.cmd clean      remove bin\
rem
rem The server needs --allow-net, because the network is a gated
rem capability and a web server is the most network a program can be. It
rem binds the LOOPBACK only: reachable from this machine and nowhere
rem else. Editing that to ListenOn("0.0.0.0", ...) puts a plaintext HTTP
rem server written over a weekend on a public address, so do not.
rem
rem The test target needs nothing. Every route is graded by calling
rem Respond(request) with fixture text, because the server is a pure
rem function of the request and the socket is twenty lines beside it.
cd /d "%~dp0"

set "REPO=..\.."
set "MILL=%REPO%\bin\mill.exe"

if "%~1"=="" goto run
if /i "%~1"=="run" goto run
if /i "%~1"=="test" goto test
if /i "%~1"=="build" goto build
if /i "%~1"=="clean" goto clean
echo usage: build.cmd [run^|test^|build^|clean]
exit /b 2

:ensure_mill
if not exist "%MILL%" (
    echo mill toolchain not built; building it into %REPO%\bin ...
    dotnet publish "%REPO%\src\Shoddy.Mill" -c Release -o "%REPO%\bin" || exit /b 1
)
exit /b 0

:run
call :ensure_mill || exit /b 1
"%MILL%" run --allow-net emley-moor.shoddy
exit /b %errorlevel%

:test
call :ensure_mill || exit /b 1
"%MILL%" run test.shoddy < nul
exit /b %errorlevel%

:build
call :ensure_mill || exit /b 1
if not exist bin mkdir bin
"%MILL%" weave emley-moor.shoddy -o bin\emley-moor.dll
echo woven into bin\ - run with: dotnet bin\emley-moor.dll (needs SHODDY_ALLOW_NET=1)
exit /b %errorlevel%

:clean
if exist bin rmdir /s /q bin
exit /b 0
