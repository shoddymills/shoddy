@echo off
setlocal EnableExtensions EnableDelayedExpansion
rem ===========================================================================
rem MAINTAINER TOOL - pushes to origin/main and deletes remote branches.
rem Assumes write access. Run from the repo root.
rem
rem scripts\shoddy-feature.cmd new NAME    create feature/NAME off up-to-date main
rem scripts\shoddy-feature.cmd ship [/y]   push the current feature branch, merge
rem                                        --no-ff into main, push main, then delete
rem                                        the branch (local + origin)
rem
rem Guards: clean tree required; 'new' refuses names already taken (local or
rem origin); 'ship' only runs from a feature/* branch, refuses when there is
rem nothing to merge, and on merge conflict aborts cleanly and puts you back
rem on your branch.  A leading 'feature/' on NAME is stripped.
rem ===========================================================================

git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (echo STOPPED: not inside a git repository. & exit /b 1)
git diff --quiet
if errorlevel 1 (echo STOPPED: unstaged changes present - commit or stash first. & exit /b 1)
git diff --cached --quiet
if errorlevel 1 (echo STOPPED: staged-but-uncommitted changes present. & exit /b 1)
git rev-parse -q --verify MERGE_HEAD >nul 2>&1
if not errorlevel 1 (echo STOPPED: a merge is in progress - finish or abort it first. & exit /b 1)

if /i "%~1"=="new" goto new
if /i "%~1"=="ship" goto ship
echo usage: scripts\shoddy-feature.cmd new NAME   create feature/NAME off up-to-date main
echo        scripts\shoddy-feature.cmd ship [/y]  push current feature branch, merge --no-ff into main, delete it
exit /b 2

:new
set "NAME=%~2"
if "%NAME%"=="" (echo usage: scripts\shoddy-feature.cmd new NAME & exit /b 2)
if /i "%NAME:~0,8%"=="feature/" set "NAME=%NAME:~8%"
echo %NAME%| findstr /r /c:"^[A-Za-z0-9][A-Za-z0-9._-]*$" >nul
if errorlevel 1 (echo STOPPED: branch name may use letters, digits, . _ - only ^(got "%NAME%"^). & exit /b 1)
set "BR=feature/%NAME%"
call :run git fetch origin --prune || exit /b 1
git show-ref --verify --quiet refs/heads/%BR%
if not errorlevel 1 (echo STOPPED: %BR% already exists locally. & exit /b 1)
git show-ref --verify --quiet refs/remotes/origin/%BR%
if not errorlevel 1 (echo STOPPED: %BR% already exists on origin. & exit /b 1)
call :run git checkout main || exit /b 1
call :run git pull --ff-only origin main || exit /b 1
call :run git checkout -b %BR% || exit /b 1
echo.
echo On %BR% ^(cut from up-to-date main^).
echo Work, commit, then: scripts\shoddy-feature.cmd ship
exit /b 0

:ship
for /f "delims=" %%b in ('git rev-parse --abbrev-ref HEAD') do set "BR=%%b"
if /i not "!BR:~0,8!"=="feature/" (echo STOPPED: current branch is "!BR!" - ship only runs from a feature/* branch. & exit /b 1)
call :run git fetch origin --prune || exit /b 1
for /f "delims=" %%n in ('git rev-list --count origin/main..HEAD') do set "N=%%n"
if "!N!"=="0" (echo STOPPED: no commits on !BR! beyond origin/main - nothing to merge. & exit /b 1)
echo.
echo Will merge these !N! commit^(s^) from !BR! into main, then delete the branch:
git log --oneline origin/main..HEAD
set "ANS=n"
rem Both spellings: the .sh twin documents -y, and RELEASING.md promises the
rem twins behave identically.
if /i "%~2"=="/y" set "ANS=y"
if /i "%~2"=="-y" set "ANS=y"
if /i not "!ANS!"=="y" set /p ANS=Proceed? (y/N)
if /i not "!ANS!"=="y" (echo aborted, nothing done. & exit /b 0)
echo ^> git push -u origin !BR!
git push -u origin !BR!
if errorlevel 1 (echo STOPPED: push rejected - origin/!BR! has commits you don't have. git pull, then re-run ship. & exit /b 1)
call :run git checkout main || exit /b 1
call :run git pull --ff-only origin main || exit /b 1
echo ^> git merge --no-ff !BR!
git merge --no-ff !BR! -m "Merge branch '!BR!'"
if errorlevel 1 (
    git merge --abort
    git checkout !BR!
    echo STOPPED: merge conflicts with main. On !BR! run: git merge main ^(resolve, commit^), then re-run ship.
    exit /b 1
)
call :run git push origin main || exit /b 1
call :run git branch -d !BR! || exit /b 1
call :run git push origin --delete !BR! || exit /b 1
echo.
echo DONE - !BR! merged into main and deleted ^(local + origin^).
exit /b 0

:run
echo ^> %*
%*
if errorlevel 1 (
    echo STOPPED: command failed: %*
    exit /b 1
)
exit /b 0
