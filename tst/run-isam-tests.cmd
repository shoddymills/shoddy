@echo off
rem Copyright (c) Stephen Vincent Foster and Shoddy Language contributors.
rem Licensed under the MIT License. See the LICENSE file in the project root.
rem
rem Runs the machines/isam.shoddy test suite in order, then deletes the
rem test data files the run produced (_isamtest.tmp, the _err_*.tmp
rem fixtures, and their .idx companions). The individual programs still
rem leave their files in place when run by hand — only this wrapper
rem cleans up. Requires bin\mill.exe already built (build.cmd build).
rem Usage: run-isam-tests.cmd

setlocal enabledelayedexpansion
cd /d "%~dp0"

if not exist bin\mill.exe (
    echo mill not built — run build.cmd build first.
    exit /b 2
)

echo ============================================================
echo  1. isamtest.shoddy — build, mutate, verify
echo ============================================================
bin\mill.exe run tst\isamtest.shoddy
if errorlevel 1 (
    echo.
    echo FAILED: isamtest.shoddy
    exit /b 1
)

echo.
echo ============================================================
echo  2. isamdump.shoddy — round-trip read (no DELETE arg: leaves the file)
echo ============================================================
bin\mill.exe run tst\isamdump.shoddy
if errorlevel 1 (
    echo.
    echo FAILED: isamdump.shoddy
    exit /b 1
)

echo.
echo ============================================================
echo  3. isam-errors — each one is EXPECTED to abort
echo ============================================================
call :checkerr dup-insert        "ISAMINSERT: DUPLICATE KEY"
call :checkerr get-missing       "ISAMGET: KEY NOT FOUND"
call :checkerr update-missing    "ISAMUPDATE: KEY NOT FOUND"
call :checkerr delete-missing    "ISAMDELETE: KEY NOT FOUND"
call :checkerr next-past-end     "ISAMNEXT: NO NEXT KEY"
call :checkerr prev-before-start "ISAMPREV: NO PREVIOUS KEY"
call :checkerr empty-first       "ISAMFIRST: EMPTY FILE"
call :checkerr empty-last        "ISAMLAST: EMPTY FILE"

echo.
echo ============================================================
echo  Cleaning up test data files:
echo ============================================================
dir /b _isamtest.tmp* _err_*.tmp* 2>nul
del /q _isamtest.tmp _isamtest.tmp.idx _err_*.tmp _err_*.tmp.idx 2>nul

exit /b 0

:checkerr
set "NAME=%~1"
set "EXPECT=%~2"
set "OUT=%TEMP%\isamerr_%NAME%.txt"
bin\mill.exe run tst\isam-errors\%NAME%.shoddy >"%OUT%" 2>&1
findstr /C:"%EXPECT%" "%OUT%" >nul
if errorlevel 1 (
    echo   [FAIL] %NAME% -- expected "%EXPECT%", got:
    type "%OUT%"
) else (
    echo   [PASS] %NAME%
)
del "%OUT%" >nul 2>&1
exit /b 0
