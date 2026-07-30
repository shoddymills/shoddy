@echo off
rem Shoddy build wrapper shim for Windows.
rem Runs build.ps1 with an execution-policy bypass so an unsigned local
rem script works regardless of the machine's PowerShell policy.
rem Usage: build.cmd <command> [args]   e.g.  build.cmd build
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
