@echo off
rem Copyright (c) 2026 Stephen Vincent Foster. All rights reserved.
rem
rem This file is part of the Shoddy Language project.
rem Licensed under the Shoddy Language License 1.0.0 (PolyForm Noncommercial
rem License 1.0.0 with Additional Use Grant). See the LICENSE file in the
rem project root for full terms.
rem
rem Runs the machines/isam.shoddy test suite in order and leaves every
rem data file on disk afterward — nothing here deletes anything (same
rem as isamdump.shoddy's own default, and isam.shoddy itself, which
rem never calls DeleteFile at all). Requires bin\mill.exe already
rem built (build.cmd build). Usage: run-isam-tests.cmd

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
echo  Data files left in place:
echo ============================================================
dir /b _isamtest.tmp _err_*.tmp 2>nul

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
