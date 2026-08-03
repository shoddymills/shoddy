@echo off
setlocal EnableExtensions EnableDelayedExpansion
rem ===========================================================================
rem MAINTAINER TOOL - pushes to origin/main and creates tags. Assumes write access.
rem
rem scripts\shoddy-release.cmd X.Y.Z [/y]   (run from the repo root)
rem
rem The whole release, from a clean repo, in one shot:
rem   main fast-forwarded to origin/main -> release/VX.Y.Z cut -> build all X.Y.Z
rem   (clean + test + package; a red test stops everything) -> commit package.json
rem   -> push branch -> tag vX.Y.Z -> push tag -> merge --no-ff into main.
rem
rem Pushing the tag is what ships: the Release workflow rebuilds on a clean runner
rem and publishes the GitHub Release with the .vsix attached. The local build here
rem is the gate, not the artifact - nothing is pushed until it is green. The .vsix
rem is build output and is never committed.
rem
rem Refuses to start unless every precondition holds: exact X.Y.Z version, repo
rem root, clean tree, no merge in progress, branch/tag not taken (local or origin).
rem /y skips the prompt.
rem ===========================================================================

set "VER=%~1"
if "%VER%"=="" (
    echo usage: scripts\shoddy-release.cmd X.Y.Z [/y]   e.g. scripts\shoddy-release.cmd 1.0.0
    exit /b 2
)
echo %VER%| findstr /r /c:"^[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*$" >nul
if errorlevel 1 (echo RELEASE STOPPED: version must be exactly X.Y.Z, digits only ^(got "%VER%"^). & exit /b 1)

set "BR=release/V%VER%"
set "TAG=v%VER%"
set "VSIX=vscode-shoddy\vscode-shoddy-%VER%.vsix"

rem --- preconditions: right place, clean state ---
git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (echo RELEASE STOPPED: not inside a git repository. & exit /b 1)
if not exist .git (echo RELEASE STOPPED: run from the repo root. & exit /b 1)
if not exist build.cmd (echo RELEASE STOPPED: this doesn't look like the shoddy repo root. & exit /b 1)
if not exist vscode-shoddy\package.json (echo RELEASE STOPPED: this doesn't look like the shoddy repo root. & exit /b 1)
git diff --quiet
if errorlevel 1 (echo RELEASE STOPPED: unstaged changes present - commit or stash first. & exit /b 1)
git diff --cached --quiet
if errorlevel 1 (echo RELEASE STOPPED: staged-but-uncommitted changes present. & exit /b 1)
git rev-parse -q --verify MERGE_HEAD >nul 2>&1
if not errorlevel 1 (echo RELEASE STOPPED: a merge is in progress - finish or abort it first. & exit /b 1)

rem --- preconditions: name not taken anywhere ---
call :run git fetch origin --tags --prune || exit /b 1
git show-ref --verify --quiet refs/heads/%BR%
if not errorlevel 1 (echo RELEASE STOPPED: branch %BR% already exists locally. & exit /b 1)
git show-ref --verify --quiet refs/remotes/origin/%BR%
if not errorlevel 1 (echo RELEASE STOPPED: branch %BR% already exists on origin. & exit /b 1)
git show-ref --verify --quiet refs/tags/%TAG%
if not errorlevel 1 (echo RELEASE STOPPED: tag %TAG% already exists locally. & exit /b 1)
git ls-remote --exit-code --tags origin %TAG% >nul 2>&1
if not errorlevel 1 (echo RELEASE STOPPED: tag %TAG% already exists on origin. & exit /b 1)

rem --- confirm ---
echo.
echo Release plan for %VER%:
echo   main          -^> fast-forwarded to origin/main
echo   %BR% -^> created; build.cmd all %VER% ^(clean + test + package^)
echo   commit + push -^> package.json ^("%TAG% release"^)
echo   tag + push    -^> %TAG%   ^(this is what triggers the Release workflow^)
echo   main          -^> merge --no-ff %BR%, pushed
echo.
if exist "release-notes\%TAG%.md" (
    echo   release notes -^> release-notes\%TAG%.md
) else (
    echo   release notes -^> MISSING: release-notes\%TAG%.md
    echo                    The release body will fall back to the merge log.
    echo                    Notes must be committed BEFORE the tag: answer n,
    echo                    write them, commit, then re-run. See release-notes\README.md.
)
set "ANS=n"
rem Both spellings: the .sh twin documents -y, and RELEASING.md promises the
rem twins behave identically.
if /i "%~2"=="/y" set "ANS=y"
if /i "%~2"=="-y" set "ANS=y"
if /i not "!ANS!"=="y" set /p ANS=Proceed? (y/N)
if /i not "!ANS!"=="y" (echo aborted, nothing done. & exit /b 0)

rem --- full checkout: release always builds from up-to-date main ---
call :run git checkout main || exit /b 1
call :run git pull --ff-only origin main || exit /b 1
call :run git checkout -b %BR% || exit /b 1

rem --- clear stale packages so the version check below can't pass on an old file ---
if exist vscode-shoddy\*.vsix del /q vscode-shoddy\*.vsix

rem --- build + test + package; nothing has been pushed yet ---
rem The leading .\ is required: with NoDefaultCurrentDirectoryInExePath set,
rem which is the case on a hardened Windows install, cmd.exe will not resolve
rem a batch file from the current directory and the bare name fails with
rem "is not recognized as an internal or external command".
call .\build.cmd all %VER%
if errorlevel 1 (
    echo BUILD/TEST FAILED - nothing was pushed.
    echo inspect, then undo with: git checkout main ^&^& git branch -D %BR%
    exit /b 1
)
if not exist "%VSIX%" (echo RELEASE STOPPED: expected package %VSIX% was not produced. & exit /b 1)
findstr /c:"\"version\": \"%VER%\"" vscode-shoddy\package.json >nul
if errorlevel 1 (echo RELEASE STOPPED: package.json was not bumped to %VER%. & exit /b 1)

rem --- publish: the version bump is the only artifact that belongs in the commit ---
call :run git add vscode-shoddy/package.json || exit /b 1
call :run git commit -m "%TAG% release" || exit /b 1
call :run git push -u origin %BR% || exit /b 1
call :run git tag %TAG% || exit /b 1
call :run git push origin %TAG% || exit /b 1
call :run git checkout main || exit /b 1
call :run git merge --no-ff %BR% -m "Merge branch '%BR%'" || exit /b 1
call :run git push origin main || exit /b 1

echo.
echo DONE - %TAG% is tagged and merged.
echo The Release workflow is now building %TAG% and will publish the GitHub Release
echo with vscode-shoddy-%VER%.vsix attached. Watch it in the Actions tab; if it fails,
echo the tag is already public, so fix forward with a new patch version.
echo Release branch kept as hotfix base: %BR%
echo   ^(delete anytime: git branch -d %BR% ^&^& git push origin --delete %BR%^)
exit /b 0

:run
echo ^> %*
%*
if errorlevel 1 (
    echo RELEASE STOPPED: command failed: %*
    exit /b 1
)
exit /b 0
