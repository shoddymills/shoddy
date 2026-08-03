@echo off
setlocal
rem Build / run the weather-glass mill (Windows).
rem Unix users: use build.sh (same commands).
rem
rem   build.cmd run 63011   fetch and draw the report for a US ZIP
rem   build.cmd test        the offline suite - no network, no --allow-net
rem   build.cmd build       weave the program into bin\
rem   build.cmd clean       remove bin\
rem
rem The run target needs --allow-net: four HTTPS GETs, to api.zippopotam.us
rem for the ZIP and to api.weather.gov for the forecast. Both get a
rem User-Agent, because the weather service answers 403 without one.
rem
rem The test target needs NOTHING. Everything between a raw response and a
rem finished row is pure, so the captured responses in files\ go through
rem the core and the result is compared line by line against
rem files\expected.out. That is the whole reason for the core/shell split.
cd /d "%~dp0"

set "REPO=..\.."
set "MILL=%REPO%\bin\mill.exe"

if "%~1"=="" goto run
if /i "%~1"=="run" goto run
if /i "%~1"=="test" goto test
if /i "%~1"=="build" goto build
if /i "%~1"=="clean" goto clean
echo usage: build.cmd [run ZIP^|test^|build^|clean]
exit /b 2

:ensure_mill
if not exist "%MILL%" (
    echo mill toolchain not built; building it into %REPO%\bin ...
    dotnet publish "%REPO%\src\Shoddy.Mill" -c Release -o "%REPO%\bin" || exit /b 1
)
exit /b 0

:run
call :ensure_mill || exit /b 1
set "ZIP=%~2"
if "%ZIP%"=="" set "ZIP=63011"
pushd "%REPO%"
bin\mill.exe run --allow-net mills\weather-glass\weather-glass.shoddy %ZIP%
popd
exit /b %errorlevel%

:test
call :ensure_mill || exit /b 1
pushd "%REPO%"
bin\mill.exe run mills\weather-glass\test.shoddy < nul
popd
exit /b %errorlevel%

:build
call :ensure_mill || exit /b 1
if not exist bin mkdir bin
"%MILL%" weave weather-glass.shoddy -o bin\weather-glass.dll
echo woven into bin\ - run with: dotnet bin\weather-glass.dll 63011 (needs SHODDY_ALLOW_NET=1)
exit /b %errorlevel%

:clean
if exist bin rmdir /s /q bin
exit /b 0
