#!/usr/bin/env pwsh
# MAINTAINER TOOL - the release, from PowerShell.
#
#   scripts/shoddy.ps1 doctor
#   scripts/shoddy.ps1 preflight [2.0.3]
#   scripts/shoddy.ps1 gate [--resume]
#   scripts/shoddy.ps1 package 2.0.3
#   scripts/shoddy.ps1 publish 2.0.3 --yes
#   scripts/shoddy.ps1 release 2.0.3
#   scripts/shoddy.ps1 status
#   scripts/shoddy.ps1 clean
#
# DELIBERATELY THIN. Every twin pair in this repo that carried real logic has
# drifted, because only one of the two ever got run - a release once had to
# fix "native stderr fatal in every PowerShell script", precisely the kind of
# fault that survives when nobody runs the other one. The logic lives once, in
# scripts/gate/driver.mjs, and both shells launch the same file.
#
# Node is already a hard pre-release dependency: verify-docs, verify-errors,
# verify-permissions and verify-host-blind are all Node, and no release has
# ever been cut without them.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$driver = Join-Path $here 'gate/driver.mjs'

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    Write-Host 'STOPPED: node is not on PATH, and the release gates are Node.' -ForegroundColor Red
    exit 1
}

# The exit code is the only thing judged, here as everywhere else in this
# toolchain: node writes progress to stdout and nothing to stderr that is
# fatal, and wrapping it in PowerShell error handling is what used to kill
# runs that had actually succeeded.
$prev = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try { & node $driver @args } finally { $ErrorActionPreference = $prev }
exit $LASTEXITCODE
